namespace LoalNas.Host.Services;

/// <summary>监控功能的本地状态文件位置（与设备密钥同目录，重启后仍然有效）。</summary>
public static class MonitorPaths
{
    public static string Directory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "loal-nas", "monitor");

    /// <summary>worker 写出的运行状态与今日事件。</summary>
    public static string StateFile => System.IO.Path.Combine(Directory, "state.json");

    /// <summary>服务端写给 worker 的指令（侦测开关、灵敏度、手动录像）。</summary>
    public static string CommandFile => System.IO.Path.Combine(Directory, "command.json");

    /// <summary>持久化的用户设置，服务重启后恢复侦测状态。</summary>
    public static string SettingsFile => System.IO.Path.Combine(Directory, "settings.json");

    public static void EnsureDirectory() => System.IO.Directory.CreateDirectory(Directory);
}
