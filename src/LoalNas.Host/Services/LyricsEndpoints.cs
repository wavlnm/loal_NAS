namespace LoalNas.Host.Services;

/// <summary>
/// 歌词查询：手机直接查外网经常超时，由 NAS 代查多个歌词源再合并返回。
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

            var candidates = await LyricsProviders
                .SearchAsync(clients, track, artist, ct)
                .ConfigureAwait(false);

            return Results.Json(candidates.Select(candidate => new
            {
                trackName = candidate.TrackName,
                artistName = candidate.ArtistName,
                syncedLyrics = candidate.SyncedLyrics,
                plainLyrics = candidate.PlainLyrics,
                duration = candidate.Duration,
                source = candidate.Source
            }));
        });

        // 手机端只传文件名和时长，由电脑端多源拉取、挑选最优并写入共享目录缓存。
        app.MapGet("/api/music/lyrics/best", async (
            string? name, double? duration,
            IHttpClientFactory clients,
            FileBrowserProcessManager manager,
            CancellationToken ct) =>
        {
            name = name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxTextLength + 100)
            {
                return Results.Json(new { error = "invalid_query" }, statusCode: 400);
            }

            var found = await LyricsSelection
                .FindBestAsync(clients, name, duration, ct)
                .ConfigureAwait(false);
            if (found == null)
            {
                return Results.Json(new { error = "lyrics_not_found" }, statusCode: 404);
            }

            var (candidate, lyrics) = found.Value;
            var key = LyricsSelection.KeyFromFileName(name);
            var saved = LyricsSelection.SaveToShare(manager.SharedRootPath, key, lyrics);
            return Results.Json(new
            {
                title = candidate.TrackName,
                lyrics,
                source = candidate.Source,
                saved
            });
        });
    }
}
