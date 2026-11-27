using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// 单文件公开分享：临时令牌、可选密码与下载次数，记录持久化在 ~/.local/share/loal-nas/shares.json。
/// 分享页与文件流不经过 FileBrowser，直接按共享根相对路径读取磁盘文件。
/// </summary>
public sealed class ShareService : IHostedService, IDisposable
{
    public const string CookieName = "nas_share";

    public enum UnlockStatus
    {
        Ok,
        BadPassword,
        Locked,
        NotFound,
    }

    private sealed class ShareDocument
    {
        public int Version { get; set; } = 1;
        public List<ShareRecord> Shares { get; set; } = new();
    }

    private sealed class UnlockAttempt
    {
        public int Failures;
        public DateTimeOffset LockedUntil;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly TimeSpan UnlockLockDuration = TimeSpan.FromSeconds(60);
    private const int UnlockFailuresBeforeLock = 5;

    private readonly ILogger<ShareService> _logger;
    private readonly IOptions<ShareOptions> _options;
    private readonly IOptions<PublicHostOptions> _host;
    private readonly FileBrowserProcessManager _manager;
    private readonly DeviceIdentityService _identity;
    private readonly object _lock = new();
    private readonly List<ShareRecord> _shares = new();
    private readonly ConcurrentDictionary<string, UnlockAttempt> _unlockAttempts = new(StringComparer.Ordinal);
    private byte[]? _cookieKey;
    private CancellationTokenSource? _cleanupCts;

    public ShareService(
        ILogger<ShareService> logger,
        IOptions<ShareOptions> options,
        IOptions<PublicHostOptions> host,
        FileBrowserProcessManager manager,
        DeviceIdentityService identity)
    {
        _logger = logger;
        _options = options;
        _host = host;
        _manager = manager;
        _identity = identity;
        Load();
    }

    private ShareOptions Options => _options.Value;

    public string PublicBaseUrl => _host.Value.PublicBaseUrl?.Trim() ?? "";

    // ── 创建/查询/撤销 ─────────────────────────────────────────────────────

    public ShareRecord? Create(
        string? resourcePath, int? expiresInSeconds, string? password, int? maxDownloads,
        out string? error)
    {
        error = null;

        if (!SharePathPolicy.TryResolveFile(
                _manager.SharedRootPath, resourcePath, out var fullPath, out var resolveError))
        {
            error = resolveError switch
            {
                SharePathPolicy.ResolveError.DirectoryNotAllowed => "directory_not_allowed",
                SharePathPolicy.ResolveError.FileNotFound => "file_not_found",
                _ => "invalid_path",
            };
            return null;
        }

        var maxExpirySeconds = (long)Math.Max(1, Options.MaxExpiryDays) * 24 * 3600;
        var ttlSeconds = expiresInSeconds ?? Math.Max(1, Options.DefaultExpiryHours) * 3600L;
        if (ttlSeconds < 60 || ttlSeconds > maxExpirySeconds)
        {
            error = "invalid_expiry";
            return null;
        }

        string? passwordHash = null;
        if (!string.IsNullOrEmpty(password))
        {
            if (password.Length > Math.Max(1, Options.PasswordMaxLength) ||
                password.Trim().Length == 0)
            {
                error = "invalid_password";
                return null;
            }

            passwordHash = SharePasswords.Hash(password);
        }

        if (maxDownloads is int limit &&
            (limit < 1 || limit > Math.Max(1, Options.MaxDownloadsPerShare)))
        {
            error = "invalid_max_downloads";
            return null;
        }

        long size;
        try
        {
            size = new FileInfo(fullPath).Length;
        }
        catch
        {
            error = "file_not_found";
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (_shares.Count(s => s.IsActive(now)) >= Math.Max(1, Options.MaxActiveShares))
            {
                error = "too_many_shares";
                return null;
            }

            var token = ShareTokens.New();
            while (_shares.Any(s => string.Equals(s.Token, token, StringComparison.Ordinal)))
            {
                token = ShareTokens.New();
            }

            var record = new ShareRecord
            {
                Token = token,
                ResourcePath = SharePathPolicy.Normalize(resourcePath) ?? resourcePath!,
                FileName = Path.GetFileName(fullPath),
                SizeBytes = size,
                CreatedAt = now,
                ExpiresAt = now.AddSeconds(ttlSeconds),
                MaxDownloads = maxDownloads,
                DownloadCount = 0,
                PasswordHash = passwordHash,
            };
            _shares.Add(record);
            SaveLocked();
            _logger.LogInformation(
                "已创建文件分享：{File}（{Days:0.##} 天有效{Password}{Limit}）",
                record.FileName,
                ttlSeconds / 86400.0,
                record.HasPassword ? "，带密码" : "",
                record.MaxDownloads is int m ? $"，限 {m} 次" : "");
            return record.Clone();
        }
    }

    public IReadOnlyList<ShareRecord> List()
    {
        lock (_lock)
        {
            return _shares
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => s.Clone())
                .ToList();
        }
    }

    public ShareRecord? Find(string? token)
    {
        if (!ShareTokens.IsValid(token))
        {
            return null;
        }

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var record = _shares.FirstOrDefault(
                s => string.Equals(s.Token, token, StringComparison.Ordinal));
            return record is not null && record.IsActive(now) ? record.Clone() : null;
        }
    }

    public bool Revoke(string token)
    {
        if (!ShareTokens.IsValid(token))
        {
            return false;
        }

        lock (_lock)
        {
            var record = _shares.FirstOrDefault(
                s => string.Equals(s.Token, token, StringComparison.Ordinal));
            if (record is null)
            {
                return false;
            }

            if (!record.Revoked)
            {
                record.Revoked = true;
                record.RevokedAt = DateTimeOffset.UtcNow;
                SaveLocked();
                _logger.LogInformation("已撤销文件分享：{File}", record.FileName);
            }

            _unlockAttempts.TryRemove(token, out _);
            return true;
        }
    }

    /// <summary>下载计数：仅在带 ?dl=1 且不是 Range 续传时调用；返回是否仍允许下载。</summary>
    public bool TryConsumeDownload(string token)
    {
        lock (_lock)
        {
            var record = _shares.FirstOrDefault(
                s => string.Equals(s.Token, token, StringComparison.Ordinal));
            if (record is null || !record.IsActive(DateTimeOffset.UtcNow))
            {
                return false;
            }

            if (record.MaxDownloads is not int max)
            {
                return true;
            }

            if (record.DownloadCount >= max)
            {
                return false;
            }

            record.DownloadCount++;
            SaveLocked();
            return true;
        }
    }

    // ── 密码解锁 ───────────────────────────────────────────────────────────

    public UnlockStatus TryUnlock(ShareRecord share, string? password, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!share.HasPassword)
        {
            return UnlockStatus.Ok;
        }

        var now = DateTimeOffset.UtcNow;
        if (_unlockAttempts.TryGetValue(share.Token, out var attempt) && attempt.LockedUntil > now)
        {
            retryAfterSeconds = Math.Max(1, (int)(attempt.LockedUntil - now).TotalSeconds);
            return UnlockStatus.Locked;
        }

        var ok = password is not null &&
                 SharePasswords.Verify(password, share.PasswordHash);
        if (ok)
        {
            _unlockAttempts.TryRemove(share.Token, out _);
            return UnlockStatus.Ok;
        }

        var state = _unlockAttempts.GetOrAdd(share.Token, _ => new UnlockAttempt());
        lock (state)
        {
            state.Failures++;
            if (state.Failures >= UnlockFailuresBeforeLock)
            {
                state.LockedUntil = DateTimeOffset.UtcNow.Add(UnlockLockDuration);
                state.Failures = 0;
            }
        }

        _logger.LogWarning("分享密码校验失败：{File}", share.FileName);
        return UnlockStatus.BadPassword;
    }

    public bool HasValidUnlockCookie(ShareRecord share, string? cookieValue)
    {
        if (!share.HasPassword)
        {
            return true;
        }

        if (string.IsNullOrEmpty(cookieValue))
        {
            return false;
        }

        var parts = cookieValue.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], share.Token, StringComparison.Ordinal))
        {
            return false;
        }

        if (!long.TryParse(parts[1], out var expiresUnix) ||
            expiresUnix < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return false;
        }

        var expected = SignCookie(share.Token, parts[1]);
        var actual = Base64UrlDecode(parts[2]);
        return actual is not null && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public string BuildUnlockCookie(ShareRecord share, bool secure)
    {
        var now = DateTimeOffset.UtcNow;
        var remaining = (long)Math.Floor((share.ExpiresAt - now).TotalSeconds);
        var lifetime = Math.Min(Options.CookieLifetimeMinutes * 60L, Math.Max(1, remaining));
        if (lifetime < 1)
        {
            lifetime = 1;
        }

        var expiresUnix = (now.ToUnixTimeSeconds() + lifetime).ToString();
        var signature = Base64UrlEncode(SignCookie(share.Token, expiresUnix));
        var value = $"{share.Token}.{expiresUnix}.{signature}";

        var builder = new StringBuilder();
        builder.Append(CookieName).Append('=').Append(value)
            .Append("; Path=/s/").Append(share.Token)
            .Append("; HttpOnly; SameSite=Lax; Max-Age=").Append(lifetime);
        if (secure)
        {
            builder.Append("; Secure");
        }

        return builder.ToString();
    }

    private byte[] SignCookie(string token, string expiresUnix) =>
        HMACSHA256.HashData(CookieKey(), Encoding.UTF8.GetBytes($"{token}.{expiresUnix}"));

    private byte[] CookieKey()
    {
        var key = _cookieKey;
        if (key is not null)
        {
            return key;
        }

        lock (_lock)
        {
            return _cookieKey ??= HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                Encoding.UTF8.GetBytes(_identity.DeviceSecret),
                outputLength: 32,
                salt: null,
                info: Encoding.UTF8.GetBytes("loal-nas/share-cookie/v1"));
        }
    }

    // ── URL ────────────────────────────────────────────────────────────────

    public string BuildUrl(string token, HttpRequest? request)
    {
        var baseUrl = PublicBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = request is null
                ? ""
                : $"{request.Scheme}://{request.Host}";
        }

        return baseUrl.TrimEnd('/') + "/s/" + token;
    }

    public bool IsSecurePublicBaseUrl =>
        PublicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // ── 持久化 ─────────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(SharePaths.StoreFile))
            {
                return;
            }

            var json = File.ReadAllText(SharePaths.StoreFile);
            var document = JsonSerializer.Deserialize<ShareDocument>(json, JsonOpts);
            if (document?.Shares is { Count: > 0 } list)
            {
                _shares.AddRange(list.Where(s =>
                    ShareTokens.IsValid(s.Token) &&
                    !string.IsNullOrWhiteSpace(s.ResourcePath) &&
                    s.ExpiresAt > DateTimeOffset.MinValue));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取分享记录失败，将从空列表开始。");
        }
    }

    private void SaveLocked()
    {
        try
        {
            SharePaths.EnsureDirectory();
            var document = new ShareDocument { Shares = _shares };
            var json = JsonSerializer.Serialize(document, JsonOpts);
            var tmp = SharePaths.StoreFile + ".tmp";
            File.WriteAllText(tmp, json);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(tmp, SharePaths.StoreFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入分享记录失败。");
        }
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-24);
        lock (_lock)
        {
            var removed = _shares.RemoveAll(s =>
                (s.Revoked && (s.RevokedAt ?? s.CreatedAt) < cutoff) ||
                (s.IsExpired(now) && s.ExpiresAt < cutoff));
            if (removed > 0)
            {
                SaveLocked();
            }
        }

        foreach (var pair in _unlockAttempts)
        {
            if (pair.Value.LockedUntil < now.AddMinutes(-10))
            {
                _unlockAttempts.TryRemove(pair.Key, out _);
            }
        }
    }

    // ── 后台清理 ───────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => CleanupLoopAsync(_cleanupCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupCts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, Options.CleanupIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                Cleanup();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "分享记录清理失败。");
        }
    }

    public void Dispose()
    {
        _cleanupCts?.Cancel();
        _cleanupCts?.Dispose();
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[]? Base64UrlDecode(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        switch (text.Length % 4)
        {
            case 2:
                text += "==";
                break;
            case 3:
                text += "=";
                break;
        }

        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
