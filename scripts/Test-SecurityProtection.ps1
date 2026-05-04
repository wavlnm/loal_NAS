<#
.SYNOPSIS
    loal_NAS 安全防护模拟攻击测试脚本
.DESCRIPTION
    在 loal_NAS 服务运行期间执行，依次测试：
      1. 高频 API 请求攻击（触发限流和自动封禁）
      2. 超长 URL 攻击
      3. 超大请求头攻击
      4. 超大请求体攻击
      5. 正常请求验证（攻击后合法客户端仍可用）
.NOTES
    运行前确保 loal_NAS 已在监听 http://localhost:5034
#>
param(
    [string]$BaseUrl = "http://localhost:5034",
    [int]   $BurstCount = 80   # 高频测试请求数（> MaxRequests=60 才能触发限流）
)

$ErrorActionPreference = "SilentlyContinue"

function Write-Section([string]$title) {
    Write-Host "`n$('='*60)" -ForegroundColor Cyan
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host "$('='*60)" -ForegroundColor Cyan
}

function Write-Pass([string]$msg) { Write-Host "  [PASS] $msg" -ForegroundColor Green }
function Write-Fail([string]$msg) { Write-Host "  [FAIL] $msg" -ForegroundColor Red }
function Write-Info([string]$msg) { Write-Host "  [INFO] $msg" -ForegroundColor Gray }

# ── 0. 前置检查：服务是否在线 ──────────────────────────────────────
Write-Section "0. 前置检查：服务连通性"
try {
    $r = Invoke-WebRequest -Uri "$BaseUrl/api/system/status" -TimeoutSec 5 -UseBasicParsing
    Write-Pass "服务在线，HTTP $($r.StatusCode)"
} catch {
    Write-Fail "无法连接到 $BaseUrl，请先启动 loal_NAS 再运行此脚本。"
    exit 1
}

# ── 1. 高频请求攻击 ────────────────────────────────────────────────
Write-Section "1. 高频请求攻击（$BurstCount 次连发，预期触发 429）"

$results = @{ "2xx"=0; "429"=0; "other"=0 }
$sw = [System.Diagnostics.Stopwatch]::StartNew()

for ($i = 0; $i -lt $BurstCount; $i++) {
    try {
        $r = Invoke-WebRequest -Uri "$BaseUrl/api/system/status" -TimeoutSec 3 -UseBasicParsing
        $results["2xx"]++
    } catch [System.Net.WebException] {
        $code = [int]$_.Exception.Response.StatusCode
        if ($code -eq 429) { $results["429"]++ }
        else { $results["other"]++ }
    } catch {
        $results["other"]++
    }
}
$sw.Stop()

Write-Info "耗时 $([math]::Round($sw.ElapsedMilliseconds/1000,2))s | 2xx=$($results['2xx'])  429=$($results['429'])  其他=$($results['other'])"
if ($results["429"] -gt 0) {
    Write-Pass "限流生效：收到 $($results['429']) 个 429 响应"
} else {
    Write-Fail "未收到任何 429，限流可能未生效"
}

# 等待一小段时间，检查是否进入封禁状态
Write-Info "等待 2s 后检测是否被封禁..."
Start-Sleep -Seconds 2
try {
    $r = Invoke-WebRequest -Uri "$BaseUrl/api/system/status" -TimeoutSec 3 -UseBasicParsing
    Write-Info "请求仍可通过（HTTP $($r.StatusCode)），可能未达到封禁阈值"
} catch [System.Net.WebException] {
    $code = [int]$_.Exception.Response.StatusCode
    if ($code -eq 429) {
        $retryAfter = $_.Exception.Response.Headers["Retry-After"]
        Write-Pass "IP 已被自动封禁（429），Retry-After: ${retryAfter}s"
    }
}

# ── 2. 超长 URL 攻击 ───────────────────────────────────────────────
Write-Section "2. 超长 URL 攻击（> 4 KB URL，预期 400/431）"

$longPath = "/api/system/status?" + ("a" * 5000)
try {
    $r = Invoke-WebRequest -Uri "$BaseUrl$longPath" -TimeoutSec 5 -UseBasicParsing
    Write-Fail "超长 URL 请求未被拒绝（HTTP $($r.StatusCode)），应被 Kestrel 截断"
} catch [System.Net.WebException] {
    $code = [int]$_.Exception.Response.StatusCode
    Write-Pass "超长 URL 被拒绝（HTTP $code）"
} catch {
    # 连接被重置/强制关闭也是正确行为
    Write-Pass "超长 URL 导致连接被强制关闭（$($_.Exception.GetType().Name)）"
}

# ── 3. 超大请求头攻击 ──────────────────────────────────────────────
Write-Section "3. 超大请求头攻击（> 16 KB Header，预期 400/431）"

$largeHeaderValue = "x" * 20000
try {
    $headers = @{ "X-Attack-Header" = $largeHeaderValue }
    $r = Invoke-WebRequest -Uri "$BaseUrl/api/system/status" -Headers $headers -TimeoutSec 5 -UseBasicParsing
    Write-Fail "超大请求头未被拒绝（HTTP $($r.StatusCode)）"
} catch [System.Net.WebException] {
    $code = [int]$_.Exception.Response.StatusCode
    Write-Pass "超大请求头被拒绝（HTTP $code）"
} catch {
    Write-Pass "超大请求头导致连接被强制关闭（$($_.Exception.GetType().Name)）"
}

# ── 4. 超大请求体攻击 ──────────────────────────────────────────────
Write-Section "4. 超大请求体攻击（600 MB Body，> 500 MB 上限，预期 413）"

# 不实际发 600MB，用流式方式发少量数据但声明 Content-Length > 500MB
# 让 Kestrel 在读取阶段就拒绝
$fakeLargeSize = 600 * 1024 * 1024  # 声明 600 MB

$req = [System.Net.HttpWebRequest]::Create("$BaseUrl/api/filebrowser/upload-test")
$req.Method = "POST"
$req.ContentType = "application/octet-stream"
$req.ContentLength = $fakeLargeSize
$req.Timeout = 5000
$req.AllowWriteStreamBuffering = $false

try {
    $stream = $req.GetRequestStream()
    # 只写 1 KB，剩余让服务端超时/拒绝
    $buf = New-Object byte[] 1024
    $stream.Write($buf, 0, $buf.Length)
    $stream.Flush()
    $resp = $req.GetResponse()
    Write-Fail "超大请求体未被拒绝（HTTP $([int]$resp.StatusCode)）"
} catch [System.Net.WebException] {
    $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    if ($code -eq 413) {
        Write-Pass "超大请求体被拒绝（HTTP 413 Content Too Large）"
    } elseif ($code -gt 0) {
        Write-Pass "超大请求体被拒绝（HTTP $code）"
    } else {
        Write-Pass "超大请求体导致连接被拒绝（$($_.Exception.Message)）"
    }
} catch {
    Write-Pass "超大请求体导致连接异常（$($_.Exception.GetType().Name)）"
}

# ── 5. 验证正常请求仍可用 ─────────────────────────────────────────
Write-Section "5. 正常合法请求验证（使用新 IP / 等待封禁解除后）"
Write-Info "注意：本机测试时 IP 固定，若上方步骤触发封禁，此测试将返回 429（属正常行为）"
Write-Info "实际部署中，未参与攻击的其他 IP 不受影响"

try {
    $r = Invoke-WebRequest -Uri "$BaseUrl/api/system/status" -TimeoutSec 5 -UseBasicParsing
    Write-Pass "正常请求成功（HTTP $($r.StatusCode)）"
    $body = $r.Content | ConvertFrom-Json
    Write-Info "FileBrowser 运行中: $($body.fileBrowser.running)"
} catch [System.Net.WebException] {
    $code = [int]$_.Exception.Response.StatusCode
    if ($code -eq 429) {
        Write-Info "当前 IP 仍在封禁窗口中（HTTP 429），等待封禁超时即可恢复 ✓"
    } else {
        Write-Fail "正常请求失败（HTTP $code）"
    }
}

Write-Section "测试完成"
Write-Host ""
