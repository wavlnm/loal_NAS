using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LoalNas.Host.Services;

/// <summary>
/// 手机端访问鉴权中间件。
/// 每个请求须携带三个头：
///   X-NAS-Timestamp : Unix 时间戳（秒）
///   X-NAS-Nonce : 8 位随机字符串
///   X-NAS-Hash  : SHA-256(secret:timestamp_seconds:nonce)
/// 显式时间戳可避免服务端靠本地当前秒数盲猜导致的偶发误判；
/// 同时维护 nonce 缓冲区防重放攻击。
/// </summary>
public sealed class PhoneAuthMiddleware
{
	private const int AllowedClockSkewSeconds = 30;
	private const int NonceRetentionSeconds = AllowedClockSkewSeconds + 10;

	// 允许跳过鉴权的路径（健康检查）
	private static readonly HashSet<string> _bypassPaths = new(StringComparer.OrdinalIgnoreCase)
	{
		"/api/system/status",
		"/"
	};

	// nonce 缓冲区：存储已见过的 nonce 及其接收时间，自动清理超过 10 秒的条目
	private readonly ConcurrentDictionary<string, long> _seenNonces = new(StringComparer.Ordinal);
	private long _lastCleanupTick = Environment.TickCount64;

	private readonly RequestDelegate _next;
	private readonly DeviceIdentityService _identity;
	private readonly ILogger<PhoneAuthMiddleware> _logger;

	public PhoneAuthMiddleware(RequestDelegate next, DeviceIdentityService identity, ILogger<PhoneAuthMiddleware> logger)
	{
		_next = next;
		_identity = identity;
		_logger = logger;
	}

	public async Task InvokeAsync(HttpContext context)
	{
		var path = context.Request.Path.Value ?? string.Empty;

		// 白名单路径直接放行
		if (_bypassPaths.Contains(path))
		{
			await _next(context);
			return;
		}

		// media-open 票据接口也放行（票据本身已鉴权）
		if (path.StartsWith("/api/system/media-open/", StringComparison.OrdinalIgnoreCase))
		{
			await _next(context);
			return;
		}

		// 预览图与原始媒体读取仍受 FileBrowser 的 X-Auth 保护，
		// 这里放行可避免图片/音频组件因无法为每个内部请求携带一次性 nonce 而变慢或失败。
		if (HttpMethods.IsGet(context.Request.Method) &&
			(path.StartsWith("/api/filebrowser/preview/", StringComparison.OrdinalIgnoreCase) ||
			 path.StartsWith("/api/filebrowser/raw/", StringComparison.OrdinalIgnoreCase)))
		{
			await _next(context);
			return;
		}

		var nonce = context.Request.Headers["X-NAS-Nonce"].FirstOrDefault()?.Trim();
		var timestampHeader = context.Request.Headers["X-NAS-Timestamp"].FirstOrDefault()?.Trim();
		var hash  = context.Request.Headers["X-NAS-Hash"].FirstOrDefault()?.Trim();

		if (string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(hash))
		{
			_logger.LogWarning("Auth rejected [{Ip}] {Method} {Path}: missing X-NAS-Nonce or X-NAS-Hash",
				context.Connection.RemoteIpAddress, context.Request.Method, path);
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			await context.Response.WriteAsJsonAsync(new { error = "Missing auth headers." });
			return;
		}

		// 防重放：检查 nonce 是否已见过
		CleanupStaleNonces();
		if (_seenNonces.ContainsKey(nonce))
		{
			_logger.LogWarning("Auth rejected [{Ip}] {Method} {Path}: replayed nonce '{Nonce}'",
				context.Connection.RemoteIpAddress, context.Request.Method, path, nonce);
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			await context.Response.WriteAsJsonAsync(new { error = "Replayed nonce." });
			return;
		}

		var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		var secret = _identity.DeviceSecret;
		var secretPreview = secret.Length >= 8 ? secret[..8] : secret;
		long? requestTimestamp = null;

		if (!string.IsNullOrWhiteSpace(timestampHeader))
		{
			if (!long.TryParse(timestampHeader, out var parsedTimestamp))
			{
				_logger.LogWarning("Auth rejected [{Ip}] {Method} {Path}: invalid X-NAS-Timestamp '{Timestamp}'",
					context.Connection.RemoteIpAddress, context.Request.Method, path, timestampHeader);
				context.Response.StatusCode = StatusCodes.Status401Unauthorized;
				await context.Response.WriteAsJsonAsync(new { error = "Invalid timestamp." });
				return;
			}

			requestTimestamp = parsedTimestamp;
		}

		var valid = false;
		if (requestTimestamp is long clientTimestamp)
		{
			var skewSeconds = Math.Abs(nowSeconds - clientTimestamp);
			if (skewSeconds <= AllowedClockSkewSeconds)
			{
				var expected = ComputeHash(secret, clientTimestamp, nonce);
				valid = string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase);
			}
		}
		else
		{
			// 兼容旧版客户端：未显式发送时间戳时，仍沿用旧的 ±1 秒猜测逻辑。
			foreach (var ts in new[] { nowSeconds, nowSeconds - 1, nowSeconds + 1 })
			{
				var expected = ComputeHash(secret, ts, nonce);
				if (string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
				{
					valid = true;
					break;
				}
			}
		}

		if (!valid)
		{
			if (requestTimestamp is long loggedClientTimestamp)
			{
				var expected = ComputeHash(secret, loggedClientTimestamp, nonce);
				var skewSeconds = Math.Abs(nowSeconds - loggedClientTimestamp);
				_logger.LogWarning(
					"[NAS-AUTH] 拒绝 [{Ip}] {Method} {Path}: hash不匹配 | " +
					"secret[:8]={Secret} serverTs={ServerTs} clientTs={ClientTs} skew={Skew}s | " +
					"收到 hash={RH}... | 期望 clientTs={Expected}...",
					context.Connection.RemoteIpAddress, context.Request.Method, path,
					secretPreview, nowSeconds, loggedClientTimestamp, skewSeconds,
					hash.Length >= 16 ? hash[..16] : hash,
					expected.Length >= 16 ? expected[..16] : expected);
			}
			else
			{
				var e0 = ComputeHash(secret, nowSeconds,     nonce);
				var em = ComputeHash(secret, nowSeconds - 1, nonce);
				var ep = ComputeHash(secret, nowSeconds + 1, nonce);
				_logger.LogWarning(
					"[NAS-AUTH] 拒绝 [{Ip}] {Method} {Path}: hash不匹配 | " +
					"secret[:8]={Secret} ts={Ts} | " +
					"收到 hash={RH}... | 期望 ts-1={Em}... ts={E0}... ts+1={Ep}...",
					context.Connection.RemoteIpAddress, context.Request.Method, path,
					secretPreview, nowSeconds,
					hash.Length >= 16 ? hash[..16] : hash,
					em.Length >= 16 ? em[..16] : em,
					e0.Length >= 16 ? e0[..16] : e0,
					ep.Length >= 16 ? ep[..16] : ep);
			}
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			await context.Response.WriteAsJsonAsync(new { error = "Auth failed." });
			return;
		}

		// 鉴权通过：记录 nonce 防止重放
		_seenNonces[nonce] = nowSeconds;
		await _next(context);
	}

	/// <summary>计算 SHA-256(secret:timestamp:nonce) 并返回小写 hex 字符串。</summary>
	private static string ComputeHash(string secret, long timestamp, string nonce)
	{
		var input = $"{secret}:{timestamp}:{nonce}";
		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
		return Convert.ToHexString(bytes).ToLowerInvariant();
	}

	/// <summary>清理超过 10 秒的 nonce（远超时间窗口，确保安全同时控制内存）。</summary>
	private void CleanupStaleNonces()
	{
		var now = Environment.TickCount64;
		// 每 5 秒最多执行一次清理
		if (now - _lastCleanupTick < 5_000) return;
		_lastCleanupTick = now;

		var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - NonceRetentionSeconds;
		foreach (var kv in _seenNonces)
		{
			if (kv.Value < cutoff)
				_seenNonces.TryRemove(kv.Key, out _);
		}
	}
}
