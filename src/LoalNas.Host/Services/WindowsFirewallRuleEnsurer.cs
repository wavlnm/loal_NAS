using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

public sealed class WindowsFirewallRuleEnsurer(IOptions<FirewallOptions> options, ILogger<WindowsFirewallRuleEnsurer> logger)
{
    private readonly FirewallOptions _options = options.Value;

    public async Task EnsureRulesForUrlsAsync(IEnumerable<string> boundUrls, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            if (!_options.AutoConfigureOnStartup)
            {
                logger.LogInformation("Windows firewall auto configuration is disabled.");
                return;
            }

            var publicPorts = ExtractPublicPorts(boundUrls).ToArray();
            if (publicPorts.Length == 0)
            {
                logger.LogInformation("No non-loopback host bindings detected. Firewall auto configuration skipped.");
                return;
            }

            foreach (var port in publicPorts)
            {
                if (await HasAllowRuleAsync(port, cancellationToken))
                {
                    logger.LogInformation("Windows firewall already allows inbound TCP {Port}.", port);
                    continue;
                }

                logger.LogWarning("Windows firewall does not allow inbound TCP {Port}. Attempting to run the firewall helper script.", port);

                var scriptStarted = await TryRunScriptAsync(port, cancellationToken);
                if (!scriptStarted)
                {
                    continue;
                }

                if (await HasAllowRuleAsync(port, cancellationToken))
                {
                    logger.LogInformation("Windows firewall rule is now present for inbound TCP {Port}.", port);
                }
                else
                {
                    logger.LogWarning("The firewall helper script completed, but no allow rule for inbound TCP {Port} was detected afterwards.", port);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed while ensuring Windows firewall rules on startup.");
        }
    }

    private async Task<bool> HasAllowRuleAsync(int port, CancellationToken cancellationToken)
    {
        var command = string.Join(' ',
            "$port = " + port + ";",
            "$exists = @(Get-NetFirewallRule -Enabled True -Direction Inbound -Action Allow -ErrorAction SilentlyContinue |",
            "Get-NetFirewallPortFilter |",
            "Where-Object { $_.Protocol -eq 'TCP' -and ($_.LocalPort -eq 'Any' -or $_.LocalPort -eq $port.ToString()) }).Count -gt 0;",
            "if ($exists) { 'true' } else { 'false' }");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        await process.WaitForExitAsync(cancellationToken);

        var output = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
        var error = (await process.StandardError.ReadToEndAsync(cancellationToken)).Trim();

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
        {
            logger.LogWarning("Failed to inspect Windows firewall rules for port {Port}: {Error}", port, error);
        }

        return string.Equals(output, "true", StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private async Task<bool> TryRunScriptAsync(int port, CancellationToken cancellationToken)
    {
        var scriptPath = ResolvePathFromBaseDirectory(_options.RelativeScriptPath);
        if (!File.Exists(scriptPath))
        {
            logger.LogWarning("Firewall helper script was not found: {ScriptPath}", scriptPath);
            return false;
        }

        var ruleName = $"loal_NAS Host TCP {port}";
        var profiles = _options.Profiles.Length == 0 ? ["Private"] : _options.Profiles;

        try
        {
            if (IsProcessElevated())
            {
                return await RunScriptDirectlyAsync(scriptPath, port, ruleName, profiles, cancellationToken);
            }

            logger.LogWarning("The app is not running as administrator. Requesting elevation to open Windows firewall port {Port}.", port);
            return await RunScriptElevatedAsync(scriptPath, port, ruleName, profiles, cancellationToken);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            logger.LogWarning("Firewall elevation prompt was canceled. Inbound TCP {Port} remains blocked until the script is approved.", port);
            return false;
        }
    }

    private async Task<bool> RunScriptDirectlyAsync(string scriptPath, int port, string ruleName, string[] profiles, CancellationToken cancellationToken)
    {
        var startInfo = BuildScriptStartInfo(scriptPath, port, ruleName, profiles);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.EnsureTimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
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
            }

            logger.LogWarning("Firewall helper script timed out after {Seconds} seconds.", _options.EnsureTimeoutSeconds);
            return false;
        }

        var output = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
        var error = (await process.StandardError.ReadToEndAsync(cancellationToken)).Trim();

        if (!string.IsNullOrWhiteSpace(output))
        {
            logger.LogInformation("Firewall script output: {Output}", output.Replace(Environment.NewLine, " | "));
        }

        if (process.ExitCode != 0)
        {
            logger.LogWarning("Firewall helper script exited with code {ExitCode}. {Error}", process.ExitCode, error);
            return false;
        }

        return true;
    }

    private async Task<bool> RunScriptElevatedAsync(string scriptPath, int port, string ruleName, string[] profiles, CancellationToken cancellationToken)
    {
        var startInfo = BuildScriptStartInfo(scriptPath, port, ruleName, profiles);
        startInfo.Verb = "runas";
        startInfo.UseShellExecute = true;

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.EnsureTimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Elevated firewall helper did not finish within {Seconds} seconds.", _options.EnsureTimeoutSeconds);
            return false;
        }

        return process.ExitCode == 0;
    }

    private static IEnumerable<int> ExtractPublicPorts(IEnumerable<string> boundUrls)
    {
        return boundUrls
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>()
            .Where(NeedsFirewallRule)
            .Select(uri => uri.Port)
            .Distinct()
            .OrderBy(port => port);
    }

    private static bool NeedsFirewallRule(Uri uri)
    {
        var host = uri.Host.Trim('[', ']');

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return true;
        }

        return !IPAddress.IsLoopback(address);
    }

    private static ProcessStartInfo BuildScriptStartInfo(string scriptPath, int port, string ruleName, string[] profiles)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe"
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-Port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("-RuleName");
        startInfo.ArgumentList.Add(ruleName);
        startInfo.ArgumentList.Add("-Profiles");

        foreach (var profile in profiles)
        {
            startInfo.ArgumentList.Add(profile);
        }

        return startInfo;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsProcessElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string ResolvePathFromBaseDirectory(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }
}