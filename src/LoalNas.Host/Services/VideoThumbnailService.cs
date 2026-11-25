using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace LoalNas.Host.Services;

/// <summary>
/// 视频封面：调用 ffmpeg 抽取一帧生成 JPEG，并按「文件路径 + 大小 + 修改时间」缓存到磁盘。
/// 缓存放在用户缓存目录（~/.cache/loalnas/video-thumbs），与共享目录分开，避免出现在文件列表和回收站里。
/// </summary>
public sealed class VideoThumbnailService
{
    public enum ThumbnailStatus { Ok, NotFound, Invalid, Failed }

    private const int ThumbnailWidth = 480;
    private const int ThumbnailQuality = 4; // ffmpeg -q:v，越小越清晰
    private const int SeekSeconds = 2;      // 跳过片头黑屏，取第 2 秒
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(20);

    // 机械盘上同时转多个视频会互相拖慢，限制并发为 2
    private static readonly SemaphoreSlim Gate = new(2, 2);

    private readonly string _shareRoot;
    private readonly string _cacheDirectory;
    private readonly ILogger<VideoThumbnailService> _logger;

    public VideoThumbnailService(FileBrowserProcessManager manager, ILogger<VideoThumbnailService> logger)
    {
        _logger = logger;
        _shareRoot = Path.GetFullPath(manager.SharedRootPath);

        var cacheRoot = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache");
        }

        _cacheDirectory = Path.Combine(cacheRoot, "loalnas", "video-thumbs");
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Cannot create video thumbnail cache directory {Directory}.", _cacheDirectory);
        }
    }

    /// <summary>返回封面 JPEG 的本地路径；调用方负责读成字节流返回给客户端。</summary>
    public async Task<(ThumbnailStatus Status, string? FilePath)> GetThumbnailAsync(
        string resourcePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourcePath) || !TryResolveWithinShare(resourcePath, out var fullPath))
        {
            return (ThumbnailStatus.Invalid, null);
        }

        if (!File.Exists(fullPath))
        {
            return (ThumbnailStatus.NotFound, null);
        }

        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Cannot stat video file {Path}.", fullPath);
            return (ThumbnailStatus.Invalid, null);
        }

        var cachePath = ResolveCachePath(fullPath, info);
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
        {
            return (ThumbnailStatus.Ok, cachePath);
        }

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Cannot create thumbnail cache directory {Directory}.", _cacheDirectory);
            return (ThumbnailStatus.Failed, null);
        }

        var generated = await GenerateAsync(fullPath, cachePath, cancellationToken);
        return generated
            ? (ThumbnailStatus.Ok, cachePath)
            : (ThumbnailStatus.Failed, null);
    }

    private string ResolveCachePath(string fullPath, FileInfo info)
    {
        var stamp = $"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)));
        return Path.Combine(_cacheDirectory, hash[..32].ToLowerInvariant() + ".jpg");
    }

    private async Task<bool> GenerateAsync(string inputPath, string cachePath, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp.jpg";
            try
            {
                // 先跳 2 秒；短视频或跳过头时退回到第 0 秒
                if (!await RunFfmpegAsync(inputPath, tempPath, SeekSeconds, cancellationToken))
                {
                    if (!await RunFfmpegAsync(inputPath, tempPath, 0, cancellationToken))
                    {
                        return false;
                    }
                }

                if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                {
                    return false;
                }

                File.Move(tempPath, cachePath, overwrite: true);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Video thumbnail generation failed for {Path}.", inputPath);
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // 临时文件清理失败不影响主流程
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<bool> RunFfmpegAsync(
        string inputPath,
        string outputPath,
        int seekSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-threads");
        startInfo.ArgumentList.Add("2");
        if (seekSeconds > 0)
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(seekSeconds.ToString());
        }
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"scale={ThumbnailWidth}:-2");
        startInfo.ArgumentList.Add("-q:v");
        startInfo.ArgumentList.Add(ThumbnailQuality.ToString());
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // 必须持续读取输出，否则管道写满会让 ffmpeg 卡住
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FfmpegTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            _logger.LogWarning("ffmpeg timed out after {Seconds}s for {Path}.", FfmpegTimeout.TotalSeconds, inputPath);
            return false;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stderr = await stderrTask;
        await stdoutTask;

        if (process.ExitCode != 0)
        {
            _logger.LogInformation("ffmpeg exit {Code} for {Path}: {Error}",
                process.ExitCode, inputPath, Truncate(stderr, 300));
            return false;
        }

        return true;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程已退出或无权结束，忽略
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private bool TryResolveWithinShare(string resourcePath, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            var relative = resourcePath.TrimStart('/');
            var candidate = Path.GetFullPath(Path.Combine(_shareRoot, relative));
            var root = _shareRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(root, StringComparison.Ordinal))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }
}