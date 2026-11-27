using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// 审批桥：插件先询问每条 shell 命令；非敏感立即放行，敏感则挂起等待手机决定，
/// 超时/断线/中断一律拒绝。每个会话同时只挂一个审批。
/// </summary>
public sealed class AgentApprovalBroker
{
    public sealed record PendingApproval(
        string Id,
        string SessionId,
        string Command,
        string Cwd,
        string? Matched,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);

    private sealed class Pending
    {
        public required PendingApproval Info;
        public required TaskCompletionSource<bool> Completion;
        public readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
        public string? Pattern;
        public bool AutoAllowed;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<AgentApprovalBroker> _logger;
    private readonly IOptions<AgentOptions> _options;
    private readonly SensitiveCommandPolicy _policy;
    private readonly AgentPolicyStore _mode;
    private readonly AgentServeManager _serve;
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly object _auditLock = new();

    public AgentApprovalBroker(
        ILogger<AgentApprovalBroker> logger,
        IOptions<AgentOptions> options,
        SensitiveCommandPolicy policy,
        AgentPolicyStore mode,
        AgentServeManager serve)
    {
        _logger = logger;
        _options = options;
        _policy = policy;
        _mode = mode;
        _serve = serve;
    }

    public PendingApproval? GetPending(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        foreach (var pair in _pending)
        {
            if (pair.Value.Info.SessionId == sessionId)
            {
                if (pair.Value.Info.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    return null;
                }

                return pair.Value.Info;
            }
        }

        return null;
    }

    public async Task<(bool Allow, string Reason)> RequestAsync(
        string? command, string? cwd, CancellationToken cancellationToken)
    {
        var sessionId = _serve.CurrentSessionId;
        var verdict = _policy.Evaluate(command);

        // 完全自主模式：不再弹确认，所有命令直接放行。
        if (_mode.Autonomous)
        {
            await AuditAsync(
                sessionId, command, cwd, verdict.Sensitive, verdict.Pattern,
                verdict.Matched, verdict.Degraded, "allow", "autonomous", 0,
                "autonomous_mode").ConfigureAwait(false);
            return (true, "autonomous");
        }

        if (string.IsNullOrEmpty(sessionId))
        {
            await AuditAsync(null, command, cwd, true, "no_session", null, false,
                "deny", "bridge", 0, "no_active_session").ConfigureAwait(false);
            return (false, "no_active_session");
        }

        if (!verdict.Sensitive)
        {
            await AuditAsync(
                sessionId, command, cwd, false, null, null, false,
                "allow", "auto", 0).ConfigureAwait(false);
            return (true, "non_sensitive");
        }

        foreach (var pair in _pending)
        {
            if (pair.Value.Info.SessionId == sessionId)
            {
                return (false, "another_approval_pending");
            }
        }

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(Math.Max(10, _options.Value.ApprovalTimeoutSeconds));
        var info = new PendingApproval(
            id, sessionId, command ?? "", cwd ?? "", verdict.Matched,
            now, now.Add(timeout));
        var pending = new Pending
        {
            Info = info,
            Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            Pattern = verdict.Pattern,
        };

        if (!_pending.TryAdd(id, pending))
        {
            return (false, "approval_conflict");
        }

        _logger.LogInformation(
            "敏感命令等待审批：session={Session} command={Command}", sessionId, command);

        var allow = false;
        var source = "timeout";
        var reason = "approval_timeout";
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var completed = await Task.WhenAny(
                pending.Completion.Task,
                Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token)).ConfigureAwait(false);
            if (completed == pending.Completion.Task)
            {
                allow = await pending.Completion.Task.ConfigureAwait(false);
                source = pending.AutoAllowed ? "autonomous" : "phone";
                reason = allow ? "approved" : "rejected";
            }
            else
            {
                reason = "approval_timeout";
            }
        }
        catch (OperationCanceledException)
        {
            reason = "approval_cancelled";
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }

        var elapsed = (int)(DateTimeOffset.UtcNow - pending.StartedAt).TotalMilliseconds;
        await AuditAsync(
            sessionId, command, cwd, true, verdict.Pattern, verdict.Matched,
            verdict.Degraded, allow ? "allow" : "deny", source, elapsed, reason)
            .ConfigureAwait(false);
        return (allow, reason);
    }

    public bool Decide(
        string sessionId, string approvalId, bool allow, out string? error)
    {
        error = null;
        if (!_pending.TryGetValue(approvalId, out var pending) ||
            pending.Info.SessionId != sessionId)
        {
            error = "approval_not_found";
            return false;
        }

        if (pending.Info.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _pending.TryRemove(approvalId, out _);
            pending.Completion.TrySetResult(false);
            error = "approval_expired";
            return false;
        }

        pending.Completion.TrySetResult(allow);
        return true;
    }

    /// <summary>切到完全自主模式时，把已挂起的审批直接放行。</summary>
    public void AllowAllPending()
    {
        foreach (var pair in _pending)
        {
            pair.Value.AutoAllowed = true;
            pair.Value.Completion.TrySetResult(true);
        }
    }

    public void CancelSession(string sessionId, string reason)
    {
        foreach (var pair in _pending)
        {
            if (pair.Value.Info.SessionId == sessionId)
            {
                pair.Value.Completion.TrySetResult(false);
                _logger.LogInformation("取消会话 {Session} 的待审批命令：{Reason}", sessionId, reason);
            }
        }
    }

    private async Task AuditAsync(
        string? sessionId, string? command, string? cwd, bool sensitive, string? pattern,
        string? matched, bool degraded, string decision, string source, int elapsedMs,
        string? reason = null)
    {
        try
        {
            AgentPaths.EnsureDirectories();
            var entry = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow,
                sessionId,
                command,
                cwd,
                sensitive,
                pattern,
                matched,
                degraded,
                decision,
                source,
                elapsedMs,
                reason,
            }, JsonOpts);
            lock (_auditLock)
            {
                File.AppendAllText(AgentPaths.AuditFile, entry + Environment.NewLine, Encoding.UTF8);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        AgentPaths.AuditFile,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "写入 Agent 审计日志失败。");
        }
    }
}
