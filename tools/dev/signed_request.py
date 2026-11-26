#!/usr/bin/env python3
"""带签名头调用 loal_NAS 本机接口的小工具（放项目里，/tmp 会被清）。

用法：
  signed_request.py GET  /api/system/terminal/status
  signed_request.py POST /api/system/terminal/exec '{"command":"echo hi"}'
  signed_request.py GET  "/api/system/screen?w=1920&q=60" -o /path/out.jpg

密钥从 ~/.local/share/loal-nas/device-secret.txt 读取，脚本不会打印它。
"""
import argparse
import hashlib
import os
import secrets
import sys
import time
import urllib.request

BASE = os.environ.get("NAS_BASE", "http://127.0.0.1:5034")
SECRET_FILE = os.path.expanduser("~/.local/share/loal-nas/device-secret.txt")


def read_secret():
    """落盘的 secret 是混淆过的（DeviceIdentityService.EncodeSecret）：
    交换过 index 2/6，并在 index 8 插了一个 '5'。这里按同样规则还原。"""
    raw = open(SECRET_FILE, encoding="utf-8").read().strip()
    if len(raw) != 65:
        return raw  # 万一以后不再混淆
    chars = list(raw[:8] + raw[9:])
    chars[2], chars[6] = chars[6], chars[2]
    return "".join(chars)


def headers():
    secret = read_secret()
    ts = str(int(time.time()))
    nonce = secrets.token_hex(8)
    digest = hashlib.sha256(f"{secret}:{ts}:{nonce}".encode()).hexdigest()
    return {
        "X-NAS-Timestamp": ts,
        "X-NAS-Nonce": nonce,
        "X-NAS-Hash": digest,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("method")
    ap.add_argument("path")
    ap.add_argument("body", nargs="?", default=None)
    ap.add_argument("-o", "--out", default=None)
    args = ap.parse_args()

    data = args.body.encode() if args.body is not None else None
    request = urllib.request.Request(
        BASE + args.path, data=data, method=args.method, headers=headers())
    if args.body is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=40) as response:
            payload = response.read()
            print(f"HTTP {response.status} {response.headers.get('Content-Type')} "
                  f"{len(payload)}B")
            if args.out:
                open(args.out, "wb").write(payload)
                print("已写入", args.out)
            else:
                print(payload[:800].decode("utf-8", "replace"))
    except urllib.error.HTTPError as error:
        print(f"HTTP {error.code}")
        print(error.read()[:800].decode("utf-8", "replace"))
        sys.exit(1)


if __name__ == "__main__":
    main()
