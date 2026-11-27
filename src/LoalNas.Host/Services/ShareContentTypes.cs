namespace LoalNas.Host.Services;

public enum ShareMediaKind
{
    None,
    Image,
    Audio,
    Video,
}

/// <summary>分享页的内容类型映射与 Content-Disposition 生成。</summary>
public static class ShareContentTypes
{
    private static readonly Dictionary<string, (string ContentType, ShareMediaKind Kind)> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = ("image/jpeg", ShareMediaKind.Image),
            [".jpeg"] = ("image/jpeg", ShareMediaKind.Image),
            [".png"] = ("image/png", ShareMediaKind.Image),
            [".gif"] = ("image/gif", ShareMediaKind.Image),
            [".webp"] = ("image/webp", ShareMediaKind.Image),
            [".bmp"] = ("image/bmp", ShareMediaKind.Image),
            [".mp3"] = ("audio/mpeg", ShareMediaKind.Audio),
            [".m4a"] = ("audio/mp4", ShareMediaKind.Audio),
            [".aac"] = ("audio/aac", ShareMediaKind.Audio),
            [".wav"] = ("audio/wav", ShareMediaKind.Audio),
            [".ogg"] = ("audio/ogg", ShareMediaKind.Audio),
            [".oga"] = ("audio/ogg", ShareMediaKind.Audio),
            [".opus"] = ("audio/opus", ShareMediaKind.Audio),
            [".flac"] = ("audio/flac", ShareMediaKind.Audio),
            [".mp4"] = ("video/mp4", ShareMediaKind.Video),
            [".m4v"] = ("video/mp4", ShareMediaKind.Video),
            [".webm"] = ("video/webm", ShareMediaKind.Video),
            [".ogv"] = ("video/ogg", ShareMediaKind.Video),
            [".mov"] = ("video/quicktime", ShareMediaKind.Video),
        };

    /// <summary>只有明确支持浏览器内联播放的类型才允许 inline；HTML/SVG 等一律不内联。</summary>
    public static string Resolve(string fileName, out ShareMediaKind kind)
    {
        var extension = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(extension) && Map.TryGetValue(extension, out var entry))
        {
            kind = entry.Kind;
            return entry.ContentType;
        }

        kind = ShareMediaKind.None;
        return "application/octet-stream";
    }

    public static string BuildContentDisposition(string fileName, bool attachment)
    {
        var cleaned = new string(fileName
            .Where(c => c >= 0x20 && c is not '"' and not '\\')
            .ToArray());
        if (cleaned.Length == 0)
        {
            cleaned = "download";
        }

        var ascii = new string(cleaned.Select(c => c < 0x80 ? c : '_').ToArray());
        var encoded = Uri.EscapeDataString(cleaned);
        var type = attachment ? "attachment" : "inline";
        return $"{type}; filename=\"{ascii}\"; filename*=UTF-8''{encoded}";
    }
}
