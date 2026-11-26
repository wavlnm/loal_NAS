#!/usr/bin/env python3
"""千私云 监控 worker（摄像头源）。

由 LoalNas.Host 按需拉起，常驻运行：
  python3 camera_worker.py --device /dev/video0 --preview-width 640 \
      --preview-quality 60 --preview-fps 8 --out-dir ~/.cache/loalnas/camera \
      --ring-dir ~/.cache/loalnas/camera-hd --state-file ... --cmd-file ... \
      --clip-dir-base /mnt/HDD-Data/loal-nas/file/监控录像 \
      --detect 1 --sensitivity mid --record-seconds 10 --cooldown-seconds 30

一条 gstreamer 管道、tee 两路，避免两个进程抢同一个 /dev/video0：
  A 预览：解码 -> 缩放到 preview-width -> jpegenc -> multifilesink(max-files=3)
        手机端取图就读这三张里最新的一张，移动侦测也用同一批帧。
  B 高清环形：原始 MJPEG 帧直接落盘（max-files 限制为 record-seconds 帧数），
        等于滚动保留"最近 N 秒"的高清画面，触发时不用重开管道。

移动侦测在 Python 里用 PIL 做帧差（缩到 160x90 灰度，均值差 + 变化像素占比
双阈值，连续 3 次确认，之后进入冷却）。触发后录 record-seconds 秒：
环形高清帧 + pw-record 录的音轨，交给 ffmpeg 合成 faststart MP4，
写到 clip-dir-base/YYYY-MM-DD/HHMMSS_auto.mp4（手动录的是 _manual.mp4）。

控制走文件，避免常驻进程和 web 进程之间的复杂 IPC：
  cmd-file   服务端写：{"detect":bool,"sensitivity":"low|mid|high","manual":int}
  state-file worker 写：运行状态、今日事件列表
收到 SIGTERM/SIGINT 或父进程消失时清理并退出。
依赖：gstreamer1.0-plugins-good/base、ffmpeg、PIL、python3-gi 不需要。
"""

import argparse
import json
import os
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


class Worker:
    def __init__(self, args):
        self.args = args
        self.detect_enabled = bool(args.detect)
        self.sensitivity = args.sensitivity
        self.manual_token = 0
        self.busy = False           # 正在录制一段事件
        self.motion_active = False
        self.last_trigger = 0.0
        self.events = []            # 今日事件（内存里维护，同时落 state）
        self.events_date = self.today()
        self.hits = 0
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
        fps = max(5, self.args.ring_fps)
        return int(self.args.record_seconds * fps) + fps

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
            if self.busy:
                continue
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

            self.prev = gray
            if self.motion_active and now - self.last_trigger >= self.args.cooldown_seconds:
                self.last_trigger = now
                self.trigger('auto')

    def trigger(self, kind):
        """录一段事件视频（阻塞式，期间侦测暂停）。"""
        threading.Thread(target=self.record, args=(kind,),
                         daemon=True).start()

    # ---------- 录像 ----------

    def record(self, kind):
        if self.busy:
            return
        self.busy = True
        a = self.args
        tmp = os.path.join(a.ring_dir, '_clip')
        started = time.time()
        clip_path = None
        score = 0.0
        try:
            shutil.rmtree(tmp, ignore_errors=True)
            os.makedirs(tmp)

            # 音轨：与视频同起同止，时长由我们控制（pw-record 没有 --duration）
            audio = os.path.join(tmp, 'a.wav')
            rec = None
            if not a.no_audio:
                try:
                    rec = subprocess.Popen(
                        ['pw-record', '--rate', '16000', '--channels', '1', audio],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                except OSError:
                    rec = None

            time.sleep(a.record_seconds)

            if rec is not None:
                rec.terminate()
                try:
                    rec.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    rec.kill()

            frames = self.snapshot_ring(tmp, since=started)
            if frames == 0:
                print('no hd frames, skip clip', file=sys.stderr, flush=True)
                return

            date = time.strftime('%Y-%m-%d')
            hhmmss = time.strftime('%H%M%S', time.localtime(started))
            folder = os.path.join(a.clip_dir_base, date)
            os.makedirs(folder, exist_ok=True)
            clip_path = os.path.join(folder, f'{hhmmss}_{kind}.mp4')

            cmd = ['ffmpeg', '-hide_banner', '-loglevel', 'error', '-y',
                   '-framerate', str(a.ring_fps), '-i',
                   os.path.join(tmp, 'seq_%08d.jpg')]
            if rec is not None and os.path.exists(audio) and os.path.getsize(audio) > 1000:
                cmd += ['-i', audio]
            else:
                cmd += ['-an']
            cmd += ['-c:v', 'libx264', '-preset', 'veryfast', '-crf', str(a.clip_crf),
                    '-pix_fmt', 'yuv420p']
            if rec is not None:
                cmd += ['-c:a', 'aac', '-b:a', '64k']
            cmd += ['-shortest', '-movflags', '+faststart', clip_path]
            res = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
            if res.returncode != 0:
                print(f'ffmpeg clip failed: {res.stderr[-300:]}',
                      file=sys.stderr, flush=True)
                clip_path = None
        except Exception as exc:  # noqa: BLE001
            print(f'record error: {exc}', file=sys.stderr, flush=True)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
            self.busy = False
            self.hits = 0

        if clip_path:
            try:
                size = os.path.getsize(clip_path)
            except OSError:
                size = 0
            self.events_date = self.today()
            self.events.append({
                'at': time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(started)),
                'kind': kind,
                'seconds': int(time.time() - started),
                'bytes': size,
                'path': os.path.relpath(clip_path, a.clip_dir_base),
            })
            self.events = self.events[-200:]
            print(f'clip saved: {clip_path} ({size // 1024}KB)',
                  file=sys.stderr, flush=True)

    def snapshot_ring(self, tmp, since=None):
        """把环形帧复制成从 1 开始的连续序列（ffmpeg 需要无缺口序列）。

        since 之后的帧才算本次事件，避免把上一轮残留的旧帧剪进片段。
        """
        names = []
        for name in sorted(n for n in os.listdir(self.args.ring_dir)
                           if n.startswith('h_') and n.endswith('.jpg')):
            path = os.path.join(self.args.ring_dir, name)
            if since is not None and os.stat(path).st_mtime < since:
                continue
            names.append(name)
        keep = int(max(1, self.args.record_seconds) * max(5, self.args.ring_fps))
        if len(names) > keep:
            names = names[-keep:]
        for idx, name in enumerate(names, start=1):
            try:
                shutil.copyfile(os.path.join(self.args.ring_dir, name),
                                os.path.join(tmp, f'seq_{idx:08d}.jpg'))
            except OSError:
                return idx - 1
        return len(names)

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
            write_json(self.args.state_file, {
                'running': True,
                'device': self.args.device,
                'previewWidth': self.args.preview_width,
                'detect': self.detect_enabled,
                'sensitivity': self.sensitivity,
                'motionActive': self.motion_active,
                'recording': self.busy,
                'lastScore': self.last_score,
                'lastMean': self.last_mean,
                'sourceResolution': self.source_resolution,
                'explicitCaps': self.used_caps,
                'previewFpsMeasured': self.preview_fps,
                'lastTrigger': self.last_trigger,
                'recordSeconds': self.args.record_seconds,
                'cooldownSeconds': self.args.cooldown_seconds,
                'events': list(self.events),
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
    ap.add_argument('--cooldown-seconds', type=int, default=30)
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
               (worker.detect_loop, worker.cmd_loop, worker.state_loop)]
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
        print('camera worker stopped', file=sys.stderr, flush=True)
    return rc


if __name__ == '__main__':
    sys.exit(main())
