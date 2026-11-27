using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// 管理常驻的 opencode serve 进程：独立 HOME/XDG、Basic Auth、审批插件环境变量、
/// 健康等待与重启。会话/上下文压缩由 OpenCode 自己维护。
/// </summary>
public sealed class AgentServeManager : IHostedService, IDisposable
{
    private readonly ILogger<AgentServeManager> _logger;
    private readonly IOptions<AgentOptions> _options;
    private readonly IOptions<PublicHostOptions> _host;
    private readonly AgentModelConfigStore _modelStore;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private Process? _process;
    private string _password = "";
    private string _approvalToken = "";
    private string? _currentSessionId;
    private volatile string? _lastError;
    private StreamWriter? _logWriter;

    public AgentServeManager(
        ILogger<AgentServeManager> logger,
        IOptions<AgentOptions> options,
        IOptions<PublicHostOptions> host,
        AgentModelConfigStore modelStore)
    {
        _logger = logger;
        _options = options;
        _host = host;
        _modelStore = modelStore;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    private AgentOptions Options => _options.Value;

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public string? LastError => _lastError;

    public string ApprovalToken
    {
        get
        {
            lock (_stateLock)
            {
                return _approvalToken;
            }
        }
    }

    public string? CurrentSessionId
    {
        get
        {
            lock (_stateLock)
            {
                return _currentSessionId;
            }
        }
    }

    public void SetCurrentSession(string? sessionId)
    {
        lock (_stateLock)
        {
            _currentSessionId = sessionId;
        }
    }

    public string BaseUrl => $"http://127.0.0.1:{Math.Max(1, Options.ServePort)}";

    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            var config = _modelStore.Snapshot();
            if (config is null || string.IsNullOrWhiteSpace(config.ApiKey))
            {
                throw new AgentUnavailableException("model_not_configured",
                    "请先在手机端配置模型与 API Key。");
            }

            if (!_modelStore.WriteRuntimeFiles())
            {
                throw new AgentUnavailableException("config_failed", "生成 OpenCode 配置失败。");
            }

            var binary = ResolveBinary();
            if (!File.Exists(binary))
            {
                throw new AgentUnavailableException("binary_missing",
                    $"未找到 OpenCode 可执行文件：{binary}");
            }

            AgentPaths.EnsureDirectories();
            Directory.CreateDirectory(Options.Workspace);

            var password = RandomToken(24);
            var approvalToken = RandomToken(32);
            lock (_stateLock)
            {
                _password = password;
                _approvalToken = approvalToken;
                _currentSessionId = null;
            }

            var startInfo = new ProcessStartInfo(binary)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Options.Workspace,
            };
            startInfo.ArgumentList.Add("serve");
            startInfo.ArgumentList.Add("--hostname");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(Math.Max(1, Options.ServePort).ToString());

            startInfo.Environment["HOME"] = AgentPaths.HomeDirectory;
            startInfo.Environment["XDG_CONFIG_HOME"] = Path.Combine(AgentPaths.HomeDirectory, ".config");
            startInfo.Environment["XDG_DATA_HOME"] = Path.Combine(AgentPaths.HomeDirectory, ".local", "share");
            startInfo.Environment["XDG_STATE_HOME"] = Path.Combine(AgentPaths.HomeDirectory, ".local", "state");
            startInfo.Environment["XDG_CACHE_HOME"] = Path.Combine(AgentPaths.HomeDirectory, ".cache");
            startInfo.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
            startInfo.Environment["TERM"] = "dumb";
            startInfo.Environment["NO_COLOR"] = "1";
            startInfo.Environment["SHELL"] = "/bin/bash";
            startInfo.Environment["OPENCODE_DISABLE_AUTOUPDATE"] = "1";
            startInfo.Environment["OPENCODE_SERVER_PASSWORD"] = password;
            startInfo.Environment["NAS_AGENT_API_KEY"] = config.ApiKey!;
            startInfo.Environment["LOALNAS_AGENT_APPROVAL_URL"] =
                $"http://127.0.0.1:{ResolveAppPort()}/internal/agent/approvals";
            startInfo.Environment["LOALNAS_AGENT_APPROVAL_TOKEN"] = approvalToken;

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => LogLine("stdout", e.Data);
            process.ErrorDataReceived += (_, e) => LogLine("stderr", e.Data);
            process.Exited += (_, _) =>
                _logger.LogWarning("opencode serve 已退出，code={Code}", SafeExitCode(process));

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                process.Dispose();
                throw new AgentUnavailableException("start_failed", ex.Message);
            }

            lock (_stateLock)
            {
                _process = process;
                _lastError = null;
            }

            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, Options.ServeStartupTimeoutSeconds));
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    var code = SafeExitCode(process);
                    StopProcessLocked();
                    throw new AgentUnavailableException(
                        "start_failed", $"opencode serve 启动失败（exit {code}），请查看服务端日志。");
                }

                try
                {
                    using var request = CreateRequest(HttpMethod.Get, "/api/info");
                    using var response = await _http.SendAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("opencode serve 已就绪：{Url}", BaseUrl);
                        return;
                    }
                }
                catch
                {
                    // 尚未就绪
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            StopProcessLocked();
            throw new AgentUnavailableException("start_timeout", "等待 opencode serve 就绪超时。");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopProcessLocked();
        }
        finally
        {
            _gate.Release();
        }

        await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string pathAndQuery, string? jsonBody, CancellationToken cancellationToken)
    {
        var request = CreateRequest(method, pathAndQuery);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string pathAndQuery)
    {
        var request = new HttpRequestMessage(method, BaseUrl + pathAndQuery);
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{_password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        return request;
    }

    private string ResolveBinary()
    {
        var path = Options.BinaryPath;
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }

    private int ResolveAppPort()
    {
        if (Uri.TryCreate(_host.Value.Url, UriKind.Absolute, out var uri) && uri.Port > 0)
        {
            return uri.Port;
        }

        return 5034;
    }

    private void StopProcessLocked()
    {
        Process? process;
        lock (_stateLock)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "停止 opencode serve 时出错。");
        }
        finally
        {
            process.Dispose();
        }
    }

    private void LogLine(string stream, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (stream == "stderr")
        {
            _logger.LogInformation("opencode: {Line}", line);
        }
        else
        {
            _logger.LogDebug("opencode: {Line}", line);
        }

        try
        {
            _logWriter ??= CreateLogWriter();
            lock (_logWriter)
            {
                _logWriter.WriteLine($"{DateTimeOffset.Now:O} [{stream}] {line}");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private StreamWriter CreateLogWriter()
    {
        AgentPaths.EnsureDirectories();
        var path = Path.Combine(AgentPaths.LogsDirectory, "opencode.log");
        var writer = new StreamWriter(new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
        {
            AutoFlush = true,
        };
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return writer;
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private static string RandomToken(int bytes) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Options.Enabled || !_modelStore.IsConfigured)
        {
            return;
        }

        try
        {
            await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogWarning(ex, "启动 opencode serve 失败，将在首次使用 AI 时重试。");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopProcessLocked();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopProcessLocked();
        _http.Dispose();
        _gate.Dispose();
        _logWriter?.Dispose();
    }
}

public sealed class AgentUnavailableException : Exception
{
    public AgentUnavailableException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
