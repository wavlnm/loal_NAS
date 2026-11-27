using System.Collections.Concurrent;

namespace LoalNas.Host.Services;

/// <summary>
/// 分享端点限流。frp 之后所有公网请求来源都是同一个内网地址，按 IP 限流没有意义，
/// 因此按 token 计数；只返回 429，不做任何封禁，避免误伤其他接收者。
/// </summary>
public sealed class ShareRateLimiter
{
    private const int WindowSeconds = 10;
    private const int PageMaxRequests = 30;
    private const int FileMaxRequests = 600;

    private sealed record Window(long WindowStart, int Count);

    private readonly ConcurrentDictionary<string, Window> _page = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Window> _file = new(StringComparer.Ordinal);

    public bool TryAcquirePage(string token) => TryAcquire(_page, token, PageMaxRequests);

    public bool TryAcquireFile(string token) => TryAcquire(_file, token, FileMaxRequests);

    public int RetryAfterSeconds => WindowSeconds;

    private static bool TryAcquire(
        ConcurrentDictionary<string, Window> table, string token, int max)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entry = table.AddOrUpdate(
            token,
            _ => new Window(now, 1),
            (_, old) => now - old.WindowStart >= WindowSeconds
                ? new Window(now, 1)
                : old with { Count = old.Count + 1 });

        if (table.Count > 10_000)
        {
            foreach (var pair in table)
            {
                if (now - pair.Value.WindowStart >= WindowSeconds)
                {
                    table.TryRemove(pair.Key, out _);
                }
            }
        }

        return entry.Count <= max;
    }
}
