using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// 在线压缩：把单个文件或文件夹（递归）打成同目录的 ZIP。任务在后台线程执行，
/// 手机端轮询进度；参照 AudioTranscodeService 的内存任务表 + 并发闸门，
/// 产物用同目录临时文件 + 原子改名，绝不覆盖已有文件。
/// </summary>
public sealed class ArchiveService : IHostedService, IDisposable
{
    public enum StartError
    {
        None,
        InvalidPath,
        InvalidLevel,
        SourceNotFound,
        RootNotAllowed,
        TooManyFiles,
        TooLarge,
        NothingToArchive,
        QueueFull,
        InsufficientDisk,
    }

    public sealed record JobInfo(
        string Key,
        string Status,
        double Progress,
        long TotalBytes,
        long ProcessedBytes,
        int FileCount,
        int SkippedCount,
        string SourcePath,
        string? OutputPath,
        string? OutputName,
        string? Error,
        bool Partial);

    public sealed record StartResult(
        StartError Error,
        string? Detail,
        JobInfo? Job,
        long AvailableBytes = 0,
        long RequiredBytes = 0);

    private sealed record SnapshotEntry(
        string FullPath, string EntryName, long Length, bool IsDirectory);

    private sealed class JobState
    {
        public required string Key;
        public required string SourcePath;
        public required string SourceFullPath;
        public required bool IsDirectory;
        public required CompressionLevel Level;
        public List<SnapshotEntry> Entries = new();
        public long TotalBytes;
        public long ProcessedBytes;
        public int FileCount;
        public int SkippedCount;
        public string Status = "queued";
        public double Progress;
        public bool Partial;
        public string? OutputPath;
        public string? OutputName;
        public string? Error;
        public required DateTimeOffset CreatedAt;
        public DateTimeOffset? FinishedAt;
        public required CancellationTokenSource Cts;
        public readonly object Sync = new();
        public long LastProgressTick;
    }

    private const int MaxFiles = 200_000;
    private const int ChunkBytes = 128 * 1024;

    private static readonly string[] Levels = ["fast", "normal", "smaller"];

    private readonly ILogger<ArchiveService> _logger;
    private readonly IOptions<ArchiveOptions> _options;
    private readonly FileBrowserProcessManager _manager;
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate;
    private CancellationTokenSource? _cleanupCts;
    private string _root = "";

    public ArchiveService(
        ILogger<ArchiveService> logger,
        IOptions<ArchiveOptions> options,
        FileBrowserProcessManager manager)
    {
        _logger = logger;
        _options = options;
        _manager = manager;
        _gate = new SemaphoreSlim(
            Math.Max(1, _options.Value.Concurrency), Math.Max(1, _options.Value.Concurrency));
        _root = Path.GetFullPath(manager.SharedRootPath);
        try
        {
            // 共享根本身是符号链接时，输出路径的相对计算要基于真实目录。
            _root = new DirectoryInfo(_root).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? _root;
        }
        catch
        {
            // 保留原始根路径
        }
    }

    private ArchiveOptions Options => _options.Value;

    // ── 对外接口 ───────────────────────────────────────────────────────────

    public StartResult Start(string? resourcePath, string? level)
    {
        var normalized = SharePathPolicy.Normalize(resourcePath);
        if (normalized is null)
        {
            return new StartResult(StartError.InvalidPath, null, null);
        }

        if (normalized.Trim('/').Length == 0)
        {
            return new StartResult(StartError.RootNotAllowed, "不能压缩共享根目录。", null);
        }

        var levelName = string.IsNullOrWhiteSpace(level) ? Options.DefaultLevel : level.Trim();
        if (!Levels.Contains(levelName, StringComparer.OrdinalIgnoreCase))
        {
            return new StartResult(StartError.InvalidLevel, "invalid_level", null);
        }

        if (!SharePathPolicy.TryResolve(
                _root, normalized, allowDirectory: true,
                out var sourceFullPath, out var isDirectory, out var resolveError))
        {
            return resolveError switch
            {
                SharePathPolicy.ResolveError.FileNotFound =>
                    new StartResult(StartError.SourceNotFound, null, null),
                _ => new StartResult(StartError.InvalidPath, null, null),
            };
        }

        if (!TryBuildSnapshot(
                sourceFullPath, isDirectory, normalized,
                out var entries, out var totalBytes, out var fileCount, out var skipped,
                out var buildError))
        {
            return new StartResult(buildError, buildError switch
            {
                StartError.TooManyFiles => $"文件数量超过 {MaxFiles} 个。",
                StartError.TooLarge => "源文件总大小超过限制。",
                _ => null,
            }, null);
        }

        if (entries.Count == 0)
        {
            return new StartResult(StartError.NothingToArchive, "源目录为空。", null);
        }

        var parentDirectory = Path.GetDirectoryName(sourceFullPath) ?? _root;
        var drive = DiskSpaceProbe.ResolveDrive(parentDirectory);
        var availableBytes = drive is { IsReady: true } ? drive.AvailableFreeSpace : 0;
        if (drive is { IsReady: true } &&
            availableBytes < totalBytes + Math.Max(0, Options.DiskHeadroomBytes))
        {
            var required = totalBytes + Math.Max(0, Options.DiskHeadroomBytes);
            return new StartResult(
                StartError.InsufficientDisk, "磁盘剩余空间不足。", null,
                availableBytes, required);
        }

        lock (_jobs)
        {
            var active = _jobs.Values.Count(j =>
                j.Status is "queued" or "processing");
            if (active >= Math.Max(1, Options.MaxQueuedJobs))
            {
                return new StartResult(StartError.QueueFull, "压缩任务过多，请稍后再试。", null);
            }
        }

        var key = Guid.NewGuid().ToString("N");
        var cts = new CancellationTokenSource(
            TimeSpan.FromMinutes(Math.Max(1, Options.TimeoutMinutes)));
        var job = new JobState
        {
            Key = key,
            SourcePath = normalized,
            SourceFullPath = sourceFullPath,
            IsDirectory = isDirectory,
            Level = levelName.ToLowerInvariant() switch
            {
                "fast" => CompressionLevel.Fastest,
                "smaller" => CompressionLevel.SmallestSize,
                _ => CompressionLevel.Optimal,
            },
            Entries = entries,
            TotalBytes = totalBytes,
            FileCount = fileCount,
            SkippedCount = skipped,
            Partial = skipped > 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Cts = cts,
        };

        _jobs[key] = job;
        _logger.LogInformation(
            "压缩任务已创建：{Source}（{Files} 个文件，{Size:F1} MB，级别 {Level}）",
            normalized, fileCount, totalBytes / (1024.0 * 1024), levelName);
        _ = Task.Run(() => RunAsync(job), CancellationToken.None);
        return new StartResult(StartError.None, null, ToInfo(job));
    }

    public JobInfo? Get(string? key)
    {
        if (string.IsNullOrEmpty(key) || !_jobs.TryGetValue(key, out var job))
        {
            return null;
        }

        return ToInfo(job);
    }

    public bool Cancel(string? key)
    {
        if (string.IsNullOrEmpty(key) || !_jobs.TryGetValue(key, out var job))
        {
            return false;
        }

        lock (job.Sync)
        {
            if (job.Status is "queued" or "processing")
            {
                job.Status = "canceled";
                job.FinishedAt = DateTimeOffset.UtcNow;
            }
        }

        try
        {
            job.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 任务刚好结束
        }

        return true;
    }

    // ── 快照与压缩 ─────────────────────────────────────────────────────────

    private bool TryBuildSnapshot(
        string sourceFullPath, bool isDirectory, string sourcePath,
        out List<SnapshotEntry> entries, out long totalBytes, out int fileCount,
        out int skipped, out StartError error)
    {
        entries = new List<SnapshotEntry>();
        totalBytes = 0;
        fileCount = 0;
        skipped = 0;
        error = StartError.None;

        if (!isDirectory)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(sourceFullPath);
            }
            catch
            {
                error = StartError.SourceNotFound;
                return false;
            }

            if (!info.Exists)
            {
                error = StartError.SourceNotFound;
                return false;
            }

            if (IsReparsePoint(info))
            {
                error = StartError.InvalidPath;
                return false;
            }

            entries.Add(new SnapshotEntry(
                sourceFullPath, SafeEntryName(info.Name), info.Length, IsDirectory: false));
            totalBytes = info.Length;
            fileCount = 1;
            if (totalBytes > Math.Max(1, Options.MaxSourceBytes))
            {
                error = StartError.TooLarge;
                return false;
            }

            return true;
        }

        var rootName = Path.GetFileName(sourceFullPath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(rootName))
        {
            error = StartError.RootNotAllowed;
            return false;
        }

        var stack = new Stack<(string Directory, string EntryPrefix)>();
        stack.Push((sourceFullPath, SafeEntryName(rootName)));
        while (stack.Count > 0)
        {
            var (directory, prefix) = stack.Pop();
            string[] files;
            string[] subdirs;
            try
            {
                files = Directory.GetFiles(directory);
                subdirs = Directory.GetDirectories(directory);
            }
            catch
            {
                skipped++;
                continue;
            }

            if (files.Length == 0 && subdirs.Length == 0 && directory != sourceFullPath)
            {
                entries.Add(new SnapshotEntry(directory, prefix + "/", 0, IsDirectory: true));
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (IsTempArchiveName(name))
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists || IsReparsePoint(info))
                    {
                        skipped++;
                        continue;
                    }
                }
                catch
                {
                    skipped++;
                    continue;
                }

                fileCount++;
                if (fileCount > MaxFiles)
                {
                    error = StartError.TooManyFiles;
                    return false;
                }

                totalBytes += info.Length;
                if (totalBytes > Math.Max(1, Options.MaxSourceBytes))
                {
                    error = StartError.TooLarge;
                    return false;
                }

                entries.Add(new SnapshotEntry(
                    file, prefix + "/" + SafeEntryName(name), info.Length, IsDirectory: false));
            }

            foreach (var subdir in subdirs)
            {
                try
                {
                    if ((File.GetAttributes(subdir) & FileAttributes.ReparsePoint) != 0)
                    {
                        skipped++;
                        continue;
                    }
                }
                catch
                {
                    skipped++;
                    continue;
                }

                stack.Push((subdir, prefix + "/" + SafeEntryName(Path.GetFileName(subdir))));
            }
        }

        return true;
    }

    private async Task RunAsync(JobState job)
    {
        var acquired = false;
        var tempPath = "";
        try
        {
            await _gate.WaitAsync(job.Cts.Token).ConfigureAwait(false);
            acquired = true;

            lock (job.Sync)
            {
                if (job.Status == "canceled")
                {
                    return;
                }

                job.Status = "processing";
                job.LastProgressTick = Environment.TickCount64;
            }

            var parentDirectory = Path.GetDirectoryName(job.SourceFullPath) ?? _root;
            var baseName = OutputBaseName(job);
            tempPath = Path.Combine(parentDirectory, $".archive-{job.Key[..8]}.part");

            using (var stream = new FileStream(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       ChunkBytes, FileOptions.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
                try
                {
                    foreach (var entry in job.Entries)
                    {
                        job.Cts.Token.ThrowIfCancellationRequested();
                        if (entry.IsDirectory)
                        {
                            var dirEntry = archive.CreateEntry(entry.EntryName);
                            dirEntry.LastWriteTime = ClampZipTime(null);
                            continue;
                        }

                        try
                        {
                            var info = new FileInfo(entry.FullPath);
                            if (!info.Exists || IsReparsePoint(info))
                            {
                                SkipJobFile(job);
                                continue;
                            }

                            var zipEntry = archive.CreateEntry(entry.EntryName, job.Level);
                            zipEntry.LastWriteTime = ClampZipTime(info.LastWriteTime);
                            using var input = new FileStream(
                                entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                ChunkBytes, FileOptions.SequentialScan);
                            using var output = zipEntry.Open();
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                job.Cts.Token.ThrowIfCancellationRequested();
                                output.Write(buffer, 0, read);
                                Interlocked.Add(ref job.ProcessedBytes, read);
                                UpdateProgress(job);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "压缩时跳过文件：{File}", entry.FullPath);
                            SkipJobFile(job);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            job.Cts.Token.ThrowIfCancellationRequested();
            var (finalPath, finalName) = MoveToFinal(tempPath, parentDirectory, baseName);
            lock (job.Sync)
            {
                job.Status = "ready";
                job.Progress = 1.0;
                job.OutputName = finalName;
                job.OutputPath = ToResourcePath(finalPath);
                job.FinishedAt = DateTimeOffset.UtcNow;
            }

            _logger.LogInformation("压缩完成：{Output}", finalName);
        }
        catch (OperationCanceledException)
        {
            lock (job.Sync)
            {
                if (job.Status is not ("ready" or "failed"))
                {
                    job.Status = "canceled";
                }

                job.FinishedAt ??= DateTimeOffset.UtcNow;
            }

            _logger.LogInformation("压缩任务已取消：{Source}", job.SourcePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "压缩任务失败：{Source}", job.SourcePath);
            lock (job.Sync)
            {
                if (job.Status != "canceled")
                {
                    job.Status = "failed";
                    job.Error = ex is IOException ? "io_error" : "archive_failed";
                }

                job.FinishedAt ??= DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }

            if (!string.IsNullOrEmpty(tempPath))
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "清理压缩临时文件失败：{Path}", tempPath);
                }
            }

            job.Cts.Dispose();
        }
    }

    private void UpdateProgress(JobState job)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref job.LastProgressTick) < 250)
        {
            return;
        }

        var processed = Interlocked.Read(ref job.ProcessedBytes);
        var progress = job.TotalBytes > 0
            ? Math.Min(0.99, (double)processed / job.TotalBytes)
            : 0.0;
        lock (job.Sync)
        {
            if (job.Status != "processing")
            {
                return;
            }

            job.Progress = progress;
            Interlocked.Exchange(ref job.LastProgressTick, now);
        }
    }

    private void SkipJobFile(JobState job)
    {
        Interlocked.Increment(ref job.SkippedCount);
        job.Partial = true;
    }

    private (string Path, string Name) MoveToFinal(
        string tempPath, string directory, string baseName)
    {
        for (var index = 0; index < 100; index++)
        {
            var name = index == 0 ? baseName + ".zip" : $"{baseName} ({index + 1}).zip";
            var destination = Path.Combine(directory, name);
            try
            {
                File.Move(tempPath, destination);
                return (destination, name);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // 文件名已被占用，换下一个序号
            }
        }

        throw new IOException("无法分配输出文件名。");
    }

    private static string OutputBaseName(JobState job)
    {
        var name = Path.GetFileName(job.SourceFullPath.TrimEnd(Path.DirectorySeparatorChar));
        if (!job.IsDirectory)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            return string.IsNullOrEmpty(stem) ? name : stem;
        }

        return string.IsNullOrEmpty(name) ? "archive" : name;
    }

    private string ToResourcePath(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
        return "/" + relative;
    }

    private JobInfo ToInfo(JobState job)
    {
        lock (job.Sync)
        {
            return new JobInfo(
                job.Key,
                job.Status,
                job.Status is "ready" ? 1.0 : job.Progress,
                job.TotalBytes,
                Interlocked.Read(ref job.ProcessedBytes),
                job.FileCount,
                job.SkippedCount,
                job.SourcePath,
                job.OutputPath,
                job.OutputName,
                job.Error,
                job.Partial || job.SkippedCount > 0);
        }
    }

    private static bool IsReparsePoint(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsTempArchiveName(string name) =>
        name.StartsWith(".archive-", StringComparison.Ordinal) &&
        name.EndsWith(".part", StringComparison.Ordinal);

    private static string SafeEntryName(string name) =>
        name.Replace('\\', '_').Replace('/', '_');

    private static DateTime ClampZipTime(DateTime? value)
    {
        var time = value ?? DateTime.Now;
        if (time.Year < 1980)
        {
            return new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        }

        if (time.Year > 2107)
        {
            return new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
        }

        return time;
    }

    // ── 生命周期与清理 ─────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => CleanupLoopAsync(_cleanupCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupCts?.Cancel();
        foreach (var job in _jobs.Values)
        {
            try
            {
                job.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已结束
            }
        }

        return Task.CompletedTask;
    }

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var cutoff = DateTimeOffset.UtcNow.AddMinutes(
                    -Math.Max(1, Options.JobRetentionMinutes));
                foreach (var pair in _jobs)
                {
                    if (pair.Value.Status is "queued" or "processing")
                    {
                        continue;
                    }

                    if ((pair.Value.FinishedAt ?? pair.Value.CreatedAt) < cutoff)
                    {
                        _jobs.TryRemove(pair.Key, out _);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "压缩任务清理失败。");
        }
    }

    public void Dispose()
    {
        _cleanupCts?.Cancel();
        _cleanupCts?.Dispose();
        _gate.Dispose();
    }
}
