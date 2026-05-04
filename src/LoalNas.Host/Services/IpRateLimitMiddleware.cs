using System.Collections.Concurrent;
using System.Net;

namespace LoalNas.Host.Services;

/// <summary>
/// 基于 IP 的双层防护：
///   层 1 — 滑动窗口限流：同一 IP 在 <see cref="WindowSeconds"/> 秒内超过 <see cref="MaxRequests"/> 次，
///           返回 429 并增加计数。
///   层 2 — 自动封禁：在窗口内累计触发 429 超过 <see cref="BanAfterRejections"/> 次，
///           封禁该 IP <see cref="BanMinutes"/> 分钟（再次请求直接 429，不复位）。
/// </summary>
internal sealed class IpRateLimitMiddleware
{
    // ── 可调参数 ─────────────────────────────────────────────────────────────
    private const int WindowSeconds     = 10;   // 限流窗口长度
    private const int MaxRequests       = 300;  // 窗口内允许的最大请求数（30次/秒，个人 NAS 宽松）
    private const int BanAfterRejections = 100; // 连续触发100次限制才封禁（个人设备，正常不会达到）
    private const int BanMinutes        = 2;    // 封禁时长（分钟）
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record WindowEntry(long WindowStart, int Count, int Rejections);

    private readonly RequestDelegate _next;
    private readonly ILogger<IpRateLimitMiddleware> _logger;

    // 限流状态表（IP → 当前窗口计数）
    private static readonly ConcurrentDictionary<string, WindowEntry> _windows = new();
    // 封禁表（IP → 封禁截止时间）
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _bans = new();

    public IpRateLimitMiddleware(RequestDelegate next, ILogger<IpRateLimitMiddleware> logger)
    {
        _next   = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = GetClientIp(context);

        // ── 层 2：封禁检查 ───────────────────────────────────────────────────
        if (_bans.TryGetValue(ip, out var bannedUntil))
        {
            if (DateTimeOffset.UtcNow < bannedUntil)
            {
                var remaining = (int)(bannedUntil - DateTimeOffset.UtcNow).TotalSeconds;
                context.Response.Headers["Retry-After"] = remaining.ToString();
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsync($"Too many requests. IP banned for {remaining}s.");
                return;
            }
            // 封禁已过期，移除
            _bans.TryRemove(ip, out _);
        }

        // ── 层 1：滑动窗口限流 ───────────────────────────────────────────────
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var entry = _windows.AddOrUpdate(
            ip,
            _ => new WindowEntry(now, 1, 0),
            (_, old) =>
            {
                // 超出窗口则重置
                if (now - old.WindowStart >= WindowSeconds)
                    return new WindowEntry(now, 1, 0);
                return old with { Count = old.Count + 1 };
            });

        if (entry.Count > MaxRequests)
        {
            // 增加拒绝计数
            var updated = _windows.AddOrUpdate(
                ip,
                _ => new WindowEntry(now, 1, 1),
                (_, old) => old with { Rejections = old.Rejections + 1 });

            // 达到自动封禁阈值
            if (updated.Rejections >= BanAfterRejections)
            {
                var until = DateTimeOffset.UtcNow.AddMinutes(BanMinutes);
                _bans[ip] = until;
                _windows.TryRemove(ip, out _);
                _logger.LogWarning("IP {Ip} banned until {Until} after exceeding rate limit repeatedly.", ip, until.LocalDateTime);
            }
            else
            {
                _logger.LogDebug("Rate limit hit for IP {Ip} ({Count}/{Max} in window).", ip, entry.Count, MaxRequests);
            }

            context.Response.Headers["Retry-After"] = WindowSeconds.ToString();
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsync("Too many requests.");
            return;
        }

        await _next(context);
    }

    private static string GetClientIp(HttpContext context)
    {
        // 优先读代理头（本场景通常无代理，但保留以防将来套 nginx）
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out _))
                return first;
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
