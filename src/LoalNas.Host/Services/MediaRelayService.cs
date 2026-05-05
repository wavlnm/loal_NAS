using System.Collections.Concurrent;
using System.Net;
using Microsoft.Net.Http.Headers;

namespace LoalNas.Host.Services;

public sealed class MediaRelayService(
	IHttpClientFactory httpClientFactory,
	FileBrowserProcessManager processManager,
	ConnectedDeviceTracker deviceTracker,
	ILogger<MediaRelayService> logger)
{
	public const string HttpClientName = "media-relay";

	private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(30);

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
		// 不转发 Accept-Encoding：让 FileBrowser 返回原始未压缩数据，
		// 保证 Content-Length 与 body 一致，浏览器才能正确 seek/Range。
		"Accept-Encoding"
	};

	private readonly ConcurrentDictionary<string, MediaRelayTicket> tickets = new();

	public MediaRelayTicketLease CreateTicket(string authToken, string resourcePath)
	{
		if (string.IsNullOrWhiteSpace(authToken))
		{
			throw new ArgumentException("缺少 X-Auth。", nameof(authToken));
		}

		var normalizedPath = NormalizeFilePath(resourcePath);
		CleanupExpiredTickets();

		var lease = new MediaRelayTicketLease(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.Add(TicketLifetime));
		tickets[lease.TicketId] = new MediaRelayTicket(lease.TicketId, authToken, normalizedPath, lease.ExpiresAt);
		return lease;
	}

	public async Task ProxyMediaAsync(HttpContext context, string ticketId)
	{
		deviceTracker.RecordActivity(context.Connection.RemoteIpAddress?.ToString());
		CleanupExpiredTickets();

		if (!tickets.TryGetValue(ticketId, out var ticket))
		{
			context.Response.StatusCode = (int)HttpStatusCode.NotFound;
			await context.Response.WriteAsJsonAsync(new
			{
				error = "media_ticket_not_found"
			}, context.RequestAborted);
			return;
		}

		if (ticket.ExpiresAt <= DateTimeOffset.UtcNow)
		{
			tickets.TryRemove(ticketId, out _);
			context.Response.StatusCode = (int)HttpStatusCode.Gone;
			await context.Response.WriteAsJsonAsync(new
			{
				error = "media_ticket_expired"
			}, context.RequestAborted);
			return;
		}

		try
		{
			await processManager.EnsureRunningAsync(context.RequestAborted);

			using var requestMessage = CreateProxyRequest(context, ticket);
			using var responseMessage = await httpClientFactory.CreateClient(HttpClientName)
				.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

			await CopyProxyResponseAsync(context, responseMessage);
		}
		catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
		{
			logger.LogDebug("Media relay request was canceled by the client.");
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
		{
			if (context.Response.HasStarted)
			{
				logger.LogDebug(exception, "Media relay stream ended after the response started.");
				return;
			}

			logger.LogWarning(exception, "Media relay request to FileBrowser failed.");

			context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
			await context.Response.WriteAsJsonAsync(new
			{
				error = "media_relay_failed",
				detail = exception.Message
			}, context.RequestAborted);
		}
	}

	private HttpRequestMessage CreateProxyRequest(HttpContext context, MediaRelayTicket ticket)
	{
		var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), BuildTargetUri(ticket))
		{
			VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
		};

		foreach (var header in context.Request.Headers)
		{
			if (HopByHopHeaders.Contains(header.Key) || header.Key.Equals("X-Auth", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
		}

		requestMessage.Headers.Host = processManager.BaseAddress.Authority;
		requestMessage.Headers.TryAddWithoutValidation("X-Auth", ticket.AuthToken);

		return requestMessage;
	}

	private Uri BuildTargetUri(MediaRelayTicket ticket)
	{
		var encodedPath = EncodeResourcePath(ticket.ResourcePath);
		return new Uri($"{processManager.BaseAddress.Scheme}://{processManager.BaseAddress.Authority}/api/raw{encodedPath}?inline=true");
	}

	private static string EncodeResourcePath(string resourcePath)
	{
		if (resourcePath == "/")
		{
			return "/";
		}

		var hasTrailingSlash = resourcePath.EndsWith('/');
		var segments = resourcePath
			.Split('/', StringSplitOptions.RemoveEmptyEntries)
			.Select(Uri.EscapeDataString);

		return $"/{string.Join('/', segments)}{(hasTrailingSlash ? "/" : string.Empty)}";
	}

	private static string NormalizeFilePath(string resourcePath)
	{
		if (string.IsNullOrWhiteSpace(resourcePath))
		{
			throw new ArgumentException("path 不能为空。", nameof(resourcePath));
		}

		var normalized = resourcePath.StartsWith('/') ? resourcePath : $"/{resourcePath}";
		if (normalized.EndsWith('/'))
		{
			throw new ArgumentException("当前中转只支持文件，不支持目录。", nameof(resourcePath));
		}

		return normalized;
	}

	private async Task CopyProxyResponseAsync(HttpContext context, HttpResponseMessage responseMessage)
	{
		context.Response.StatusCode = (int)responseMessage.StatusCode;

		foreach (var header in responseMessage.Headers)
		{
			context.Response.Headers[header.Key] = header.Value.ToArray();
		}

		foreach (var header in responseMessage.Content.Headers)
		{
			context.Response.Headers[header.Key] = header.Value.ToArray();
		}

		context.Response.Headers.Remove(HeaderNames.TransferEncoding);

		await responseMessage.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
	}

	private void CleanupExpiredTickets()
	{
		var now = DateTimeOffset.UtcNow;
		foreach (var pair in tickets)
		{
			if (pair.Value.ExpiresAt <= now)
			{
				tickets.TryRemove(pair.Key, out _);
			}
		}
	}
}

public sealed record MediaRelayTicketLease(string TicketId, DateTimeOffset ExpiresAt);

internal sealed record MediaRelayTicket(string TicketId, string AuthToken, string ResourcePath, DateTimeOffset ExpiresAt);