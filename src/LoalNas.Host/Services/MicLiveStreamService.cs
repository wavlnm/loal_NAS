using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LoalNas.Host.Services;

/// <summary>
/// 听现场：把 PipeWire 麦克风采成 MP3 实时流，chunked 直接吐给手机端播放器。
/// 只允许一路并发（同一台机器上开两路麦克风没意义，也省 CPU）。
/// </summary>
public sealed class MicLiveStreamService
{
    private readonly ILogger<MicLiveStreamService> _logger;
    private int _active;

    public MicLiveStreamService(ILogger<MicLiveStreamService> logger)
    {
        _logger = logger;
    }

    public bool IsBusy => Volatile.Read(ref _active) == 1;

    public async Task StreamAsync(
        HttpContext context,
        int bitrateKbps,
        int sampleRate,
        int maxSeconds,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = "listen_busy" });
            return;
        }

        Process? process = null;
        context.Response.ContentType = "audio/mpeg";
        context.Response.Headers.CacheControl = "no-store";
        var startedAt = Stopwatch.StartNew();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(maxSeconds, 5, 1800)));

            var startInfo = new ProcessStartInfo("/bin/bash")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                // 子进程 stderr 不接管，直接进服务日志（journal），方便排查采集失败原因；
                // 接管了却不去读会把管道塞满，反而卡死 ffmpeg。
                RedirectStandardError = false,
                RedirectStandardInput = false,
            };
            var bitrate = Math.Clamp(bitrateKbps, 16, 192);
            var rate = Math.Clamp(sampleRate, 8000, 48000);
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(
                "pw-record --rate " + rate + " --channels 1 - " +
                "| ffmpeg -hide_banner -loglevel error -f s16le -ar " + rate + " -ac 1 -i - " +
                "-c:a libmp3lame -b:a " + bitrate + "k -f mp3 -");
            foreach (var entry in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>())
            {
                if (entry.Key is string key
                    && (key == "HOME" || key == "PATH")
                    && !string.IsNullOrWhiteSpace(entry.Value?.ToString()))
                {
                    startInfo.Environment[key] = entry.Value?.ToString();
                }
            }

            // systemd 服务环境里 XDG_RUNTIME_DIR / DBUS_SESSION_BUS_ADDRESS 常常是空的，
            // 而 pw-record 必须靠它们连到用户会话的 PipeWire，所以按 uid 兜底。
            var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrWhiteSpace(runtimeDir))
            {
                runtimeDir = $"/run/user/{GetUid()}";
            }

            startInfo.Environment["XDG_RUNTIME_DIR"] = runtimeDir;
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            {
                startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={runtimeDir}/bus";
            }
            else
            {
                startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] =
                    Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            }

            process = Process.Start(startInfo);
            if (process is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            _logger.LogInformation("监控听现场流已开始（{Bitrate}k @{Rate}Hz）", bitrate, rate);
            using var stream = process.StandardOutput.BaseStream;
            var buffer = new byte[8 * 1024];
            while (!timeoutCts.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, timeoutCts.Token);
                if (read <= 0)
                {
                    break;
                }

                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token);
                await context.Response.Body.FlushAsync(timeoutCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端停止播放，正常收尾
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "监控听现场流中断（已推 {Seconds} 秒）", startedAt.Elapsed.Seconds);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(2000);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "结束听现场进程时出错。");
                }

                process.Dispose();
            }

            Interlocked.Exchange(ref _active, 0);
        }
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
}
