using System.Text.Json;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// 扫描 apps/*/app.json，编译公开路由。平台只读取清单，不加载应用代码。
/// </summary>
public sealed class AppRegistry
{
    public const string ManifestFileName = "app.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly AppsOptions _options;
    private readonly ILogger<AppRegistry> _logger;
    private readonly Dictionary<string, AppManifest> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AppManifest> _ordered = new();
    private readonly List<AppManifest> _byPrefixLength = new();

    public AppRegistry(IOptions<AppsOptions> options, ILogger<AppRegistry> logger)
    {
        _options = options.Value;
        _logger = logger;
        Load();
    }

    public string RootDirectory => ResolveRootDirectory();

    public IReadOnlyList<AppManifest> All => _ordered;

    public AppManifest? Find(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        return _apps.TryGetValue(appId.Trim(), out var manifest) ? manifest : null;
    }

    /// <summary>把 /apps/{id}/... 拆成清单与相对路径（以 / 开头，根路径为空串）。</summary>
    public bool TryResolvePath(string path, out AppManifest manifest, out string restPath)
    {
        manifest = null!;
        restPath = string.Empty;

        foreach (var candidate in _byPrefixLength)
        {
            var prefix = candidate.PublicPrefix;
            if (path.Length < prefix.Length)
            {
                continue;
            }

            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (path.Length > prefix.Length && path[prefix.Length] != '/')
            {
                continue;
            }

            manifest = candidate;
            restPath = path.Length == prefix.Length ? string.Empty : path[prefix.Length..];
            return true;
        }

        return false;
    }

    /// <summary>该请求是否命中应用的公开路由（不要求手机端 HMAC）。</summary>
    public bool IsPublicPath(string path)
    {
        if (!TryResolvePath(path, out var manifest, out var restPath) || !manifest.Enabled)
        {
            return false;
        }

        return manifest.Match(restPath) is not null;
    }

    private void Load()
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("应用托管已禁用。");
            return;
        }

        var root = ResolveRootDirectory();
        if (!Directory.Exists(root))
        {
            _logger.LogInformation("应用目录不存在：{Directory}", root);
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<AppManifest>(json, JsonOptions);
                if (manifest is null)
                {
                    _logger.LogWarning("应用清单为空：{Path}", manifestPath);
                    continue;
                }

                Normalize(manifest, directory);

                if (_apps.ContainsKey(manifest.Id))
                {
                    _logger.LogWarning("应用 ID 重复，已跳过：{Id}（{Path}）", manifest.Id, manifestPath);
                    continue;
                }

                _apps[manifest.Id] = manifest;
                _ordered.Add(manifest);
                _logger.LogInformation(
                    "已加载应用：{Id}（{Name}），前缀 {Prefix}，房间上限 {MaxRooms}",
                    manifest.Id, manifest.Name, manifest.PublicPrefix, manifest.MaxRooms);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取应用清单失败：{Path}", manifestPath);
            }
        }

        _byPrefixLength.AddRange(_ordered.OrderByDescending(a => a.PublicPrefix.Length));
    }

    private void Normalize(AppManifest manifest, string directory)
    {
        manifest.Id = string.IsNullOrWhiteSpace(manifest.Id)
            ? Path.GetFileName(directory).Trim()
            : manifest.Id.Trim();

        if (!IsValidId(manifest.Id))
        {
            throw new InvalidOperationException($"应用 ID 不合法：{manifest.Id}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Command))
        {
            throw new InvalidOperationException("app.json 缺少 command。");
        }

        manifest.Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name.Trim();
        manifest.AppDirectory = directory;

        var prefix = string.IsNullOrWhiteSpace(manifest.PublicPrefix)
            ? $"/apps/{manifest.Id}"
            : manifest.PublicPrefix.Trim().TrimEnd('/');
        if (!prefix.StartsWith("/apps/", StringComparison.OrdinalIgnoreCase) ||
            prefix.Length <= "/apps/".Length)
        {
            throw new InvalidOperationException($"publicPrefix 必须以 /apps/ 开头：{prefix}");
        }

        manifest.PublicPrefix = prefix;

        if (manifest.StartupTimeoutSeconds <= 0)
        {
            manifest.StartupTimeoutSeconds = 20;
        }

        if (manifest.MaxRooms <= 0)
        {
            manifest.MaxRooms = Math.Max(1, _options.MaxRoomsPerApp);
        }

        if (manifest.DefaultRoomLifetimeHours <= 0)
        {
            manifest.DefaultRoomLifetimeHours = Math.Max(1, _options.DefaultRoomLifetimeHours);
        }

        if (manifest.MaxRoomLifetimeHours <= 0)
        {
            manifest.MaxRoomLifetimeHours = Math.Max(
                manifest.DefaultRoomLifetimeHours, _options.MaxRoomLifetimeHours);
        }

        var dataName = string.IsNullOrWhiteSpace(manifest.DataDirectoryName)
            ? manifest.Id
            : manifest.DataDirectoryName.Trim();
        manifest.DataDirectory = Path.Combine(_options.ResolveDataDirectory(), dataName);

        if (string.IsNullOrWhiteSpace(manifest.HealthPath))
        {
            manifest.HealthPath = "/health";
        }
        else if (!manifest.HealthPath.StartsWith('/'))
        {
            manifest.HealthPath = "/" + manifest.HealthPath;
        }

        foreach (var template in manifest.PublicRoutes)
        {
            if (string.IsNullOrEmpty(template))
            {
                manifest.Routes.Add(AppRoute.Compile(string.Empty));
                continue;
            }

            var normalized = template.StartsWith('/') ? template : "/" + template;
            manifest.Routes.Add(AppRoute.Compile(normalized));
        }
    }

    private string ResolveRootDirectory()
    {
        var configured = _options.Directory;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "apps")
            : Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    }

    private static bool IsValidId(string id) =>
        id.Length is > 0 and <= 32 &&
        id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') &&
        id[0] is >= 'a' and <= 'z' or >= '0' and <= '9';
}
