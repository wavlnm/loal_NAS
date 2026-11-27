namespace LoalNas.Host.Configuration;

/// <summary>任务式 AI 助手（OpenCode 嵌入）配置。</summary>
public sealed class AgentOptions
{
    public const string SectionName = "LoalNas:Agent";

    public bool Enabled { get; set; } = true;

    /// <summary>相对发布目录或绝对路径。</summary>
    public string BinaryPath { get; set; } = "tools/opencode/bin/opencode";

    /// <summary>OpenCode 独立 HOME/配置/数据目录；留空为 ~/.local/share/loal-nas/agent。</summary>
    public string HomeDirectory { get; set; } = "";

    /// <summary>专用工作目录（共享根下的 AI工作区）。</summary>
    public string Workspace { get; set; } = "/mnt/HDD-Data/loal-nas/file/AI工作区";

    /// <summary>共享根，只读开放给 Agent。</summary>
    public string SharedRoot { get; set; } = "/mnt/HDD-Data/loal-nas/file";

    public int ServePort { get; set; } = 4097;

    public int ServeStartupTimeoutSeconds { get; set; } = 60;

    public int ApprovalTimeoutSeconds { get; set; } = 120;

    public int MaxConcurrentRuns { get; set; } = 1;

    public int MaxPromptChars { get; set; } = 8000;

    public int SessionListLimit { get; set; } = 30;

    public int MessageListLimit { get; set; } = 200;

    public string DefaultProvider { get; set; } = "deepseek";

    public string DefaultModel { get; set; } = "deepseek-flash";

    public List<AgentProviderPreset> Providers { get; set; } = new();

    public List<string> SensitivePatterns { get; set; } = new();
}

public sealed class AgentProviderPreset
{
    public string Id { get; set; } = "";
    public string Display { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public List<string> Models { get; set; } = new();
}
