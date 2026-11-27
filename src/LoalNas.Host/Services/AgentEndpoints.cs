using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>AI 助手的 HTTP 端点：会话/模型/审批（HMAC）+ 插件审批桥（loopback+bearer）。</summary>
public static class AgentEndpoints
{
    private sealed record CreateSessionRequest(string? Title);
    private sealed record PromptRequest(string? Text);
    private sealed record ApprovalDecisionRequest(string? Decision);
    private sealed record ModelUpdateRequest(
        string? ProviderId, string? BaseUrl, string? Model, string? ApiKey, bool ClearKey);
    private sealed record PluginApprovalRequest(string? Command, string? Cwd, string? Kind);
    private sealed record AgentModeRequest(bool? Autonomous);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void MapAgentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/agent/sessions", async (
            HttpContext context, AgentServeManager serve, AgentSessionProxy sessions,
            IOptions<AgentOptions> options, CancellationToken ct) =>
        {
            if (!await TryEnsureRunningAsync(serve, ct))
            {
                return AgentUnavailable(serve);
            }

            try
            {
                var active = await GetActiveAsync(sessions, ct);
                var root = await sessions.ListSessionsAsync(
                    options.Value.SessionListLimit, ct);
                var items = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                    ? data.EnumerateArray().Select(item => MapSession(item, active)).ToArray()
                    : Array.Empty<object>();
                return Results.Ok(new { sessions = items });
            }
            catch (AgentUnavailableException ex)
            {
                return AgentError(ex);
            }
        });

        app.MapPost("/api/agent/sessions", async (
            HttpContext context, AgentServeManager serve, AgentSessionProxy sessions,
            CancellationToken ct) =>
        {
            var request = await ReadJsonAsync<CreateSessionRequest>(context, ct);
            if (request is null)
            {
                return BadRequest("invalid_request");
            }

            if (!await TryEnsureRunningAsync(serve, ct))
            {
                return AgentUnavailable(serve);
            }

            try
            {
                var root = await sessions.CreateSessionAsync(request.Title, ct);
                if (!root.TryGetProperty("data", out var data))
                {
                    return Results.Json(new { error = "opencode_error" }, statusCode: 502);
                }

                var id = data.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                var title = data.TryGetProperty("title", out var titleValue)
                    ? titleValue.GetString()
                    : null;
                return Results.Ok(new { id, title = string.IsNullOrWhiteSpace(title) ? "新对话" : title });
            }
            catch (AgentUnavailableException ex)
            {
                return AgentError(ex);
            }
        });

        app.MapGet("/api/agent/sessions/{sessionId}", async (
            string sessionId, AgentServeManager serve, AgentSessionProxy sessions,
            AgentApprovalBroker approvals, IOptions<AgentOptions> options,
            CancellationToken ct) =>
        {
            var id = AgentSessionProxy.ValidateSessionId(sessionId);
            if (id is null)
            {
                return BadRequest("invalid_session");
            }

            if (!await TryEnsureRunningAsync(serve, ct))
            {
                return AgentUnavailable(serve);
            }

            try
            {
                var active = await GetActiveAsync(sessions, ct);
                var info = await sessions.GetSessionAsync(id, ct);
                var messages = await sessions.GetMessagesAsync(
                    id, options.Value.MessageListLimit, ct);
                return Results.Ok(new
                {
                    session = MapSession(
                        info.TryGetProperty("data", out var data) ? data : info, active),
                    messages = MapMessages(messages),
                    pendingApproval = approvals.GetPending(id),
                });
            }
            catch (AgentUnavailableException ex)
            {
                return AgentError(ex);
            }
        });

        app.MapPost("/api/agent/sessions/{sessionId}/messages", async (
            string sessionId, HttpContext context, AgentServeManager serve,
            AgentSessionProxy sessions, IOptions<AgentOptions> options, CancellationToken ct) =>
        {
            var id = AgentSessionProxy.ValidateSessionId(sessionId);
            if (id is null)
            {
                return BadRequest("invalid_session");
            }

            var request = await ReadJsonAsync<PromptRequest>(context, ct);
            var text = request?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return BadRequest("invalid_request");
            }

            if (text.Length > Math.Max(100, options.Value.MaxPromptChars))
            {
                return Results.Json(new { error = "prompt_too_long" }, statusCode: 413);
            }

            if (!await TryEnsureRunningAsync(serve, ct))
            {
                return AgentUnavailable(serve);
            }

            try
            {
                // 单活动会话：避免两个会话同时运行时，审批归属无法区分。
                var active = await GetActiveAsync(sessions, ct);
                if (active.Count > 0)
                {
                    return Results.Json(new { error = "session_busy" }, statusCode: 409);
                }

                // 旧会话可能记录着只读的 plan agent，发消息前统一切到可编辑的 build。
                await sessions.SetAgentAsync(id, "build", ct);
                serve.SetCurrentSession(id);
                await sessions.PromptAsync(id, text, ct);
                return Results.Json(new { accepted = true }, statusCode: 202);
            }
            catch (AgentUnavailableException ex)
            {
                return AgentError(ex);
            }
        });

        app.MapPost("/api/agent/sessions/{sessionId}/interrupt", async (
            string sessionId, AgentServeManager serve, AgentSessionProxy sessions,
            AgentApprovalBroker approvals, CancellationToken ct) =>
        {
            var id = AgentSessionProxy.ValidateSessionId(sessionId);
            if (id is null)
            {
                return BadRequest("invalid_session");
            }

            if (!await TryEnsureRunningAsync(serve, ct))
            {
                return AgentUnavailable(serve);
            }

            try
            {
                approvals.CancelSession(id, "user_interrupt");
                await sessions.InterruptAsync(id, ct);
                return Results.Ok(new { interrupted = true });
            }
            catch (AgentUnavailableException ex)
            {
                return AgentError(ex);
            }
        });

        app.MapPost("/api/agent/sessions/{sessionId}/approvals/{approvalId}", async (
            string sessionId, string approvalId, HttpContext context,
            AgentApprovalBroker approvals, CancellationToken ct) =>
        {
            var id = AgentSessionProxy.ValidateSessionId(sessionId);
            if (id is null)
            {
                return BadRequest("invalid_session");
            }

            var decision = context.Request.Query["decision"].FirstOrDefault();
            if (string.IsNullOrEmpty(decision))
            {
                var request = await ReadJsonAsync<ApprovalDecisionRequest>(context, ct);
                decision = request?.Decision;
            }

            var allow = string.Equals(decision, "allow", StringComparison.OrdinalIgnoreCase);
            var deny = string.Equals(decision, "deny", StringComparison.OrdinalIgnoreCase);
            if (!allow && !deny)
            {
                return BadRequest("invalid_decision");
            }

            return approvals.Decide(id, approvalId, allow, out var error)
                ? Results.Ok(new { decided = true, allow })
                : Results.Json(new { error = error ?? "approval_failed" }, statusCode: 404);
        });

        app.MapGet("/api/agent/model", (AgentModelConfigStore store) =>
            Results.Ok(store.View()));

        app.MapPut("/api/agent/model", async (
            HttpContext context, AgentModelConfigStore store, AgentServeManager serve,
            AgentSessionProxy sessions, CancellationToken ct) =>
        {
            var request = await ReadJsonAsync<ModelUpdateRequest>(context, ct);
            if (request is null)
            {
                return BadRequest("invalid_request");
            }

            if (serve.IsRunning)
            {
                try
                {
                    var active = await GetActiveAsync(sessions, ct);
                    if (active.Count > 0)
                    {
                        return Results.Json(
                            new { error = "session_busy" }, statusCode: 409);
                    }
                }
                catch
                {
                    // 无法确认时允许保存
                }
            }

            if (!store.Save(
                    request.ProviderId, request.BaseUrl, request.Model,
                    request.ApiKey, request.ClearKey, out var error))
            {
                return Results.Json(new { error = error ?? "save_failed" }, statusCode: 400);
            }

            if (serve.IsRunning)
            {
                try
                {
                    await serve.RestartAsync(ct);
                }
                catch (Exception ex)
                {
                    return Results.Json(new
                    {
                        error = "agent_restart_failed",
                        detail = ex.Message,
                        model = store.View(),
                    }, statusCode: 502);
                }
            }

            return Results.Ok(store.View());
        });

        app.MapGet("/api/agent/mode", (AgentPolicyStore mode) =>
            Results.Ok(new { autonomous = mode.Autonomous }));

        app.MapPut("/api/agent/mode", async (
            HttpContext context, AgentPolicyStore mode, AgentApprovalBroker approvals,
            CancellationToken ct) =>
        {
            var request = await ReadJsonAsync<AgentModeRequest>(context, ct);
            if (request?.Autonomous is not bool autonomous)
            {
                return BadRequest("invalid_request");
            }

            mode.Set(autonomous);
            if (autonomous)
            {
                approvals.AllowAllPending();
            }

            return Results.Ok(new { autonomous = mode.Autonomous });
        });

        app.MapPost("/api/agent/model/test", async (
            AgentModelConfigStore store, CancellationToken ct) =>
        {
            var config = store.Snapshot();
            if (config is null || string.IsNullOrWhiteSpace(config.ApiKey))
            {
                return Results.Json(new { error = "model_not_configured" }, statusCode: 409);
            }

            var started = DateTimeOffset.UtcNow;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiKey);
            var baseUrl = config.BaseUrl.TrimEnd('/');

            try
            {
                using var models = await client.GetAsync($"{baseUrl}/models", ct);
                if (models.IsSuccessStatusCode)
                {
                    return Results.Ok(new
                    {
                        ok = true,
                        latencyMs = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                        model = config.Model,
                    });
                }

                var body = JsonSerializer.Serialize(new
                {
                    model = config.Model,
                    messages = new[] { new { role = "user", content = "ping" } },
                    max_tokens = 1,
                    stream = false,
                });
                using var chat = await client.PostAsync(
                    $"{baseUrl}/chat/completions",
                    new StringContent(body, Encoding.UTF8, "application/json"), ct);
                if (chat.IsSuccessStatusCode)
                {
                    return Results.Ok(new
                    {
                        ok = true,
                        latencyMs = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                        model = config.Model,
                    });
                }

                var detail = await chat.Content.ReadAsStringAsync(ct);
                return Results.Json(new
                {
                    error = "provider_error",
                    detail = detail.Length > 400 ? detail[..400] : detail,
                }, statusCode: 502);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    error = "provider_error",
                    detail = ex.Message,
                }, statusCode: 502);
            }
        });

        app.MapPost("/internal/agent/approvals", async (
            HttpContext context, AgentServeManager serve, AgentApprovalBroker approvals,
            CancellationToken ct) =>
        {
            if (!Program.IsLoopback(context) ||
                context.Request.Headers.ContainsKey("X-Forwarded-For") ||
                context.Request.Headers.ContainsKey("X-Real-IP"))
            {
                return Results.Json(new { decision = "deny", reason = "forbidden" },
                    statusCode: 403);
            }

            var token = ReadBearer(context.Request.Headers.Authorization.ToString());
            var expected = serve.ApprovalToken;
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(expected) ||
                !FixedTimeEquals(token, expected))
            {
                return Results.Json(new { decision = "deny", reason = "unauthorized" },
                    statusCode: 401);
            }

            var request = await ReadJsonAsync<PluginApprovalRequest>(context, ct);
            if (request is null)
            {
                return Results.Json(new { decision = "deny", reason = "invalid_request" },
                    statusCode: 400);
            }

            var (allow, reason) = await approvals.RequestAsync(
                request.Command, request.Cwd, ct);
            return Results.Ok(new { decision = allow ? "allow" : "deny", reason });
        });
    }

    private static async Task<bool> TryEnsureRunningAsync(
        AgentServeManager serve, CancellationToken ct)
    {
        try
        {
            await serve.EnsureRunningAsync(ct);
            return true;
        }
        catch (AgentUnavailableException)
        {
            return false;
        }
    }

    private static IResult AgentUnavailable(AgentServeManager serve) =>
        Results.Json(new
        {
            error = "agent_unavailable",
            detail = serve.LastError ?? "AI 服务未就绪。",
        }, statusCode: 503);

    private static IResult AgentError(AgentUnavailableException ex)
    {
        var status = ex.Code switch
        {
            "model_not_configured" => 409,
            "invalid_session" => 400,
            "session_not_found" => 404,
            "opencode_error" => 502,
            _ => 503,
        };
        return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: status);
    }

    private static IResult BadRequest(string error) =>
        Results.Json(new { error }, statusCode: 400);

    private static async Task<HashSet<string>> GetActiveAsync(
        AgentSessionProxy sessions, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var root = await sessions.ActiveSessionsAsync(ct);
            if (root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in data.EnumerateObject())
                {
                    result.Add(property.Name);
                }
            }
        }
        catch
        {
            // active 查询失败不影响会话列表
        }

        return result;
    }

    private static object MapSession(JsonElement item, HashSet<string> active)
    {
        var id = item.TryGetProperty("id", out var idValue)
            ? idValue.GetString() ?? ""
            : "";
        var title = item.TryGetProperty("title", out var titleValue)
            ? titleValue.GetString()
            : null;
        long created = 0;
        long updated = 0;
        if (item.TryGetProperty("time", out var time))
        {
            if (time.TryGetProperty("created", out var c) && c.TryGetInt64(out var cv))
            {
                created = cv;
            }
            if (time.TryGetProperty("updated", out var u) && u.TryGetInt64(out var uv))
            {
                updated = uv;
            }
        }

        return new
        {
            id,
            title = string.IsNullOrWhiteSpace(title) ? "新对话" : title,
            createdAt = created,
            updatedAt = updated,
            active = active.Contains(id),
        };
    }

    private static object[] MapMessages(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<object>();
        foreach (var item in data.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeValue)
                ? typeValue.GetString() ?? ""
                : "";
            if (type is not ("user" or "assistant" or "compaction" or "shell"))
            {
                continue;
            }

            var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            long created = 0;
            if (item.TryGetProperty("time", out var time) &&
                time.TryGetProperty("created", out var c) && c.TryGetInt64(out var cv))
            {
                created = cv;
            }

            if (type == "user")
            {
                result.Add(new
                {
                    id,
                    kind = "user",
                    text = item.TryGetProperty("text", out var text) ? text.GetString() : "",
                    createdAt = created,
                    tools = Array.Empty<object>(),
                });
                continue;
            }

            if (type == "assistant" &&
                item.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                var texts = new List<string>();
                var tools = new List<object>();
                foreach (var part in content.EnumerateArray())
                {
                    var partType = part.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                    if (partType == "text" &&
                        part.TryGetProperty("text", out var partText))
                    {
                        var value = partText.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            texts.Add(value);
                        }
                    }
                    else if (partType == "tool")
                    {
                        tools.Add(MapTool(part));
                    }
                }

                result.Add(new
                {
                    id,
                    kind = "assistant",
                    text = string.Join("\n", texts),
                    createdAt = created,
                    tools,
                });
                continue;
            }

            if (type == "compaction")
            {
                result.Add(new
                {
                    id,
                    kind = "compaction",
                    text = item.TryGetProperty("text", out var text) ? text.GetString() : "上下文已压缩",
                    createdAt = created,
                    tools = Array.Empty<object>(),
                });
                continue;
            }

            result.Add(new
            {
                id,
                kind = type,
                text = "",
                createdAt = created,
                tools = Array.Empty<object>(),
            });
        }

        return result.ToArray();
    }

    private static object MapTool(JsonElement part)
    {
        var name = part.TryGetProperty("name", out var nameValue)
            ? nameValue.GetString() ?? "tool"
            : "tool";
        var status = "";
        string? command = null;
        var output = "";
        if (part.TryGetProperty("state", out var state))
        {
            status = state.TryGetProperty("status", out var statusValue)
                ? statusValue.GetString() ?? ""
                : "";
            if (state.TryGetProperty("input", out var input) &&
                input.ValueKind == JsonValueKind.Object)
            {
                if (input.TryGetProperty("command", out var cmd))
                {
                    command = cmd.GetString();
                }
                else if (input.TryGetProperty("cmd", out var cmd2))
                {
                    command = cmd2.GetString();
                }
            }

            if (state.TryGetProperty("content", out var content))
            {
                output = ExtractContentText(content);
            }
            else if (state.TryGetProperty("error", out var error))
            {
                output = ExtractContentText(error);
            }
        }

        return new
        {
            name,
            status,
            command,
            output = output.Length > 4000 ? output[..4000] : output,
        };
    }

    private static string ExtractContentText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    parts.Add(item.GetString() ?? "");
                }
                else if (item.ValueKind == JsonValueKind.Object &&
                         item.TryGetProperty("text", out var text))
                {
                    parts.Add(text.GetString() ?? "");
                }
            }

            return string.Join("\n", parts);
        }

        return "";
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context, CancellationToken ct)
        where T : class
    {
        if ((context.Request.ContentLength ?? 0) > 64 * 1024)
        {
            return null;
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadBearer(string header) =>
        header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
