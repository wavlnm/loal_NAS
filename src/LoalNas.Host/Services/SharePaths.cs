namespace LoalNas.Host.Services;

/// <summary>分享功能的本地持久化路径（与设备密钥同目录，重启后仍然有效）。</summary>
public static class SharePaths
{
    public static string Directory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "loal-nas");

    public static string StoreFile => System.IO.Path.Combine(Directory, "shares.json");

    public static void EnsureDirectory() => System.IO.Directory.CreateDirectory(Directory);
}
