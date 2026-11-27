using System.Text;
using System.Text.Json;
using LoalNas.Host.Configuration;
using Microsoft.AspNetCore.Http.Features;

namespace LoalNas.Host.Services;

/// <summary>分享相关的 HTTP 端点：手机端管理接口（HMAC 鉴权）+ 公网分享页（token 鉴权）。</summary>
public static class ShareEndpoints
{
    private sealed record ShareCreateRequest(
        string? Path, int? ExpiresInSeconds, string? Password, int? MaxDownloads);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void MapShareEndpoints(this WebApplication app)
    {
        app.MapPost("/api/shares", async (HttpContext context, ShareService shares) =>
        {
            if ((context.Request.ContentLength ?? 0) > 4096)
            {
                return Results.Json(new { error = "invalid_request" }, statusCode: 400);
            }

            ShareCreateRequest? request = null;
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                request = JsonSerializer.Deserialize<ShareCreateRequest>(body, JsonOpts);
            }
            catch (JsonException)
            {
                // fall through
            }

            if (request is null)
            {
                return Results.Json(new { error = "invalid_request" }, statusCode: 400);
            }

            var record = shares.Create(
                request.Path, request.ExpiresInSeconds, request.Password,
                request.MaxDownloads, out var error);
            if (record is null)
            {
                var status = error == "too_many_shares" ? 409 : 400;
                return Results.Json(new { error = error ?? "error" }, statusCode: status);
            }

            return Results.Ok(new
            {
                token = record.Token,
                url = shares.BuildUrl(record.Token, context.Request),
                fileName = record.FileName,
                size = record.SizeBytes,
                expiresAt = record.ExpiresAt,
                hasPassword = record.HasPassword,
                maxDownloads = record.MaxDownloads,
                downloadCount = record.DownloadCount,
            });
        });

        app.MapGet("/api/shares", (HttpContext context, ShareService shares) =>
        {
            var now = DateTimeOffset.UtcNow;
            var items = shares.List().Select(record => new
            {
                token = record.Token,
                url = shares.BuildUrl(record.Token, context.Request),
                resourcePath = record.ResourcePath,
                fileName = record.FileName,
                size = record.SizeBytes,
                createdAt = record.CreatedAt,
                expiresAt = record.ExpiresAt,
                maxDownloads = record.MaxDownloads,
                downloadCount = record.DownloadCount,
                hasPassword = record.HasPassword,
                expired = record.IsExpired(now),
                revoked = record.Revoked,
            }).ToArray();
            return Results.Ok(new { items });
        });

        app.MapDelete("/api/shares/{token}", (string token, ShareService shares) =>
            shares.Revoke(token)
                ? Results.Ok(new { revoked = true })
                : Results.NotFound(new { error = "not_found" }));

        app.MapMethods("/s/{token}", new[] { "GET", "HEAD" },
            (string token, HttpContext context, ShareService shares, ShareRateLimiter limiter) =>
                ServePage(token, context, shares, limiter));

        app.MapPost("/s/{token}/unlock",
            (string token, HttpContext context, ShareService shares, ShareRateLimiter limiter) =>
                UnlockAsync(token, context, shares, limiter));

        app.MapMethods("/s/{token}/file", new[] { "GET", "HEAD" },
            (string token, HttpContext context, ShareService shares, ShareRateLimiter limiter,
                FileBrowserProcessManager manager) =>
                ServeFile(token, context, shares, limiter, manager));
    }

    private static IResult ServePage(
        string token, HttpContext context, ShareService shares, ShareRateLimiter limiter)
    {
        if (!ShareTokens.IsValid(token))
        {
            return NotFoundPage(context);
        }

        if (!limiter.TryAcquirePage(token))
        {
            return TooManyRequests(context);
        }

        var share = shares.Find(token);
        if (share is null)
        {
            return NotFoundPage(context);
        }

        var needPassword = share.HasPassword &&
            !shares.HasValidUnlockCookie(share, context.Request.Cookies[ShareService.CookieName]);
        ApplyPageHeaders(context);
        return Results.Content(
            SharePage.Render(share, needPassword, exhausted: !needPassword && share.IsExhausted),
            "text/html; charset=utf-8");
    }

    private static async Task<IResult> UnlockAsync(
        string token, HttpContext context, ShareService shares, ShareRateLimiter limiter)
    {
        if (!ShareTokens.IsValid(token))
        {
            return NotFoundPage(context);
        }

        if (!limiter.TryAcquirePage(token))
        {
            return TooManyRequests(context);
        }

        var share = shares.Find(token);
        if (share is null)
        {
            return NotFoundPage(context);
        }

        if (!share.HasPassword)
        {
            return Results.Redirect("/s/" + token);
        }

        if (!context.Request.HasFormContentType ||
            (context.Request.ContentLength ?? 0) > 4096)
        {
            return Results.Content(
                SharePage.RenderError("请求无效", "请从分享页面输入密码。"),
                "text/html; charset=utf-8", statusCode: 400);
        }

        var form = await context.Request.ReadFormAsync(new FormOptions
        {
            ValueLengthLimit = 1024,
            MultipartBodyLengthLimit = 4096,
            ValueCountLimit = 8,
        });
        var status = shares.TryUnlock(share, form["password"].ToString(), out var retryAfter);

        switch (status)
        {
            case ShareService.UnlockStatus.Ok:
                var secure = context.Request.IsHttps || shares.IsSecurePublicBaseUrl;
                context.Response.Headers.Append(
                    "Set-Cookie", shares.BuildUnlockCookie(share, secure));
                return Results.Redirect("/s/" + token);

            case ShareService.UnlockStatus.Locked:
                context.Response.Headers["Retry-After"] = Math.Max(1, retryAfter).ToString();
                ApplyPageHeaders(context);
                return Results.Content(
                    SharePage.RenderError("尝试次数过多", "请稍后再试。"),
                    "text/html; charset=utf-8", statusCode: 429);

            default:
                ApplyPageHeaders(context);
                return Results.Content(
                    SharePage.Render(
                        share, needPassword: true, exhausted: false, notice: "密码不正确，请重试。"),
                    "text/html; charset=utf-8", statusCode: 401);
        }
    }

    private static IResult ServeFile(
        string token, HttpContext context, ShareService shares, ShareRateLimiter limiter,
        FileBrowserProcessManager manager)
    {
        if (!ShareTokens.IsValid(token))
        {
            return NotFoundPage(context);
        }

        if (!limiter.TryAcquireFile(token))
        {
            return TooManyRequests(context);
        }

        var share = shares.Find(token);
        if (share is null)
        {
            return NotFoundPage(context);
        }

        if (share.HasPassword &&
            !shares.HasValidUnlockCookie(share, context.Request.Cookies[ShareService.CookieName]))
        {
            return Results.Text("需要密码，请先打开分享页面。", "text/plain; charset=utf-8",
                statusCode: 401);
        }

        if (!SharePathPolicy.TryResolveFile(
                manager.SharedRootPath, share.ResourcePath, out var fullPath, out _))
        {
            return NotFoundPage(context);
        }

        var download = context.Request.Query["dl"] == "1";
        var hasRange = !string.IsNullOrEmpty(context.Request.Headers.Range);
        // 只有真正的 GET 下载才计数；HEAD 探测、Range 续传与预览都不消耗次数。
        if (download && HttpMethods.IsGet(context.Request.Method) && !hasRange &&
            !shares.TryConsumeDownload(token))
        {
            ApplyPageHeaders(context);
            return Results.Content(
                SharePage.RenderError("下载次数已用完", "此分享的下载次数已达上限，请联系分享者。"),
                "text/html; charset=utf-8", statusCode: 410);
        }

        FileStream stream;
        DateTimeOffset lastModified;
        try
        {
            var info = new FileInfo(fullPath);
            lastModified = info.LastWriteTimeUtc;
            stream = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch
        {
            return NotFoundPage(context);
        }

        var contentType = ShareContentTypes.Resolve(share.FileName, out var kind);
        var inline = !download && kind != ShareMediaKind.None;
        context.Response.Headers["Content-Disposition"] =
            ShareContentTypes.BuildContentDisposition(share.FileName, attachment: !inline);
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Cache-Control"] = "private, no-store";

        return Results.File(
            stream, contentType, lastModified: lastModified, enableRangeProcessing: true);
    }

    private static IResult NotFoundPage(HttpContext context)
    {
        ApplyPageHeaders(context);
        return Results.Content(
            SharePage.RenderError("链接不存在或已失效", "请向分享者确认链接是否正确，或让对方重新生成。"),
            "text/html; charset=utf-8", statusCode: 404);
    }

    private static IResult TooManyRequests(HttpContext context)
    {
        context.Response.Headers["Retry-After"] = "10";
        ApplyPageHeaders(context);
        return Results.Content(
            SharePage.RenderError("访问过于频繁", "请稍等几秒再试。"),
            "text/html; charset=utf-8", statusCode: 429);
    }

    private static void ApplyPageHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["Cache-Control"] = "no-store";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] =
            "default-src 'none'; img-src 'self'; media-src 'self'; " +
            "style-src 'unsafe-inline'; form-action 'self'; " +
            "base-uri 'none'; frame-ancestors 'none'";
    }
}
