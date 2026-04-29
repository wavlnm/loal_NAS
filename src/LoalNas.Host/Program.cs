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
	private static async Task Main(string[] args)
	{
		var app = BuildApplication(args);
		var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LoalNas.Host.Startup");
		var firewallEnsurer = app.Services.GetRequiredService<WindowsFirewallRuleEnsurer>();
		var fileBrowserManager = app.Services.GetRequiredService<FileBrowserProcessManager>();
		var appLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

		var started = false;

		try
		{
			await app.StartAsync();
			started = true;

			var boundUrls = app.Urls.ToArray();
			Ipv6EndpointReporter.LogAvailableAddresses(startupLogger, boundUrls);
			_ = firewallEnsurer.EnsureRulesForUrlsAsync(boundUrls);

			ApplicationConfiguration.Initialize();
			using var form = new HostStatusForm(fileBrowserManager, appLifetime, boundUrls);
			Application.Run(form);
		}
		finally
		{
			if (started)
			{
				await app.StopAsync();
			}

			await app.DisposeAsync();
		}
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
			AutomaticDecompression = DecompressionMethods.All
		});

 		builder.Services.AddSingleton<FileBrowserProcessManager>();
		builder.Services.AddHostedService(services => services.GetRequiredService<FileBrowserProcessManager>());
		builder.Services.AddSingleton<FileBrowserApiProxy>();
		builder.Services.AddSingleton<WindowsFirewallRuleEnsurer>();

		var app = builder.Build();
		MapEndpoints(app);
		return app;
	}

	private static void MapEndpoints(WebApplication app)
	{
		app.MapGet("/", (FileBrowserProcessManager manager) => Results.Ok(new
	{
			name = "loal_NAS MVP host",
			fileBrowser = new
			{
				running = manager.IsRunning,
				baseUrl = manager.BaseAddress.ToString(),
				sharedRoot = manager.SharedRootPath
			},
			endpoints = new
			{
				status = "/api/system/status",
				fileBrowserApi = "/api/filebrowser/{...}"
			}
		}));

		app.MapGet("/api/system/status", (FileBrowserProcessManager manager) => Results.Ok(new
		{
			fileBrowserRunning = manager.IsRunning,
			fileBrowserBaseUrl = manager.BaseAddress.ToString(),
			sharedRootPath = manager.SharedRootPath
		}));

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
}
