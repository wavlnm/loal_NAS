using System.Diagnostics;
using System.Net;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// Linux 防火墙自动放行。Tool=auto 时按 ufw → firewalld → iptables 顺序探测；
/// 也可在配置里强制指定 ufw/firewalld/iptables/none。需要 root 权限，失败时记录手动命令。
/// </summary>
public sealed class LinuxFirewallEnsurer(IOptions<FirewallOptions> options, ILogger<LinuxFirewallEnsurer> logger)
{
    private readonly FirewallOptions _options = options.Value;

    public async Task EnsureRulesForUrlsAsync(IEnumerable<string> boundUrls, CancellationToken ct = default)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            if (!_options.AutoConfigureOnStartup)
            {
                logger.LogInformation("防火墙自动放行已禁用。");
                return;
            }

            var ports = ExtractPublicPorts(boundUrls).ToArray();
            if (ports.Length == 0)
            {
                logger.LogInformation("未检测到对外监听端口，跳过防火墙放行。");
                return;
            }

            var tool = await ResolveToolAsync(ct);
            if (tool == "none")
            {
                logger.LogInformation("未检测到活动防火墙（或 Tool=none）。如需手动放行：{Ports}", string.Join(',', ports));
                return;
            }

            logger.LogInformation("使用 {Tool} 自动放行端口：{Ports}", tool, string.Join(',', ports));
            foreach (var port in ports)
            {
                await EnsurePortAsync(tool, port, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "防火墙自动放行过程中出错。");
        }
    }

    private async Task<string> ResolveToolAsync(CancellationToken ct)
    {
        var configured = (_options.Tool ?? "auto").Trim().ToLowerInvariant();
        if (configured != "auto")
        {
            return configured;
        }

        if (HasCommand("ufw") && await IsUfwActiveAsync(ct))
        {
            return "ufw";
        }

        if (HasCommand("firewall-cmd") && await IsFirewalldRunningAsync(ct))
        {
            return "firewalld";
        }

        if (HasCommand("iptables"))
        {
            return "iptables";
        }

        return "none";
    }

    private async Task EnsurePortAsync(string tool, int port, CancellationToken ct)
    {
        switch (tool)
        {
            case "ufw":
                await RunAndLog("ufw", ["allow", $"{port}/tcp"], ct);
                break;
            case "firewalld":
                await RunAndLog("firewall-cmd", ["--permanent", $"--add-port={port}/tcp"], ct);
                await RunAndLog("firewall-cmd", ["--reload"], ct);
                break;
            case "iptables":
                await EnsureIptablesRuleAsync("iptables", port, ct);
                if (HasCommand("ip6tables"))
                {
                    await EnsureIptablesRuleAsync("ip6tables", port, ct);
                }
                break;
            default:
                logger.LogWarning("未知防火墙工具 {Tool}，跳过端口 {Port}。", tool, port);
                break;
        }
    }

    /// <summary>先 -C 检查规则是否已存在，避免每次重启重复插入。</summary>
    private async Task EnsureIptablesRuleAsync(string bin, int port, CancellationToken ct)
    {
        var check = await RunAsync(bin, ["-C", "INPUT", "-p", "tcp", "--dport", port.ToString(), "-j", "ACCEPT"], ct);
        if (check.Exit == 0)
        {
            logger.LogInformation("{Bin} 已存在放行规则，跳过端口 {Port}。", bin, port);
            return;
        }

        await RunAndLog(bin, ["-I", "INPUT", "-p", "tcp", "--dport", port.ToString(), "-j", "ACCEPT"], ct);
    }

    private async Task RunAndLog(string file, string[] args, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(file, args, ct);
        var display = $"{file} {string.Join(' ', args)}";
        if (exit == 0)
        {
            logger.LogInformation("放行成功：{Cmd}", display);
        }
        else
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            logger.LogWarning("放行失败（exit={Exit}）：{Cmd}；{Detail}。若因权限不足，请用 root 运行服务，或手动执行：sudo {Cmd}",
                exit, display, detail, display);
        }
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string file, string[] args, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, stdout.Trim(), stderr.Trim());
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private async Task<bool> IsUfwActiveAsync(CancellationToken ct)
    {
        var (exit, stdout, _) = await RunAsync("ufw", ["status"], ct);
        return exit == 0 && stdout.Contains("Status: active", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsFirewalldRunningAsync(CancellationToken ct)
    {
        var (exit, stdout, _) = await RunAsync("firewall-cmd", ["--state"], ct);
        return exit == 0 && stdout.Contains("running", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCommand(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .Any(File.Exists);
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
}
