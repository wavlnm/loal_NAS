using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// 回收站自动清理：启动时清一次，之后按间隔定期清理超过保留期的条目。
/// </summary>
public sealed class TrashCleanupService(
    TrashService trash,
    IOptions<TrashOptions> options,
    ILogger<TrashCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = options.Value.RetentionDays;
        var interval = TimeSpan.FromHours(Math.Max(1, options.Value.CleanupIntervalHours));

        RunOnce(retentionDays);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                RunOnce(retentionDays);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    private void RunOnce(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return;
        }

        try
        {
            var threshold = DateTimeOffset.Now.AddDays(-retentionDays);
            var removed = trash.PurgeOlderThan(threshold);
            if (removed > 0)
            {
                logger.LogInformation(
                    "回收站自动清理：移除 {Count} 项（保留 {Days} 天）", removed, retentionDays);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "回收站自动清理失败");
        }
    }
}
