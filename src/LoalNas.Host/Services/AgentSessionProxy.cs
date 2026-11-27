using System.Text;
using System.Text.Json;

namespace LoalNas.Host.Services;

/// <summary>对 opencode serve 的会话 HTTP API 做薄代理与 JSON 解析。</summary>
public sealed class AgentSessionProxy
{
    private readonly AgentServeManager _serve;

    public AgentSessionProxy(AgentServeManager serve)
    {
        _serve = serve;
    }

    public async Task<JsonElement> ListSessionsAsync(int limit, CancellationToken ct)
    {
        using var response = await _serve.SendAsync(
            HttpMethod.Get, $"/api/session?limit={Math.Clamp(limit, 1, 100)}&order=desc",
            null, ct).ConfigureAwait(false);
        return await ReadAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> CreateSessionAsync(string? title, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { title, agent = "build" });
        using var response = await _serve.SendAsync(
            HttpMethod.Post, "/api/session", body, ct).ConfigureAwait(false);
        return await ReadAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        using var response = await _serve.SendAsync(
            HttpMethod.Get, $"/api/session/{SessionId(sessionId)}", null, ct).ConfigureAwait(false);
        return await ReadAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> GetMessagesAsync(string sessionId, int limit, CancellationToken ct)
    {
        using var response = await _serve.SendAsync(
            HttpMethod.Get,
            $"/api/session/{SessionId(sessionId)}/message?limit={Math.Clamp(limit, 1, 500)}&order=asc",
            null, ct).ConfigureAwait(false);
        return await ReadAsync(response, ct).ConfigureAwait(false);
    }

    public async Task SetAgentAsync(string sessionId, string agent, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { agent });
        using var response = await _serve.SendAsync(
            HttpMethod.Post, $"/api/session/{SessionId(sessionId)}/agent", body, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> PromptAsync(string sessionId, string text, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { text });
        using var response = await _serve.SendAsync(
            HttpMethod.Post, $"/api/session/{SessionId(sessionId)}/prompt", body, ct)
            .ConfigureAwait(false);
        return await ReadAsync(response, ct).ConfigureAwait(false);
    }

    public async Task InterruptAsync(string sessionId, CancellationToken ct)
    {
        using var response = await _serve.SendAsync(
            HttpMethod.Post, $"/api/session/{SessionId(sessionId)}/interrupt?resume=false",
            null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> ActiveSessionsAsync(CancellationToken ct)
    {
        using var response = await _serve.SendAsync(
            HttpMethod.Get, "/api/session/active", null, ct).ConfigureAwait(false);
        return await ReadAsync(response, ct).ConfigureAwait(false);
    }

    public static string? ValidateSessionId(string? sessionId) =>
        sessionId is { Length: > 3 and < 140 } &&
        sessionId.StartsWith("ses", StringComparison.Ordinal) &&
        sessionId.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            ? sessionId
            : null;

    private static string SessionId(string sessionId) =>
        ValidateSessionId(sessionId) ?? throw new AgentUnavailableException(
            "invalid_session", "无效的会话 ID。");

    private static async Task<JsonElement> ReadAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AgentUnavailableException(
                response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "session_not_found"
                    : "opencode_error",
                $"OpenCode 返回 {(int)response.StatusCode}：{Trim(text)}");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AgentUnavailableException("opencode_error", "OpenCode 返回了无效 JSON。");
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AgentUnavailableException(
                "opencode_error",
                $"OpenCode 返回 {(int)response.StatusCode}：{Trim(text)}");
        }
    }

    private static string Trim(string text) =>
        text.Length <= 500 ? text : text[..500] + "…";
}
