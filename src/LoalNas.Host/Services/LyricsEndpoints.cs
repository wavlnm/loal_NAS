namespace LoalNas.Host.Services;

/// <summary>
/// 歌词查询代理：手机到 LRCLIB 经常超时，由 NAS 代查再返回搜索结果。
/// </summary>
public static class LyricsEndpoints
{
    public const string HttpClientName = "lyrics";

    private const int MaxTextLength = 200;

    public static void MapLyricsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/music/lyrics/search", async (
            string? track, string? artist,
            IHttpClientFactory clients, CancellationToken ct) =>
        {
            track = track?.Trim();
            artist = artist?.Trim();
            if (string.IsNullOrWhiteSpace(track) ||
                track.Length > MaxTextLength ||
                (artist?.Length ?? 0) > MaxTextLength)
            {
                return Results.Json(new { error = "invalid_query" }, statusCode: 400);
            }

            var query = $"track_name={Uri.EscapeDataString(track)}";
            if (!string.IsNullOrWhiteSpace(artist))
            {
                query += $"&artist_name={Uri.EscapeDataString(artist)}";
            }

            try
            {
                using var response = await clients.CreateClient(HttpClientName)
                    .GetAsync($"https://lrclib.net/api/search?{query}", ct)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return Results.Json(
                        new { error = "lyrics_unavailable" },
                        statusCode: StatusCodes.Status502BadGateway);
                }

                var json = await response.Content.ReadAsStringAsync(ct)
                    .ConfigureAwait(false);
                if (json.Length > 2 * 1024 * 1024)
                {
                    return Results.Json(
                        new { error = "lyrics_too_large" },
                        statusCode: StatusCodes.Status502BadGateway);
                }

                return Results.Text(json, "application/json; charset=utf-8");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Json(
                    new { error = "lyrics_timeout" },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (HttpRequestException)
            {
                return Results.Json(
                    new { error = "lyrics_unavailable" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}
