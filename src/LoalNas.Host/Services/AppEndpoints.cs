using System.Text.Json;

namespace LoalNas.Host.Services;

/// <summary>手机端房间管理接口（/api/apps/...，必须带签名头）。</summary>
public static class AppEndpoints
{
    public static void MapAppEndpoints(this WebApplication app)
    {
        app.MapGet("/api/apps", (
            AppRegistry registry,
            AppProcessManager processes,
            AppRoomService rooms) =>
        {
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new
            {
                apps = registry.All.Select(manifest => new
                {
                    id = manifest.Id,
                    name = manifest.Name,
                    enabled = manifest.Enabled,
                    prefix = manifest.PublicPrefix,
                    running = processes.IsRunning(manifest),
                    roomCount = rooms.CountForApp(manifest.Id),
                    maxRooms = manifest.MaxRooms,
                    defaultRoomLifetimeHours = manifest.DefaultRoomLifetimeHours,
                    maxRoomLifetimeHours = manifest.MaxRoomLifetimeHours,
                    urlTemplate = manifest.PublicPrefix + "/{token}",
                }),
            });
        });

        app.MapPost("/api/apps/{appId}/rooms", async (
            HttpContext context,
            string appId,
            CreateAppRoomRequest? request,
            AppRegistry registry,
            AppRoomService rooms) =>
        {
            var manifest = registry.Find(appId);
            if (manifest is null || !manifest.Enabled)
            {
                return Results.NotFound(new { error = "app_not_found" });
            }

            try
            {
                var room = await rooms.CreateAsync(
                    manifest,
                    request?.ExpiresInSeconds,
                    request?.Options,
                    context.Request,
                    context.RequestAborted);

                return Results.Ok(new
                {
                    token = room.Token,
                    hostKey = room.HostKey,
                    url = room.Url,
                    ownerUrl = room.OwnerUrl,
                    createdAt = room.CreatedAt,
                    expiresAt = room.ExpiresAt,
                    remainingSeconds = room.RemainingSeconds(DateTimeOffset.UtcNow),
                });
            }
            catch (AppRoomException ex)
            {
                return Results.Json(
                    new { error = ex.Code, detail = ex.Message },
                    statusCode: ex.StatusCode);
            }
        });

        app.MapGet("/api/apps/{appId}/rooms", (
            string appId,
            AppRegistry registry,
            AppRoomService rooms,
            AppProcessManager processes) =>
        {
            var manifest = registry.Find(appId);
            if (manifest is null)
            {
                return Results.NotFound(new { error = "app_not_found" });
            }

            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new
            {
                appId = manifest.Id,
                name = manifest.Name,
                running = processes.IsRunning(manifest),
                maxRooms = manifest.MaxRooms,
                defaultRoomLifetimeHours = manifest.DefaultRoomLifetimeHours,
                maxRoomLifetimeHours = manifest.MaxRoomLifetimeHours,
                rooms = rooms.ListForApp(manifest.Id).Select(room => new
                {
                    token = room.Token,
                    url = room.Url,
                    ownerUrl = room.OwnerUrl,
                    createdAt = room.CreatedAt,
                    expiresAt = room.ExpiresAt,
                    remainingSeconds = room.RemainingSeconds(now),
                }),
            });
        });

        app.MapDelete("/api/apps/{appId}/rooms/{token}", async (
            string appId,
            string token,
            AppRoomService rooms,
            HttpContext context) =>
        {
            var closed = await rooms.CloseAsync(
                appId, token, "manual_close", context.RequestAborted);
            return closed
                ? Results.Ok(new { closed = true })
                : Results.NotFound(new { error = "room_not_found" });
        });

        app.MapDelete("/api/apps/{appId}/rooms", async (
            string appId,
            AppRegistry registry,
            AppRoomService rooms,
            HttpContext context) =>
        {
            if (registry.Find(appId) is null)
            {
                return Results.NotFound(new { error = "app_not_found" });
            }

            await rooms.CloseAllAsync(appId, "manual_close_all", context.RequestAborted);
            return Results.Ok(new { closed = true });
        });

        app.MapPost("/api/apps/{appId}/stop", async (
            string appId,
            AppRegistry registry,
            AppRoomService rooms,
            HttpContext context) =>
        {
            if (registry.Find(appId) is null)
            {
                return Results.NotFound(new { error = "app_not_found" });
            }

            await rooms.StopAppAsync(appId, context.RequestAborted);
            return Results.Ok(new { stopped = true });
        });

        app.MapGet("/api/apps/{appId}/rooms/{token}/qr.png", (
            string appId,
            string token,
            AppRegistry registry,
            AppRoomService rooms) =>
        {
            var manifest = registry.Find(appId);
            if (manifest is null)
            {
                return Results.NotFound(new { error = "app_not_found" });
            }

            var room = rooms.Find(manifest.Id, token);
            if (room is null)
            {
                return Results.NotFound(new { error = "room_not_found" });
            }

            return Results.File(QrService.CreatePng(room.Url), "image/png");
        });
    }
}

internal sealed record CreateAppRoomRequest(
    int? ExpiresInSeconds,
    Dictionary<string, JsonElement>? Options);
