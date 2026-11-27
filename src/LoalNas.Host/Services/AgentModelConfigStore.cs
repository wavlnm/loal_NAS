using System.Text.Json;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

public sealed class AgentModelConfig
{
    public int Version { get; set; } = 1;
    public string ProviderId { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 手机端配置的模型信息只落盘在服务端（0600）；同时生成 OpenCode 的 opencode.json
/// 与插件文件，Key 只通过子进程环境变量传入。
/// </summary>
public sealed class AgentModelConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<AgentModelConfigStore> _logger;
    private readonly IOptions<AgentOptions> _options;
    private readonly object _lock = new();
    private AgentModelConfig? _config;

    public AgentModelConfigStore(
        ILogger<AgentModelConfigStore> logger, IOptions<AgentOptions> options)
    {
        _logger = logger;
        _options = options;
        AgentPaths.EnsureDirectories();
        Load();
        if (IsConfigured)
        {
            TryWriteRuntimeFiles();
        }
    }

    private AgentOptions Options => _options.Value;

    public bool IsConfigured =>
        _config is not null &&
        !string.IsNullOrWhiteSpace(_config.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.Model) &&
        !string.IsNullOrWhiteSpace(_config.ApiKey);

    public AgentModelConfig? Snapshot()
    {
        lock (_lock)
        {
            return _config is null ? null : Clone(_config);
        }
    }

    public AgentModelConfigView View()
    {
        lock (_lock)
        {
            var config = _config;
            return new AgentModelConfigView(
                config?.ProviderId ?? Options.DefaultProvider,
                config?.BaseUrl ?? Preset(Options.DefaultProvider)?.BaseUrl ?? "",
                config?.Model ?? Options.DefaultModel,
                config?.ApiKey is { Length: > 0 },
                Mask(config?.ApiKey),
                Options.Providers.Select(p => new AgentProviderView(
                    p.Id, p.Display, p.BaseUrl, p.Models)).ToArray());
        }
    }

    public bool Save(
        string? providerId, string? baseUrl, string? model, string? apiKey,
        bool clearKey, out string? error)
    {
        error = null;
        var preset = Preset(providerId ?? "");
        var resolvedProvider = string.IsNullOrWhiteSpace(providerId)
            ? Options.DefaultProvider
            : providerId.Trim();
        var resolvedBase = string.IsNullOrWhiteSpace(baseUrl)
            ? preset?.BaseUrl ?? ""
            : baseUrl.Trim();
        var resolvedModel = string.IsNullOrWhiteSpace(model)
            ? preset?.Models.FirstOrDefault() ?? Options.DefaultModel
            : model.Trim();

        if (string.IsNullOrWhiteSpace(resolvedBase) ||
            !Uri.TryCreate(resolvedBase, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            error = "invalid_base_url";
            return false;
        }

        if (string.IsNullOrWhiteSpace(resolvedModel) || resolvedModel.Length > 200)
        {
            error = "invalid_model";
            return false;
        }

        lock (_lock)
        {
            var nextKey = clearKey ? null : _config?.ApiKey;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                nextKey = apiKey.Trim();
            }

            _config = new AgentModelConfig
            {
                ProviderId = resolvedProvider,
                BaseUrl = resolvedBase,
                Model = resolvedModel,
                ApiKey = nextKey,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            try
            {
                SaveLocked();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存模型配置失败。");
                error = "save_failed";
                return false;
            }
        }

        WriteRuntimeFiles();
        return true;
    }

    /// <summary>重启 OpenCode 前调用：确保 opencode.json 与插件存在。</summary>
    public bool WriteRuntimeFiles()
    {
        try
        {
            TryWriteRuntimeFiles();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "生成 OpenCode 运行配置失败。");
            return false;
        }
    }

    private void TryWriteRuntimeFiles()
    {
        var config = Snapshot();
        if (config is null || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return;
        }

        AgentPaths.EnsureDirectories();
        var workspace = Path.GetFullPath(Options.Workspace);
        var sharedRoot = Path.GetFullPath(Options.SharedRoot);
        var loalDataRoot = Path.GetDirectoryName(AgentPaths.Directory) ?? AgentPaths.Directory;
        Directory.CreateDirectory(workspace);

        var document = new Dictionary<string, object?>
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["model"] = $"nas/{config.Model}",
            ["providers"] = new Dictionary<string, object?>
            {
                ["nas"] = new Dictionary<string, object?>
                {
                    ["name"] = "千私云模型",
                    ["env"] = new[] { "NAS_AGENT_API_KEY" },
                    ["package"] = "@opencode/ai/providers/openai-compatible",
                    ["settings"] = new Dictionary<string, object?>
                    {
                        ["baseURL"] = config.BaseUrl,
                    },
                    ["models"] = new Dictionary<string, object?>
                    {
                        [config.Model] = new Dictionary<string, object?>(),
                    },
                },
            },
            ["default_agent"] = "build",
            ["permissions"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["action"] = "edit",
                    ["resource"] = "*",
                    ["effect"] = "allow",
                },
                new Dictionary<string, object?>
                {
                    ["action"] = "edit",
                    ["resource"] = "/*",
                    ["effect"] = "deny",
                },
                new Dictionary<string, object?>
                {
                    ["action"] = "edit",
                    ["resource"] = workspace + "/*",
                    ["effect"] = "allow",
                },
                new Dictionary<string, object?>
                {
                    ["action"] = "read",
                    ["resource"] = loalDataRoot + "/*",
                    ["effect"] = "deny",
                },
                new Dictionary<string, object?>
                {
                    ["action"] = "external_directory",
                    ["resource"] = sharedRoot + "/*",
                    ["effect"] = "allow",
                },
                new Dictionary<string, object?>
                {
                    ["action"] = "shell",
                    ["resource"] = "*",
                    ["effect"] = "allow",
                },
            },
        };

        var json = JsonSerializer.Serialize(document, JsonOpts);
        WritePrivateFile(AgentPaths.OpenCodeConfigFile, json);

        var pluginSource = Path.Combine(
            AppContext.BaseDirectory, "tools", "opencode", "plugins", "loalnas-approval.ts");
        if (File.Exists(pluginSource))
        {
            var pluginTarget = Path.Combine(
                AgentPaths.PluginDirectory, "loalnas-approval.ts");
            File.Copy(pluginSource, pluginTarget, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    pluginTarget, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        else
        {
            _logger.LogWarning("未找到 OpenCode 审批插件：{Path}", pluginSource);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AgentPaths.ConfigFile))
            {
                return;
            }

            var json = File.ReadAllText(AgentPaths.ConfigFile);
            _config = JsonSerializer.Deserialize<AgentModelConfig>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取模型配置失败，将使用默认值。");
        }
    }

    private void SaveLocked()
    {
        AgentPaths.EnsureDirectories();
        var json = JsonSerializer.Serialize(_config, JsonOpts);
        WritePrivateFile(AgentPaths.ConfigFile, json);
    }

    private static void WritePrivateFile(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, path, overwrite: true);
    }

    private AgentProviderPreset? Preset(string id) =>
        Options.Providers.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private static string? Mask(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return key.Length <= 8
            ? "已保存"
            : $"{key[..4]}…{key[^4..]}";
    }

    private static AgentModelConfig Clone(AgentModelConfig source) => new()
    {
        Version = source.Version,
        ProviderId = source.ProviderId,
        BaseUrl = source.BaseUrl,
        Model = source.Model,
        ApiKey = source.ApiKey,
        UpdatedAt = source.UpdatedAt,
    };

    public sealed record AgentModelConfigView(
        string ProviderId,
        string BaseUrl,
        string Model,
        bool HasKey,
        string? KeyMasked,
        AgentProviderView[] Presets);

    public sealed record AgentProviderView(
        string Id, string Display, string BaseUrl, List<string> Models);
}
