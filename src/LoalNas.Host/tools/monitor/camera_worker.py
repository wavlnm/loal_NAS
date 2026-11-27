#!/usr/bin/env python3
"""千私云 监控 worker（摄像头源）。

由 LoalNas.Host 按需拉起，常驻运行：
  python3 camera_worker.py --device /dev/video0 --preview-width 640 \
      --preview-quality 60 --preview-fps 8 --out-dir ~/.cache/loalnas/camera \
      --ring-dir ~/.cache/loalnas/camera-hd --state-file ... --cmd-file ... \
      --clip-dir-base /mnt/HDD-Data/loal-nas/file/监控录像 \
      --detect 1 --sensitivity mid --record-seconds 10 \
      --quiet-seconds 5 --max-segment-seconds 60 --cooldown-seconds 0

一条 gstreamer 管道、tee 两路，避免两个进程抢同一个 /dev/video0：
  A 预览：解码 -> 缩放到 preview-width -> jpegenc -> multifilesink(max-files=3)
        手机端取图就读这三张里最新的一张，移动侦测也用同一批帧。
  B 高清环形：原始 MJPEG 帧直接落盘（max-files 按 30fps 预留
        max-segment-seconds + quiet-seconds + 余量），等于滚动保留"最近 N 秒"
        的高清画面，触发时不用重开管道。这一路不做限帧，落盘帧率就是摄像头的
        真实出帧率（多为 25/30fps），合成 MP4 时必须用实测帧率，否则播放速度
        会整体变慢或变快。

移动侦测在 Python 里用 PIL 做帧差（缩到 160x90 灰度，均值差 + 变化像素占比
双阈值，连续 3 次确认）。触发后持续录制：连续 quiet-seconds 秒没有运动才结束；
单段达到 max-segment-seconds 秒仍有运动时自动分片，下一段接着录。
环形高清帧 + 每段独立 pw-record 音轨，交给 ffmpeg 合成 faststart MP4，
写到 clip-dir-base/YYYY-MM-DD/HHMMSS_auto.mp4（手动录的是 _manual.mp4，
仍固定 record-seconds 秒）。

控制走文件，避免常驻进程和 web 进程之间的复杂 IPC：
  cmd-file   服务端写：{"detect":bool,"sensitivity":"low|mid|high","manual":int}
  state-file worker 写：运行状态、今日事件列表
收到 SIGTERM/SIGINT 或父进程消失时清理并退出。
依赖：gstreamer1.0-plugins-good/base、ffmpeg、PIL、python3-gi 不需要。
"""

import argparse
import json
import os
import queue
import shutil
import signal
import subprocess
import sys
import threading
import time

try:
    from PIL import Image, ImageChops, ImageFilter, ImageStat
except ImportError:  # 没有 PIL 就只出预览，不做侦测
    Image = ImageChops = ImageFilter = ImageStat = None

running = True

# 暗光下传感器噪点很大，所以先高斯模糊再比"差值超过 24 的像素占比"，
# 阈值取实测噪底（模糊后均值差≈0.5、变化占比 0.0000）之上若干倍：
#   high  ≈ 画面里 0.4% 的像素明显变化（约 58 像素，160x90 尺度）
#   mid   ≈ 1.5%（一只手/一个人走近镜头的一角）
#   low   ≈ 5%（人横穿画面）
SENSITIVITY = {
    # (亮度均值差阈值, 变化像素占比阈值)
    'low': (8.0, 0.05),
    'mid': (4.0, 0.015),
    'high': (2.0, 0.004),
}
DIFF_SIZE = (160, 90)
DIFF_CUT = 24  # 灰度差超过这个值才算"变化像素"
# UVC 摄像头 MJPEG 的常见出帧率上限；环形缓冲按它预留容量（合成时用实测帧率），
# 避免默认 --ring-fps 10 时缓冲只够存三分之一时长的画面。
RING_PLAN_FPS = 30


def on_term(signum, frame):
    global running
    running = False


def write_json(path, data):
    tmp = path + '.tmp'
    try:
        with open(tmp, 'w', encoding='utf-8') as fh:
            json.dump(data, fh, ensure_ascii=False)
        os.replace(tmp, path)
    except OSError:
        pass


def read_json(path):
    try:
        with open(path, encoding='utf-8') as fh:
            return json.load(fh)
    except Exception:  # noqa: BLE001
        return None


def clean_dir(path, prefix):
    """删掉目录里以 prefix 开头的帧文件。

    multifilesink 每次启动都从 0 开始编号，上一轮留下的更大编号文件不会被覆盖，
    会被当成"最新帧"读走或剪进片段，所以启动和退出都必须清干净。
    """
    try:
        names = os.listdir(path)
    except OSError:
        return
    for name in names:
        if not name.startswith(prefix):
            continue
        item = os.path.join(path, name)
        if not os.path.isfile(item):
            continue
        try:
            os.unlink(item)
        except OSError:
            pass


def clean_clip_dirs(path):
    """清掉异常退出遗留的分段临时目录。"""
    try:
        names = os.listdir(path)
    except OSError:
        return
    for name in names:
        if not name.startswith('_clip'):
            continue
        item = os.path.join(path, name)
        if os.path.isdir(item):
            shutil.rmtree(item, ignore_errors=True)


class Worker:
    def __init__(self, args):
        self.args = args
        self.detect_enabled = bool(args.detect)
        self.sensitivity = args.sensitivity
        self.manual_token = 0
        self.busy = False           # 正在录制事件/手动片段
        self.motion_active = False
        self.last_trigger = 0.0
        self.events = []            # 今日事件（内存里维护，同时落 state）
        self.events_date = self.today()
        self.hits = 0
        # 跨线程共享的状态（检测线程更新运动时间，事件线程读取并决定何时结束）
        self.state_lock = threading.Lock()
        self.last_motion_at = 0.0
        self.event_active = False
        self.segment_index = 0
        self.segment_started_at = 0.0
        self._encode_queue = queue.Queue()
        self.last_score = 0.0
        self.last_mean = 0.0
        self.used_caps = True
        self.caps_retried = False
        self.source_resolution = None
        self.prev = None
        self.preview_fps = 0.0
        self._last_index = None
        self._last_index_time = 0.0

    @staticmethod
    def today():
        return time.strftime('%Y-%m-%d')

    # ---------- 管道 ----------

    def start_pipeline(self, with_caps=True):
        a = self.args
        source_caps = (f'image/jpeg,width={a.clip_width},height={a.clip_height}'
                       if with_caps and a.clip_width > 0 else None)
        pipeline = ['gst-launch-1.0', '-e', 'v4l2src', f'device={a.device}', '!']
        if source_caps:
            pipeline += [source_caps, '!']
        pipeline += [
            'tee', 'name=t',
            't.', '!', 'queue', '!', 'jpegdec', '!', 'videoconvert', '!',
            'videoscale', '!',
            f'video/x-raw,width={a.preview_width},height={self.preview_height()}',
            '!', 'videorate', '!', f'video/x-raw,framerate={a.preview_fps}/1',
            '!', 'jpegenc', f'quality={a.preview_jpeg_quality}', '!',
            'multifilesink', f'location={a.out_dir}/f_%05d.jpg', 'max-files=3',
            't.', '!', 'queue', '!', 'multifilesink',
            f'location={a.ring_dir}/h_%08d.jpg', f'max-files={self.ring_size()}',
        ]
        self.used_caps = bool(source_caps)
        self.gst = subprocess.Popen(pipeline, stdout=subprocess.DEVNULL,
                                    stderr=subprocess.PIPE, text=True)

    def preview_height(self):
        # 按高清源（默认 1280x720）的宽高比等比推预览高度，并取偶数
        base_w = max(2, self.args.clip_width)
        base_h = max(2, self.args.clip_height)
        return max(2, int(round(self.args.preview_width * base_h / base_w / 2)) * 2)

    def measure_source(self):
        """从环形高清帧里读一次真实分辨率，写进状态供排查。"""
        if self.source_resolution:
            return
        try:
            names = sorted(n for n in os.listdir(self.args.ring_dir)
                           if n.startswith('h_') and n.endswith('.jpg'))
            if not names:
                return
            with Image.open(os.path.join(self.args.ring_dir, names[-1])) as im:
                self.source_resolution = list(im.size)
                print(f'源分辨率 {self.source_resolution} '
                      f'（显式 caps={"是" if self.used_caps else "否，用设备默认"}）',
                      file=sys.stderr, flush=True)
        except Exception:  # noqa: BLE001
            pass

    def max_preview_index(self):
        best = -1
        try:
            for name in os.listdir(self.args.out_dir):
                if not (name.startswith('f_') and name.endswith('.jpg')):
                    continue
                try:
                    best = max(best, int(name[2:-4]))
                except ValueError:
                    pass
        except OSError:
            return best
        return best

    def measure_preview_fps(self):
        """multifilesink 的编号就是累计帧数，用编号增量算真实出帧率。"""
        idx = self.max_preview_index()
        now = time.time()
        if idx < 0:
            return
        if self._last_index is None:
            self._last_index, self._last_index_time = idx, now
            return
        if now - self._last_index_time >= 2.0:
            delta = idx - self._last_index
            if delta >= 0:
                self.preview_fps = round(delta / (now - self._last_index_time), 1)
            self._last_index, self._last_index_time = idx, now

    def ring_size(self):
        # 覆盖一个最长分段 + 静止判定窗口 + 快照/编码余量；
        # 按 30fps 上限预留才能覆盖摄像头原生帧率。
        seconds = (self.args.max_segment_seconds + self.args.quiet_seconds + 5)
        return int(seconds * RING_PLAN_FPS) + RING_PLAN_FPS

    def stop_pipeline(self):
        gst = getattr(self, 'gst', None)
        if gst and gst.poll() is None:
            gst.terminate()
            try:
                gst.wait(timeout=3)
            except subprocess.TimeoutExpired:
                gst.kill()

    # ---------- 侦测 ----------

    def newest_frame(self):
        """取最新一张"已经写完"的预览帧路径。"""
        best = None
        best_mtime = 0.0
        now = time.time()
        try:
            for name in os.listdir(self.args.out_dir):
                if not name.startswith('f_'):
                    continue
                path = os.path.join(self.args.out_dir, name)
                try:
                    st = os.stat(path)
                except OSError:
                    continue
                if st.st_size < 200 or now - st.st_mtime < 0.06:
                    continue  # 半张图或空文件
                if st.st_mtime > best_mtime:
                    best, best_mtime = path, st.st_mtime
        except OSError:
            return None
        return best

    def detect_loop(self):
        interval = 1.0 / max(1.0, self.args.detect_fps)
        while running:
            time.sleep(interval)
            if not self.detect_enabled or Image is None:
                self.hits = 0
                continue
            # 录像期间也要继续检测：事件循环靠 last_motion_at 判断何时静止，
            # 不能像以前那样 busy 就跳过整轮。
            path = self.newest_frame()
            if not path:
                continue
            try:
                with Image.open(path) as im:
                    gray = (im.convert('L').resize(DIFF_SIZE)
                            .filter(ImageFilter.GaussianBlur(2)))
            except Exception:  # noqa: BLE001
                continue

            now = time.time()

            if self.prev is not None:
                diff = ImageChops.difference(gray, self.prev)
                stat = ImageStat.Stat(diff)
                mean = sum(stat.mean) / len(stat.mean)
                hist = diff.histogram()
                changed = sum(hist[DIFF_CUT:]) / float(DIFF_SIZE[0] * DIFF_SIZE[1])
                self.last_score = round(changed, 4)
                self.last_mean = round(mean, 2)
                mean_th, ratio_th = SENSITIVITY[self.sensitivity]
                if mean > mean_th and changed > ratio_th:
                    self.hits += 1
                else:
                    self.hits = 0
                self.motion_active = self.hits >= self.args.confirm_hits
                if self.motion_active:
                    with self.state_lock:
                        self.last_motion_at = now

            self.prev = gray
            if (self.motion_active and not self.busy
                    and now - self.last_trigger >= self.args.cooldown_seconds):
                self.last_trigger = now
                self.trigger('auto')

    def trigger(self, kind):
        """认领一次录制；busy 必须在起线程前设置，避免检测线程重复触发。"""
        now = time.time()
        with self.state_lock:
            if self.busy:
                return
            self.busy = True
            self.segment_started_at = now
            if kind == 'auto':
                self.event_active = True
                self.last_motion_at = now
                self.segment_index = 1
        target = self.record_event if kind == 'auto' else self.record_manual
        threading.Thread(target=target, args=(now,), daemon=True).start()

    # ---------- 录像 ----------

    def _start_audio(self, tmp):
        audio = os.path.join(tmp, 'a.wav')
        if self.args.no_audio:
            return audio, None
        try:
            rec = subprocess.Popen(
                ['pw-record', '--rate', '16000', '--channels', '1', audio],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except OSError:
            return audio, None
        return audio, rec

    @staticmethod
    def _stop_audio(rec):
        if rec is None:
            return
        rec.terminate()
        try:
            rec.wait(timeout=3)
        except subprocess.TimeoutExpired:
            rec.kill()

    def _new_segment(self, started, index, kind):
        tmp = os.path.join(
            self.args.ring_dir, f'_clip_{int(started * 1000)}_{kind}_{index}')
        shutil.rmtree(tmp, ignore_errors=True)
        os.makedirs(tmp)
        audio, rec = self._start_audio(tmp)
        with self.state_lock:
            self.segment_index = index
            self.segment_started_at = started
        return tmp, audio, rec

    def record_event(self, started):
        """自动事件：持续检测，静止 quiet-seconds 秒结束，超长自动分片。"""
        a = self.args
        segment_start = started
        index = 1
        tmp = audio = rec = None
        next_seg = None
        try:
            tmp, audio, rec = self._new_segment(segment_start, index, 'auto')
            while running:
                this_start = segment_start
                ended = None
                reason = 'quiet'
                # 1 秒一轮；静止判定/分片边界最多晚约 1 秒。
                while running:
                    time.sleep(1.0)
                    now = time.time()
                    with self.state_lock:
                        last_motion = self.last_motion_at
                    quiet = now - last_motion
                    if quiet >= a.quiet_seconds:
                        ended, reason = now, 'quiet'
                        break
                    if (now - segment_start >= a.max_segment_seconds
                            and quiet < 3.0):
                        ended, reason = now, 'max'
                        break
                if ended is None:
                    ended, reason = time.time(), 'stop'

                self._stop_audio(rec)
                rec = None

                next_seg = None
                if reason == 'max' and running:
                    # 先起下一段音轨再快照上一段，尽量缩小分片间的音频空隙。
                    segment_start = ended
                    index += 1
                    next_seg = self._new_segment(segment_start, index, 'auto')

                self._snapshot_segment('auto', this_start, ended, tmp, audio)
                tmp = None
                if next_seg is None:
                    break
                tmp, audio, rec = next_seg
                next_seg = None
        except Exception as exc:  # noqa: BLE001
            print(f'record event error: {exc}', file=sys.stderr, flush=True)
            self._stop_audio(rec)
            if tmp:
                shutil.rmtree(tmp, ignore_errors=True)
            if next_seg:
                self._stop_audio(next_seg[2])
                shutil.rmtree(next_seg[0], ignore_errors=True)
        finally:
            with self.state_lock:
                self.busy = False
                self.event_active = False
                self.segment_index = 0
                self.segment_started_at = 0.0
            self.last_trigger = time.time()
            self.hits = 0
            self.motion_active = False
            # 录像期间占用的是高清分支，帧基准已过期，重新取一帧做参考
            self.prev = None

    def record_manual(self, started):
        """手动录像保持固定 record-seconds 秒。"""
        a = self.args
        tmp = audio = rec = None
        try:
            tmp, audio, rec = self._new_segment(started, 1, 'manual')
            time.sleep(a.record_seconds)
            ended = time.time()
            self._stop_audio(rec)
            rec = None
            self._snapshot_segment('manual', started, ended, tmp, audio)
            tmp = None
        except Exception as exc:  # noqa: BLE001
            print(f'record manual error: {exc}', file=sys.stderr, flush=True)
            self._stop_audio(rec)
            if tmp:
                shutil.rmtree(tmp, ignore_errors=True)
        finally:
            with self.state_lock:
                self.busy = False
                self.segment_index = 0
                self.segment_started_at = 0.0
            self.hits = 0
            self.prev = None

    def _snapshot_segment(self, kind, started, ended, tmp, audio):
        """立刻按时间窗复制环形帧，再把 ffmpeg 编码丢给后台线程。"""
        frames, fps = self.snapshot_ring(tmp, since=started, until=ended)
        if frames == 0:
            print('no hd frames, skip clip', file=sys.stderr, flush=True)
            shutil.rmtree(tmp, ignore_errors=True)
            return
        fps = min(60.0, max(1.0, fps))
        self._encode_queue.put((tmp, audio, kind, started, ended, fps, frames))

    def encode_loop(self):
        """串行编码，避免多段长事件同时跑 ffmpeg 抢 CPU。"""
        a = self.args
        while running:
            try:
                tmp, audio, kind, started, ended, fps, _ = \
                    self._encode_queue.get(timeout=0.5)
            except queue.Empty:
                continue
            clip_path = None
            try:
                date = time.strftime('%Y-%m-%d')
                hhmmss = time.strftime('%H%M%S', time.localtime(started))
                folder = os.path.join(a.clip_dir_base, date)
                os.makedirs(folder, exist_ok=True)
                clip_path = self._unique_clip_path(folder, hhmmss, kind)

                cmd = ['ffmpeg', '-hide_banner', '-loglevel', 'error', '-y',
                       '-framerate', f'{fps:.3f}', '-i',
                       os.path.join(tmp, 'seq_%08d.jpg')]
                has_audio = (audio and os.path.exists(audio)
                             and os.path.getsize(audio) > 1000)
                if has_audio:
                    cmd += ['-i', audio, '-c:a', 'aac', '-b:a', '64k']
                else:
                    cmd += ['-an']
                cmd += ['-c:v', 'libx264', '-preset', 'veryfast',
                        '-crf', str(a.clip_crf), '-pix_fmt', 'yuv420p',
                        '-shortest', '-movflags', '+faststart', clip_path]
                res = subprocess.run(cmd, capture_output=True, text=True,
                                     timeout=300)
                if res.returncode != 0:
                    print(f'ffmpeg clip failed: {res.stderr[-300:]}',
                          file=sys.stderr, flush=True)
                    clip_path = None
            except Exception as exc:  # noqa: BLE001
                print(f'encode error: {exc}', file=sys.stderr, flush=True)
                clip_path = None
            finally:
                shutil.rmtree(tmp, ignore_errors=True)
                self._encode_queue.task_done()

            if not clip_path:
                continue
            try:
                size = os.path.getsize(clip_path)
            except OSError:
                size = 0
            with self.state_lock:
                self.events_date = self.today()
                self.events.append({
                    'at': time.strftime('%Y-%m-%d %H:%M:%S',
                                        time.localtime(started)),
                    'kind': kind,
                    'seconds': max(1, int(round(ended - started))),
                    'bytes': size,
                    'path': os.path.relpath(clip_path, a.clip_dir_base),
                })
                self.events = self.events[-200:]
            print(f'clip saved: {clip_path} ({size // 1024}KB)',
                  file=sys.stderr, flush=True)

    @staticmethod
    def _unique_clip_path(folder, hhmmss, kind):
        path = os.path.join(folder, f'{hhmmss}_{kind}.mp4')
        suffix = 2
        while os.path.exists(path):
            path = os.path.join(folder, f'{hhmmss}_{kind}_{suffix}.mp4')
            suffix += 1
        return path

    def snapshot_ring(self, tmp, since=None, until=None):
        """把环形帧复制成从 1 开始的连续序列，返回 (帧数, 实测帧率)。

        since/until 限定本段的时间窗，避免把上一段或下一段的帧剪进来。
        环形这一路不做限帧，落盘帧率就是摄像头真实出帧率（常见 25/30fps），
        用首尾帧落盘时间的跨度估帧率，交给 ffmpeg 后播放速度才与现实一致。
        """
        names = []
        stamps = {}
        for name in sorted(n for n in os.listdir(self.args.ring_dir)
                           if n.startswith('h_') and n.endswith('.jpg')):
            path = os.path.join(self.args.ring_dir, name)
            try:
                mtime = os.stat(path).st_mtime
            except OSError:
                continue
            if since is not None and mtime < since:
                continue
            if until is not None and mtime > until:
                continue
            names.append(name)
            stamps[name] = mtime
        fps = max(5.0, float(self.args.ring_fps))
        if len(names) >= 2 and stamps[names[-1]] > stamps[names[0]]:
            fps = (len(names) - 1) / (stamps[names[-1]] - stamps[names[0]])
        for idx, name in enumerate(names, start=1):
            try:
                shutil.copyfile(os.path.join(self.args.ring_dir, name),
                                os.path.join(tmp, f'seq_{idx:08d}.jpg'))
            except OSError:
                return idx - 1, fps
        return len(names), fps

    # ---------- 指令与状态 ----------

    def cmd_loop(self):
        last = 0.0
        while running:
            time.sleep(0.4)
            try:
                mtime = os.stat(self.args.cmd_file).st_mtime
            except OSError:
                continue
            if mtime == last:
                continue
            last = mtime
            cmd = read_json(self.args.cmd_file) or {}
            if 'detect' in cmd:
                self.detect_enabled = bool(cmd['detect'])
            sens = cmd.get('sensitivity')
            if sens in SENSITIVITY:
                self.sensitivity = sens
            if not self.detect_enabled:
                self.motion_active = False
                self.prev = None
            token = int(cmd.get('manual') or 0)
            if token and token != self.manual_token:
                self.manual_token = token
                if not self.busy:
                    self.trigger('manual')

    def state_loop(self):
        while running:
            time.sleep(1.0)
            if Image is not None:
                self.measure_source()
            self.measure_preview_fps()
            if self.events_date != self.today():
                self.events_date = self.today()
                self.events = []
            with self.state_lock:
                busy = self.busy
                event_active = self.event_active
                segment_index = self.segment_index
                segment_started = self.segment_started_at
                last_motion = self.last_motion_at
                events = list(self.events)
            quiet_remaining = 0.0
            if event_active:
                quiet_remaining = max(
                    0.0, self.args.quiet_seconds - (time.time() - last_motion))
            write_json(self.args.state_file, {
                'running': True,
                'device': self.args.device,
                'previewWidth': self.args.preview_width,
                'detect': self.detect_enabled,
                'sensitivity': self.sensitivity,
                'motionActive': self.motion_active,
                'recording': busy,
                'eventActive': event_active,
                'segmentIndex': segment_index,
                'segmentStartedAt': segment_started,
                'lastMotionAt': last_motion,
                'quietRemaining': round(quiet_remaining, 1),
                'lastScore': self.last_score,
                'lastMean': self.last_mean,
                'sourceResolution': self.source_resolution,
                'explicitCaps': self.used_caps,
                'previewFpsMeasured': self.preview_fps,
                'lastTrigger': self.last_trigger,
                'recordSeconds': self.args.record_seconds,
                'quietSeconds': self.args.quiet_seconds,
                'maxSegmentSeconds': self.args.max_segment_seconds,
                'cooldownSeconds': self.args.cooldown_seconds,
                'events': events,
                'imageLib': Image is not None,
            })


def main():
    global running
    running = True
    ap = argparse.ArgumentParser()
    ap.add_argument('--device', default='/dev/video0')
    ap.add_argument('--preview-width', type=int, default=640)
    ap.add_argument('--preview-fps', type=int, default=8)
    ap.add_argument('--preview-jpeg-quality', type=int, default=6)
    ap.add_argument('--ring-fps', type=int, default=10)
    ap.add_argument('--clip-width', type=int, default=1280)
    ap.add_argument('--clip-height', type=int, default=720)
    ap.add_argument('--detect-fps', type=int, default=3)
    ap.add_argument('--confirm-hits', type=int, default=3)
    ap.add_argument('--record-seconds', type=int, default=10)
    ap.add_argument('--quiet-seconds', type=int, default=5)
    ap.add_argument('--max-segment-seconds', type=int, default=60)
    ap.add_argument('--cooldown-seconds', type=int, default=0)
    ap.add_argument('--clip-crf', type=int, default=23)
    ap.add_argument('--detect', type=int, default=0)
    ap.add_argument('--sensitivity', default='mid', choices=sorted(SENSITIVITY))
    ap.add_argument('--no-audio', action='store_true')
    ap.add_argument('--out-dir', required=True)
    ap.add_argument('--ring-dir', required=True)
    ap.add_argument('--state-file', required=True)
    ap.add_argument('--cmd-file', required=True)
    ap.add_argument('--clip-dir-base', required=True)
    args = ap.parse_args()

    signal.signal(signal.SIGTERM, on_term)
    signal.signal(signal.SIGINT, on_term)

    if not os.path.exists(args.device):
        print(f'设备不存在: {args.device}', file=sys.stderr, flush=True)
        return 2

    for d in (args.out_dir, args.ring_dir,
              os.path.dirname(args.state_file), args.clip_dir_base):
        os.makedirs(d, exist_ok=True)
    clean_dir(args.out_dir, 'f_')
    clean_dir(args.ring_dir, 'h_')
    clean_clip_dirs(args.ring_dir)

    worker = Worker(args)

    # 先认领指令文件里的现状：否则上次遗留的 manual 时间戳会让新 worker 一起来就录一段。
    initial = read_json(args.cmd_file) or {}
    worker.manual_token = int(initial.get('manual') or 0)
    if 'detect' in initial:
        worker.detect_enabled = bool(initial['detect'])
    if initial.get('sensitivity') in SENSITIVITY:
        worker.sensitivity = initial['sensitivity']

    worker.start_pipeline()
    print(f'camera worker up: {args.device} preview={args.preview_width} '
          f'@{args.preview_fps}fps ring={worker.ring_size()}帧 '
          f'detect={worker.detect_enabled}/{worker.sensitivity}',
          file=sys.stderr, flush=True)

    threads = [threading.Thread(target=t, daemon=True) for t in
               (worker.detect_loop, worker.cmd_loop, worker.state_loop,
                worker.encode_loop)]
    for th in threads:
        th.start()

    rc = 0
    try:
        while running:
            time.sleep(0.5)
            if os.getppid() == 1:
                print('parent gone, exiting', file=sys.stderr, flush=True)
                break
            if worker.gst.poll() is not None:
                err = (worker.gst.stderr.read() or '')[-400:] if worker.gst.stderr else ''
                if worker.used_caps and not worker.caps_retried:
                    # 有些 UVC 驱动不接受显式 1280x720 协商，退回设备默认格式再试一次
                    worker.caps_retried = True
                    print(f'显式高清 caps 起流失败，改用设备默认：{err[-200:]}',
                          file=sys.stderr, flush=True)
                    worker.start_pipeline(with_caps=False)
                    continue
                print(f'gst exited rc={worker.gst.returncode}: {err}',
                      file=sys.stderr, flush=True)
                rc = 1
                break
    finally:
        running = False
        worker.stop_pipeline()
        write_json(args.state_file, {'running': False,
                                     'stoppedAt': time.strftime('%Y-%m-%d %H:%M:%S')})
        clean_dir(args.out_dir, 'f_')
        clean_dir(args.ring_dir, 'h_')
        clean_clip_dirs(args.ring_dir)
        print('camera worker stopped', file=sys.stderr, flush=True)
    return rc


if __name__ == '__main__':
    sys.exit(main())
