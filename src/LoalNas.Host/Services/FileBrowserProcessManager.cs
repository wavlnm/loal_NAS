using System.Diagnostics;
using System.Globalization;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

public sealed class FileBrowserProcessManager(
    IOptions<FileBrowserOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<FileBrowserProcessManager> logger) : IHostedService, IDisposable
{
    private readonly FileBrowserOptions _options = options.Value;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private Process? _process;
    private string? _runtimeDirectory;
    private string? _rootDirectory;
    private string? _databasePath;
    private string? _executablePath;

    public Uri BaseAddress => new($"http://{_options.Address}:{_options.Port}/");

    public bool IsRunning => _process is { HasExited: false };

    public string SharedRootPath => _rootDirectory ??= Path.Combine(ResolveRuntimeDirectory(), _options.RootFolderName);

    public Task StartAsync(CancellationToken cancellationToken) => EnsureRunningAsync(cancellationToken);

    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        Process? processToWait;

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning)
            {
                return;
            }

            var runtimeDirectory = ResolveRuntimeDirectory();
            var rootDirectory = SharedRootPath;
            var databasePath = ResolveDatabasePath();
            var executablePath = ResolveExecutablePath();

            Directory.CreateDirectory(runtimeDirectory);
            Directory.CreateDirectory(rootDirectory);

            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException($"FileBrowser executable was not found: {executablePath}");
            }

            logger.LogInformation("Starting FileBrowser. Root: {RootDirectory}; Database: {DatabasePath}", rootDirectory, databasePath);

            var process = new Process
            {
                StartInfo = BuildStartInfo(executablePath, runtimeDirectory, rootDirectory, databasePath),
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    logger.LogInformation("FileBrowser: {Message}", eventArgs.Data);
                }
            };

            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    logger.LogWarning("FileBrowser: {Message}", eventArgs.Data);
                }
            };

            process.Exited += (_, _) =>
            {
                logger.LogWarning("FileBrowser exited with code {ExitCode}", TryGetExitCode(process));
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start FileBrowser process.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            processToWait = process;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        await WaitUntilReadyAsync(processToWait, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is null)
            {
                return;
            }

            if (!_process.HasExited)
            {
                logger.LogInformation("Stopping FileBrowser.");
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(cancellationToken);
            }

            _process.Dispose();
            _process = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Dispose()
    {
        _process?.Dispose();
        _lifecycleLock.Dispose();
    }

    private ProcessStartInfo BuildStartInfo(string executablePath, string runtimeDirectory, string rootDirectory, string databasePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = runtimeDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("--address");
        startInfo.ArgumentList.Add(_options.Address);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(_options.Port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(rootDirectory);
        startInfo.ArgumentList.Add("--database");
        startInfo.ArgumentList.Add(databasePath);
        startInfo.ArgumentList.Add("--noauth");
        startInfo.ArgumentList.Add("--disableExec");

        return startInfo;
    }

    private async Task WaitUntilReadyAsync(Process process, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(FileBrowserApiProxy.HttpClientName);
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(_options.StartupTimeoutSeconds);

        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"FileBrowser exited before becoming ready. Exit code: {TryGetExitCode(process)}");
            }

            try
            {
                using var response = await client.GetAsync(BaseAddress, cancellationToken);
                if ((int)response.StatusCode < 500)
                {
                    logger.LogInformation("FileBrowser is ready at {Address}", BaseAddress);
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException($"FileBrowser did not become ready within {_options.StartupTimeoutSeconds} seconds.");
    }

    private string ResolveDatabasePath()
    {
        _databasePath ??= Path.Combine(ResolveRuntimeDirectory(), _options.DatabaseFileName);
        return _databasePath;
    }

    private string ResolveExecutablePath()
    {
        _executablePath ??= ResolvePathFromBaseDirectory(_options.RelativeExecutablePath);
        return _executablePath;
    }

    private string ResolveRuntimeDirectory()
    {
        _runtimeDirectory ??= ResolvePathFromBaseDirectory(_options.RuntimeDirectoryName);
        return _runtimeDirectory;
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

    private static string ResolvePathFromBaseDirectory(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }
}