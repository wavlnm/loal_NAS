using System.Text.Json;

namespace LoalNas.Host.Services;

/// <summary>回收站条目（持久化到 index.json 的元数据）。</summary>
public sealed class TrashEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string OriginalPath { get; set; } = ""; // 以 / 开头的共享根相对路径
    public DateTimeOffset DeletedAt { get; set; }
    public bool IsDir { get; set; }
    public long? Size { get; set; }
}

/// <summary>
/// 回收站：把删除操作变为移动到共享目录之外的同盘 .trash 目录（同一文件系统，rename 即可，快且不占额外空间）。
/// 元数据存在 .trash/index.json，支持列出、恢复、彻底删除、清空。
/// 设计为独立服务 + HTTP 接口，后续要演进成多租户 SaaS 能力时可平滑扩展。
/// </summary>
public sealed class TrashService
{
    public enum TrashStatus { Ok, NotFound, Invalid, Conflict, Error }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<TrashService> _logger;
    private readonly object _lock = new();
    private readonly string _shareRoot;
    private readonly string _trashRoot;
    private readonly string _manifestPath;
    private readonly List<TrashEntry> _entries = new();

    public TrashService(FileBrowserProcessManager manager, ILogger<TrashService> logger)
    {
        _logger = logger;
        _shareRoot = Path.GetFullPath(manager.SharedRootPath);
        var parent = Path.GetDirectoryName(_shareRoot) ?? _shareRoot;
        _trashRoot = Path.Combine(parent, ".trash");
        _manifestPath = Path.Combine(_trashRoot, "index.json");
        Directory.CreateDirectory(_trashRoot);
        Load();
        DropMissingEntries();
    }

    public string TrashRoot => _trashRoot;

    public IReadOnlyList<TrashEntry> List()
    {
        lock (_lock)
        {
            return _entries.OrderByDescending(e => e.DeletedAt).ToList();
        }
    }

    public TrashStatus MoveToTrash(string resourcePath, out TrashEntry? entry)
    {
        entry = null;
        if (!TryResolveWithinShare(resourcePath, out var full))
        {
            return TrashStatus.Invalid;
        }

        var isDir = Directory.Exists(full);
        if (!isDir && !File.Exists(full))
        {
            return TrashStatus.NotFound;
        }

        lock (_lock)
        {
            var id = Guid.NewGuid().ToString("N");
            var stored = Path.Combine(_trashRoot, id);
            try
            {
                if (isDir)
                {
                    Directory.Move(full, stored);
                }
                else
                {
                    File.Move(full, stored);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "移到回收站失败：{Path}", full);
                return TrashStatus.Error;
            }

            var item = new TrashEntry
            {
                Id = id,
                Name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)),
                OriginalPath = NormalizeResourcePath(resourcePath),
                DeletedAt = DateTimeOffset.Now,
                IsDir = isDir,
                Size = isDir ? null : TryGetLength(stored),
            };
            _entries.Add(item);
            Save();
            entry = item;
            _logger.LogInformation("已移入回收站：{Path} -> {Id}", item.OriginalPath, id);
            return TrashStatus.Ok;
        }
    }

    public TrashStatus Restore(string id, out string? error)
    {
        error = null;
        lock (_lock)
        {
            var item = _entries.FirstOrDefault(e => e.Id == id);
            if (item is null)
            {
                error = "回收站条目不存在";
                return TrashStatus.NotFound;
            }

            var stored = Path.Combine(_trashRoot, id);
            if (!Directory.Exists(stored) && !File.Exists(stored))
            {
                _entries.Remove(item);
                Save();
                error = "回收站文件已丢失";
                return TrashStatus.NotFound;
            }

            if (!TryResolveWithinShare(item.OriginalPath, out var target))
            {
                error = "原始路径非法";
                return TrashStatus.Invalid;
            }

            if (File.Exists(target) || Directory.Exists(target))
            {
                error = "原位置已存在同名文件或目录";
                return TrashStatus.Conflict;
            }

            try
            {
                var parent = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(parent);
                if (item.IsDir)
                {
                    Directory.Move(stored, target);
                }
                else
                {
                    File.Move(stored, target);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logger.LogWarning(ex, "恢复回收站条目失败：{Id}", id);
                return TrashStatus.Error;
            }

            _entries.Remove(item);
            Save();
            _logger.LogInformation("已从回收站恢复：{Path}", item.OriginalPath);
            return TrashStatus.Ok;
        }
    }

    public TrashStatus Purge(string id)
    {
        lock (_lock)
        {
            var item = _entries.FirstOrDefault(e => e.Id == id);
            if (item is null)
            {
                return TrashStatus.NotFound;
            }

            var stored = Path.Combine(_trashRoot, id);
            try
            {
                if (Directory.Exists(stored))
                {
                    Directory.Delete(stored, recursive: true);
                }
                else if (File.Exists(stored))
                {
                    File.Delete(stored);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "彻底删除回收站条目失败：{Id}", id);
                return TrashStatus.Error;
            }

            _entries.Remove(item);
            Save();
            return TrashStatus.Ok;
        }
    }

    public int Empty()
    {
        int removed = 0;
        foreach (var id in List().Select(e => e.Id).ToList())
        {
            if (Purge(id) == TrashStatus.Ok)
            {
                removed++;
            }
        }
        return removed;
    }

    /// <summary>永久删除删除时间早于 threshold 的条目，返回删除数量。</summary>
    public int PurgeOlderThan(DateTimeOffset threshold)
    {
        int removed = 0;
        foreach (var id in List()
                     .Where(e => e.DeletedAt < threshold)
                     .Select(e => e.Id)
                     .ToList())
        {
            if (Purge(id) == TrashStatus.Ok)
            {
                removed++;
            }
        }
        return removed;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_manifestPath))
            {
                var json = File.ReadAllText(_manifestPath);
                var items = JsonSerializer.Deserialize<List<TrashEntry>>(json, JsonOpts);
                if (items is not null)
                {
                    _entries.AddRange(items);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取回收站索引失败，将从空索引开始");
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries, JsonOpts);
            var tmp = _manifestPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _manifestPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入回收站索引失败");
        }
    }

    private void DropMissingEntries()
    {
        lock (_lock)
        {
            var removed = _entries.RemoveAll(e =>
                !Directory.Exists(Path.Combine(_trashRoot, e.Id)) &&
                !File.Exists(Path.Combine(_trashRoot, e.Id)));
            if (removed > 0)
            {
                Save();
            }
        }
    }

    private bool TryResolveWithinShare(string resourcePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            return false;
        }

        var relative = resourcePath.TrimStart('/');
        if (relative.Length == 0)
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(_shareRoot, relative));
        }
        catch
        {
            return false;
        }

        var rootWithSep = _shareRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _shareRoot
            : _shareRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static string NormalizeResourcePath(string resourcePath)
    {
        var p = resourcePath.Replace('\\', '/');
        if (!p.StartsWith('/'))
        {
            p = "/" + p;
        }
        return p;
    }

    private static long? TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return null;
        }
    }
}
