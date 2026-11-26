using System.Diagnostics;
using System.Runtime.InteropServices;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// 看屏幕：按需拉起常驻抓取 worker（X11 下用 gstreamer ximagesrc 读根窗口），
/// 读取它写到缓存目录的最新一帧；空闲超过 IdleSeconds 自动停掉抓取进程。
/// 不走 mutter ScreenCast —— GNOME 51 的录屏代码有 stride 断言崩溃，会把桌面弄崩。
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    public sealed record ScreenFrame(byte[] Bytes, string ContentType, string ETag);

    private readonly ILogger<ScreenCaptureService> _logger;
    private readonly ScreenCaptureOptions _options;
    private readonly string _scriptPath;
    private readonly string _outDir;
    private readonly object _gate = new();
    private readonly Timer _watchdog;

    private Process? _worker;
    private int _width;
    private int _quality;
    private string _format = "";
    private long _lastUseTicks = Environment.TickCount64;

    public ScreenCaptureService(IOptions<ScreenCaptureOptions> options, ILogger<ScreenCaptureService> logger)
    {
        _logger = logger;
        _options = options.Value;
        _scriptPath = Path.Combine(AppContext.BaseDirectory, _options.WorkerScript);
        _outDir = string.IsNullOrWhiteSpace(_options.OutputDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "loalnas", "screen")
            : _options.OutputDirectory;

        try
        {
            Directory.CreateDirectory(_outDir);
            ClearFrames();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "初始化抓帧目录失败：{Dir}", _outDir);
        }

        _watchdog = new Timer(_ => StopIfIdle(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>把请求参数裁剪到配置允许的范围，避免各处重复写策略。</summary>
    public (int Width, int Quality, string Format) Normalize(int? width, int? quality, string? format)
        => (
            Math.Clamp(width ?? _options.DefaultWidth, _options.MinWidth, _options.MaxWidth),
            Math.Clamp(quality ?? _options.DefaultQuality, 10, 95),
            string.Equals(format, "png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg");

    /// <summary>取最新一帧；没有可用帧时返回 null。参数变化会自动重启抓取进程。</summary>
    public async Task<ScreenFrame?> GetFrameAsync(int width, int quality, string format, CancellationToken cancellationToken)
    {
        EnsureWorker(width, quality, format);
        Interlocked.Exchange(ref _lastUseTicks, Environment.TickCount64);

        var deadline = Environment.TickCount64 + 4000;
        while (Environment.TickCount64 < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var frame = TryReadLatest();
            if (frame is not null)
            {
                return frame;
            }

            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("等不到屏幕帧（{Width}px {Quality} {Format}）", width, quality, format);
        return null;
    }

    private void EnsureWorker(int width, int quality, string format)
    {
        lock (_gate)
        {
            var alive = _worker is { HasExited: false };
            if (alive && _width == width && _quality == quality && _format == format)
            {
                return;
            }

            StopWorkerLocked();
            if (!File.Exists(_scriptPath))
            {
                _logger.LogError("抓取脚本不存在：{Path}", _scriptPath);
                return;
            }

            try
            {
                // 清掉上一轮参数产生的旧帧，避免新参数的首个请求拿到旧画面。
                ClearFrames();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "清理旧帧失败。");
            }

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
            startInfo.ArgumentList.Add(_scriptPath);
            startInfo.ArgumentList.Add("--width");
            startInfo.ArgumentList.Add(width.ToString());
            startInfo.ArgumentList.Add("--quality");
            startInfo.ArgumentList.Add(quality.ToString());
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add(format);
            startInfo.ArgumentList.Add("--fps");
            startInfo.ArgumentList.Add(Math.Clamp(_options.Framerate, 1, 30).ToString());
            startInfo.ArgumentList.Add("--out-dir");
            startInfo.ArgumentList.Add(_outDir);
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
                        _logger.LogDebug("screen-worker: {Line}", e.Data);
                    }
                };
                worker.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _logger.LogInformation("screen-worker: {Line}", e.Data);
                    }
                };
                worker.BeginOutputReadLine();
                worker.BeginErrorReadLine();

                _worker = worker;
                _width = width;
                _quality = quality;
                _format = format;
                _logger.LogInformation("抓取进程已启动：{Width}px q{Quality} {Format}", width, quality, format);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动抓取进程失败。");
            }
        }
    }

    private void ClearFrames()
    {
        foreach (var file in Directory.EnumerateFiles(_outDir, "f_*"))
        {
            File.Delete(file);
        }
    }

    private ScreenFrame? TryReadLatest()
    {
        try
        {
            var now = DateTime.UtcNow;
            var newest = new DirectoryInfo(_outDir)
                .EnumerateFiles("f_*")
                // 跳过正在写入的文件（刚生成的），避免读到半张图。
                .Where(f => f.LastWriteTimeUtc < now.AddMilliseconds(-30))
                // 抓取进程停摆时不能把旧帧当成当前画面返回。
                .Where(f => f.LastWriteTimeUtc > now.AddSeconds(-Math.Max(1, _options.MaxFrameAgeSeconds)))
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
            var etag = $"\"{newest.Name}-{bytes.Length}-{newest.LastWriteTimeUtc.Ticks}\"";
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
            if (_worker is null)
            {
                return;
            }

            var idleMs = Environment.TickCount64 - Interlocked.Read(ref _lastUseTicks);
            if (idleMs < _options.IdleSeconds * 1000L)
            {
                return;
            }

            _logger.LogInformation("看屏幕已空闲 {Seconds} 秒，停止抓取会话。", _options.IdleSeconds);
            StopWorkerLocked();
        }
    }

    private void StopWorkerLocked()
    {
        var worker = _worker;
        _worker = null;
        _width = 0;
        _quality = 0;
        _format = string.Empty;
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
            StopWorkerLocked();
        }
    }
}