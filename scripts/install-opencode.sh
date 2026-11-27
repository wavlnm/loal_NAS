#!/usr/bin/env bash
set -euo pipefail

# 安装固定版本的 OpenCode v2 二进制到 tools/opencode/bin/opencode。
# 代理通过环境变量提供（https_proxy/http_proxy），不写死。
VERSION="${OPENCODE_VERSION:-2.4.2}"
ARCH="linux-x64"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="${1:-$ROOT/tools/opencode/bin}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

URL_PRIMARY="https://cdn.npmmirror.com/packages/%40opencode/cli-${ARCH}/${VERSION}/cli-${ARCH}-${VERSION}.tgz"
URL_FALLBACK="https://registry.npmjs.org/@opencode/cli-${ARCH}/-/cli-${ARCH}-${VERSION}.tgz"
echo "下载 OpenCode ${VERSION}: $URL_PRIMARY"
if ! curl -fL --retry 2 --connect-timeout 15 -o "$TMP/cli.tgz" "$URL_PRIMARY"; then
  echo "镜像下载失败，改用 npm registry：$URL_FALLBACK"
  curl -fL --retry 3 --connect-timeout 15 -o "$TMP/cli.tgz" "$URL_FALLBACK"
fi
tar -xzf "$TMP/cli.tgz" -C "$TMP"

BIN="$(find "$TMP" -type f -name opencode | head -1)"
if [ -z "$BIN" ]; then
  echo "未在包内找到 opencode 可执行文件。" >&2
  exit 1
fi

mkdir -p "$DEST"
install -m 0755 "$BIN" "$DEST/opencode"
printf '%s\n' "$VERSION" > "$DEST/VERSION"
"$DEST/opencode" --version | head -1
echo "已安装到 $DEST/opencode"
