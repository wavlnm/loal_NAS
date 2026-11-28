namespace LoalNas.Host.Configuration;

/// <summary>
/// 通用应用/游戏托管配置。宿主只负责启动进程、分配端口、代理请求和记录房间令牌，
/// 不关心应用内部规则（例如棋局是否结束）。
/// </summary>
public sealed class AppsOptions
{
    public const string SectionName = "LoalNas:Apps";

    public bool Enabled { get; set; } = true;

    /// <summary>应用清单目录，相对发布目录（每个应用一个子目录，内含 app.json）。</summary>
    public string Directory { get; set; } = "apps";

    /// <summary>应用数据/日志根目录；留空则使用 ~/.local/share/loal-nas/apps。</summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>建房时未指定有效期时的默认值。</summary>
    public int DefaultRoomLifetimeHours { get; set; } = 6;

    public int MaxRoomLifetimeHours { get; set; } = 24;

    public int MaxRoomsPerApp { get; set; } = 8;

    /// <summary>关闭最后一个房间时，等待应用优雅退出的时间。</summary>
    public int StopGraceSeconds { get; set; } = 4;

    public string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
        {
            return Path.GetFullPath(DataDirectory);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "loal-nas",
            "apps");
    }
}
