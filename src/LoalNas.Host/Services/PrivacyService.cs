using System.Text.Json;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// 共享根内 .private 隐私目录：密码哈希、失败锁定，以及给 AI 的路径判定。
/// 隐藏/密码属于手机端管理员窗口，本服务只负责密码与目录。
/// </summary>
public sealed class PrivacyService
{
    public enum SetupStatus { Ok, AlreadyConfigured, PasswordTooShort, PasswordMismatch, Invalid }
    public enum UnlockStatus { Ok, BadPassword, Locked, NotConfigured, Unavailable }

    private sealed class StoreDocument
    {
        public int Version { get; set; } = 1;
        public string PasswordHash { get; set; } = "";
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string StoreFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "loal-nas", "privacy.json");

    private readonly ILogger<PrivacyService> _logger;
    private readonly IOptions<PrivacyOptions> _options;
    private readonly FileBrowserProcessManager _manager;
    private readonly object _lock = new();
    private string? _passwordHash;
    private int _failedAttempts;
    private DateTimeOffset _lockedUntil;

    public PrivacyService(
        ILogger<PrivacyService> logger,
        IOptions<PrivacyOptions> options,
        FileBrowserProcessManager manager)
    {
        _logger = logger;
        _options = options;
        _manager = manager;
        Load();
        try
        {
            EnsureReady();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "初始化隐私目录失败。");
        }
    }

    private PrivacyOptions Options => _options.Value;

    public string PrivateDirectory => Path.GetFullPath(Path.Combine(
        _manager.SharedRootPath, Options.DirectoryName));

    public string PrivateResourcePath => "/" + Options.DirectoryName;

    public bool IsConfigured
    {
        get
        {
            lock (_lock)
            {
                return !string.IsNullOrEmpty(_passwordHash);
            }
        }
    }

    public void EnsureReady()
    {
        Directory.CreateDirectory(PrivateDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                PrivateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var attributes = File.GetAttributes(PrivateDirectory);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("隐私目录不能是符号链接。");
        }
    }

    public (bool Configured, int LockoutSecondsRemaining) Status()
    {
        lock (_lock)
        {
            var remaining = _lockedUntil > DateTimeOffset.UtcNow
                ? (int)Math.Ceiling((_lockedUntil - DateTimeOffset.UtcNow).TotalSeconds)
                : 0;
            return (!string.IsNullOrEmpty(_passwordHash), Math.Max(0, remaining));
        }
    }

    public SetupStatus Setup(string? password, string? confirmPassword)
    {
        if (password is null || confirmPassword is null)
        {
            return SetupStatus.Invalid;
        }

        lock (_lock)
        {
            if (!string.IsNullOrEmpty(_passwordHash))
            {
                return SetupStatus.AlreadyConfigured;
            }

            if (password.Length < Math.Max(1, Options.MinPasswordLength) ||
                password.Length > Math.Max(1, Options.MaxPasswordLength))
            {
                return SetupStatus.PasswordTooShort;
            }

            if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
            {
                return SetupStatus.PasswordMismatch;
            }

            try
            {
                EnsureReady();
                _passwordHash = SharePasswords.Hash(password);
                _failedAttempts = 0;
                _lockedUntil = default;
                SaveLocked();
                _logger.LogInformation("隐私目录密码已设置。");
                return SetupStatus.Ok;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "设置隐私目录密码失败。");
                return SetupStatus.Invalid;
            }
        }
    }

    public UnlockStatus Unlock(string? password, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_passwordHash))
            {
                return UnlockStatus.NotConfigured;
            }

            var now = DateTimeOffset.UtcNow;
            if (_lockedUntil > now)
            {
                retryAfterSeconds = Math.Max(1, (int)(_lockedUntil - now).TotalSeconds);
                return UnlockStatus.Locked;
            }

            if (password is not null && SharePasswords.Verify(password, _passwordHash))
            {
                _failedAttempts = 0;
                _lockedUntil = default;
                return UnlockStatus.Ok;
            }

            _failedAttempts++;
            if (_failedAttempts >= Math.Max(1, Options.MaxFailedAttempts))
            {
                _failedAttempts = 0;
                _lockedUntil = now.AddSeconds(Math.Max(1, Options.LockoutSeconds));
                retryAfterSeconds = Math.Max(1, Options.LockoutSeconds);
                return UnlockStatus.Locked;
            }

            return UnlockStatus.BadPassword;
        }
    }

    public bool IsPrivacyPath(string? resourcePath)
    {
        if (string.IsNullOrEmpty(resourcePath))
        {
            return false;
        }

        var path = resourcePath.Replace('\\', '/');
        return string.Equals(path, PrivateResourcePath, StringComparison.Ordinal) ||
            path.StartsWith(PrivateResourcePath + "/", StringComparison.Ordinal);
    }

    /// <summary>AI 的 shell 命令只要引用 .private 或隐私绝对路径，就由审批桥直接拒绝。</summary>
    public bool IsPrivacyCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return command.Contains(Options.DirectoryName, StringComparison.OrdinalIgnoreCase) ||
            command.Contains(PrivateDirectory, StringComparison.Ordinal);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StoreFile))
            {
                return;
            }

            var document = JsonSerializer.Deserialize<StoreDocument>(
                File.ReadAllText(StoreFile), JsonOpts);
            _passwordHash = document?.PasswordHash;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取隐私目录密码失败，将按未设置处理。");
        }
    }

    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(StoreFile)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new StoreDocument
        {
            PasswordHash = _passwordHash ?? "",
            UpdatedAt = DateTimeOffset.UtcNow,
        }, JsonOpts);
        var tmp = StoreFile + ".tmp";
        File.WriteAllText(tmp, json);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, StoreFile, overwrite: true);
    }
}
