namespace LoalNas.Host.Services;

/// <summary>按实际挂载点解析路径所在的磁盘。</summary>
public static class DiskSpaceProbe
{
    /// <summary>
    /// Linux 上 Path.GetPathRoot 对任意绝对路径都返回 "/"，无法反映真实挂载点。
    /// 这里用已挂载文件系统的挂载点做最长前缀匹配，找到路径实际所在的磁盘。
    /// </summary>
    public static DriveInfo? ResolveDrive(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }

        var mounts = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => d.Name)
            .OrderByDescending(n => n.Length);

        foreach (var mount in mounts)
        {
            var prefix = mount.EndsWith('/') ? mount : mount + "/";
            if (full == mount || full.StartsWith(prefix, StringComparison.Ordinal))
            {
                try
                {
                    return new DriveInfo(mount);
                }
                catch
                {
                    // 忽略不可访问的挂载点
                }
            }
        }

        return null;
    }
}
