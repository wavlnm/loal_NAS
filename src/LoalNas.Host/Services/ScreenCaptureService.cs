using System.Diagnostics;
using System.Runtime.InteropServices;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// 画面抓取：桌面源（gstreamer ximagesrc 读 X 根窗口）与摄像头源（v4l2 + 帧差侦测 worker）。
/// 两者都由本服务按需拉起常驻进程，HTTP 侧只读进程写在缓存目录里的最新一帧；
/// 参数变化会自动重启抓取进程，空闲超过 IdleSeconds 自动停止（摄像头源在侦测开启时由
/// MonitorService 标记 KeepAlive，不受空闲停止影响）。
/// 桌面源不走 mutter ScreenCast —— GNOME 51 的录屏代码有 stride 断言崩溃，会弄崩整个桌面。
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    public const string SourceDesktop = "desktop";
    public const string SourceCamera = "camera";

    public sealed record ScreenFrame(byte[] Bytes, string ContentType, string ETag);

    private sealed class Channel
    {
        public required string Key;
        public required string ScriptPath;
        public required string OutDir;
        public required Func<Channel, int, int, string, IEnumerable<string>> BuildArgs;
        public int MinWidth;
        public int MaxWidth;
        public int DefaultWidth;
        public int DefaultQuality;
        public int IdleSeconds;
        public int MaxFrameAgeSeconds;

        public Process? Worker;
        public int Width;
        public int Quality;
        public string Format = "";
        public long LastUseTicks = Environment.TickCount64;
        public bool KeepAlive;
    }

    private readonly ILogger<ScreenCaptureService> _logger;
    private readonly ScreenCaptureOptions _options;
    private readonly MonitorOptions _monitor;
    private readonly Channel _desktop;
    private readonly Channel _camera;
    private readonly object _gate = new();
    private readonly Timer _watchdog;

    public ScreenCaptureService(
        IOptions<ScreenCaptureOptions> options,
        IOptions<MonitorOptions> monitor,
        FileBrowserProcessManager manager,
        ILogger<ScreenCaptureService> logger)
    {
        _logger = logger;
        _options = options.Value;
        _monitor = monitor.Value;

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "loalnas");

        _desktop = new Channel
        {
            Key = SourceDesktop,
            ScriptPath = Path.Combine(AppContext.BaseDirectory, _options.WorkerScript),
            OutDir = string.IsNullOrWhiteSpace(_options.OutputDirectory)
                ? Path.Combine(cacheRoot, "screen")
                : _options.OutputDirectory,
            MinWidth = _options.MinWidth,
            MaxWidth = _options.MaxWidth,
            DefaultWidth = _options.DefaultWidth,
            DefaultQuality = _options.DefaultQuality,
            IdleSeconds = _options.IdleSeconds,
            MaxFrameAgeSeconds = _options.MaxFrameAgeSeconds,
            BuildArgs = (channel, width, quality, format) => new[]
            {
                "--width", width.ToString(),
                "--quality", quality.ToString(),
                "--format", format,
                "--fps", Math.Clamp(_options.Framerate, 1, 30).ToString(),
                "--out-dir", channel.OutDir,
            },
        };

        _camera = new Channel
        {
            Key = SourceCamera,
            ScriptPath = Path.Combine(AppContext.BaseDirectory, _monitor.WorkerScript),
            OutDir = Path.Combine(cacheRoot, "camera"),
            MinWidth = _monitor.MinWidth,
            MaxWidth = _monitor.MaxWidth,
            DefaultWidth = _monitor.DefaultWidth,
            DefaultQuality = _monitor.DefaultQuality,
            IdleSeconds = _monitor.IdleSeconds,
            MaxFrameAgeSeconds = _monitor.MaxFrameAgeSeconds,
            BuildArgs = (channel, width, quality, format) => new List<string>
            {
                "--device", _monitor.CameraDevice,
                "--preview-width", width.ToString(),
                "--preview-fps", Math.Clamp(_monitor.PreviewFps, 1, 30).ToString(),
                "--preview-jpeg-quality", quality.ToString(),
                "--ring-fps", Math.Clamp(_monitor.RingFps, 5, 30).ToString(),
                "--clip-width", _monitor.ClipWidth.ToString(),
                "--clip-height", _monitor.ClipHeight.ToString(),
                "--detect-fps", Math.Clamp(_monitor.DetectFps, 1, 10).ToString(),
                "--confirm-hits", Math.Clamp(_monitor.ConfirmHits, 1, 10).ToString(),
                "--record-seconds", Math.Clamp(_monitor.RecordSeconds, 3, 60).ToString(),
                "--cooldown-seconds", Math.Clamp(_monitor.CooldownSeconds, 5, 600).ToString(),
                "--clip-crf", Math.Clamp(_monitor.ClipCrf, 18, 32).ToString(),
                "--detect", "0",
                "--sensitivity", SensitivityOrDefault(null),
                "--out-dir", channel.OutDir,
                "--ring-dir", Path.Combine(cacheRoot, "camera-hd"),
                "--state-file", MonitorPaths.StateFile,
                "--cmd-file", MonitorPaths.CommandFile,
                "--clip-dir-base", Path.Combine(
                    Path.GetFullPath(manager.SharedRootPath), _monitor.ClipFolderName),
            }.Concat(_monitor.ClipWithAudio ? Array.Empty<string>() : new[] { "--no-audio" }),
        };

        foreach (var channel in new[] { _desktop, _camera })
        {
            try
            {
                Directory.CreateDirectory(channel.OutDir);
                ClearFrames(channel);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "初始化抓帧目录失败：{Dir}", channel.OutDir);
            }
        }

        _watchdog = new Timer(_ => StopIfIdle(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>把请求参数裁剪到该源允许的范围，避免各处重复写策略。</summary>
    public (string Source, int Width, int Quality, string Format) Normalize(
        string? source, int? width, int? quality, string? format)
    {
        var channel = IsCamera(source) ? _camera : _desktop;
        return (
            channel.Key,
            Math.Clamp(width ?? channel.DefaultWidth, channel.MinWidth, channel.MaxWidth),
            Math.Clamp(quality ?? channel.DefaultQuality, 10, 95),
            string.Equals(format, "png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg");
    }

    public static bool IsCamera(string? source) =>
        string.Equals(source, SourceCamera, StringComparison.OrdinalIgnoreCase);

    /// <summary>取最新一帧；没有可用帧时返回 null。参数变化会自动重启抓取进程。</summary>
    public async Task<ScreenFrame?> GetFrameAsync(
        string source, int width, int quality, string format, CancellationToken cancellationToken)
    {
        var channel = IsCamera(source) ? _camera : _desktop;
        EnsureWorker(channel, width, quality, format);
        Interlocked.Exchange(ref channel.LastUseTicks, Environment.TickCount64);

        var deadline = Environment.TickCount64 + 4000;
        while (Environment.TickCount64 < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var frame = TryReadLatest(channel);
            if (frame is not null)
            {
                return frame;
            }

            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("等不到屏幕帧（{Source} {Width}px {Quality} {Format}）",
            channel.Key, width, quality, format);
        return null;
    }

    /// <summary>
    /// 侦测开启时即使手机没在取图也要让摄像头进程跑着（默认档位）。
    /// 由 MonitorService 的定时器和开关切换时调用。
    /// </summary>
    public void EnsureCameraChannelAlive(bool alive)
    {
        _camera.KeepAlive = alive;
        if (!alive)
        {
            return;
        }

        lock (_gate)
        {
            // 已经在跑就别用默认档位去覆盖它：否则手机请求的 1280 会被每 5 秒
            // 一次的保活拉回 640，来回重启还会让画面忽大忽小。
            if (_camera.Worker is { HasExited: false })
            {
                return;
            }

            EnsureWorker(_camera, _camera.DefaultWidth, _camera.DefaultQuality, "jpeg");
        }
    }

    /// <summary>停止指定源的抓取进程（侦测关闭、总开关关闭时用）。</summary>
    public void Stop(string source)
    {
        var channel = IsCamera(source) ? _camera : _desktop;
        lock (_gate)
        {
            StopWorkerLocked(channel);
        }
    }

    /// <summary>worker 上报的运行参数（宽度/帧率），用于状态接口。</summary>
    public (int Width, int Quality, bool Running)? CameraWorkerState()
    {
        var alive = _camera.Worker is { HasExited: false };
        return alive ? (_camera.Width, _camera.Quality, true) : null;
    }

    private static string SensitivityOrDefault(string? sensitivity) =>
        sensitivity is ("low" or "mid" or "high") ? sensitivity : "mid";

    private void EnsureWorker(Channel channel, int width, int quality, string format)
    {
        lock (_gate)
        {
            var alive = channel.Worker is { HasExited: false };
            if (alive && channel.Width == width && channel.Quality == quality && channel.Format == format)
            {
                return;
            }

            StopWorkerLocked(channel);
            if (!File.Exists(channel.ScriptPath))
            {
                _logger.LogError("抓取脚本不存在：{Path}", channel.ScriptPath);
                return;
            }

            try
            {
                // 清掉上一轮参数产生的旧帧，避免新参数的首个请求拿到旧画面。
                ClearFrames(channel);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "清理旧帧失败。");
            }

            var startInfo = BuildStartInfo(channel, width, quality, format);
            if (startInfo is null)
            {
                return;
            }

            try
            {
                var worker = Process.Start(startInfo);
                if (worker is null)
                {
                    _logger.LogError("抓取进程启动失败。");
                    return;
                }

                worker.EnableRaisingEvents = true;
                worker.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _logger.LogDebug("{Source}-worker: {Line}", channel.Key, e.Data);
                    }
                };
                worker.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _logger.LogInformation("{Source}-worker: {Line}", channel.Key, e.Data);
                    }
                };
                worker.BeginOutputReadLine();
                worker.BeginErrorReadLine();

                channel.Worker = worker;
                channel.Width = width;
                channel.Quality = quality;
                channel.Format = format;
                _logger.LogInformation("抓取进程已启动：{Source} {Width}px q{Quality} {Format}",
                    channel.Key, width, quality, format);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动抓取进程失败。");
            }
        }
    }

    private ProcessStartInfo? BuildStartInfo(Channel channel, int width, int quality, string format)
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDir))
        {
            runtimeDir = $"/run/user/{GetUid()}";
        }

        var startInfo = new ProcessStartInfo("/usr/bin/python3")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(channel.ScriptPath);
        foreach (var argument in channel.BuildArgs(channel, width, quality, format))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        startInfo.Environment["XDG_RUNTIME_DIR"] = runtimeDir;
        startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={runtimeDir}/bus";
        startInfo.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";

        var (display, xauthority) = ReadSessionDisplay();
        startInfo.Environment["DISPLAY"] = display;
        if (!string.IsNullOrEmpty(xauthority))
        {
            startInfo.Environment["XAUTHORITY"] = xauthority;
        }

        return startInfo;
    }

    private void ClearFrames(Channel channel)
    {
        foreach (var file in Directory.EnumerateFiles(channel.OutDir, "f_*"))
        {
            File.Delete(file);
        }
    }

    private ScreenFrame? TryReadLatest(Channel channel)
    {
        try
        {
            var now = DateTime.UtcNow;
            var newest = new DirectoryInfo(channel.OutDir)
                .EnumerateFiles("f_*")
                // 跳过正在写入的文件（刚生成的），避免读到半张图。
                .Where(f => f.LastWriteTimeUtc < now.AddMilliseconds(-30))
                // 抓取进程停摆时不能把旧帧当成当前画面返回。
                .Where(f => f.LastWriteTimeUtc > now.AddSeconds(-Math.Max(1, channel.MaxFrameAgeSeconds)))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null || newest.Length == 0)
            {
                return null;
            }

            // 文件正被写入时可能读不全，直接跳过这一轮。
            using var stream = new FileStream(newest.FullName, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream((int)Math.Min(newest.Length, 8 * 1024 * 1024));
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0)
            {
                return null;
            }

            var contentType = newest.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";
            var etag = $"\"{channel.Key}-{newest.Name}-{bytes.Length}-{newest.LastWriteTimeUtc.Ticks}\"";
            return new ScreenFrame(bytes, contentType, etag);
        }
        catch (IOException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取屏幕帧失败。");
            return null;
        }
    }

    private void StopIfIdle()
    {
        lock (_gate)
        {
            foreach (var channel in new[] { _desktop, _camera })
            {
                if (channel.Worker is null || channel.KeepAlive)
                {
                    continue;
                }

                var idleMs = Environment.TickCount64 - Interlocked.Read(ref channel.LastUseTicks);
                if (idleMs < channel.IdleSeconds * 1000L)
                {
                    continue;
                }

                _logger.LogInformation("{Source} 抓取已空闲 {Seconds} 秒，停止进程。",
                    channel.Key, channel.IdleSeconds);
                StopWorkerLocked(channel);
            }
        }
    }

    private void StopWorkerLocked(Channel channel)
    {
        var worker = channel.Worker;
        channel.Worker = null;
        channel.Width = 0;
        channel.Quality = 0;
        channel.Format = string.Empty;
        if (worker is null)
        {
            return;
        }

        try
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                worker.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "停止抓取进程时出错。");
        }
        finally
        {
            worker.Dispose();
        }
    }

    /// <summary>从当前图形会话进程读 DISPLAY/XAUTHORITY（X11 抓图需要）。</summary>
    private static (string Display, string? Xauthority) ReadSessionDisplay()
    {
        string? fallbackDisplay = null;
        string? fallbackXauth = null;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out _))
                {
                    continue;
                }

                string comm;
                try
                {
                    comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                }
                catch
                {
                    continue;
                }

                var preferred = comm == "xfce4-session";
                if (!preferred && comm != "gnome-session-bi" && comm != "gnome-shell")
                {
                    continue;
                }

                string env;
                try
                {
                    env = File.ReadAllText(Path.Combine(dir, "environ"));
                }
                catch
                {
                    continue;
                }

                string? display = null;
                string? xauth = null;
                foreach (var entry in env.Split('\0'))
                {
                    if (entry.StartsWith("DISPLAY=", StringComparison.Ordinal))
                    {
                        display = entry[8..];
                    }
                    else if (entry.StartsWith("XAUTHORITY=", StringComparison.Ordinal))
                    {
                        xauth = entry[11..];
                    }
                }

                if (display is null)
                {
                    continue;
                }

                if (preferred)
                {
                    return (display, xauth);
                }

                fallbackDisplay ??= display;
                fallbackXauth ??= xauth;
            }
        }
        catch
        {
            // 读不到就用默认 :0。
        }

        return (fallbackDisplay ?? ":0", fallbackXauth);
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    public void Dispose()
    {
        _watchdog.Dispose();
        lock (_gate)
        {
            StopWorkerLocked(_desktop);
            StopWorkerLocked(_camera);
        }
    }
}
