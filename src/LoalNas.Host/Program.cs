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
			options.Limits.MaxRequestBodySize = null;
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
				name = "loal_NAS MVP host",
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
				name = "loal_NAS MVP host",
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
			(HttpContext context, FileBrowserApiProxy proxy) => proxy.ProxyApiAsync(context, string.Empty));
		app.MapMethods("/api/filebrowser/{**path}", proxyMethods,
			(HttpContext context, string path, FileBrowserApiProxy proxy) => proxy.ProxyApiAsync(context, path));
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
