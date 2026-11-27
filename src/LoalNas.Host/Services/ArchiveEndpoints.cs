using System.Text;
using System.Text.Json;

namespace LoalNas.Host.Services;

/// <summary>在线压缩任务的管理端点（全部要求手机端 HMAC 鉴权）。</summary>
public static class ArchiveEndpoints
{
    private sealed record CreateRequest(string? Path, string? Level);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void MapArchiveEndpoints(this WebApplication app)
    {
        app.MapPost("/api/archive/jobs", async (HttpContext context, ArchiveService archive) =>
        {
            if ((context.Request.ContentLength ?? 0) > 4096)
            {
                return Results.Json(new { error = "invalid_request" }, statusCode: 400);
            }

            CreateRequest? request = null;
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                request = JsonSerializer.Deserialize<CreateRequest>(body, JsonOpts);
            }
            catch (JsonException)
            {
                // fall through
            }

            if (request is null)
            {
                return Results.Json(new { error = "invalid_request" }, statusCode: 400);
            }

            var result = archive.Start(request.Path, request.Level);
            return result.Error switch
            {
                ArchiveService.StartError.None =>
                    Results.Json(result.Job, statusCode: 202),
                ArchiveService.StartError.InvalidPath =>
                    Results.Json(new { error = "invalid_path" }, statusCode: 400),
                ArchiveService.StartError.InvalidLevel =>
                    Results.Json(new { error = "invalid_level" }, statusCode: 400),
                ArchiveService.StartError.SourceNotFound =>
                    Results.Json(new { error = "source_not_found" }, statusCode: 404),
                ArchiveService.StartError.RootNotAllowed =>
                    Results.Json(new { error = "root_not_allowed", detail = result.Detail },
                        statusCode: 403),
                ArchiveService.StartError.TooManyFiles =>
                    Results.Json(new { error = "too_many_files", detail = result.Detail },
                        statusCode: 413),
                ArchiveService.StartError.TooLarge =>
                    Results.Json(new { error = "source_too_large", detail = result.Detail },
                        statusCode: 413),
                ArchiveService.StartError.NothingToArchive =>
                    Results.Json(new { error = "nothing_to_archive", detail = result.Detail },
                        statusCode: 400),
                ArchiveService.StartError.QueueFull =>
                    Results.Json(new { error = "archive_queue_full", detail = result.Detail },
                        statusCode: 429),
                ArchiveService.StartError.InsufficientDisk =>
                    Results.Json(new
                    {
                        error = "insufficient_disk_space",
                        detail = result.Detail ?? "磁盘剩余空间不足。",
                        availableBytes = result.AvailableBytes,
                        requiredBytes = result.RequiredBytes,
                    }, statusCode: StatusCodes.Status507InsufficientStorage),
                _ => Results.Json(new { error = "archive_failed" }, statusCode: 500),
            };
        });

        app.MapGet("/api/archive/jobs/{key}", (string key, ArchiveService archive) =>
        {
            var job = archive.Get(key);
            return job is null
                ? Results.NotFound(new { error = "job_not_found" })
                : Results.Ok(job);
        });

        app.MapPost("/api/archive/jobs/{key}/cancel", (string key, ArchiveService archive) =>
        {
            if (!archive.Cancel(key))
            {
                return Results.NotFound(new { error = "job_not_found" });
            }

            var job = archive.Get(key);
            return Results.Ok(new { key, status = job?.Status ?? "canceled" });
        });
    }
}
