#!/usr/bin/env python3
"""监控/看屏接口的一轮完整回归检查（本机签名请求，端到端实测）。

用法：python3 tools/dev/check_monitor.py [--skip-record]
不改动任何设置：结束时把移动侦测恢复成检查前的状态。
"""

import json
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from signed_request import BASE, headers  # noqa: E402


def call(method, path, body=None, extra_headers=(), out=None, max_time=None):
    args = ['curl', '-s', '-D', '/dev/stderr', '-o', out or '/dev/null',
            '-X', method, BASE + path]
    for key, value in headers().items():
        args += ['-H', f'{key}: {value}']
    if body is not None:
        args += ['-H', 'Content-Type: application/json', '-d', body]
    for h in extra_headers:
        args += ['-H', h]
    if max_time:
        args = ['timeout', str(max_time)] + args
    proc = subprocess.run(args, capture_output=True, text=True)
    head = proc.stderr
    status = 0
    match = re.search(r'HTTP/[\d.]+ (\d{3})', head)
    if match:
        status = int(match.group(1))
    etag = None
    match = re.search(r'(?i)^etag: (.+)\r?$', head, re.MULTILINE)
    if match:
        etag = match.group(1).strip()
    size = 0
    if out and os.path.exists(out):
        size = os.path.getsize(out)
    else:
        size = len(proc.stdout)
    return status, etag, size, head


def jcall(method, path, body=None):
    proc = subprocess.run(
        ['curl', '-s', '-X', method, BASE + path]
        + [x for k, v in headers().items() for x in ('-H', f'{k}: {v}')]
        + (['-H', 'Content-Type: application/json', '-d', body] if body is not None else []),
        capture_output=True, text=True, timeout=40)
    try:
        return json.loads(proc.stdout)
    except json.JSONDecodeError:
        return {'_raw': proc.stdout[:300]}


def main():
    skip_record = '--skip-record' in sys.argv
    problems = []

    before = jcall('GET', '/api/monitor/status')
    detect_before = before['detect']['enabled']
    sens_before = before['detect']['sensitivity']
    print(f"检查前：侦测={detect_before} 灵敏度={sens_before} "
          f"源分辨率={before['worker'].get('sourceResolution') if before.get('worker') else None}")

    # 1. 桌面源取图 + ETag/304
    status, etag, size, _ = call('GET', '/api/system/screen?w=1920&q=85', out='/tmp/chk_desk.jpg')
    print(f'桌面源：HTTP {status} {size // 1024}KB etag={etag}')
    if status != 200 or size < 10240:
        problems.append('桌面源取图异常')
    status304, _, _, _ = call('GET', '/api/system/screen?w=1920&q=85',
                              extra_headers=[f'If-None-Match: {etag}'] if etag else [])
    print(f'桌面源二次请求（带 ETag）：HTTP {status304}（应为 304）')
    if status304 != 304:
        problems.append('ETag/304 未命中')

    # 2. 摄像头源两档
    for width in (640, 1280):
        status, _, size, _ = call('GET', f'/api/system/screen?source=camera&w={width}&q=60',
                                  out=f'/tmp/chk_cam_{width}.jpg')
        print(f'摄像头源 {width}：HTTP {status} {size // 1024}KB')
        if status != 200 or size < 2048:
            problems.append(f'摄像头源 {width} 异常')

    # 3. 状态里应能看到 1280x720 源与预览帧率
    time.sleep(3)
    status_now = jcall('GET', '/api/monitor/status')
    worker = status_now.get('worker') or {}
    print(f"worker：running={worker.get('running')} 源={worker.get('sourceResolution')} "
          f"预览fps={worker.get('previewFps')} 显式caps={worker.get('explicitCaps')}")
    if worker.get('sourceResolution') != '1280x720':
        problems.append(f"源分辨率不是 1280x720（{worker.get('sourceResolution')}）")
    if not worker.get('running'):
        problems.append('摄像头进程未运行')

    # 4. 片段列表
    clips = jcall('GET', '/api/monitor/clips')
    print(f"片段：日期={clips.get('dates')} 今日 {len(clips.get('items', []))} 件")

    # 5. 侦测开高灵敏度，观察 40 秒是否误报
    jcall('POST', '/api/monitor/detect', json.dumps({'enabled': True, 'sensitivity': 'high'}))
    print('已开侦测（high），观察 40 秒误报…')
    time.sleep(40)
    after = jcall('GET', '/api/monitor/status')
    w2 = after.get('worker') or {}
    print(f"  40 秒后：lastScore={w2.get('lastScore')} lastMean={w2.get('lastMean')} "
          f"motionActive={w2.get('motionActive')} 今日片段={after.get('clipsToday')}")
    if (w2.get('lastScore') or 0) > 0.004:
        problems.append(f"静止画面被判为移动（lastScore={w2.get('lastScore')}）")

    # 6. 听现场：票据 + 取 4 秒流
    ticket = jcall('POST', '/api/monitor/listen-ticket', '{}')
    path = ticket.get('path')
    if not path:
        problems.append('听现场票据申请失败')
    else:
        status, _, size, _ = call('GET', path, out='/tmp/chk_listen.mp3', max_time=7)
        dur = subprocess.run(
            ['ffprobe', '-v', 'error', '-show_entries', 'format=duration',
             '-of', 'csv=p=0', '/tmp/chk_listen.mp3'],
            capture_output=True, text=True).stdout.strip()
        print(f'听现场：HTTP {status} {size // 1024}KB 时长={dur}s')
        if status != 200 or size < 8000:
            problems.append('听现场音频流异常')

    # 7. 手动录一段
    if not skip_record:
        clips_before = len(jcall('GET', '/api/monitor/clips').get('items', []))
        jcall('POST', '/api/monitor/record', '{}')
        print('已请求手动录像，等 25 秒…')
        time.sleep(25)
        clips_after = jcall('GET', '/api/monitor/clips').get('items', [])
        newest = clips_after[0]['name'] if clips_after else None
        print(f'片段数 {clips_before} -> {len(clips_after)}，最新 {newest}')
        if len(clips_after) <= clips_before:
            problems.append('手动录像没有产出新片段')
        else:
            full = os.path.join('/mnt/HDD-Data/loal-nas/file/监控录像',
                                time.strftime('%Y-%m-%d'), newest)
            info = subprocess.run(
                ['ffprobe', '-v', 'error', '-show_entries',
                 'stream=codec_type,width,height', '-show_entries', 'format=duration',
                 '-of', 'csv=p=0', full],
                capture_output=True, text=True).stdout.strip().replace('\n', ' ')
            print(f'  新片段：{info}')
            if '1280,720' not in info:
                problems.append('事件片段不是 720p')

    # 恢复检查前的侦测设置
    jcall('POST', '/api/monitor/detect',
          json.dumps({'enabled': detect_before, 'sensitivity': sens_before}))
    print(f'已恢复侦测设置：{detect_before}/{sens_before}')

    print('\n结论：' + ('全部通过' if not problems else '；'.join(problems)))
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
