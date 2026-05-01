using System.Net;
using System.Net.Http.Headers;
using Microsoft.Net.Http.Headers;

namespace LoalNas.Host.Services;

public sealed class FileBrowserApiProxy(
    IHttpClientFactory httpClientFactory,
    FileBrowserProcessManager processManager,
    ConnectedDeviceTracker deviceTracker,
    ILogger<FileBrowserApiProxy> logger)
{
    public const string HttpClientName = "filebrowser-proxy";

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    public async Task ProxyApiAsync(HttpContext context, string? path)
    {
        deviceTracker.RecordActivity(context.Connection.RemoteIpAddress?.ToString());
        try
        {
            await processManager.EnsureRunningAsync(context.RequestAborted);

            using var requestMessage = CreateProxyRequest(context, path);
            using var responseMessage = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

            await CopyProxyResponseAsync(context, responseMessage);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Proxy request to FileBrowser failed.");

            context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "filebrowser_proxy_failed",
                detail = exception.Message
            }, context.RequestAborted);
        }
    }

    private HttpRequestMessage CreateProxyRequest(HttpContext context, string? path)
    {
        var requestUri = BuildTargetUri(context, path);
        var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), requestUri)
        {
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        var hasBody = context.Request.ContentLength is > 0 ||
            context.Request.Headers.ContainsKey(HeaderNames.TransferEncoding);

        if (hasBody)
        {
            requestMessage.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                requestMessage.Content ??= new StreamContent(context.Request.Body);
                requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        requestMessage.Headers.Host = processManager.BaseAddress.Authority;

        return requestMessage;
    }

    private Uri BuildTargetUri(HttpContext context, string? path)
    {
        var trimmedPath = path?.TrimStart('/') ?? string.Empty;
        var apiPath = string.IsNullOrEmpty(trimmedPath) ? "/api/" : $"/api/{trimmedPath}";
        var target = $"{processManager.BaseAddress.Scheme}://{processManager.BaseAddress.Authority}{apiPath}{context.Request.QueryString}";

        return new Uri(target);
    }

    private async Task CopyProxyResponseAsync(HttpContext context, HttpResponseMessage responseMessage)
    {
        context.Response.StatusCode = (int)responseMessage.StatusCode;

        foreach (var header in responseMessage.Headers)
        {
            context.Response.Headers[header.Key] = RewriteResponseHeader(header.Key, header.Value.ToArray());
        }

        foreach (var header in responseMessage.Content.Headers)
        {
            context.Response.Headers[header.Key] = RewriteResponseHeader(header.Key, header.Value.ToArray());
        }

        context.Response.Headers.Remove(HeaderNames.TransferEncoding);

        await responseMessage.Content.CopyToAsync(context.Response.Body);
    }

    private string[] RewriteResponseHeader(string headerName, string[] values)
    {
        if (!headerName.Equals(HeaderNames.Location, StringComparison.OrdinalIgnoreCase))
        {
            return values;
        }

        return values.Select(RewriteLocationValue).ToArray();
    }

    private string RewriteLocationValue(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return location;
        }

        if (location.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return $"/api/filebrowser{location[4..]}";
        }

        if (Uri.TryCreate(location, UriKind.Absolute, out var absoluteUri) &&
            absoluteUri.Scheme.Equals(processManager.BaseAddress.Scheme, StringComparison.OrdinalIgnoreCase) &&
            absoluteUri.Authority.Equals(processManager.BaseAddress.Authority, StringComparison.OrdinalIgnoreCase) &&
            absoluteUri.AbsolutePath.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return $"/api/filebrowser{absoluteUri.PathAndQuery[4..]}";
        }

        return location;
    }
}