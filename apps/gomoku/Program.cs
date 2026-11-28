using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoalNas.Gomoku;

var port = GetIntArg(args, "port") ?? 5099;
var controlToken = GetArg(args, "control-token") ?? "";
var basePath = GetArg(args, "base-path") ?? "/apps/gomoku";
var dataDirectory = GetArg(args, "data-dir") ?? "";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddSingleton<RoomStore>();
builder.Services.AddHostedService<RoomTimer>();

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Gomoku");

logger.LogInformation(
    "五子棋应用启动：端口 {Port}，前缀 {BasePath}，数据目录 {DataDirectory}",
    port, basePath, string.IsNullOrEmpty(dataDirectory) ? "（不使用）" : dataDirectory);

// 控制接口只允许宿主访问（随机令牌由宿主通过启动参数传入）。
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var isControl = path.Equals("/rooms", StringComparison.Ordinal) ||
                    path.StartsWith("/rooms/", StringComparison.Ordinal) ||
                    path.Equals("/shutdown", StringComparison.Ordinal);

    if (isControl)
    {
        var provided = context.Request.Headers["X-App-Control"].ToString();
        if (string.IsNullOrEmpty(controlToken) || !FixedTimeEquals(provided, controlToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }
    }

    await next();
});

app.MapGet("/health", (RoomStore store) => Results.Ok(new
{
    ok = true,
    rooms = store.Count,
    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
}));

// ── 控制接口（仅宿主） ─────────────────────────────────────────────────────
app.MapPost("/rooms", (CreateRoomRequest request, RoomStore store) =>
{
    var timeout = 60;
    if (request.Options is not null &&
        request.Options.TryGetValue("moveTimeoutSeconds", out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out var configured))
    {
        timeout = configured;
    }

    if (!store.TryCreate(request.Token, request.HostKey, timeout, out _, out var error))
    {
        return Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);
    }

    return Results.Ok(new { ok = true });
});

app.MapDelete("/rooms/{token}", (string token, RoomStore store) =>
    store.Remove(token)
        ? Results.Ok(new { ok = true })
        : Results.NotFound(new { error = "room_not_found" }));

app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(150);
        lifetime.StopApplication();
    });

    return Results.Accepted(value: new { stopping = true });
});

// ── 公开页面与对局接口 ─────────────────────────────────────────────────────
var gamePagePath = Path.Combine(AppContext.BaseDirectory, "web", "game.html");
var gamePage = File.Exists(gamePagePath)
    ? await File.ReadAllTextAsync(gamePagePath)
    : Pages.FallbackPage;

app.MapGet("/", () => Results.Content(Pages.LandingPage, "text/html; charset=utf-8"));

app.MapGet("/{token}", (string token, RoomStore store) =>
{
    if (!RoomStore.IsValidToken(token) || store.Find(token) is null)
    {
        return Results.Content(Pages.ClosedPage, "text/html; charset=utf-8", statusCode: 404);
    }

    return Results.Content(gamePage, "text/html; charset=utf-8");
});

app.MapPost("/{token}/join", (string token, JoinRequest request, RoomStore store) =>
{
    var room = store.Find(token);
    if (room is null)
    {
        return RoomGone();
    }

    var clientId = NormalizeClientId(request.ClientId);
    if (clientId is null)
    {
        return Results.BadRequest(new { error = "invalid_client" });
    }

    room.Join(clientId, request.HostKey, out _);
    var snapshot = room.Snapshot(clientId);
    return snapshot.Removed ? RoomGone() : Results.Ok(snapshot);
});

app.MapGet("/{token}/state", async (
    HttpContext context,
    string token,
    string? clientId,
    long? version,
    string? wait,
    RoomStore store) =>
{
    var room = store.Find(token);
    if (room is null)
    {
        return Results.Json(new { error = "room_not_found", removed = true },
            statusCode: StatusCodes.Status404NotFound);
    }

    var snapshot = room.Snapshot(clientId);
    if (snapshot.Removed)
    {
        return Results.Json(snapshot, statusCode: StatusCodes.Status410Gone);
    }

    if (wait == "1" && version is long expected && expected == snapshot.Version)
    {
        snapshot = await room.WaitForChangeAsync(
            clientId, snapshot.Version, TimeSpan.FromSeconds(20), context.RequestAborted);

        if (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }

        if (snapshot.Removed)
        {
            return Results.Json(snapshot, statusCode: StatusCodes.Status410Gone);
        }
    }

    return Results.Ok(snapshot);
});

app.MapPost("/{token}/move", (string token, MoveRequest request, RoomStore store) =>
{
    var room = store.Find(token);
    if (room is null)
    {
        return RoomGone();
    }

    var clientId = NormalizeClientId(request.ClientId);
    if (clientId is null)
    {
        return Results.BadRequest(new { error = "invalid_client" });
    }

    var error = room.Move(clientId, request.X, request.Y, out _);
    if (error is not null)
    {
        return ActionError(error);
    }

    var snapshot = room.Snapshot(clientId);
    return snapshot.Removed ? RoomGone() : Results.Ok(snapshot);
});

app.MapPost("/{token}/resign", (string token, ClientRequest request, RoomStore store) =>
{
    var room = store.Find(token);
    if (room is null)
    {
        return RoomGone();
    }

    var clientId = NormalizeClientId(request.ClientId);
    if (clientId is null)
    {
        return Results.BadRequest(new { error = "invalid_client" });
    }

    var error = room.Resign(clientId);
    if (error is not null)
    {
        return ActionError(error);
    }

    var snapshot = room.Snapshot(clientId);
    return snapshot.Removed ? RoomGone() : Results.Ok(snapshot);
});

app.MapPost("/{token}/rematch", (string token, RematchRequest request, RoomStore store) =>
{
    var room = store.Find(token);
    if (room is null)
    {
        return RoomGone();
    }

    var clientId = NormalizeClientId(request.ClientId);
    if (clientId is null)
    {
        return Results.BadRequest(new { error = "invalid_client" });
    }

    var error = room.Rematch(clientId, request.Action);
    if (error is not null)
    {
        return ActionError(error);
    }

    var snapshot = room.Snapshot(clientId);
    return snapshot.Removed ? RoomGone() : Results.Ok(snapshot);
});

app.Run();

static string? NormalizeClientId(string? clientId)
{
    var value = clientId?.Trim();
    return value is { Length: >= 8 and <= 64 } &&
           value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        ? value
        : null;
}

static IResult RoomGone() =>
    Results.Json(new { error = "room_not_found", removed = true },
        statusCode: StatusCodes.Status410Gone);

static IResult ActionError(string code) => Results.Json(
    new { error = code },
    statusCode: code is "removed" or "room_not_found"
        ? StatusCodes.Status410Gone
        : StatusCodes.Status409Conflict);

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--" + name)
        {
            return i + 1 < args.Length ? args[i + 1] : null;
        }

        if (args[i].StartsWith("--" + name + "=", StringComparison.Ordinal))
        {
            return args[i][("--" + name + "=").Length..];
        }
    }

    return null;
}

static int? GetIntArg(string[] args, string name) =>
    int.TryParse(GetArg(args, name), out var value) ? value : null;

static bool FixedTimeEquals(string left, string right)
{
    if (left.Length != right.Length)
    {
        return false;
    }

    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}

internal sealed record CreateRoomRequest(
    string? Token,
    string? HostKey,
    Dictionary<string, JsonElement>? Options);

internal sealed record JoinRequest(string? ClientId, string? HostKey);

internal sealed record ClientRequest(string? ClientId);

internal sealed record MoveRequest(string? ClientId, int X, int Y);

internal sealed record RematchRequest(string? ClientId, string? Action);

internal sealed class RoomTimer(RoomStore store, ILogger<RoomTimer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    store.SweepAll();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "扫描超时房间失败。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }
}

internal static class Pages
{
    public const string FallbackPage = """
<!doctype html>
<html lang="zh"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>五子棋</title>
<style>body{font-family:system-ui,sans-serif;padding:40px;color:#333}</style>
</head><body><h1>五子棋</h1><p>对局页面缺失，请重新部署应用。</p></body></html>
""";

    public const string LandingPage = """
<!doctype html>
<html lang="zh"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>五子棋</title>
<style>
body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
background:#f6f8fc;color:#1c2028;font-family:"Segoe UI","Noto Sans CJK SC",system-ui,sans-serif}
.card{background:#fff;border:1px solid #e6eaf2;border-radius:18px;padding:36px 30px;
max-width:430px;margin:20px;text-align:center}
h1{font-size:22px;margin:0 0 12px}p{color:#727c8a;font-size:14px;line-height:1.8;margin:0}
</style></head><body><div class="card">
<h1>五子棋</h1>
<p>请先在千私云手机端「工具 → 五子棋」创建房间，<br>再把房间链接发给好友。</p>
<p style="margin-top:14px;font-size:12px">公开链接不能创建房间，也不会唤醒应用进程。</p>
</div></body></html>
""";

    public const string ClosedPage = """
<!doctype html>
<html lang="zh"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>房间已关闭</title>
<style>
body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
background:#f6f8fc;color:#1c2028;font-family:"Segoe UI","Noto Sans CJK SC",system-ui,sans-serif}
.card{background:#fff;border:1px solid #e6eaf2;border-radius:18px;padding:36px 30px;
max-width:430px;margin:20px;text-align:center}
h1{font-size:20px;margin:0 0 12px}p{color:#727c8a;font-size:14px;line-height:1.8;margin:0}
</style></head><body><div class="card">
<h1>房间已关闭或已过期</h1>
<p>请让房主在千私云手机端重新创建房间。</p>
</div></body></html>
""";
}
