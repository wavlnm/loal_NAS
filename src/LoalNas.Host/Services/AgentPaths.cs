namespace LoalNas.Host.Services;

/// <summary>Agent 的本地数据目录（模型配置、OpenCode HOME、日志、审计）。</summary>
public static class AgentPaths
{
    public static string Directory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "loal-nas", "agent");

    public static string ConfigFile => System.IO.Path.Combine(Directory, "model-config.json");
    public static string HomeDirectory => System.IO.Path.Combine(Directory, "home");
    public static string ConfigDirectory => System.IO.Path.Combine(HomeDirectory, ".config", "opencode");
    public static string PluginDirectory => System.IO.Path.Combine(ConfigDirectory, "plugins");
    public static string OpenCodeConfigFile => System.IO.Path.Combine(ConfigDirectory, "opencode.json");
    public static string LogsDirectory => System.IO.Path.Combine(Directory, "logs");
    public static string AuditFile => System.IO.Path.Combine(Directory, "audit.log");
    public static string ModeFile => System.IO.Path.Combine(Directory, "approval-mode.json");

    public static void EnsureDirectories()
    {
        System.IO.Directory.CreateDirectory(Directory);
        System.IO.Directory.CreateDirectory(HomeDirectory);
        System.IO.Directory.CreateDirectory(ConfigDirectory);
        System.IO.Directory.CreateDirectory(PluginDirectory);
        System.IO.Directory.CreateDirectory(LogsDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(HomeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
