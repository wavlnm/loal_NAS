#!/usr/bin/env bash
# 安装 loal_NAS Linux 版为 systemd 服务（不会自动启用，需手动 enable --now）
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
sudo cp "$HERE/loal-nas.service" /etc/systemd/system/loal-nas.service
sudo systemctl daemon-reload
echo "单元已安装到 /etc/systemd/system/loal-nas.service"
echo "启用并立即启动： sudo systemctl enable --now loal-nas"
echo "查看运行日志：   journalctl -u loal-nas -f"
echo "本地配对面板：   http://127.0.0.1:5034/admin"
