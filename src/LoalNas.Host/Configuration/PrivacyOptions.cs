namespace LoalNas.Host.Configuration;

/// <summary>共享根内 .private 隐私目录与管理员窗口配置。</summary>
public sealed class PrivacyOptions
{
    public const string SectionName = "LoalNas:Privacy";

    public string DirectoryName { get; set; } = ".private";

    public int UnlockMinutes { get; set; } = 3;

    public int MinPasswordLength { get; set; } = 6;

    public int MaxPasswordLength { get; set; } = 64;

    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutSeconds { get; set; } = 60;
}
