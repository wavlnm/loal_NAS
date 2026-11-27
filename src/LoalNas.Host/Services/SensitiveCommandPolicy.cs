using System.Text.RegularExpressions;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// 敏感 bash 命令判定。正则只用于"是否弹确认"，不是安全沙箱；
/// 匹配超时按敏感处理（fail-closed）。
/// </summary>
public sealed class SensitiveCommandPolicy
{
    public sealed record Verdict(bool Sensitive, string? Pattern, string? Matched, bool Degraded);

    private static readonly string[] FallbackPatterns =
    [
        // rm 同时带递归/强制（-rf/-fr/-r -f/--recursive --force）
        @"\brm\s+(?:-[a-zA-Z]*r[a-zA-Z]*f|-[a-zA-Z]*f[a-zA-Z]*r|-[a-zA-Z]*r\s+-[a-zA-Z]*f|-[a-zA-Z]*f\s+-[a-zA-Z]*r|--recursive\s+--force|--force\s+--recursive)",
        @"\b(sudo|su)\b",
        @"\b(curl|wget|nc|ncat|netcat|ssh|scp|sftp|rsync|telnet|ftp)\b",
        @"\b(dd|mkfs(\.\w+)?|shutdown|reboot|poweroff|halt)\b",
        @"(device-secret|model-config\.json|\.local/share/loal-nas)",
    ];

    private readonly ILogger<SensitiveCommandPolicy> _logger;
    private readonly IReadOnlyList<(string Source, Regex Regex)> _patterns;

    public SensitiveCommandPolicy(
        IOptions<AgentOptions> options, ILogger<SensitiveCommandPolicy> logger)
    {
        _logger = logger;
        var configured = options.Value.SensitivePatterns;
        var source = configured.Count > 0 ? configured : FallbackPatterns.ToList();
        var compiled = new List<(string, Regex)>();
        foreach (var pattern in source)
        {
            try
            {
                compiled.Add((pattern, new Regex(
                    pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled,
                    TimeSpan.FromMilliseconds(100))));
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "忽略无效的敏感命令正则：{Pattern}", pattern);
            }
        }

        _patterns = compiled;
    }

    public Verdict Evaluate(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new Verdict(true, "empty", null, false);
        }

        foreach (var (source, regex) in _patterns)
        {
            try
            {
                var match = regex.Match(command);
                if (match.Success)
                {
                    return new Verdict(true, source, match.Value, false);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return new Verdict(true, source, null, true);
            }
        }

        return new Verdict(false, null, null, false);
    }
}
