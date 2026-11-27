using System.Text.Json;

namespace LoalNas.Host.Services;

/// <summary>审批策略的持久化开关：完全自主模式开启后所有命令自动放行。</summary>
public sealed class AgentPolicyStore
{
    private sealed class Document
    {
        public bool Autonomous { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<AgentPolicyStore> _logger;
    private readonly object _lock = new();
    private bool _autonomous;

    public AgentPolicyStore(ILogger<AgentPolicyStore> logger)
    {
        _logger = logger;
        AgentPaths.EnsureDirectories();
        Load();
    }

    public bool Autonomous
    {
        get
        {
            lock (_lock)
            {
                return _autonomous;
            }
        }
    }

    public void Set(bool autonomous)
    {
        lock (_lock)
        {
            if (_autonomous == autonomous)
            {
                return;
            }

            _autonomous = autonomous;
            try
            {
                var json = JsonSerializer.Serialize(
                    new Document { Autonomous = autonomous }, JsonOpts);
                var tmp = AgentPaths.ModeFile + ".tmp";
                File.WriteAllText(tmp, json);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                File.Move(tmp, AgentPaths.ModeFile, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存审批模式失败。");
            }

            _logger.LogWarning("AI 审批模式已切换：{Mode}",
                autonomous ? "完全自主（不拦截）" : "敏感命令确认");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AgentPaths.ModeFile))
            {
                return;
            }

            var document = JsonSerializer.Deserialize<Document>(
                File.ReadAllText(AgentPaths.ModeFile), JsonOpts);
            _autonomous = document?.Autonomous == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取审批模式失败，默认启用确认。");
        }
    }
}
