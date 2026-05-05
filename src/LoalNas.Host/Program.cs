using System.Net;
using LoalNas.Host.Configuration;
using LoalNas.Host.Forms;
using LoalNas.Host.Services;
using Microsoft.Extensions.Hosting;
using System.Windows.Forms;

namespace LoalNas.Host;

internal static class Program
{
	[STAThread]
	private static void Main(string[] args)
	{
		// 将 Console 输出重定向到日志文件（按日期滚动，最多保留 7 天）
		RedirectConsoleToFile();

		var app = BuildApplication(args);
		var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LoalNas.Host.Startup");
		var firewallEnsurer = app.Services.GetRequiredService<WindowsFirewallRuleEnsurer>();
		var fileBrowserManager = app.Services.GetRequiredService<FileBrowserProcessManager>();
		var appLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
		var deviceTracker = app.Services.GetRequiredService<ConnectedDeviceTracker>();
		var deviceIdentity = app.Services.GetRequiredService<DeviceIdentityService>();

		// Start web host on a background thread so the main thread stays STA for WinForms/Clipboard
		Exception? hostStartError = null;
		string[]? boundUrls = null;
		var hostReady = new System.Threading.ManualResetEventSlim(false);

		var hostThread = Task.Run(async () =>
		{
			try
			{
				await app.StartAsync();
				boundUrls = app.Urls.ToArray();
				hostReady.Set();
				await app.WaitForShutdownAsync();
			}
			catch (Exception ex)
			{
				hostStartError = ex;
				hostReady.Set();
			}
		});

		hostReady.Wait();

		if (hostStartError != null)
			throw new InvalidOperationException("Web host failed to start.", hostStartError);

		Ipv6EndpointReporter.LogAvailableAddresses(startupLogger, boundUrls!);
		_ = firewallEnsurer.EnsureRulesForUrlsAsync(boundUrls!);

		ApplicationConfiguration.Initialize();
		using var form = new HostStatusForm(fileBrowserManager, appLifetime, deviceTracker, deviceIdentity, boundUrls!);
		Application.Run(form);

		app.StopAsync().GetAwaiter().GetResult();
		app.DisposeAsync().GetAwaiter().GetResult();
	}

 	private static WebApplication BuildApplication(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

 		var hostOptions = builder.Configuration
			.GetSection(PublicHostOptions.SectionName)
			.Get<PublicHostOptions>() ?? new PublicHostOptions();

 		builder.WebHost.UseUrls(hostOptions.Url);

 		builder.WebHost.ConfigureKestrel(options =>
		{
			// 默认请求体上限 500 MB（文件上传场景）；可按路由再单独放宽
			options.Limits.MaxRequestBodySize = 2 * 1024 * 1024 * 1024L;
			// 请求行（URL + 方法）最大 4 KB，防超长 URL 攻击
			options.Limits.MaxRequestLineSize = 4 * 1024;
			// 所有请求头合计最大 16 KB
			options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
			// 并发连接上限：防止大量空连接耗尽资源（Slowloris 等）
			options.Limits.MaxConcurrentConnections = 200;
			options.Limits.MaxConcurrentUpgradedConnections = 50; // WebSocket 等升级连接
			// 慢速攻击防护：请求头必须在 10 s 内发完，Keep-Alive 空闲 60 s 后断开
			options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
			options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
		});

 		builder.Services.Configure<PublicHostOptions>(
			builder.Configuration.GetSection(PublicHostOptions.SectionName));

 		builder.Services.Configure<FirewallOptions>(
			builder.Configuration.GetSection(FirewallOptions.SectionName));

 		builder.Services.Configure<FileBrowserOptions>(
			builder.Configuration.GetSection(FileBrowserOptions.SectionName));

 		builder.Services.AddHttpClient(FileBrowserApiProxy.HttpClientName, client =>
		{
			client.Timeout = Timeout.InfiniteTimeSpan;
		}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
		{
			AllowAutoRedirect = false,
			UseCookies = false,
			AutomaticDecompression = DecompressionMethods.None
		});
		// 媒体中转专用 HttpClient：禁用自动解压，保证 Content-Length/Content-Encoding
		// 原样透传给浏览器，浏览器才能正确 Range seek 流式播放。
		builder.Services.AddHttpClient(MediaRelayService.HttpClientName, client =>
		{
			client.Timeout = Timeout.InfiniteTimeSpan;
		}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
		{
			AllowAutoRedirect = false,
			UseCookies = false,
			AutomaticDecompression = DecompressionMethods.None
		});
 		builder.Services.AddSingleton<FileBrowserProcessManager>();
		builder.Services.AddHostedService(services => services.GetRequiredService<FileBrowserProcessManager>());
		builder.Services.AddSingleton<FileBrowserApiProxy>();
		builder.Services.AddSingleton<MediaRelayService>();
		builder.Services.AddSingleton<WindowsFirewallRuleEnsurer>();
		builder.Services.AddSingleton<ConnectedDeviceTracker>();
		builder.Services.AddSingleton<DeviceIdentityService>();

		var app = builder.Build();
		// IP 限流 + 自动封禁（需在路由匹配之前执行）
		app.UseMiddleware<LoalNas.Host.Services.IpRateLimitMiddleware>();
		// 手机端访问鉴权（基于扫码分发的 secret + 时间戳 + nonce）
		app.UseMiddleware<LoalNas.Host.Services.PhoneAuthMiddleware>();
		MapEndpoints(app);
		return app;
	}

	private static void MapEndpoints(WebApplication app)
	{
		app.MapGet("/", (FileBrowserProcessManager manager) =>
		{
			var storage = TryCreateStorageSnapshot(manager.SharedRootPath);
			return Results.Ok(new
			{
				name = "千私云电脑",
				fileBrowser = new
				{
					running = manager.IsRunning,
					baseUrl = manager.BaseAddress.ToString(),
					sharedRoot = manager.SharedRootPath
				},
				storage,
				endpoints = new
				{
					status = "/api/system/status",
					fileBrowserApi = "/api/filebrowser/{...}",
					mediaTicket = "/api/system/media-tickets"
				}
			});
		});

		app.MapGet("/api/system/status", (FileBrowserProcessManager manager) =>
		{
			var storage = TryCreateStorageSnapshot(manager.SharedRootPath);
			return Results.Ok(new
			{
				name = "千私云电脑",
				fileBrowser = new
				{
					running = manager.IsRunning,
					baseUrl = manager.BaseAddress.ToString(),
					sharedRoot = manager.SharedRootPath
				},
				storage,
				fileBrowserRunning = manager.IsRunning,
				fileBrowserBaseUrl = manager.BaseAddress.ToString(),
				sharedRootPath = manager.SharedRootPath
			});
		});

		app.MapPost("/api/system/media-tickets",
			(HttpContext context, CreateMediaRelayTicketRequest request, MediaRelayService relay) =>
			{
				var authToken = context.Request.Headers["X-Auth"].ToString().Trim();
				if (string.IsNullOrEmpty(authToken))
				{
					return Results.Unauthorized();
				}

				try
				{
					var ticket = relay.CreateTicket(authToken, request.Path);
					var url = $"{context.Request.Scheme}://{context.Request.Host}/api/system/media-open/{ticket.TicketId}";
					return Results.Ok(new
					{
						url,
						expiresAt = ticket.ExpiresAt
					});
				}
				catch (ArgumentException exception)
				{
					return Results.BadRequest(new
					{
						error = "invalid_media_ticket_request",
						detail = exception.Message
					});
				}
			});

		app.MapMethods("/api/system/media-open/{ticketId}", new[]
		{
			HttpMethods.Get,
			HttpMethods.Head
		}, (HttpContext context, string ticketId, MediaRelayService relay) => relay.ProxyMediaAsync(context, ticketId));

		var proxyMethods = new[]
		{
			HttpMethods.Get,
			HttpMethods.Post,
			HttpMethods.Put,
			HttpMethods.Patch,
			HttpMethods.Delete,
			HttpMethods.Head,
			HttpMethods.Options
		};

		app.MapMethods("/api/filebrowser", proxyMethods,
			(HttpContext context, FileBrowserProcessManager manager, FileBrowserApiProxy proxy, ILoggerFactory loggerFactory) =>
			{
				if (HttpMethods.IsPost(context.Request.Method) &&
				    CheckDiskSpaceInsufficient(context, manager.SharedRootPath, loggerFactory, out var diskError))
				{
					return diskError!;
				}
				return proxy.ProxyApiAsync(context, string.Empty);
			});
		app.MapMethods("/api/filebrowser/{**path}", proxyMethods,
			(HttpContext context, string path, FileBrowserProcessManager manager, FileBrowserApiProxy proxy, ILoggerFactory loggerFactory) =>
			{
				if (HttpMethods.IsPost(context.Request.Method) &&
				    CheckDiskSpaceInsufficient(context, manager.SharedRootPath, loggerFactory, out var diskError))
				{
					return diskError!;
				}
				return proxy.ProxyApiAsync(context, path);
			});
	}

	/// <summary>
	/// 检查磁盘剩余空间是否足以容纳本次上传。
	/// 要求：可用空间 > 上传大小 + 100 MB 盈余。
	/// </summary>
	/// <returns>true 表示空间不足，diskError 已填充；false 表示空间充足。</returns>
	private static bool CheckDiskSpaceInsufficient(
		HttpContext context,
		string sharedRootPath,
		ILoggerFactory loggerFactory,
		out Task<IResult>? diskError)
	{
		diskError = null;
		var logger = loggerFactory.CreateLogger("DiskSpaceCheck");
		var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
		var path = context.Request.Path;

		var contentLength = context.Request.ContentLength;
		if (contentLength is null or <= 0)
		{
			logger.LogInformation(
				"Upload request from {Ip} to {Path} has no Content-Length; bypassing disk check and forwarding to FileBrowser.",
				ip, path);
			return false;
		}

		const long surplusBytes = 100 * 1024 * 1024L; // 100 MB 盈余
		try
		{
			var driveRoot = Path.GetPathRoot(Path.GetFullPath(sharedRootPath));
			if (string.IsNullOrWhiteSpace(driveRoot))
			{
				logger.LogWarning(
					"Upload request from {Ip} to {Path} ({SizeMb:F1} MB): could not resolve drive root from shared path '{SharedPath}'; bypassing disk check.",
					ip, path, contentLength.Value / (1024.0 * 1024), sharedRootPath);
				return false;
			}

			var drive = new DriveInfo(driveRoot);
			if (!drive.IsReady)
			{
				logger.LogWarning(
					"Upload request from {Ip} to {Path} ({SizeMb:F1} MB): drive '{Drive}' is not ready; bypassing disk check.",
					ip, path, contentLength.Value / (1024.0 * 1024), driveRoot);
				return false;
			}

			var available = drive.AvailableFreeSpace;
			if (available >= contentLength + surplusBytes)
			{
				logger.LogDebug(
					"Upload request from {Ip} to {Path} ({SizeMb:F1} MB): disk check passed (available {AvailMb:F0} MB).",
					ip, path, contentLength.Value / (1024.0 * 1024), available / (1024.0 * 1024));
				return false;
			}

			var availableMb = available / (1024.0 * 1024);
			var requiredMb = (contentLength.Value + surplusBytes) / (1024.0 * 1024);
			logger.LogWarning(
				"Upload rejected for {Ip} to {Path}: insufficient disk space. File {SizeMb:F1} MB, available {AvailMb:F0} MB, required {ReqMb:F0} MB (incl. 100 MB surplus).",
				ip, path, contentLength.Value / (1024.0 * 1024), availableMb, requiredMb);

			diskError = Task.FromResult(Results.Json(
				new
				{
					error = "insufficient_disk_space",
					detail = $"磁盘剩余空间不足。可用 {availableMb:F0} MB，需要至少 {requiredMb:F0} MB（含 100 MB 盈余）。",
					availableBytes = available,
					requiredBytes = contentLength.Value + surplusBytes
				},
				statusCode: StatusCodes.Status507InsufficientStorage));
			return true;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex,
				"Upload request from {Ip} to {Path} ({SizeMb:F1} MB): disk space check threw an exception; bypassing and forwarding to FileBrowser.",
				ip, path, contentLength.Value / (1024.0 * 1024));
			return false;
		}
	}

	/// <summary>
	/// 将 Console.Out / Console.Error 重定向到按日期滚动的日志文件。
	/// 日志目录：当前工作目录下的 logs/，文件名格式 host-yyyy-MM-dd.log。
	/// 保留最近 7 天，启动时自动清理更早的文件。
	/// </summary>
	private static void RedirectConsoleToFile()
	{
		try
		{
			// 使用可执行文件所在目录，安装包安装后为安装目录，开发时为 bin/Debug/net8.0/
			var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
			Directory.CreateDirectory(logsDir);

			// 清理 7 天前的旧日志
			var cutoff = DateTime.UtcNow.AddDays(-7);
			foreach (var old in Directory.EnumerateFiles(logsDir, "host-*.log"))
			{
				try
				{
					if (File.GetLastWriteTimeUtc(old) < cutoff)
						File.Delete(old);
				}
				catch { /* 删除失败不阻断启动 */ }
			}

			var logFile = Path.Combine(logsDir, $"host-{DateTime.Now:yyyy-MM-dd}.log");
			var writer = new StreamWriter(logFile, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };

			// 同时保留控制台输出（如果有控制台窗口的话）
			var multi = new MultiTextWriter(Console.Out, writer);
			Console.SetOut(multi);
			Console.SetError(multi);

			// 打印日志文件路径，方便定位
			Console.WriteLine($"[LoalNas] Log file: {logFile}");
		}
		catch { /* 重定向失败时静默，不影响主流程 */ }
	}

	/// <summary>将写操作同时转发给多个 TextWriter。</summary>
	private sealed class MultiTextWriter(params TextWriter[] writers) : TextWriter
	{
		public override System.Text.Encoding Encoding => writers[0].Encoding;

		public override void Write(char value)
		{
			foreach (var w in writers) w.Write(value);
		}

		public override void Write(string? value)
		{
			foreach (var w in writers) w.Write(value);
		}

		public override void WriteLine(string? value)
		{
			foreach (var w in writers) w.WriteLine(value);
		}

		public override void Flush()
		{
			foreach (var w in writers) w.Flush();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				foreach (var w in writers) w.Dispose();
			base.Dispose(disposing);
		}
	}

	private static StorageSnapshot? TryCreateStorageSnapshot(string sharedRootPath)
	{
		try
		{
			var fullPath = Path.GetFullPath(sharedRootPath);
			var driveRoot = Path.GetPathRoot(fullPath);
			if (string.IsNullOrWhiteSpace(driveRoot))
			{
				return null;
			}

			var driveInfo = new DriveInfo(driveRoot);
			if (!driveInfo.IsReady)
			{
				return null;
			}

			var totalBytes = driveInfo.TotalSize;
			var freeBytes = driveInfo.TotalFreeSpace;
			var usedBytes = totalBytes - freeBytes;
			return new StorageSnapshot(driveInfo.Name, totalBytes, usedBytes, freeBytes);
		}
		catch
		{
			return null;
		}
	}
}

internal sealed record CreateMediaRelayTicketRequest(string Path);
internal sealed record StorageSnapshot(string DriveName, long TotalBytes, long UsedBytes, long FreeBytes);
