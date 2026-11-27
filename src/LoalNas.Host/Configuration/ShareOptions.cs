namespace LoalNas.Host.Configuration;

/// <summary>公开分享链接配置（单文件、临时令牌、可选密码/下载次数）。</summary>
public sealed class ShareOptions
{
    public const string SectionName = "LoalNas:Share";

    /// <summary>未指定有效期时的默认时长。</summary>
    public int DefaultExpiryHours { get; set; } = 24;

    public int MaxExpiryDays { get; set; } = 30;

    public int MaxDownloadsPerShare { get; set; } = 1000;

    public int MaxActiveShares { get; set; } = 500;

    public int CleanupIntervalMinutes { get; set; } = 5;

    /// <summary>密码解锁 cookie 的有效期（不会超过分享自身到期时间）。</summary>
    public int CookieLifetimeMinutes { get; set; } = 120;

    public int PasswordMaxLength { get; set; } = 64;
}
