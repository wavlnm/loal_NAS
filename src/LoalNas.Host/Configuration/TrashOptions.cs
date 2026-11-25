namespace LoalNas.Host.Configuration;

/// <summary>回收站自动清理配置。RetentionDays&lt;=0 表示不自动清理。</summary>
public sealed class TrashOptions
{
    public const string SectionName = "LoalNas:Trash";

    public int RetentionDays { get; set; } = 30;

    public int CleanupIntervalHours { get; set; } = 1;
}
