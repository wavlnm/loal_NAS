using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace LoalNas.Host.Services;

/// <summary>
/// 音频转码兜底：把 FLAC 等手机端难以直接解码的音频转成 MP3（256kbps），
/// 转码结果缓存在 ~/.cache/loalnas/audio-cache，按「路径+大小+修改时间」区分版本。
/// 转码在后台进行，客户端轮询进度，完成后通过 /api/system/audio-cache/{key} 以支持 Range 的方式播放。
/// </summary>
public sealed class AudioTranscodeService
{
    public sealed record AudioInfo(string Key, string Status, double Progress, string? Error, string Url);

    private static readonly SemaphoreSlim Gate = new(2, 2);
    private const int BitrateKbps = 256;

    private sealed class Job
    {
        public required string Key { get; init; }
        public required string SourcePath { get; init; }
        public required string OutputPath { get; init; }
        public volatile string Status = "processing";
        public double Progress;
        public string? Error;
    }

    private readonly string _shareRoot;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly ILogger<AudioTranscodeService> _logger;

    public AudioTranscodeService(FileBrowserProcessManager manager, ILogger<AudioTranscodeService> logger)
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

        _cacheDirectory = Path.Combine(cacheRoot, "loalnas", "audio-cache");
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Cannot create audio cache directory {Directory}.", _cacheDirectory);
        }
    }

    /// <summary>请求转码：已有缓存直接返回 ready，否则启动（或复用）后台转码任务。</summary>
    public AudioInfo Request(string resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath) || !TryResolveWithinShare(resourcePath, out var fullPath))
        {
            throw new ArgumentException("路径无效");
        }

        if (!File.Exists(fullPath))
        {
            throw new ArgumentException("文件不存在");
        }

        var fileName = Path.GetFileName(fullPath);
        var info = new FileInfo(fullPath);
        var stamp = $"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..32].ToLowerInvariant();
        var outputPath = Path.Combine(_cacheDirectory, key + ".mp3");

        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
        {
            return new AudioInfo(key, "ready", 1, null, BuildUrl(key, fileName));
        }

        if (_jobs.TryGetValue(key, out var existing) && existing.Status == "failed")
        {
            _jobs.TryRemove(key, out _);
        }

        var job = _jobs.GetOrAdd(key, cacheKey =>
        {
            var created = new Job
            {
                Key = cacheKey,
                SourcePath = fullPath,
                OutputPath = outputPath
            };
            _ = Task.Run(() => RunAsync(created));
            return created;
        });

        return Snapshot(job, fileName);
    }

    public AudioInfo? GetStatus(string key)
    {
        string? fileName = null;
        if (_jobs.TryGetValue(key, out var job))
        {
            fileName = Path.GetFileName(job.SourcePath);
            return Snapshot(job, fileName);
        }

        var cached = Path.Combine(_cacheDirectory, key + ".mp3");
        return File.Exists(cached) && new FileInfo(cached).Length > 0
            ? new AudioInfo(key, "ready", 1, null, BuildUrl(key, key + ".mp3"))
            : null;
    }

    public string? ResolveCacheFile(string key)
    {
        if (key.Length != 32 || !key.All(Uri.IsHexDigit))
        {
            return null;
        }

        var path = Path.Combine(_cacheDirectory, key + ".mp3");
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    private AudioInfo Snapshot(Job job, string fileName) =>
        new(job.Key, job.Status, job.Progress, job.Error, BuildUrl(job.Key, fileName + ".mp3"));

    private static string BuildUrl(string key, string fileName) =>
        $"/api/system/audio-cache/{key}/{Uri.EscapeDataString(fileName)}";

    private async Task RunAsync(Job job)
    {
        await Gate.WaitAsync();
        var tempPath = job.OutputPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            var duration = await ProbeDurationAsync(job.SourcePath);
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            void Arg(string value) => startInfo.ArgumentList.Add(value);
            Arg("-hide_banner");
            Arg("-nostdin");
            Arg("-loglevel");
            Arg("error");
            Arg("-y");
            Arg("-i");
            Arg(job.SourcePath);
            Arg("-vn");
            Arg("-map");
            Arg("0:a:0");
            Arg("-ac");
            Arg("2");
            Arg("-c:a");
            Arg("libmp3lame");
            Arg("-b:a");
            Arg($"{BitrateKbps}k");
            Arg("-ar");
            Arg("44100");
            Arg("-id3v2_version");
            Arg("3");
            Arg("-progress");
            Arg("pipe:1");
            Arg("-nostats");
            Arg("-f");
            Arg("mp3");
            Arg(tempPath);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stderrTask = process.StandardError.ReadToEndAsync();
            var progressTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    const string prefix = "out_time_us=";
                    if (duration > 0 && line.StartsWith(prefix, StringComparison.Ordinal) &&
                        long.TryParse(line[prefix.Length..], out var microseconds) && microseconds > 0)
                    {
                        job.Progress = Math.Clamp(microseconds / (duration * 1_000_000d), 0, 0.99);
                    }
                }
            });

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            await progressTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                job.Status = "failed";
                job.Error = Truncate(stderr, 300);
                _logger.LogWarning("Audio transcode failed ({Code}) for {Path}: {Error}",
                    process.ExitCode, job.SourcePath, job.Error);
                return;
            }

            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            {
                job.Status = "failed";
                job.Error = "转码输出为空";
                return;
            }

            File.Move(tempPath, job.OutputPath, overwrite: true);
            job.Progress = 1;
            job.Status = "ready";
            _logger.LogInformation("Audio transcoded: {Path} -> {Output}", job.SourcePath, job.OutputPath);
        }
        catch (Exception exception)
        {
            job.Status = "failed";
            job.Error = exception.Message;
            _logger.LogWarning(exception, "Audio transcode crashed for {Path}.", job.SourcePath);
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
            Gate.Release();
        }
    }

    /// <summary>读取时长；进度解析在 WaitForExit 之前完成会阻塞管道，所以这里单独起进程读取。</summary>
    private async Task<double> ProbeDurationAsync(string sourcePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("format=duration");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("default=nw=1:nk=1");
            startInfo.ArgumentList.Add(sourcePath);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return double.TryParse(output.Trim(), out var value) ? value : 0;
        }
        catch
        {
            return 0;
        }
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
            // 忽略
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