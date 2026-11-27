using System.Net;
using LoalNas.Host.Configuration;
using LoalNas.Host.Services;

namespace LoalNas.Host;

internal static class Program
{
    // 本地 /admin 面板改状态用的令牌（每次启动随机；页面里内嵌给同源 JS 使用）
    private static readonly string AdminToken =
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    private static async Task Main(string[] args)
    {
        var app = BuildApplication(args);

        await app.StartAsync();

        var boundUrls = app.Urls.ToArray();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LoalNas.Host.Startup");
        var firewall = app.Services.GetRequiredService<LinuxFirewallEnsurer>();

        Ipv6EndpointReporter.LogAvailableAddresses(logger, boundUrls);
        _ = firewall.EnsureRulesForUrlsAsync(boundUrls);

        var port = GetBoundPort(boundUrls);
        logger.LogInformation("千私云已启动。本地配对面板： http://127.0.0.1:{Port}/admin", port);
        logger.LogInformation("对外服务地址： http://[::]:{Port}（手机/局域网设备访问）", port);

        await app.WaitForShutdownAsync();
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
            options.Limits.MaxRequestBodySize = 2 * 1024 * 1024 * 1024L;
            options.Limits.MaxRequestLineSize = 4 * 1024;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.MaxConcurrentConnections = 200;
            options.Limits.MaxConcurrentUpgradedConnections = 50;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
        });

        builder.Services.Configure<PublicHostOptions>(builder.Configuration.GetSection(PublicHostOptions.SectionName));
        builder.Services.Configure<FirewallOptions>(builder.Configuration.GetSection(FirewallOptions.SectionName));
        builder.Services.Configure<TrashOptions>(builder.Configuration.GetSection(TrashOptions.SectionName));
        builder.Services.Configure<ScreenCaptureOptions>(builder.Configuration.GetSection(ScreenCaptureOptions.SectionName));
        builder.Services.Configure<RemoteTerminalOptions>(builder.Configuration.GetSection(RemoteTerminalOptions.SectionName));
        builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection(MonitorOptions.SectionName));
        builder.Services.Configure<ShareOptions>(builder.Configuration.GetSection(ShareOptions.SectionName));
        builder.Services.Configure<ArchiveOptions>(builder.Configuration.GetSection(ArchiveOptions.SectionName));
        builder.Services.Configure<FileBrowserOptions>(builder.Configuration.GetSection(FileBrowserOptions.SectionName));

        builder.Services.AddHttpClient(FileBrowserApiProxy.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None
        });

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
        builder.Services.AddSingleton<VideoThumbnailService>();
        builder.Services.AddSingleton<AudioTranscodeService>();
        builder.Services.AddSingleton<RemoteTerminalService>();
        builder.Services.AddSingleton<ScreenCaptureService>();
        builder.Services.AddSingleton<MicLiveStreamService>();
        builder.Services.AddSingleton<MonitorService>();
        builder.Services.AddHostedService(services => services.GetRequiredService<MonitorService>());
        builder.Services.AddSingleton<LinuxFirewallEnsurer>();
        builder.Services.AddSingleton<ConnectedDeviceTracker>();
        builder.Services.AddSingleton<DeviceIdentityService>();
        builder.Services.AddSingleton<TrashService>();
        builder.Services.AddHostedService<TrashCleanupService>();
        builder.Services.AddSingleton<ShareRateLimiter>();
        builder.Services.AddSingleton<ShareService>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ShareService>());
        builder.Services.AddSingleton<ArchiveService>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ArchiveService>());

        var app = builder.Build();
        app.UseMiddleware<IpRateLimitMiddleware>();
        app.UseMiddleware<PhoneAuthMiddleware>();
        MapEndpoints(app);
        app.MapShareEndpoints();
        app.MapArchiveEndpoints();
        return app;
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", (FileBrowserProcessManager manager) => Results.Ok(BuildStatusPayload(manager)));

        app.MapGet("/api/system/status", (FileBrowserProcessManager manager) => Results.Ok(BuildStatusPayload(manager)));

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
                    // 携带真实文件名/扩展名，厂商浏览器会按扩展名识别为可播放媒体
                    var fileName = Path.GetFileName(request.Path);
                    var safeName = string.IsNullOrWhiteSpace(fileName) ? "media" : Uri.EscapeDataString(fileName);
                    var url = $"{context.Request.Scheme}://{context.Request.Host}/api/system/media-open/{ticket.TicketId}/{safeName}";
                    return Results.Ok(new { url, expiresAt = ticket.ExpiresAt });
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new { error = "invalid_media_ticket_request", detail = exception.Message });
                }
            });

        app.MapMethods("/api/system/media-open/{ticketId}", new[] { HttpMethods.Get, HttpMethods.Head },
            (HttpContext context, string ticketId, MediaRelayService relay) => relay.ProxyMediaAsync(context, ticketId));

        // 带文件名的形式：/api/system/media-open/{ticketId}/{fileName}
        app.MapMethods("/api/system/media-open/{ticketId}/{**fileName}", new[] { HttpMethods.Get, HttpMethods.Head },
            (HttpContext context, string ticketId, string? fileName, MediaRelayService relay) => relay.ProxyMediaAsync(context, ticketId));

        // ── 音频转码兜底（FLAC 等 → MP3，缓存后按 Range 播放） ───────────────────
        app.MapPost("/api/system/audio-transcode",
            (HttpContext context, AudioTranscodeRequest request, AudioTranscodeService service) =>
            {
                var authToken = context.Request.Headers["X-Auth"].ToString().Trim();
                if (string.IsNullOrEmpty(authToken))
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var info = service.Request(request.Path);
                    return Results.Ok(new
                    {
                        key = info.Key,
                        status = info.Status,
                        progress = info.Progress,
                        error = info.Error,
                        url = info.Url
                    });
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new { error = "invalid_audio_request", detail = exception.Message });
                }
            });

        app.MapGet("/api/system/audio-transcode/status/{key}",
            (string key, AudioTranscodeService service) =>
            {
                var info = service.GetStatus(key);
                return info is null
                    ? Results.NotFound()
                    : Results.Ok(new
                    {
                        key = info.Key,
                        status = info.Status,
                        progress = info.Progress,
                        error = info.Error,
                        url = info.Url
                    });
            });

        app.MapGet("/api/system/audio-cache/{key}/{**fileName}",
            (string key, AudioTranscodeService service) =>
            {
                var path = service.ResolveCacheFile(key);
                return path is null
                    ? Results.NotFound()
                    : Results.File(path, "audio/mpeg", enableRangeProcessing: true);
            });

        // ── 视频封面（ffmpeg 抽帧，按文件缓存） ─────────────────────────────────
        app.MapGet("/api/video-thumb",
            async (HttpContext context, string? path, VideoThumbnailService thumbs) =>
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return Results.BadRequest(new { error = "missing_path" });
                }

                var (status, filePath) = await thumbs.GetThumbnailAsync(path, context.RequestAborted);
                switch (status)
                {
                    case VideoThumbnailService.ThumbnailStatus.Ok:
                        context.Response.Headers["Cache-Control"] = "private, max-age=86400";
                        return Results.File(filePath!, "image/jpeg");
                    case VideoThumbnailService.ThumbnailStatus.NotFound:
                        return Results.NotFound();
                    case VideoThumbnailService.ThumbnailStatus.Invalid:
                        return Results.BadRequest(new { error = "invalid_path" });
                    default:
                        return Results.StatusCode(StatusCodes.Status500InternalServerError);
                }
            });

        // ── 远程终端（默认状态由 LoalNas:RemoteTerminal 配置决定；/admin 可临时开关） ────
        app.MapGet("/api/system/terminal/status",
            (RemoteTerminalService terminal) =>
                Results.Ok(new { enabled = terminal.Enabled, expiresAt = terminal.ExpiresAt }));

        app.MapPost("/api/system/terminal/exec",
            async (HttpContext context, TerminalExecRequest request, RemoteTerminalService terminal) =>
            {
                if (!terminal.Enabled)
                {
                    // 未开启时连能力都不暴露
                    return Results.NotFound();
                }

                var command = (request.Command ?? string.Empty).Trim();
                if (command.Length == 0)
                {
                    return Results.BadRequest(new { error = "empty_command" });
                }
                if (command.Length > 4000)
                {
                    return Results.BadRequest(new { error = "command_too_long" });
                }

                var result = await terminal.ExecuteAsync(
                    command,
                    context.Connection.RemoteIpAddress?.ToString(),
                    context.RequestAborted);

                return Results.Ok(new
                {
                    exitCode = result.ExitCode,
                    stdout = result.Stdout,
                    stderr = result.Stderr,
                    truncated = result.Truncated,
                    timedOut = result.TimedOut,
                    durationMs = result.DurationMs,
                });
            });

        // ── 画面取图：source=desktop（默认，兼容旧客户端）| camera ────────────
        app.MapGet("/api/system/screen",
            async (HttpContext context, ScreenCaptureService screen, MonitorService monitor,
                    string? source, int? w, int? q, string? format) =>
            {
                if (ScreenCaptureService.IsCamera(source) && !monitor.StatusEnabled)
                {
                    return Results.Json(new { error = "monitor_disabled" }, statusCode: 403);
                }

                var (resolvedSource, width, quality, fmt) = screen.Normalize(source, w, q, format);
                var frame = await screen.GetFrameAsync(resolvedSource, width, quality, fmt, context.RequestAborted);
                if (frame is null)
                {
                    return Results.Json(new { error = "no_frame" }, statusCode: 503);
                }

                context.Response.Headers.CacheControl = "no-store";
                var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
                if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == frame.ETag)
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                context.Response.Headers.ETag = frame.ETag;
                return Results.File(frame.Bytes, frame.ContentType);
            });

        // ── 监控：状态、移动侦测开关、手动录像、事件片段 ─────────────────────
        app.MapGet("/api/monitor/status", (MonitorService monitor) =>
            monitor.StatusEnabled
                ? Results.Ok(monitor.Status())
                : Results.Json(new { error = "monitor_disabled" }, statusCode: 403));

        app.MapPost("/api/monitor/detect",
            (MonitorService monitor, MonitorDetectRequest request) =>
            {
                if (!monitor.StatusEnabled)
                {
                    return Results.Json(new { error = "monitor_disabled" }, statusCode: 403);
                }

                monitor.ConfigureDetect(request.Enabled, request.Sensitivity);
                return Results.Ok(monitor.Status());
            });

        app.MapPost("/api/monitor/record", (MonitorService monitor) =>
        {
            if (!monitor.StatusEnabled)
            {
                return Results.Json(new { error = "monitor_disabled" }, statusCode: 403);
            }

            monitor.RequestManualRecording();
            return Results.Ok(new { accepted = true });
        });

        app.MapGet("/api/monitor/clips", (MonitorService monitor, string? date) =>
            monitor.StatusEnabled
                ? Results.Ok(new
                {
                    dates = monitor.ClipDates(),
                    date = string.IsNullOrWhiteSpace(date) ? DateTime.Now.ToString("yyyy-MM-dd") : date,
                    items = monitor.ListClips(date),
                })
                : Results.Json(new { error = "monitor_disabled" }, statusCode: 403));

        // 听现场：播放器插件带不了每请求签名头，和音频转码缓存一样用一次性不可猜票据放行。
        app.MapPost("/api/monitor/listen-ticket", (MonitorService monitor) =>
        {
            if (!monitor.StatusEnabled)
            {
                return Results.Json(new { error = "monitor_disabled" }, statusCode: 403);
            }

            var options = monitor.ListenOptions;
            if (!options.Enabled)
            {
                return Results.Json(new { error = "listen_disabled" }, statusCode: 403);
            }

            var key = monitor.IssueListenTicket();
            return Results.Ok(new
            {
                path = $"/api/system/monitor-live/{key}/live.mp3",
                expiresInSeconds = Math.Clamp(options.ListenTicketTtlSeconds, 30, 3600),
            });
        });

        app.MapGet("/api/system/monitor-live/{key}/live.mp3",
            async (HttpContext context, string key, MonitorService monitor, MicLiveStreamService live) =>
            {
                if (!monitor.TryConsumeListenTicket(key))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsJsonAsync(new { error = "bad_ticket" });
                    return;
                }

                var options = monitor.ListenOptions;
                await live.StreamAsync(context, options.ListenBitrateKbps,
                    options.ListenSampleRate, options.ListenMaxSeconds,
                    context.RequestAborted);
            });

        app.MapGet("/admin/terminal/status", (HttpContext context, RemoteTerminalService terminal) =>
            IsLoopback(context)
                ? Results.Ok(new { enabled = terminal.Enabled, expiresAt = terminal.ExpiresAt })
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapPost("/admin/terminal/enable",
            (HttpContext context, AdminTerminalEnableRequest request, RemoteTerminalService terminal) =>
            {
                if (!IsLoopback(context) || !HasAdminToken(context))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                var minutes = Math.Clamp(request.Minutes, 0, 60 * 24 * 30);
                terminal.Enable(minutes <= 0 ? null : TimeSpan.FromMinutes(minutes));
                return Results.Ok(new { enabled = terminal.Enabled, expiresAt = terminal.ExpiresAt });
            });

        app.MapPost("/admin/terminal/disable", (HttpContext context, RemoteTerminalService terminal) =>
        {
            if (!IsLoopback(context) || !HasAdminToken(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            terminal.Disable();
            return Results.Ok(new { enabled = false });
        });

        // ── 回收站 ─────────────────────────────────────────────────────────────
        app.MapGet("/api/trash", (TrashService trash) => Results.Ok(new { items = trash.List() }));

        app.MapPost("/api/trash/restore", async (HttpContext context, TrashService trash) =>
        {
            string body;
            using (var reader = new StreamReader(context.Request.Body))
            {
                body = await reader.ReadToEndAsync();
            }

            TrashRestoreRequest? request = null;
            try
            {
                request = System.Text.Json.JsonSerializer.Deserialize<TrashRestoreRequest>(
                    body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                // 按无效请求处理
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Id))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_request" });
                return;
            }

            var status = trash.Restore(request.Id, out var error);
            context.Response.StatusCode = status switch
            {
                TrashService.TrashStatus.Ok => StatusCodes.Status200OK,
                TrashService.TrashStatus.NotFound => StatusCodes.Status404NotFound,
                TrashService.TrashStatus.Conflict => StatusCodes.Status409Conflict,
                TrashService.TrashStatus.Invalid => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status500InternalServerError,
            };
            await context.Response.WriteAsJsonAsync(new { ok = status == TrashService.TrashStatus.Ok, error });
        });

        app.MapDelete("/api/trash/{id}", (string id, TrashService trash) =>
            trash.Purge(id) == TrashService.TrashStatus.Ok
                ? Results.NoContent()
                : Results.NotFound(new { error = "not_found" }));

        app.MapDelete("/api/trash", (TrashService trash) => Results.Ok(new { removed = trash.Empty() }));

        var proxyMethods = new[]
        {
            HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch,
            HttpMethods.Delete, HttpMethods.Head, HttpMethods.Options
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
            async (HttpContext context, string path, FileBrowserProcessManager manager, FileBrowserApiProxy proxy, ILoggerFactory loggerFactory, TrashService trash) =>
            {
                // 删除改为移入回收站（仅针对 resources 下的具体资源，根目录不处理）
                if (HttpMethods.IsDelete(context.Request.Method) &&
                    path.StartsWith("resources", StringComparison.OrdinalIgnoreCase))
                {
                    var resourcePath = path["resources".Length..];
                    if (!string.IsNullOrWhiteSpace(resourcePath) && resourcePath != "/")
                    {
                        var status = trash.MoveToTrash(resourcePath, out var entry);
                        if (status == TrashService.TrashStatus.Ok)
                        {
                            await context.Response.WriteAsJsonAsync(new { trashed = true, id = entry!.Id });
                            return;
                        }

                        context.Response.StatusCode = status switch
                        {
                            TrashService.TrashStatus.NotFound => StatusCodes.Status404NotFound,
                            TrashService.TrashStatus.Invalid => StatusCodes.Status400BadRequest,
                            _ => StatusCodes.Status500InternalServerError,
                        };
                        await context.Response.WriteAsJsonAsync(new { error = status.ToString().ToLowerInvariant() });
                        return;
                    }
                }

                if (HttpMethods.IsPost(context.Request.Method) &&
                    CheckDiskSpaceInsufficient(context, manager.SharedRootPath, loggerFactory, out var diskError))
                {
                    var result = await diskError!;
                    await result.ExecuteAsync(context);
                    return;
                }

                await proxy.ProxyApiAsync(context, path);
            });

        // ── 本地回环面板：展示配对二维码，完成密钥传递 ──────────────────────────
        app.MapGet("/admin", (HttpContext context) =>
            IsLoopback(context)
                ? Results.Content(BuildAdminHtml(), "text/html; charset=utf-8")
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapGet("/api/device/info", (HttpContext context, FileBrowserProcessManager manager, DeviceIdentityService identity) =>
        {
            if (!IsLoopback(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var ipv6 = NetworkInfoService.GetStablePublicIpv6()?.ToString();
            var lanIpv4 = NetworkInfoService.GetLanIpv4Addresses().Select(a => a.ToString()).ToArray();
            return Results.Ok(new
            {
                deviceName = identity.DeviceName,
                deviceId = identity.DeviceId,
                addresses = new { ipv6, lanIpv4 },
                fileBrowser = new
                {
                    running = manager.IsRunning,
                    sharedRoot = manager.SharedRootPath
                },
                storage = TryCreateStorageSnapshot(manager.SharedRootPath)
            });
        });

        app.MapGet("/api/device/qr.png", (HttpContext context, DeviceIdentityService identity, IConfiguration config) =>
        {
            if (!IsLoopback(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var payload = BuildBindingPayload(identity, config);
            var png = QrService.CreatePng(payload);
            return Results.File(png, "image/png");
        });

        app.MapGet("/api/device/payload", (HttpContext context, DeviceIdentityService identity, IConfiguration config) =>
            IsLoopback(context)
                ? Results.Content(BuildBindingPayload(identity, config), "application/json; charset=utf-8")
                : Results.StatusCode(StatusCodes.Status403Forbidden));
    }

    private static object BuildStatusPayload(FileBrowserProcessManager manager) => new
    {
        name = "千私云",
        fileBrowser = new
        {
            running = manager.IsRunning,
            baseUrl = manager.BaseAddress.ToString(),
            sharedRoot = manager.SharedRootPath
        },
        storage = TryCreateStorageSnapshot(manager.SharedRootPath),
        endpoints = new
        {
            status = "/api/system/status",
            fileBrowserApi = "/api/filebrowser/{...}",
            mediaTicket = "/api/system/media-tickets",
            admin = "/admin（仅本机）"
        }
    };

    /// <summary>二维码内容：设备 ID、名称、secret、可达地址（局域网优先，附带可选公网入口）。</summary>
    private static string BuildBindingPayload(DeviceIdentityService identity, IConfiguration config)
    {
        var lan = NetworkInfoService.GetLanIpv4Addresses().FirstOrDefault()?.ToString();
        var ipv6 = NetworkInfoService.GetStablePublicIpv6()?.ToString();
        var publicBase = config["LoalNas:Host:PublicBaseUrl"]?.Trim();
        if (string.IsNullOrWhiteSpace(publicBase))
        {
            publicBase = null;
        }

        var payload = new Dictionary<string, object?>
        {
            ["deviceId"] = identity.DeviceId,
            ["deviceName"] = identity.DeviceName,
            ["secret"] = identity.DeviceSecret,
            ["ip"] = lan ?? ipv6,
            ["lan"] = lan,
            ["public"] = publicBase
        };

        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    private static bool IsLoopback(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(remote))
        {
            return true;
        }

        // IPv4-mapped IPv6 (::ffff:127.0.0.1)
        return remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4());
    }

    private static int GetBoundPort(string[] boundUrls)
    {
        foreach (var url in boundUrls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return uri.Port;
            }
        }

        return 5034;
    }

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
            return false;
        }

        const long surplusBytes = 100 * 1024 * 1024L;
        try
        {
            var drive = DiskSpaceProbe.ResolveDrive(sharedRootPath);
            if (drive is null || !drive.IsReady)
            {
                return false;
            }

            var available = drive.AvailableFreeSpace;
            if (available >= contentLength + surplusBytes)
            {
                return false;
            }

            var availableMb = available / (1024.0 * 1024);
            var requiredMb = (contentLength.Value + surplusBytes) / (1024.0 * 1024);
            logger.LogWarning(
                "Upload rejected for {Ip} to {Path}: insufficient disk space. File {SizeMb:F1} MB, available {AvailMb:F0} MB, required {ReqMb:F0} MB.",
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
            logger.LogWarning(ex, "Disk space check failed for {Ip} to {Path}; bypassing.", ip, path);
            return false;
        }
    }

    private static StorageSnapshot? TryCreateStorageSnapshot(string sharedRootPath)
    {
        try
        {
            var driveInfo = DiskSpaceProbe.ResolveDrive(sharedRootPath);
            if (driveInfo is null || !driveInfo.IsReady)
            {
                return null;
            }

            var totalBytes = driveInfo.TotalSize;
            // 实际占用用 TotalFreeSpace 计算；可用空间用 AvailableFreeSpace（Linux ext4 会扣除保留块）
            var usedBytes = totalBytes - driveInfo.TotalFreeSpace;
            var freeBytes = driveInfo.AvailableFreeSpace;
            return new StorageSnapshot(driveInfo.Name, totalBytes, usedBytes, freeBytes);
        }
        catch
        {
            return null;
        }
    }

    private static bool HasAdminToken(HttpContext context) =>
        string.Equals(
            context.Request.Headers["X-Admin-Token"].ToString(),
            AdminToken,
            StringComparison.Ordinal);

    private static string BuildAdminHtml() => AdminHtml.Replace("__ADMIN_TOKEN__", AdminToken);

    private const string AdminHtml = """
<!doctype html>
<html lang="zh">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>千私云 · 设备配对</title>
<style>
  :root { --bg:#f6f8fc; --card:#fff; --border:#e6eaf2; --text:#1c2028; --muted:#727c8a; --accent:#316bff; }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--text);
         font-family:"Segoe UI","Noto Sans CJK SC",system-ui,sans-serif; }
  main { max-width:720px; margin:0 auto; padding:32px 20px 56px; }
  h1 { font-size:26px; margin:0 0 4px; }
  .sub { color:var(--muted); margin:0 0 24px; font-size:14px; }
  .card { background:var(--card); border:1px solid var(--border); border-radius:18px;
          padding:22px; margin-bottom:18px; }
  .qr-wrap { display:flex; gap:22px; align-items:center; flex-wrap:wrap; }
  #qr { width:220px; height:220px; background:#fff; border:1px solid var(--border);
        border-radius:14px; padding:8px; }
  .hint { flex:1; min-width:220px; color:var(--muted); font-size:13px; line-height:1.7; }
  h2 { font-size:16px; margin:0 0 14px; }
  dl { margin:0; display:grid; grid-template-columns:120px 1fr; gap:10px 14px; font-size:13px; }
  dt { color:var(--muted); }
  dd { margin:0; word-break:break-all; font-weight:600; }
  .badge { display:inline-block; padding:2px 10px; border-radius:999px; font-size:12px; font-weight:700; }
  .ok { background:#e8f9f0; color:#1aa268; }
  .off { background:#fdeff2; color:#d24e5b; }
  .note { color:var(--muted); font-size:12px; line-height:1.7; margin:8px 2px 0; }
  .bar { height:10px; border-radius:5px; background:#eaeeF4; overflow:hidden; margin-top:6px; }
  .bar > i { display:block; height:100%; background:var(--accent); }
  button { border:1px solid var(--border); background:#fff; color:var(--text);
           border-radius:10px; padding:6px 12px; font-size:12px; cursor:pointer; }
</style>
</head>
<body>
<main>
  <h1>千私云</h1>
  <p class="sub">用手机客户端扫描下方二维码，完成密钥传递与设备配对</p>

  <section class="card">
    <div class="qr-wrap">
      <img id="qr" alt="配对二维码" src="/api/device/qr.png">
      <div class="hint">
        二维码内含设备 ID、名称、访问密钥（secret）和当前可达地址。<br>
        手机扫码后即可自动填充连接信息，无需手动输入。<br><br>
        <button onclick="location.reload()">刷新二维码</button>
      </div>
    </div>
  </section>

  <section class="card">
    <h2>设备信息</h2>
    <dl id="details"><dt>状态</dt><dd>加载中…</dd></dl>
  </section>

  <section class="card">
    <h2>远程终端</h2>
    <p class="note" style="margin:0 0 12px">
      开启后，手机端「工具 → 终端指令」可以让电脑执行命令（以 kali 身份）。到期自动关闭，服务重启也会关闭。
    </p>
    <p style="font-size:13px;margin:0 0 12px">状态：<span id="term-state">读取中…</span></p>
    <div style="display:flex;gap:8px;flex-wrap:wrap">
      <button onclick="termEnable(30)">开启 30 分钟</button>
      <button onclick="termEnable(120)">开启 2 小时</button>
      <button onclick="termEnable(480)">开启 8 小时</button>
      <button onclick="termEnable(0)">永久开启（不推荐）</button>
      <button onclick="termDisable()">立即关闭</button>
    </div>
  </section>

  <p class="note">
    此页面仅本机（回环地址）可访问；远程查看请通过 SSH 隧道转发 5034 端口。<br>
    二维码包含设备密钥，请勿截图外传。文件传输不经过任何云端中转。
  </p>
</main>
<script>
function fmt(bytes){ if(bytes==null) return "—";
  var u=["B","KB","MB","GB","TB"],i=0,n=bytes;
  while(n>=1024&&i<u.length-1){n/=1024;i++;}
  return n.toFixed(i?1:0)+" "+u[i]; }
fetch('/api/device/info').then(function(r){return r.json();}).then(function(d){
  var st=d.fileBrowser&&d.fileBrowser.running;
  var s=d.storage||{};
  var pct=s.totalBytes?Math.round(s.usedBytes/s.totalBytes*100):0;
  var addr=d.addresses||{};
  var lan=(addr.lanIpv4||[]).join(', ')||'—';
  var html='';
  html+='<dt>设备名称</dt><dd>'+(d.deviceName||'—')+'</dd>';
  html+='<dt>设备 ID</dt><dd>'+(d.deviceId||'—')+'</dd>';
  html+='<dt>文件服务</dt><dd><span class="badge '+(st?'ok':'off')+'">'+(st?'运行中':'未运行')+'</span></dd>';
  html+='<dt>公网 IPv6</dt><dd>'+(addr.ipv6||'未检测到')+'</dd>';
  html+='<dt>局域网 IPv4</dt><dd>'+lan+'</dd>';
  html+='<dt>共享目录</dt><dd>'+((d.fileBrowser&&d.fileBrowser.sharedRoot)||'—')+'</dd>';
  html+='<dt>存储空间</dt><dd>'+fmt(s.usedBytes)+' / '+fmt(s.totalBytes)+'（'+pct+'%）'+
        '<div class="bar"><i style="width:'+pct+'%"></i></div></dd>';
  document.getElementById('details').innerHTML=html;
}).catch(function(e){
  document.getElementById('details').innerHTML='<dt>状态</dt><dd>读取失败</dd>';
});

var ADMIN_TOKEN='__ADMIN_TOKEN__';
function termRefresh(){
  fetch('/admin/terminal/status').then(function(r){return r.json();}).then(function(d){
    var el=document.getElementById('term-state');
    if(!d.enabled){ el.textContent='已关闭'; return; }
    if(!d.expiresAt){ el.textContent='已开启（永久）'; return; }
    el.textContent='已开启，到期 '+new Date(d.expiresAt).toLocaleString();
  }).catch(function(){ document.getElementById('term-state').textContent='读取失败'; });
}
function termEnable(minutes){
  fetch('/admin/terminal/enable',{method:'POST',headers:{'X-Admin-Token':ADMIN_TOKEN,'Content-Type':'application/json'},
    body:JSON.stringify({minutes:minutes})}).then(termRefresh);
}
function termDisable(){
  fetch('/admin/terminal/disable',{method:'POST',headers:{'X-Admin-Token':ADMIN_TOKEN}}).then(termRefresh);
}
termRefresh();
</script>
</body>
</html>
""";
}

internal sealed record CreateMediaRelayTicketRequest(string Path);
internal sealed record AudioTranscodeRequest(string Path);
internal sealed record TerminalExecRequest(string? Command);
internal sealed record MonitorDetectRequest(bool? Enabled, string? Sensitivity);
internal sealed record AdminTerminalEnableRequest(int Minutes);
internal sealed record TrashRestoreRequest(string Id);
internal sealed record StorageSnapshot(string DriveName, long TotalBytes, long UsedBytes, long FreeBytes);
