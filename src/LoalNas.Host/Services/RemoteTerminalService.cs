using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LoalNas.Host.Services;

public sealed record TerminalExecResult(
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool Truncated,
    bool TimedOut,
    long DurationMs);

/// <summary>
/// 手机端远程终端：默认关闭，只能在本地 /admin 面板开启（可带有效期，重启服务即失效）。
/// 每次执行都会写审计日志：共享目录下 .terminal-logs/terminal-yyyy-MM-dd.log；
/// 跨天后旧日志自动移入回收站，复用回收站 30 天保留策略。
/// </summary>
public sealed class RemoteTerminalService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const int MaxOutputChars = 64 * 1024;
    private const int MaxLoggedCommandChars = 2000;
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<RemoteTerminalService> _logger;
    private readonly TrashService _trash;
    private readonly string _shareRoot;
    private readonly string _logDirectory;
    private readonly SemaphoreSlim _execGate = new(1, 1);
    private readonly object _stateLock = new();

    private bool _enabled;
    private DateTimeOffset? _expiresAt;

    public RemoteTerminalService(
        FileBrowserProcessManager manager,
        TrashService trash,
        ILogger<RemoteTerminalService> logger)
    {
        _logger = logger;
        _trash = trash;
        _shareRoot = Path.GetFullPath(manager.SharedRootPath);
        _logDirectory = Path.Combine(_shareRoot, ".terminal-logs");
    }

    public bool Enabled
    {
        get
        {
            lock (_stateLock)
            {
                return _enabled && (_expiresAt is null || DateTimeOffset.Now < _expiresAt.Value);
            }
        }
    }

    public DateTimeOffset? ExpiresAt
    {
        get
        {
            lock (_stateLock)
            {
                return _enabled ? _expiresAt : null;
            }
        }
    }

    public void Enable(TimeSpan? duration)
    {
        lock (_stateLock)
        {
            _enabled = true;
            _expiresAt = duration is null ? null : DateTimeOffset.Now.Add(duration.Value);
        }
        _logger.LogInformation("远程终端已开启，有效期：{Duration}", duration?.ToString() ?? "永久");
    }

    public void Disable()
    {
        lock (_stateLock)
        {
            _enabled = false;
            _expiresAt = null;
        }
        _logger.LogInformation("远程终端已关闭。");
    }

    public async Task<TerminalExecResult> ExecuteAsync(
        string command,
        string? clientIp,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.StartNew();

        await _execGate.WaitAsync(cancellationToken);
        try
        {
            var workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                workingDirectory = _shareRoot;
            }

            var startInfo = new ProcessStartInfo("/bin/bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            };
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);
            startInfo.Environment["TERM"] = "dumb";

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var stdoutTruncated = false;
            var stderrTruncated = false;

            process.Start();

            var outTask = DrainAsync(process.StandardOutput, stdout, isStdout: true);
            var errTask = DrainAsync(process.StandardError, stderr, isStdout: false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ExecutionTimeout);

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "结束终端进程失败。");
                }

                try
                {
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch
                {
                    // 忽略
                }
            }

            try
            {
                var results = await Task.WhenAll(outTask, errTask);
                stdoutTruncated = results[0];
                stderrTruncated = results[1];
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取终端输出失败。");
            }

            var result = new TerminalExecResult(
                timedOut ? null : process.ExitCode,
                stdout.ToString(),
                stderr.ToString(),
                stdoutTruncated || stderrTruncated,
                timedOut,
                startedAt.ElapsedMilliseconds);

            WriteAudit(clientIp, command, result);
            return result;
        }
        finally
        {
            _execGate.Release();
        }
    }

    private static async Task<bool> DrainAsync(StreamReader reader, StringBuilder sink, bool isStdout)
    {
        var buffer = new char[8192];
        var truncated = false;

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory());
            }
            catch (Exception)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            if (sink.Length + read <= MaxOutputChars)
            {
                sink.Append(buffer, 0, read);
                continue;
            }

            truncated = true;
            var room = MaxOutputChars - sink.Length;
            if (room > 0)
            {
                sink.Append(buffer, 0, room);
            }

            // 超限后继续读完但丢弃，避免子进程阻塞在管道上
            sink.Append(isStdout ? "\n…（stdout 输出过长，已截断）" : "\n…（stderr 输出过长，已截断）");
            // 防止重复追加截断提示
            await reader.ReadToEndAsync();
            break;
        }

        return truncated;
    }

    private void WriteAudit(string? clientIp, string command, TerminalExecResult result)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);

            var fileName = $"terminal-{DateTimeOffset.Now:yyyy-MM-dd}.log";
            RotateOldLogs(fileName);

            var line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["ts"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                ["ip"] = clientIp ?? "-",
                ["command"] = command.Length > MaxLoggedCommandChars
                    ? command[..MaxLoggedCommandChars] + "…"
                    : command,
                ["exitCode"] = result.ExitCode,
                ["durationMs"] = result.DurationMs,
                ["truncated"] = result.Truncated,
                ["timedOut"] = result.TimedOut,
            }, JsonOpts);

            File.AppendAllText(Path.Combine(_logDirectory, fileName), line + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入终端审计日志失败。");
        }
    }

    /// <summary>跨天时把前几天的日志移入回收站，沿用 30 天保留。</summary>
    private void RotateOldLogs(string currentFileName)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "terminal-*.log"))
            {
                if (string.Equals(Path.GetFileName(file), currentFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                _trash.MoveToTrash($".terminal-logs/{Path.GetFileName(file)}", out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "滚动终端日志失败。");
        }
    }
}
