using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// 管理每个应用的子进程：按需启动、分配 127.0.0.1 随机端口、健康检查、转发控制指令、停止进程。
/// 应用崩溃不会自动重启；房间注册表会随进程退出一起清空。
/// </summary>
public sealed class AppProcessManager : IHostedService, IDisposable
{
    public const string ControlClientName = "app-control";
    public const string ControlHeaderName = "X-App-Control";

    private readonly AppRegistry _registry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<AppsOptions> _options;
    private readonly ILogger<AppProcessManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<string, AppRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _runtimesLock = new();

    public AppProcessManager(
        AppRegistry registry,
        IHttpClientFactory httpClientFactory,
        IOptions<AppsOptions> options,
        ILogger<AppProcessManager> logger,
        ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>进程意外退出（不是平台主动停止）时触发，参数为应用 ID。</summary>
    public event Action<string>? ProcessExited;

    private AppsOptions Options => _options.Value;

    public bool IsRunning(AppManifest manifest) => GetRuntime(manifest).IsRunning;

    public bool IsRunning(string appId)
    {
        var manifest = _registry.Find(appId);
        return manifest is not null && IsRunning(manifest);
    }

    public Uri? GetRunningBaseAddress(AppManifest manifest)
    {
        var runtime = GetRuntime(manifest);
        return runtime.IsRunning ? runtime.BaseAddress : null;
    }

    public Task EnsureRunningAsync(AppManifest manifest, CancellationToken cancellationToken)
    {
        var runtime = GetRuntime(manifest);
        return EnsureRunningCoreAsync(runtime, cancellationToken);
    }

    /// <summary>向应用控制接口发请求；调用前确保进程已启动。</summary>
    public async Task<(HttpStatusCode Status, string Body)> ControlAsync(
        AppManifest manifest,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        await EnsureRunningAsync(manifest, cancellationToken);
        return await SendControlAsync(manifest, method, path, body, cancellationToken)
            ?? throw new AppStartException("应用进程未运行。");
    }

    /// <summary>仅当进程已在运行时发送控制请求；不会因为清理动作而重新拉起进程。</summary>
    public Task<(HttpStatusCode Status, string Body)?> TryControlIfRunningAsync(
        AppManifest manifest,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        return SendControlAsync(manifest, method, path, body, cancellationToken);
    }

    private async Task<(HttpStatusCode Status, string Body)?> SendControlAsync(
        AppManifest manifest,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var runtime = GetRuntime(manifest);
        if (!runtime.IsRunning)
        {
            return null;
        }

        using var request = new HttpRequestMessage(
            method,
            new Uri(runtime.BaseAddress, path.TrimStart('/')));
        request.Headers.TryAddWithoutValidation(ControlHeaderName, runtime.ControlToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _httpClientFactory.CreateClient(ControlClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response.StatusCode, text);
    }

    public async Task StopAppAsync(AppManifest manifest, bool graceful, CancellationToken cancellationToken)
    {
        var runtime = GetRuntime(manifest);
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            await StopLockedAsync(runtime, graceful, cancellationToken);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<AppRuntime> runtimes;
        lock (_runtimesLock)
        {
            runtimes = _runtimes.Values.ToList();
        }

        foreach (var runtime in runtimes)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                await runtime.Gate.WaitAsync(timeout.Token);
                try
                {
                    await StopLockedAsync(runtime, graceful: false, timeout.Token);
                }
                finally
                {
                    runtime.Gate.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "停止应用 {Id} 时出错。", runtime.Manifest.Id);
            }
        }
    }

    public void Dispose()
    {
        List<AppRuntime> runtimes;
        lock (_runtimesLock)
        {
            runtimes = _runtimes.Values.ToList();
        }

        foreach (var runtime in runtimes)
        {
            try
            {
                runtime.Process?.Kill(entireProcessTree: true);
                runtime.Process?.Dispose();
            }
            catch
            {
                // 忽略退出阶段的清理错误
            }
        }
    }

    private async Task EnsureRunningCoreAsync(AppRuntime runtime, CancellationToken cancellationToken)
    {
        if (runtime.IsRunning)
        {
            return;
        }

        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            if (runtime.IsRunning)
            {
                return;
            }

            if (runtime.Process is not null)
            {
                runtime.Process.Dispose();
                runtime.Process = null;
            }

            var manifest = runtime.Manifest;
            Directory.CreateDirectory(manifest.DataDirectory);

            var port = GetFreePort();
            var controlToken = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

            var startInfo = BuildStartInfo(manifest, port, controlToken);
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };

            var appLogger = _loggerFactory.CreateLogger($"LoalNas.App.{manifest.Id}");
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    appLogger.LogInformation("{Line}", args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    appLogger.LogWarning("{Line}", args.Data);
                }
            };
            process.Exited += (_, _) => OnProcessExited(runtime, process);

            _logger.LogInformation(
                "启动应用 {Id}：{Command}（端口 {Port}）",
                manifest.Id, startInfo.FileName, port);

            try
            {
                if (!process.Start())
                {
                    throw new AppStartException("Process.Start 返回 false。");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                runtime.Process = process;
                runtime.Port = port;
                runtime.ControlToken = controlToken;
                runtime.StartedAt = DateTimeOffset.UtcNow;
                runtime.LastError = null;
                runtime.Stopping = false;

                await WaitUntilReadyAsync(runtime, process, cancellationToken);
            }
            catch (Exception ex)
            {
                runtime.LastError = ex.Message;
                runtime.Process = null;
                runtime.Port = 0;
                runtime.ControlToken = string.Empty;
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
                catch
                {
                    // 忽略清理错误
                }
                finally
                {
                    process.Dispose();
                }

                _logger.LogWarning(ex, "应用 {Id} 启动失败。", manifest.Id);
                throw ex as AppStartException ?? new AppStartException(
                    $"应用 {manifest.Id} 启动失败：{ex.Message}", ex);
            }
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private async Task WaitUntilReadyAsync(
        AppRuntime runtime, Process process, CancellationToken cancellationToken)
    {
        var manifest = runtime.Manifest;
        var client = _httpClientFactory.CreateClient(ControlClientName);
        var healthUri = new Uri(runtime.BaseAddress, manifest.HealthPath.TrimStart('/'));
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(manifest.StartupTimeoutSeconds);

        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new AppStartException(
                    $"应用在就绪前退出，退出码 {TryGetExitCode(process)}。");
            }

            try
            {
                using var response = await client.GetAsync(healthUri, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("应用 {Id} 已就绪：{Address}", manifest.Id, runtime.BaseAddress);
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // 端口尚未监听
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 单次健康检查超时，继续重试
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }

        throw new AppStartException(
            $"应用 {manifest.Id} 在 {manifest.StartupTimeoutSeconds} 秒内未就绪。");
    }

    private void OnProcessExited(AppRuntime runtime, Process process)
    {
        if (!ReferenceEquals(runtime.Process, process))
        {
            // 旧进程的迟到事件（平台已清理该运行时）
            return;
        }

        var exitCode = TryGetExitCode(process);
        if (runtime.Stopping)
        {
            _logger.LogInformation("应用 {Id} 已退出（退出码 {ExitCode}）。", runtime.Manifest.Id, exitCode);
            return;
        }

        runtime.LastError = $"进程退出（{exitCode}）";
        _logger.LogWarning("应用 {Id} 意外退出（退出码 {ExitCode}）。", runtime.Manifest.Id, exitCode);

        try
        {
            ProcessExited?.Invoke(runtime.Manifest.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理应用退出事件失败。");
        }
    }

    private async Task StopLockedAsync(
        AppRuntime runtime, bool graceful, CancellationToken cancellationToken)
    {
        var process = runtime.Process;
        if (process is null)
        {
            return;
        }

        runtime.Stopping = true;
        try
        {
            if (!process.HasExited)
            {
                if (graceful)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(
                            HttpMethod.Post,
                            new Uri(runtime.BaseAddress, "shutdown"));
                        request.Headers.TryAddWithoutValidation(ControlHeaderName, runtime.ControlToken);
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(2));
                        using var response = await _httpClientFactory.CreateClient(ControlClientName)
                            .SendAsync(request, cts.Token);
                    }
                    catch
                    {
                        // 应用可能已经退出或控制接口不可用，继续走强杀
                    }

                    try
                    {
                        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        waitCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Options.StopGraceSeconds)));
                        await process.WaitForExitAsync(waitCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // 优雅退出超时，强杀
                    }
                }

                if (!process.HasExited)
                {
                    _logger.LogInformation("强制结束应用 {Id} 进程。", runtime.Manifest.Id);
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        finally
        {
            process.Dispose();
            runtime.Process = null;
            runtime.Port = 0;
            runtime.ControlToken = string.Empty;
            runtime.Stopping = false;
        }
    }

    private ProcessStartInfo BuildStartInfo(
        AppManifest manifest, int port, string controlToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCommand(manifest.Command, manifest.AppDirectory),
            WorkingDirectory = ResolveWorkingDirectory(manifest, port, controlToken),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in manifest.Args)
        {
            startInfo.ArgumentList.Add(Expand(argument, manifest, port, controlToken));
        }

        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        return startInfo;
    }

    private static string ResolveWorkingDirectory(
        AppManifest manifest, int port, string controlToken)
    {
        var configured = Expand(manifest.WorkingDirectory, manifest, port, controlToken);
        return string.IsNullOrWhiteSpace(configured) ? manifest.AppDirectory : configured;
    }

    private static string ResolveCommand(string command, string appDirectory)
    {
        if (Path.IsPathRooted(command))
        {
            return command;
        }

        if (command.Contains('/') || command.Contains('\\') || command.StartsWith('.'))
        {
            return Path.GetFullPath(Path.Combine(appDirectory, command));
        }

        // 交给 PATH 解析（dotnet、node 等）
        return command;
    }

    private static string Expand(
        string value, AppManifest manifest, int port, string controlToken)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value
            .Replace("{port}", port.ToString(), StringComparison.Ordinal)
            .Replace("{basePath}", manifest.PublicPrefix, StringComparison.Ordinal)
            .Replace("{controlToken}", controlToken, StringComparison.Ordinal)
            .Replace("{appDir}", manifest.AppDirectory, StringComparison.Ordinal)
            .Replace("{dataDir}", manifest.DataDirectory, StringComparison.Ordinal);
    }

    private AppRuntime GetRuntime(AppManifest manifest)
    {
        lock (_runtimesLock)
        {
            if (!_runtimes.TryGetValue(manifest.Id, out var runtime))
            {
                runtime = new AppRuntime(manifest);
                _runtimes[manifest.Id] = runtime;
            }

            return runtime;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    internal sealed class AppRuntime(AppManifest manifest)
    {
        public AppManifest Manifest { get; } = manifest;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Process? Process { get; set; }
        public int Port { get; set; }
        public string ControlToken { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public string? LastError { get; set; }
        public bool Stopping { get; set; }

        public bool IsRunning => Process is { HasExited: false };

        public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");
    }
}

public sealed class AppStartException : Exception
{
    public AppStartException(string message) : base(message)
    {
    }

    public AppStartException(string message, Exception inner) : base(message, inner)
    {
    }
}
