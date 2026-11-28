using System.Text;
using System.Text.RegularExpressions;

namespace LoalNas.Host.Services;

/// <summary>
/// 歌词筛选与缓存：文件名拆歌名/歌手 -> 多源候选 -> 打分选最优 -> 写入共享目录。
/// 手机端只负责展示，不再自己比较候选。
/// </summary>
public static class LyricsSelection
{
    private const int MaxVariants = 5;

    // 与手机端保持一致的“仅繁体”字表，用于给繁体歌词降权。
    private const string TraditionalOnlyChars =
        "們這說過來對時會讓還願風雲裡後開關無與為個嗎麼幾點當發現實語誰愛聽" +
        "問間長東車馬鳥龍書學國樂戀憶夢舊歡離轉經遠運選擇錯錢靜頭題顏飛飯" +
        "飲館駕驗體髮鬥魚麗黃齊齒龜話記認識謝請該變總兒處覺腦燈聲見親媽歲" +
        "醫師頁網線陽陰隊陸陳義議場報紙筆畫圖讀寫從兩萬億鳳鄉邊適遲進連鐘" +
        "鐵銀鋼門聞階際險雙難雖霧靈頂順預領類飽驅驚";

    private static readonly HashSet<char> TraditionalOnly = new(TraditionalOnlyChars);

    private static readonly Regex Separator = new(
        @"^\s*(.+?)\s*[-–—_]\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex ArtistSeparator = new(
        @"\s*(?:&|、|,|，|/|•|feat\.?|ft\.?)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LeadingTrackNumber = new(
        @"^\s*\d{1,3}\s*[-._]\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingNumber = new(
        @"^\s*\d{2,3}\s+", RegexOptions.Compiled);
    private static readonly Regex TimedLine = new(
        @"^\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\]",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Brackets = new(
        @"[（）()\[\]【】]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string KeyFromFileName(string fileName)
    {
        var key = fileName;
        var dot = key.LastIndexOf('.');
        if (dot > 0)
        {
            key = key[..dot];
        }
        key = LeadingTrackNumber.Replace(key, string.Empty);
        key = LeadingNumber.Replace(key, string.Empty);
        key = key.Replace('/', '_').Replace('\\', '_').Replace('\0', '_').Trim();
        return key.Length == 0 ? "未命名" : key;
    }

    public static List<(string Track, string? Artist)> BuildVariants(string fileName)
    {
        var baseName = KeyFromFileName(fileName);
        var variants = new List<(string Track, string? Artist)> { (baseName, null) };
        var match = Separator.Match(baseName);
        if (!match.Success)
        {
            return variants;
        }

        var left = match.Groups[1].Value.Trim();
        var right = match.Groups[2].Value.Trim();
        if (left.Length == 0 || right.Length == 0 || left == right)
        {
            return variants;
        }

        variants.Add((left, right));
        variants.Add((right, left));
        foreach (var artist in ArtistVariants(right).Take(2))
        {
            variants.Add((left, artist));
        }
        foreach (var artist in ArtistVariants(left).Take(2))
        {
            variants.Add((right, artist));
        }
        return variants;
    }

    public static async Task<(LyricsCandidate Candidate, string Lyrics)?> FindBestAsync(
        IHttpClientFactory clients,
        string fileName,
        double? durationSeconds,
        CancellationToken ct)
    {
        LyricsCandidate? best = null;
        var bestLyrics = string.Empty;
        var bestScore = int.MaxValue;
        var bestSynced = false;
        var bestLineCount = 0;

        foreach (var (track, artist) in BuildVariants(fileName).Take(MaxVariants))
        {
            var candidates = await LyricsProviders
                .SearchAsync(clients, track, artist, ct)
                .ConfigureAwait(false);
            foreach (var candidate in candidates)
            {
                var lyrics = candidate.SyncedLyrics ?? candidate.PlainLyrics ?? string.Empty;
                var lineCount = CountLyricLines(lyrics);
                if (lineCount < 3)
                {
                    continue;
                }

                var score = ScoreCandidate(track, artist, candidate, lyrics, lineCount, durationSeconds);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    bestLyrics = lyrics;
                    bestSynced = candidate.SyncedLyrics != null;
                    bestLineCount = lineCount;
                }
            }

            if (best != null && bestScore < 50 && bestSynced && bestLineCount >= 5)
            {
                break;
            }
        }

        return best == null ? null : (best, bestLyrics);
    }

    public static bool SaveToShare(string sharedRoot, string key, string lyrics)
    {
        try
        {
            var directory = Path.Combine(sharedRoot, "音乐播放器", "lyrics");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, key + ".lrc"),
                lyrics,
                new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ScoreCandidate(
        string wantedTitle,
        string? wantedArtist,
        LyricsCandidate candidate,
        string lyrics,
        int lineCount,
        double? actualDurationSeconds)
    {
        var track = Normalize(candidate.TrackName);
        var artist = Normalize(candidate.ArtistName);
        var title = Normalize(wantedTitle);
        var score = 0;

        if (track == title)
        {
            score -= 40;
        }
        else if (track.Contains(title) || title.Contains(track))
        {
            score += 40;
        }
        else
        {
            score += 300;
        }

        var wantedArtistNormalized = Normalize(wantedArtist);
        if (wantedArtistNormalized.Length > 0)
        {
            score += artist.Contains(wantedArtistNormalized) ? -30 : 300;
        }
        else
        {
            score += 80;
        }

        if (candidate.SyncedLyrics == null)
        {
            score += 100;
            if (lineCount < 8)
            {
                score += 60;
            }
        }
        else
        {
            score -= Math.Min(lineCount, 40) / 4;
        }

        if (candidate.Duration is double duration && actualDurationSeconds is > 0)
        {
            score += (int)Math.Round(Math.Abs(duration - actualDurationSeconds.Value));
        }
        else
        {
            score += 50;
        }

        // 繁体降权，等价于优先简体。
        score += Math.Clamp(CountTraditional(lyrics) * 3, 0, 60);
        return score;
    }

    private static string Normalize(string? value) => Brackets.Replace(
        Whitespace.Replace((value ?? string.Empty).ToLowerInvariant(), string.Empty),
        string.Empty);

    private static int CountTraditional(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (TraditionalOnly.Contains(ch))
            {
                count++;
            }
        }
        return count;
    }

    private static int CountLyricLines(string text)
    {
        var timed = TimedLine.Matches(text).Count;
        if (timed > 0)
        {
            return timed;
        }

        var lines = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                lines++;
            }
        }
        return lines;
    }

    private static IEnumerable<string> ArtistVariants(string value)
    {
        var parts = ArtistSeparator
            .Split(value)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
        return parts.Count == 0 ? new[] { value.Trim() } : parts;
    }
}
