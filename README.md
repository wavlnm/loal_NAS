# loal_NAS

把个人电脑变成私人云盘的 Windows 端 MVP。

当前仓库已经落了一个最小可运行方案：ASP.NET Core 宿主在启动时自动拉起本地 FileBrowser，并通过自己的 API 前缀把 FileBrowser 的文件接口代理出去。

## 当前 MVP 行为

1. 宿主进程启动时自动启动 FileBrowser。
2. 宿主进程关闭时自动关闭 FileBrowser。
3. 宿主默认监听 `http://[::]:5034`，可被同局域网 IPv6 地址访问。
4. FileBrowser 只监听 `127.0.0.1`，不会直接暴露到公网。
5. 首次运行会自动创建一个单独的 `file` 目录，只暴露这个目录。
6. 你的外部接口前缀固定为 `/api/filebrowser/*`。
7. FileBrowser 原生 API 路径保持不变，只是去掉最前面的 `/api` 再挂到你的前缀下。
8. 当前登录流程直接复用 FileBrowser 的 noauth 模式：`GET /api/filebrowser/login` 会返回 JWT，后续请求需要带上 `X-Auth: <token>`。
9. 宿主启动后会在终端打印当前电脑可用的 IPv6 访问地址。
10. 宿主启动时会检查 Windows 防火墙是否已放行对外监听端口；如果没有，会自动运行防火墙脚本尝试放行。

## 目录说明

- `src/LoalNas.Host`：最小 C# 宿主服务。
- `tools/filebrowser/filebrowser.exe`：仓库内置的 FileBrowser 二进制。
- `src/LoalNas.Host/bin/.../runtime/filebrowser/file`：首次运行后自动创建的受控文件目录。

## 运行方式

1. 在仓库根目录执行 `dotnet run --project src/LoalNas.Host`。
2. 宿主默认监听 `http://[::]:5034`，从其它设备访问时要写成 `http://[你的IPv6地址]:5034`。
3. 访问 `GET /api/system/status` 确认宿主和 FileBrowser 都已启动。
4. 先调用 `GET /api/filebrowser/login` 拿到 token。
5. 手机端后续对接 `/api/filebrowser/*` 时，在请求头里带 `X-Auth: <token>`。

说明：

- `http://[::]:5034` 表示监听所有 IPv6 地址，不是实际访问地址。
- 手机端实际访问时，要把 `::` 换成电脑当前的 IPv6 地址，例如 `http://[2408:xxxx:....]:5034`。
- 如果使用链路本地地址 `fe80::...`，通常还需要网卡作用域；面向普通用户时更推荐使用公网 IPv6 或 ULA 地址。
- 启动成功后，终端会直接打印可用 IPv6 URL，优先使用其中的 `Global IPv6` 或 `Unique local IPv6`。
- 如果 5034 端口尚未放行，宿主会在启动时自动检查并尝试运行防火墙脚本。
- 如果当前进程不是管理员权限，Windows 可能会弹出 UAC 提示来请求放行脚本提升权限。

## 防火墙脚本

- 脚本位置：`scripts/Enable-LoalNasFirewallRule.ps1`
- 默认行为：放行 Windows 入站 TCP 5034 端口，作用于 `Private` 配置文件。
- 管理员 PowerShell 执行：`powershell -ExecutionPolicy Bypass -File .\scripts\Enable-LoalNasFirewallRule.ps1`
- 如果当前网络被 Windows 标记为 Public，可执行：`powershell -ExecutionPolicy Bypass -File .\scripts\Enable-LoalNasFirewallRule.ps1 -Profiles Public`
- 如果你明确需要所有网络配置文件都放行，可执行：`powershell -ExecutionPolicy Bypass -File .\scripts\Enable-LoalNasFirewallRule.ps1 -Profiles Any`

示例：

- 登录取 token：`GET /api/filebrowser/login`
- 文件列表：`GET /api/filebrowser/resources/`，并带 `X-Auth: <token>`
- 文件上传：沿用 FileBrowser 原生上传接口，只是把前缀改成 `/api/filebrowser`

## 为什么当前选择启动即常驻

对 MVP 来说，启动即常驻比“来请求再拉起 FileBrowser”更省代码，也更稳：

1. 不需要额外处理冷启动超时。
2. 不需要在每个请求上判断和等待子进程拉起。
3. 后续手机端调试时行为更稳定。

如果后面确认内存占用或待机策略有压力，再改成按需拉起更合适。

## Push troubleshooting notes

This repository originally failed to push over HTTPS because outbound access to github.com:443 was unstable.

The working fix was:

1. Keep the repository remote on SSH over port 443: `ssh://git@ssh.github.com:443/wavlnm/loal_NAS.git`
2. Do not reuse the default `~/.ssh/id_rsa` key when it is already bound to another repository as a deploy key
3. Create a repository-specific SSH key for this repository
4. Add that key to GitHub as a writable deploy key
5. Configure Git to use that key with `core.sshCommand`

Current local configuration:

- Remote: `origin -> ssh://git@ssh.github.com:443/wavlnm/loal_NAS.git`
- SSH key: `C:/Users/Administrator/.ssh/id_ed25519_loal_NAS`
- Branch tracking: `master -> origin/master`

If push fails again, first verify:

- `ssh -T -p 443 git@ssh.github.com`
- `git config --get core.sshCommand`
- `git remote -v`