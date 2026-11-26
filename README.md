# loal_NAS · Linux 重建版

把个人电脑变成私人云盘的 **Linux 端**实现，核心功能与原 Windows 版（`/home/kali/项目/loal_NAS`）一致：
外壳程序拉起内置的 [FileBrowser](https://github.com/filebrowser/filebrowser) 子进程（仅监听 `127.0.0.1`），
再用 ASP.NET Core 在 `[::]:5034` 对外，把 FileBrowser 的 API 反代到 `/api/filebrowser/*`，
并在其上叠加 IP 限流封禁、手机端签名鉴权、媒体中转票据、上传磁盘检查等能力。

技术栈：.NET 10（C#，与原项目同一套代码逻辑）+ FileBrowser v2.63.23（Linux amd64）。

## 与 Windows 版的差异

| 方面 | Windows 原版 | Linux 重建版 |
| --- | --- | --- |
| 目标框架 | `net8.0-windows` + WinForms | `net10.0`，无 WinForms |
| 状态/配对界面 | 桌面 WinForms 窗口 | 本地 Web 面板 `/admin`（仅回环可访问） |
| 二维码 | QRCoder + System.Drawing | QRCoder `PngByteQRCode`，免 libgdiplus |
| 设备 ID | 注册表 MachineGuid | `/etc/machine-id` |
| 防火墙 | PowerShell `New-NetFirewallRule` | ufw / firewalld / iptables 自适应放行 |
| 云上报 | 上报 IPv6 到 `reportzs.me` | **已移除**（纯本地/局域网，地址随二维码下发） |
| 注册状态机 | 未注册/注册中/已注册 | **已移除**（无云账号概念） |

核心 Web 逻辑（反代、签名鉴权、限流、媒体中转、磁盘检查、设备追踪、网络探测）与原版保持一致。

## 目录结构

```
loal-nas-linux/
├─ src/LoalNas.Host/        # C# 源码
│  ├─ Program.cs            # Web 宿主 + 端点 + 本地面板
│  ├─ Configuration/        # Host / FileBrowser / Firewall 配置类
│  └─ Services/             # 反代、鉴权、限流、媒体中转、设备身份、网络探测、防火墙、二维码
├─ tools/filebrowser/       # 内置 FileBrowser Linux 二进制
├─ publish/                 # dotnet publish 产物（可直接运行）
└─ deploy/                  # systemd 单元与安装脚本
```

## 运行

开发运行：

```bash
cd src/LoalNas.Host
dotnet run -c Release
```

直接跑发布产物：

```bash
./publish/LoalNas.Host
```

启动后：

- 对外服务：`http://[::]:5034`（手机/局域网设备访问；有公网 IPv6 时可外网直连）
- 本地配对面板：`http://127.0.0.1:5034/admin`（**仅回环可访问**，远程请用 SSH 隧道：`ssh -L 5034:127.0.0.1:5034 用户@主机`）

## 手机配对流程（密钥传递）

1. 浏览器打开 `http://127.0.0.1:5034/admin`，页面显示配对二维码。
2. 二维码内容为 `{ deviceId, deviceName, secret, ip }`，`ip` 优先取公网 IPv6，否则取局域网 IPv4。
3. 手机客户端扫码后得到 `secret` 与地址，后续每个请求携带三个头：
   - `X-NAS-Timestamp`：Unix 秒级时间戳
   - `X-NAS-Nonce`：随机字符串（一次性，防重放）
   - `X-NAS-Hash`：`SHA-256(secret:timestamp:nonce)` 的小写 hex
4. 时间戳允许 ±30 秒偏移；同一 nonce 在窗口内重复使用会被拒绝（401）。

> 面板与 `/api/device/*` 接口仅限回环访问，避免 `secret` 经公网泄露。

## 主要端点

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/`、`/api/system/status` | 服务状态、存储快照（免鉴权） |
| ANY | `/api/filebrowser/{...}` | 反代 FileBrowser API（需手机签名头） |
| POST | `/api/system/media-tickets` | 用 `X-Auth` 换取短时媒体中转票据 |
| GET/HEAD | `/api/system/media-open/{ticketId}/{fileName}` | 票据取流，支持 Range/拖动（URL 带真实扩展名） |
| GET | `/api/trash` | 回收站列表（需手机签名头） |
| POST | `/api/trash/restore` | 恢复条目，body `{"id":"..."}`（原位置冲突返回 409） |
| DELETE | `/api/trash/{id}` | 彻底删除单条 |
| DELETE | `/api/trash` | 清空回收站 |
| GET | `/api/system/terminal/status` | 远程终端开关状态（需手机签名头） |
| POST | `/api/system/terminal/exec` | 执行一条 bash 指令，body `{"command":"..."}`；未开启时直接 404 |
| GET | `/api/system/screen` | 看屏幕：返回电脑当前一帧画面，参数 `w`(640~1920) / `q`(10~95) / `format=jpeg\|png`；支持 `If-None-Match` → 304 |
| GET/POST | `/admin/terminal/{status,enable,disable}` | 本地临时开关终端（仅回环 + Admin Token） |

> 删除语义：`DELETE /api/filebrowser/resources/*` 不再真正删除，而是把文件/目录移动到共享目录外的 `.trash/`（同一磁盘，rename 即可），可在回收站恢复。`.trash` 不在 FileBrowser 根目录内，不会出现在文件列表。
>
> 自动清理：`LoalNas:Trash:RetentionDays`（默认 30 天，`<=0` 关闭），`CleanupIntervalHours` 控制检查间隔（默认每小时）；启动时也会立即清理一次。
| GET | `/admin` | 本地配对面板（仅回环） |
| GET | `/api/device/info` | 设备信息 JSON（仅回环） |
| GET | `/api/device/qr.png` | 配对二维码 PNG（仅回环） |
| GET | `/api/device/payload` | 二维码原始载荷（仅回环，调试用） |

## 防火墙自动放行

`appsettings.json` 中 `LoalNas:Firewall:AutoConfigureOnStartup=true` 时，启动会自动放行对外端口：

- `Tool=auto`：按 ufw（活动）→ firewalld（运行）→ iptables 顺序探测；
- 也可强制 `ufw` / `firewalld` / `iptables` / `none`。

放行需要 `CAP_NET_ADMIN`。systemd 单元已通过 `AmbientCapabilities=CAP_NET_ADMIN` 授予，
因此以普通用户 `kali` 运行即可，无需 root。若手动前台运行且非 root，会记录警告并打印手动命令：

```bash
sudo iptables -I INPUT -p tcp --dport 5034 -j ACCEPT
```

## 安装为系统服务

```bash
./deploy/install-service.sh                 # 复制单元并 daemon-reload（不自动启用）
sudo systemctl enable --now loal-nas        # 开机自启并立即启动
journalctl -u loal-nas -f                   # 查看日志
```

## 配置（appsettings.json）

- `LoalNas:Host:Url`：对外监听地址，默认 `http://[::]:5034`。
- `LoalNas:FileBrowser:Port`：FileBrowser 内部端口，默认 `38080`（仅 127.0.0.1）。
- `LoalNas:FileBrowser:RuntimeDirectoryName`：运行目录，默认相对发布目录的 `runtime/filebrowser`；
  **可填绝对路径**（如 `/home/kali/千私云数据`）把共享文件放到别处。
- `LoalNas:FileBrowser:RootFolderName`：实际共享的子目录名，默认 `file`。
- `LoalNas:Firewall:Tool`：防火墙工具，默认 `auto`。
- `LoalNas:RemoteTerminal:Enabled`：远程终端是否默认开启，默认 `true`；`DurationMinutes` 为启动时的有效期（`0` = 永久）。`/admin` 面板的临时开关只影响当前进程，重启后回到这里的默认值。
- `LoalNas:ScreenCapture:*`：看屏幕参数 —— `MaxWidth`/`MinWidth` 允许的宽度范围、`DefaultWidth`/`DefaultQuality` 未带参数时的默认值、`Framerate` 抓取进程出帧频率（默认 10）、`MaxFrameAgeSeconds` 帧新鲜度上限（默认 5 秒，超过则视为抓取停摆并重新拉起）、`IdleSeconds` 空闲多久停掉抓取进程（默认 60）、`WorkerScript` 抓取脚本相对发布目录的路径、`OutputDirectory` 帧缓存目录（留空用 `~/.cache/loalnas/screen`）。

## 看屏幕（X11）

服务端 `ScreenCaptureService` 按需拉起常驻抓取进程 `tools/screen-capture/screen_worker.py`，
用 gstreamer `ximagesrc` 直接读 X 根窗口，写最新几帧到缓存目录，HTTP 侧只读文件；
参数变化会重启抓取进程并清掉旧帧，空闲 `IdleSeconds` 后自动停止。

- 只支持 **X11 会话**（Xfce/Xorg）。Wayland/GNOME 的 `org.gnome.Mutter.ScreenCast` 路径在 GNOME 51 上有
  stride 断言崩溃（`meta-stream-source.c:994`，会把整个桌面弄崩），已弃用。
- 息屏（DPMS）不影响 X 根窗口帧缓冲，黑屏也能抓到真实桌面；抓取进程**不会**主动点亮屏幕。
- 依赖：`gstreamer1.0-plugins-good`（`ximagesrc`）、`gstreamer1.0-plugins-base`、`x11-xserver-utils`（`xdpyinfo`）。
- `DISPLAY`/`XAUTHORITY` 由服务端从图形会话进程（优先 `xfce4-session`）的 `/proc/*/environ` 读取后传给抓取进程，
  所以服务本身跑在 system 服务里也不需要写死显示编号。
- 带宽参考（1920x1080 桌面实测，公网 3Mbps ≈ 0.35MB/s）：`1280 q60` 约 100~180KB、`1920 q85` 约 330~560KB、
  PNG 完全取决于画面内容（大片纯色时比 JPEG 还小，壁纸/视频则可达 400KB+）。公网建议 1280 档。
- 安全：接口走常规每请求签名头鉴权（`X-NAS-Timestamp/Nonce/Hash`），未加 `/admin` 开关 —— 已知代价是
  画面内容（含屏幕上的密码、验证码）会一并推给手机。

设备身份与 `secret` 持久化在 `~/.local/share/loal-nas/`（`device-id.txt`、`device-secret.txt`）。
删除 `device-secret.txt` 会在下次启动重新生成密钥（手机需重新扫码）。

## 安全说明

- FileBrowser 以 `--noauth --disableExec` 启动，只监听 `127.0.0.1`，不直接对外；对外一律经外壳的签名鉴权。
- 面板与设备接口仅回环可达；`secret` 只通过二维码下发，不在面板明文展示。
- 已移除云端上报，文件传输与地址发现均不经过任何第三方服务器。

## 已知问题与排查

- **视频黑屏一直转圈**：优先换浏览器（Chrome / VLC 等）。实测部分厂商自带浏览器（如荣耀 HonorBrowser）即使服务端正确返回 `206 video/mp4` 也无法播放；同一链接换 Chrome/VLC 正常。
- **视频能下不能边下边播**：MP4 的 `moov` 索引必须在文件头（faststart）。若在末尾，边下边播的播放器会卡住。无损修复：
  `ffmpeg -i in.mp4 -c copy -movflags +faststart out.mp4`
- 媒体链接已带真实扩展名；媒体响应会剥离 FileBrowser 的 `Content-Security-Policy`、`X-Content-Type-Options` 等头，以提升浏览器兼容性。

## 本机验证结果

冒烟测试（.NET 10 / Kali，amd64）已通过：

- `/api/system/status`、`/admin`、`/api/device/info`、`/api/device/qr.png`（PNG 584×584）均 200；
- 设备 ID 来自 `/etc/machine-id`，正确探测到公网 IPv6 与局域网 IPv4；
- 完整鉴权链路：合法签名 → 登录得 JWT（200）→ 带签名访问目录（200）；
- 重放 nonce / 无签名头 / 错误 hash 均返回 401；
- 防火墙自动放行逻辑触发正常（非 root 时按预期降级为告警+手动命令）。
