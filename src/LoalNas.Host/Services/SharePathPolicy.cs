namespace LoalNas.Host.Services;

/// <summary>
/// 分享文件的路径校验：只允许共享根内的普通文件；逐段解析符号链接，防止
/// 通过链接逃逸到根目录之外。资源路径与 FileBrowser 一致，使用以 / 开头的相对路径。
/// </summary>
public static class SharePathPolicy
{
    public enum ResolveError
    {
        None,
        InvalidPath,
        DirectoryNotAllowed,
        FileNotFound,
    }

    public static string? Normalize(string? resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath) || resourcePath.Contains('\0'))
        {
            return null;
        }

        var path = resourcePath.Replace('\\', '/').Trim();
        if (path.Length == 0)
        {
            return null;
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return path;
    }

    public static bool TryResolveFile(
        string root, string? resourcePath, out string fullPath, out ResolveError error)
    {
        fullPath = "";
        error = ResolveError.None;

        var normalized = Normalize(resourcePath);
        if (normalized is null || normalized.Trim('/').Length == 0)
        {
            error = ResolveError.InvalidPath;
            return false;
        }

        var relative = normalized.TrimStart('/');
        string rootFull;
        string candidate;
        try
        {
            rootFull = Path.GetFullPath(root);
            candidate = Path.GetFullPath(Path.Combine(rootFull, relative));
        }
        catch
        {
            error = ResolveError.InvalidPath;
            return false;
        }

        if (!IsUnder(rootFull, candidate))
        {
            error = ResolveError.InvalidPath;
            return false;
        }

        string realRoot;
        try
        {
            realRoot = ResolveFinalTarget(rootFull) ?? rootFull;
        }
        catch
        {
            error = ResolveError.InvalidPath;
            return false;
        }

        // 逐段解析目录/文件的链接目标，任何一段指向根外都拒绝。
        // 从解析后的真实根开始拼接，这样共享根本身是符号链接时也不会误判。
        var current = realRoot;
        try
        {
            foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                var resolved = ResolveFinalTarget(current);
                if (resolved is not null)
                {
                    current = resolved;
                }

                if (!IsUnder(realRoot, current) &&
                    !string.Equals(
                        current.TrimEnd(Path.DirectorySeparatorChar),
                        realRoot.TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.Ordinal))
                {
                    error = ResolveError.InvalidPath;
                    return false;
                }
            }

            var attributes = File.GetAttributes(current);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                error = ResolveError.DirectoryNotAllowed;
                return false;
            }

            if (!File.Exists(current))
            {
                error = ResolveError.FileNotFound;
                return false;
            }
        }
        catch
        {
            error = ResolveError.FileNotFound;
            return false;
        }

        fullPath = current;
        return true;
    }

    private static string? ResolveFinalTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnder(string root, string candidate)
    {
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSep, StringComparison.Ordinal);
    }
}
