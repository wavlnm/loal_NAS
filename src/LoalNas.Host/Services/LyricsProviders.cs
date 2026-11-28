using System.Net;
using System.Text.Json;

namespace LoalNas.Host.Services;

public sealed record LyricsCandidate(
    string TrackName,
    string? ArtistName,
    string? SyncedLyrics,
    string? PlainLyrics,
    double? Duration,
    string Source);

/// <summary>
/// 多源歌词查询：LRCLIB（开放库）+ 网易云、QQ 音乐的网页端接口。
/// 每个源独立超时、失败互不影响，结果合并后交给手机端打分挑选。
/// </summary>
public static class LyricsProviders
{
    public const string BrowserHttpClientName = "lyrics-web";

    private const int MaxLyricsLength = 100_000;
    private const int MaxCandidatesPerSource = 8;
    private const int MaxDetailLookups = 2;
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(7);

    public static async Task<List<LyricsCandidate>> SearchAsync(
        IHttpClientFactory clients,
        string track,
        string? artist,
        CancellationToken ct)
    {
        var tasks = new[]
        {
            RunSafeAsync(() => SearchLrclibAsync(clients, track, artist, ct)),
            RunSafeAsync(() => SearchNetEaseAsync(clients, track, artist, ct)),
            RunSafeAsync(() => SearchQqAsync(clients, track, artist, ct)),
        };
        var batches = await Task.WhenAll(tasks).ConfigureAwait(false);
        var merged = new List<LyricsCandidate>();
        var seen = new HashSet<string>();
        foreach (var batch in batches)
        {
            foreach (var candidate in batch)
            {
                var lyrics = candidate.SyncedLyrics ?? candidate.PlainLyrics ?? string.Empty;
                var key = string.Join(
                    "|",
                    candidate.TrackName,
                    candidate.ArtistName ?? string.Empty,
                    candidate.Duration?.ToString("0.#") ?? "-",
                    lyrics.Length,
                    lyrics.Length > 64 ? lyrics[..64] : lyrics);
                if (seen.Add(key))
                {
                    merged.Add(candidate);
                }
            }
        }
        return merged;
    }

    private static async Task<List<LyricsCandidate>> RunSafeAsync(
        Func<Task<List<LyricsCandidate>>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch
        {
            return new List<LyricsCandidate>();
        }
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(SourceTimeout);
        return source;
    }

    private static async Task<List<LyricsCandidate>> SearchLrclibAsync(
        IHttpClientFactory clients,
        string track,
        string? artist,
        CancellationToken ct)
    {
        using var timeout = CreateTimeout(ct);
        var query = $"track_name={Uri.EscapeDataString(track)}";
        if (!string.IsNullOrWhiteSpace(artist))
        {
            query += $"&artist_name={Uri.EscapeDataString(artist)}";
        }

        using var response = await clients.CreateClient(LyricsEndpoints.HttpClientName)
            .GetAsync($"https://lrclib.net/api/search?{query}", timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new List<LyricsCandidate>();
        }

        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (json.Length > 2 * 1024 * 1024)
        {
            return new List<LyricsCandidate>();
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<LyricsCandidate>();
        }

        var list = new List<LyricsCandidate>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var synced = NormalizeText(GetString(item, "syncedLyrics"));
            var plain = NormalizeText(GetString(item, "plainLyrics"));
            if (synced == null && plain == null)
            {
                continue;
            }

            list.Add(new LyricsCandidate(
                GetString(item, "trackName") ?? track,
                GetString(item, "artistName"),
                synced,
                plain,
                GetDouble(item, "duration"),
                "lrclib"));
            if (list.Count >= MaxCandidatesPerSource)
            {
                break;
            }
        }
        return list;
    }

    private static async Task<List<LyricsCandidate>> SearchNetEaseAsync(
        IHttpClientFactory clients,
        string track,
        string? artist,
        CancellationToken ct)
    {
        using var timeout = CreateTimeout(ct);
        var client = clients.CreateClient(BrowserHttpClientName);
        var keyword = string.IsNullOrWhiteSpace(artist) ? track : $"{track} {artist}";
        var url = "https://music.163.com/api/search/get" +
            $"?s={Uri.EscapeDataString(keyword)}&type=1&limit=5";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new List<LyricsCandidate>();
        }

        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (json.Length > 2 * 1024 * 1024)
        {
            return new List<LyricsCandidate>();
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array)
        {
            return new List<LyricsCandidate>();
        }

        var lookups = new List<Task<LyricsCandidate?>>();
        foreach (var song in songs.EnumerateArray())
        {
            if (lookups.Count >= MaxDetailLookups)
            {
                break;
            }
            var id = GetLong(song, "id");
            if (id == null)
            {
                continue;
            }
            lookups.Add(FetchNetEaseLyricsAsync(
                client,
                id.Value,
                GetString(song, "name") ?? track,
                ReadArtistNames(song, "artists"),
                GetDouble(song, "duration"),
                timeout.Token));
        }

        var results = await Task.WhenAll(lookups).ConfigureAwait(false);
        return results.Where(item => item != null).Select(item => item!).ToList();
    }

    private static async Task<LyricsCandidate?> FetchNetEaseLyricsAsync(
        HttpClient client,
        long id,
        string track,
        string? artist,
        double? durationMs,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://music.163.com/api/song/lyric?id={id}&lv=-1&kv=-1&tv=-1");
            request.Headers.Referrer = new Uri("https://music.163.com/");
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("lrc", out var lrc) ||
                lrc.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var text = NormalizeText(GetString(lrc, "lyric"));
            if (text == null)
            {
                return null;
            }
            var (synced, plain) = SplitLyrics(text);
            if (synced == null && plain == null)
            {
                return null;
            }

            return new LyricsCandidate(
                track,
                artist,
                synced,
                plain,
                durationMs.HasValue ? durationMs.Value / 1000.0 : null,
                "netease");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<LyricsCandidate>> SearchQqAsync(
        IHttpClientFactory clients,
        string track,
        string? artist,
        CancellationToken ct)
    {
        using var timeout = CreateTimeout(ct);
        var client = clients.CreateClient(BrowserHttpClientName);
        var keyword = string.IsNullOrWhiteSpace(artist) ? track : $"{track} {artist}";
        var url = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp" +
            $"?w={Uri.EscapeDataString(keyword)}&format=json&n=5&p=1&new_json=1";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new List<LyricsCandidate>();
        }

        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (json.Length > 2 * 1024 * 1024)
        {
            return new List<LyricsCandidate>();
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("song", out var songNode) ||
            songNode.ValueKind != JsonValueKind.Object ||
            !songNode.TryGetProperty("list", out var songs) ||
            songs.ValueKind != JsonValueKind.Array)
        {
            return new List<LyricsCandidate>();
        }

        var lookups = new List<Task<LyricsCandidate?>>();
        foreach (var song in songs.EnumerateArray())
        {
            if (lookups.Count >= MaxDetailLookups)
            {
                break;
            }
            var mid = GetString(song, "mid") ?? GetString(song, "songmid");
            if (string.IsNullOrWhiteSpace(mid))
            {
                continue;
            }
            lookups.Add(FetchQqLyricsAsync(
                client,
                mid,
                GetString(song, "name") ?? GetString(song, "title") ?? track,
                ReadArtistNames(song, "singer"),
                GetDouble(song, "interval"),
                timeout.Token));
        }

        var results = await Task.WhenAll(lookups).ConfigureAwait(false);
        return results.Where(item => item != null).Select(item => item!).ToList();
    }

    private static async Task<LyricsCandidate?> FetchQqLyricsAsync(
        HttpClient client,
        string mid,
        string track,
        string? artist,
        double? durationSeconds,
        CancellationToken ct)
    {
        try
        {
            var url = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg" +
                $"?songmid={Uri.EscapeDataString(mid)}&format=json&nobase64=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri("https://y.qq.com/");
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // 接口有时返回 JSONP 包装（MusicJsonCallback(...)），截取最外层对象。
            var start = body.IndexOf('{');
            var end = body.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body[start..(end + 1)]);
            var raw = GetString(doc.RootElement, "lyric");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var text = NormalizeText(WebUtility.HtmlDecode(raw));
            if (text == null)
            {
                return null;
            }
            var (synced, plain) = SplitLyrics(text);
            if (synced == null && plain == null)
            {
                return null;
            }

            return new LyricsCandidate(track, artist, synced, plain, durationSeconds, "qq");
        }
        catch
        {
            return null;
        }
    }

    private static (string? Synced, string? Plain) SplitLyrics(string text)
    {
        var hasTimestamps = System.Text.RegularExpressions.Regex.IsMatch(
            text,
            @"^\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\]",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        return hasTimestamps ? (text, null) : (null, text);
    }

    private static string? ReadArtistNames(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var artists) ||
            artists.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var names = artists
            .EnumerateArray()
            .Select(item => GetString(item, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        return names.Count == 0 ? null : string.Join("/", names);
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var trimmed = value.Trim();
        return trimmed.Length > MaxLyricsLength ? trimmed[..MaxLyricsLength] : trimmed;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? GetDouble(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : null;

    private static long? GetLong(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;
}
