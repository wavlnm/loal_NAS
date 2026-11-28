using System.Net;
using System.Net.Http.Headers;
using Microsoft.Net.Http.Headers;

namespace LoalNas.Host.Services;

/// <summary>
/// /apps/{appId}/... 反向代理。公开请求先查平台房间注册表，令牌不存在直接 404，不转发也不启动进程。
/// </summary>
public sealed class AppProxy
{
    public const string HttpClientName = "app-proxy";

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
    };

    private readonly AppProcessManager _processManager;
    private readonly AppRoomService _rooms;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppProxy> _logger;

    public AppProxy(
        AppProcessManager processManager,
        AppRoomService rooms,
        IHttpClientFactory httpClientFactory,
        ILogger<AppProxy> logger)
    {
        _processManager = processManager;
        _rooms = rooms;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context, AppManifest app, string restPath)
    {
        var match = app.Match(restPath);

        // 公开路由里的 {token} 必须先在平台注册表中存在且未过期。
        if (match?.Token is { Length: > 0 } token)
        {
            if (_rooms.Find(app.Id, token) is null)
            {
                await WriteRoomClosedAsync(context);
                return;
            }
        }

        if (!app.Enabled)
        {
            await WriteStatusAsync(context, StatusCodes.Status404NotFound,
                "app_disabled", "应用未启用。");
            return;
        }

        var baseAddress = _processManager.GetRunningBaseAddress(app);
        if (baseAddress is null)
        {
            if (restPath.Length == 0 && AcceptsHtml(context))
            {
                await WriteIdleHtmlAsync(context);
                return;
            }

            if (match is not null)
            {
                await WriteStatusAsync(context, StatusCodes.Status404NotFound,
                    "app_not_running", "应用未启动。请先在千私云手机端创建房间。");
            }
            else
            {
                await WriteStatusAsync(context, StatusCodes.Status404NotFound,
                    "app_not_running", "应用未启动。");
            }
            return;
        }

        try
        {
            using var requestMessage = CreateProxyRequest(context, baseAddress, restPath);
            using var responseMessage = await _httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

            await CopyProxyResponseAsync(context, responseMessage);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 浏览器断开（长轮询很常见），无需响应
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "应用 {AppId} 代理请求失败。", app.Id);
            if (!context.Response.HasStarted)
            {
                await WriteStatusAsync(context, StatusCodes.Status502BadGateway,
                    "app_proxy_failed", "应用无响应。");
            }
        }
    }

    private static HttpRequestMessage CreateProxyRequest(
        HttpContext context, Uri baseAddress, string restPath)
    {
        var relative = restPath.Length == 0 ? string.Empty : restPath.TrimStart('/');
        var target = new Uri(baseAddress, relative + context.Request.QueryString);

        var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), target)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        var hasBody = context.Request.ContentLength is > 0 ||
            context.Request.Headers.ContainsKey(HeaderNames.TransferEncoding);
        if (hasBody)
        {
            requestMessage.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key) ||
                header.Key.Equals(HeaderNames.Host, StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals(AppProcessManager.ControlHeaderName, StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals(HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase) ||
                header.Key.StartsWith("X-NAS-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                requestMessage.Content ??= new StreamContent(context.Request.Body);
                requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        return requestMessage;
    }

    private static async Task CopyProxyResponseAsync(
        HttpContext context, HttpResponseMessage responseMessage)
    {
        context.Response.StatusCode = (int)responseMessage.StatusCode;

        foreach (var header in responseMessage.Headers)
        {
            if (!HopByHopHeaders.Contains(header.Key))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in responseMessage.Content.Headers)
        {
            if (!HopByHopHeaders.Contains(header.Key))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        context.Response.Headers.Remove(HeaderNames.TransferEncoding);

        await responseMessage.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static Task WriteRoomClosedAsync(HttpContext context)
    {
        const string html = """
<!doctype html>
<html lang="zh"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>房间已关闭</title>
<style>
body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
background:#f6f8fc;color:#1c2028;font-family:"Segoe UI","Noto Sans CJK SC",system-ui,sans-serif}
.card{background:#fff;border:1px solid #e6eaf2;border-radius:18px;padding:36px 30px;max-width:420px;
margin:20px;text-align:center}
h1{font-size:20px;margin:0 0 12px}p{color:#727c8a;font-size:14px;line-height:1.8;margin:0}
</style></head><body><div class="card">
<h1>房间已关闭或已过期</h1>
<p>请让房主在千私云手机端重新创建房间，再打开新的链接。</p>
</div></body></html>
""";

        if (AcceptsHtml(context))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/html; charset=utf-8";
            return context.Response.WriteAsync(html);
        }

        return WriteStatusAsync(context, StatusCodes.Status404NotFound,
            "room_closed", "房间已关闭或已过期。");
    }

    private static Task WriteIdleHtmlAsync(HttpContext context)
    {
        const string html = """
<!doctype html>
<html lang="zh"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>应用未启动</title>
<style>
body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
background:#f6f8fc;color:#1c2028;font-family:"Segoe UI","Noto Sans CJK SC",system-ui,sans-serif}
.card{background:#fff;border:1px solid #e6eaf2;border-radius:18px;padding:36px 30px;max-width:420px;
margin:20px;text-align:center}
h1{font-size:20px;margin:0 0 12px}p{color:#727c8a;font-size:14px;line-height:1.8;margin:0}
</style></head><body><div class="card">
<h1>应用未启动</h1>
<p>请先在千私云手机端创建房间，再把房间链接发给好友。<br>公开链接不能创建房间，也不会唤醒应用进程。</p>
</div></body></html>
""";

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(html);
    }

    private static Task WriteStatusAsync(
        HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { error = code, detail = message });
    }

    private static bool AcceptsHtml(HttpContext context) =>
        context.Request.Headers.Accept.ToString()
            .Contains("text/html", StringComparison.OrdinalIgnoreCase);
}
