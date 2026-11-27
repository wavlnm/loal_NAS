namespace LoalNas.Host.Services;

/// <summary>
/// 一条文件分享记录。Token 即访问凭证，明文保存以便手机端随时重新复制链接；
/// 存储文件权限固定为 0600。
/// </summary>
public sealed class ShareRecord
{
    public string Token { get; set; } = "";
    public string ResourcePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int? MaxDownloads { get; set; }
    public int DownloadCount { get; set; }
    public string? PasswordHash { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsActive(DateTimeOffset now) => !Revoked && !IsExpired(now);

    public bool IsExhausted => MaxDownloads is int max && DownloadCount >= max;

    public ShareRecord Clone() => (ShareRecord)MemberwiseClone();
}

public static class ShareTokens
{
    private const int TokenBytes = 16;
    public const int TokenLength = 22; // 16 字节 Base64Url 去掉填充后的长度

    public static string New()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static bool IsValid(string? token) =>
        token is { Length: TokenLength } &&
        token.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}
