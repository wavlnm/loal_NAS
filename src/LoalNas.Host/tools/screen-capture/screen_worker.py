#!/usr/bin/env python3
"""千私云 看屏幕 抓取 worker（X11 通道）。

由 LoalNas.Host 的 ScreenCaptureService 按需拉起，常驻运行：
  python3 screen_worker.py --width 1280 --quality 60 --format jpeg --fps 10 \
      --out-dir ~/.cache/loalnas/screen

X11 下直接用 gstreamer ximagesrc 读 X 根窗口，不经过 mutter 录屏代码
（Wayland 的 ScreenCast 路径在 GNOME 51 上有 stride 断言崩溃，已弃用）。
DPMS 息屏只关物理输出，X 根窗口帧缓冲仍在，所以息屏也能抓到真实桌面，
这里**不主动点亮屏幕**（`xset dpms force on` 会把家里的屏幕强行点亮）。
收到 SIGTERM/SIGINT 或父进程消失时清理并退出。
依赖：gstreamer1.0-plugins-good（ximagesrc）、x11-xserver-utils。
"""

import argparse
import os
import signal
import subprocess
import sys
import time

running = True


def on_term(signum, frame):
    global running
    running = False


def clean_frames(out_dir):
    for name in os.listdir(out_dir):
        if name.startswith('f_'):
            try:
                os.unlink(os.path.join(out_dir, name))
            except OSError:
                pass


def native_size(display):
    """用 xdpyinfo 读真实屏幕尺寸，失败返回 None。"""
    try:
        out = subprocess.run(['xdpyinfo', '-display', display],
                             capture_output=True, text=True,
                             timeout=5).stdout
    except Exception:  # noqa: BLE001
        return None
    for line in out.splitlines():
        if 'dimensions:' in line:
            parts = line.split()[1].split('x')
            try:
                return int(parts[0]), int(parts[1])
            except ValueError:
                return None
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--width', type=int, default=1280)
    ap.add_argument('--quality', type=int, default=60)
    ap.add_argument('--format', choices=('jpeg', 'png'), default='jpeg')
    ap.add_argument('--fps', type=int, default=10)
    ap.add_argument('--out-dir', required=True)
    args = ap.parse_args()

    signal.signal(signal.SIGTERM, on_term)
    signal.signal(signal.SIGINT, on_term)

    os.makedirs(args.out_dir, exist_ok=True)
    clean_frames(args.out_dir)

    display = os.environ.get('DISPLAY', ':0')
    fps = max(1, min(args.fps, 30))

    native = native_size(display)
    width = args.width
    if native:
        if width >= native[0]:
            # 请求宽度不低于原生分辨率：直接原尺寸抓，省掉 videoscale 的整帧重算。
            width, height = native[0], native[1]
            scale = False
        else:
            height = max(2, int(round(width * native[1] / native[0] / 2)) * 2)
            scale = True
    else:
        # 读不到原生尺寸：只约束宽度，高度交给 videoscale 按比例推。
        height = max(2, int(round(width * 1080 / 1920 / 2)) * 2)
        scale = True

    pipeline = ['gst-launch-1.0', '-e', 'ximagesrc', 'use-damage=false', '!',
                f'video/x-raw,framerate={fps}/1', '!', 'videoconvert']
    if scale:
        pipeline += ['!', 'videoscale', '!',
                     f'video/x-raw,width={width},height={height}']
    if args.format == 'jpeg':
        pipeline += ['!', 'jpegenc', f'quality={args.quality}']
        suffix = 'jpg'
    else:
        pipeline += ['!', 'pngenc']
        suffix = 'png'
    pipeline += ['!', 'multifilesink',
                 f'location={args.out_dir}/f_%05d.{suffix}', 'max-files=3']

    print(f'start capture {width}x{height} @ {fps}fps {args.format}',
          file=sys.stderr, flush=True)

    gst = None
    try:
        gst = subprocess.Popen(pipeline, stdout=subprocess.DEVNULL,
                               stderr=subprocess.PIPE, text=True)
        while running:
            time.sleep(0.5)
            if os.getppid() == 1:
                print('parent gone, exiting', file=sys.stderr, flush=True)
                break
            if gst.poll() is not None:
                err = (gst.stderr.read() or '')[-400:] if gst.stderr else ''
                print(f'gst exited rc={gst.returncode}: {err}',
                      file=sys.stderr, flush=True)
                break
    except Exception as exc:  # noqa: BLE001
        print(f'fatal: {exc}', file=sys.stderr, flush=True)
        return 1
    finally:
        if gst and gst.poll() is None:
            gst.terminate()
            try:
                gst.wait(timeout=3)
            except subprocess.TimeoutExpired:
                gst.kill()
        clean_frames(args.out_dir)
        print('worker stopped', file=sys.stderr, flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
