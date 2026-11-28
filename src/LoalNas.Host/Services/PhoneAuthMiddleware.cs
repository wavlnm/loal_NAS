using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LoalNas.Host.Services;

/// <summary>
/// 手机端访问鉴权中间件。
/// 每个请求须携带：
///   X-NAS-Timestamp : Unix 时间戳（秒）
///   X-NAS-Nonce     : 随机字符串
///   X-NAS-Hash      : SHA-256(secret:timestamp:nonce) 小写 hex
/// 维护 nonce 缓冲区防重放。本地面板路径（/admin、/api/device）由回环限制保护，这里放行。
/// </summary>
public sealed class PhoneAuthMiddleware
{
    private const int AllowedClockSkewSeconds = 30;
    private const int NonceRetentionSeconds = AllowedClockSkewSeconds + 10;

    private static readonly HashSet<string> _bypassPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/system/status",
        "/",
        "/admin",
    };

    // 公网分享页走 token 鉴权，不要求手机端 HMAC 头；只放行严格形状的路径。
    private static readonly Regex _shareReadPath = new(
        @"^/s/[A-Za-z0-9_-]{22}(?:/file)?$", RegexOptions.Compiled);
    private static readonly Regex _shareUnlockPath = new(
        @"^/s/[A-Za-z0-9_-]{22}/unlock$", RegexOptions.Compiled);
    // 插件审批桥：仅本机 POST，handler 内部再做 loopback + bearer 校验。
    private static readonly Regex _internalApprovalPath = new(
        @"^/internal/agent/approvals$", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, long> _seenNonces = new(StringComparer.Ordinal);
    private long _lastCleanupTick = Environment.TickCount64;

    private readonly RequestDelegate _next;
    private readonly DeviceIdentityService _identity;
    private readonly AppRegistry _apps;
    private readonly ILogger<PhoneAuthMiddleware> _logger;

    public PhoneAuthMiddleware(
        RequestDelegate next,
        DeviceIdentityService identity,
        AppRegistry apps,
        ILogger<PhoneAuthMiddleware> logger)
    {
        _next = next;
        _identity = identity;
        _apps = apps;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (_bypassPaths.Contains(path))
        {
            await _next(context);
            return;
        }

        // 公开分享：页面/文件读取（GET/HEAD）与密码解锁（POST）由 token 自身鉴权。
        if ((HttpMethods.IsGet(context.Request.Method) ||
             HttpMethods.IsHead(context.Request.Method)) &&
            _shareReadPath.IsMatch(path))
        {
            await _next(context);
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method) && _shareUnlockPath.IsMatch(path))
        {
            await _next(context);
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method) && _internalApprovalPath.IsMatch(path))
        {
            await _next(context);
            return;
        }

        // 应用公开路由（清单 publicRoutes 决定）：由应用自身的房间令牌鉴权，不要求手机端 HMAC。
        if (_apps.IsPublicPath(path))
        {
            await _next(context);
            return;
        }

        // 本地面板/设备信息接口（仅回环可达，见 Program.cs 的限制）
        if (path.StartsWith("/api/device", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // media-open 票据接口放行（票据本身已鉴权）
        if (path.StartsWith("/api/system/media-open/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // 转码缓存文件：播放器无法按请求轮换 nonce，靠不可猜的 key（sha256 前 32 位）保护
        if (HttpMethods.IsGet(context.Request.Method) &&
            (path.StartsWith("/api/system/audio-cache/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/api/system/monitor-live/", StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // 预览图与原始媒体读取仍受 FileBrowser 的 X-Auth 保护，放行以免内部组件逐个带 nonce
        if (HttpMethods.IsGet(context.Request.Method) &&
            (path.StartsWith("/api/filebrowser/preview/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/api/filebrowser/raw/", StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        var nonce = context.Request.Headers["X-NAS-Nonce"].FirstOrDefault()?.Trim();
        var timestampHeader = context.Request.Headers["X-NAS-Timestamp"].FirstOrDefault()?.Trim();
        var hash = context.Request.Headers["X-NAS-Hash"].FirstOrDefault()?.Trim();

        if (string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(hash))
        {
            _logger.LogWarning("Auth rejected [{Ip}] {Method} {Path}: missing X-NAS-Nonce or X-NAS-Hash",
                context.Connection.RemoteIpAddress, context.Request.Method, path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing auth headers." });
            return;
        }

        CleanupStaleNonces();
        if (_seenNonces.ContainsKey(nonce))
        {
            _logger.LogWarning("Auth rejected [{Ip}] {Method} {Path}: replayed nonce",
                context.Connection.RemoteIpAddress, context.Request.Method, path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Replayed nonce." });
            return;
        }

        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var secret = _identity.DeviceSecret;
        long? requestTimestamp = null;

        if (!string.IsNullOrWhiteSpace(timestampHeader))
        {
            if (!long.TryParse(timestampHeader, out var parsedTimestamp))
            {
                _logger.LogWarning("Auth rejected [{Ip}] {Method} {Path}: invalid X-NAS-Timestamp",
                    context.Connection.RemoteIpAddress, context.Request.Method, path);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid timestamp." });
                return;
            }

            requestTimestamp = parsedTimestamp;
        }

        var valid = false;
        if (requestTimestamp is long clientTimestamp)
        {
            var skewSeconds = Math.Abs(nowSeconds - clientTimestamp);
            if (skewSeconds <= AllowedClockSkewSeconds)
            {
                var expected = ComputeHash(secret, clientTimestamp, nonce);
                valid = string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase);
            }
        }
        else
        {
            // 兼容旧客户端：未带时间戳时沿用 ±1 秒猜测
            foreach (var ts in new[] { nowSeconds, nowSeconds - 1, nowSeconds + 1 })
            {
                var expected = ComputeHash(secret, ts, nonce);
                if (string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
                {
                    valid = true;
                    break;
                }
            }
        }

        if (!valid)
        {
            _logger.LogWarning("Auth rejected [{Ip}] {Method} {Path}: hash 不匹配",
                context.Connection.RemoteIpAddress, context.Request.Method, path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Auth failed." });
            return;
        }

        _seenNonces[nonce] = nowSeconds;
        await _next(context);
    }

    private static string ComputeHash(string secret, long timestamp, string nonce)
    {
        var input = $"{secret}:{timestamp}:{nonce}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void CleanupStaleNonces()
    {
        var now = Environment.TickCount64;
        if (now - _lastCleanupTick < 5_000) return;
        _lastCleanupTick = now;

        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - NonceRetentionSeconds;
        foreach (var kv in _seenNonces)
        {
            if (kv.Value < cutoff)
                _seenNonces.TryRemove(kv.Key, out _);
        }
    }
}
