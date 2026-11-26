namespace LoalNas.Host.Configuration;

/// <summary>
/// 手机端远程终端指令的默认开关配置。服务启动时按此配置初始化状态；
/// /admin 面板的临时开启/关闭只影响当前进程，重启后又回到这里的默认值。
/// </summary>
public sealed class RemoteTerminalOptions
{
    public const string SectionName = "LoalNas:RemoteTerminal";

    /// <summary>启动后是否直接允许执行终端指令。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>启动时开启的有效期（分钟）；0 表示永久有效。</summary>
    public int DurationMinutes { get; set; }
}
