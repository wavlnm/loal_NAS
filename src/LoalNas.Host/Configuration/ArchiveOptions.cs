namespace LoalNas.Host.Configuration;

/// <summary>在线压缩（ZIP）配置。</summary>
public sealed class ArchiveOptions
{
    public const string SectionName = "LoalNas:Archive";

    /// <summary>fast / normal / smaller。</summary>
    public string DefaultLevel { get; set; } = "normal";

    public int Concurrency { get; set; } = 2;

    public int MaxQueuedJobs { get; set; } = 20;

    /// <summary>结束任务记录保留时长；产物 zip 不受影响。</summary>
    public int JobRetentionMinutes { get; set; } = 60;

    public int TimeoutMinutes { get; set; } = 60;

    public long MaxSourceBytes { get; set; } = 50L * 1024 * 1024 * 1024;

    public long DiskHeadroomBytes { get; set; } = 100L * 1024 * 1024;
}
