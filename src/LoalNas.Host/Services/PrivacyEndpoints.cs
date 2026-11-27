using System.Text;
using System.Text.Json;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>隐私目录密码端点（手机 HMAC 鉴权保护）。</summary>
public static class PrivacyEndpoints
{
    private sealed record SetupRequest(string? Password, string? ConfirmPassword);
    private sealed record UnlockRequest(string? Password);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void MapPrivacyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/privacy/status", (PrivacyService privacy) =>
        {
            var (configured, remaining) = privacy.Status();
            return Results.Ok(new
            {
                configured,
                lockoutSecondsRemaining = remaining,
            });
        });

        app.MapPost("/api/privacy/setup", async (
            HttpContext context, PrivacyService privacy) =>
        {
            var request = await ReadJsonAsync<SetupRequest>(context);
            if (request is null)
            {
                return Results.Json(new { error = "invalid_request" }, statusCode: 400);
            }

            try
            {
                privacy.EnsureReady();
            }
            catch
            {
                return Results.Json(new { error = "privacy_unavailable" }, statusCode: 503);
            }

            return privacy.Setup(request.Password, request.ConfirmPassword) switch
            {
                PrivacyService.SetupStatus.Ok =>
                    Results.Ok(new { configured = true }),
                PrivacyService.SetupStatus.AlreadyConfigured =>
                    Results.Json(new { error = "already_configured" }, statusCode: 409),
                PrivacyService.SetupStatus.PasswordTooShort =>
                    Results.Json(new { error = "password_too_short" }, statusCode: 400),
                PrivacyService.SetupStatus.PasswordMismatch =>
                    Results.Json(new { error = "password_mismatch" }, statusCode: 400),
                _ => Results.Json(new { error = "invalid_request" }, statusCode: 400),
            };
        });

        app.MapPost("/api/privacy/unlock", async (
            HttpContext context, PrivacyService privacy,
            IOptions<PrivacyOptions> options) =>
        {
            var request = await ReadJsonAsync<UnlockRequest>(context);
            if (request is null)
            {
                return Results.Json(new { error = "invalid_request" }, statusCode: 400);
            }

            try
            {
                privacy.EnsureReady();
            }
            catch
            {
                return Results.Json(new { error = "privacy_unavailable" }, statusCode: 503);
            }

            var status = privacy.Unlock(request.Password, out var retryAfter);
            switch (status)
            {
                case PrivacyService.UnlockStatus.Ok:
                    return Results.Ok(new
                    {
                        unlocked = true,
                        expiresInSeconds = Math.Max(1, options.Value.UnlockMinutes) * 60,
                    });
                case PrivacyService.UnlockStatus.BadPassword:
                    return Results.Json(new { error = "invalid_password" }, statusCode: 401);
                case PrivacyService.UnlockStatus.NotConfigured:
                    return Results.Json(new { error = "not_configured" }, statusCode: 409);
                case PrivacyService.UnlockStatus.Locked:
                    context.Response.Headers["Retry-After"] =
                        Math.Max(1, retryAfter).ToString();
                    return Results.Json(new
                    {
                        error = "locked_out",
                        retryAfterSeconds = Math.Max(1, retryAfter),
                    }, statusCode: 429);
                default:
                    return Results.Json(new { error = "privacy_unavailable" }, statusCode: 503);
            }
        });
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context) where T : class
    {
        if ((context.Request.ContentLength ?? 0) > 4096)
        {
            return null;
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
