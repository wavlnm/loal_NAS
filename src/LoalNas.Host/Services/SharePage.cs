using System.Net;

namespace LoalNas.Host.Services;

/// <summary>分享页 HTML（自包含，无外部资源；不输出脚本，避免 token 进入 JS）。</summary>
public static class SharePage
{
    public static string Render(
        ShareRecord share, bool needPassword, bool exhausted, string? notice = null)
    {
        var token = WebUtility.HtmlEncode(share.Token);
        var fileName = WebUtility.HtmlEncode(share.FileName);
        var size = FormatSize(share.SizeBytes);
        var expires = share.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var remaining = share.MaxDownloads is int max
            ? $"剩余下载次数：{Math.Max(0, max - share.DownloadCount)} / {max}"
            : "下载次数：不限";

        var body = new System.Text.StringBuilder();
        body.Append("<header><span class=\"brand\">千私云</span><span class=\"dot\">·</span>文件分享</header>");
        body.Append("<h1>").Append(fileName).Append("</h1>");
        body.Append("<p class=\"meta\">").Append(size)
            .Append(" <span class=\"sep\">|</span> 有效期至 ").Append(expires)
            .Append(" <span class=\"sep\">|</span> ").Append(remaining).Append("</p>");

        if (!string.IsNullOrEmpty(notice))
        {
            body.Append("<p class=\"notice\">").Append(WebUtility.HtmlEncode(notice)).Append("</p>");
        }

        if (needPassword)
        {
            body.Append("<form method=\"post\" action=\"/s/").Append(token).Append("/unlock\">")
                .Append("<label for=\"pw\">此分享需要密码</label>")
                .Append("<input id=\"pw\" name=\"password\" type=\"password\" ")
                .Append("autocomplete=\"current-password\" autofocus maxlength=\"64\" required>")
                .Append("<button type=\"submit\">打开文件</button>")
                .Append("</form>");
        }
        else
        {
            ShareContentTypes.Resolve(share.FileName, out var kind);
            AppendPreview(body, token, kind);
            if (exhausted)
            {
                body.Append("<p class=\"notice\">下载次数已用完，当前仅支持在线预览。</p>");
            }
            else
            {
                body.Append("<a class=\"download\" href=\"/s/").Append(token)
                    .Append("/file?dl=1\" download>下载文件</a>");
            }
        }

        body.Append("<footer>链接到期后自动失效；请勿公开传播。</footer>");

        return $$"""
<!doctype html>
<html lang="zh">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex, nofollow">
<title>{{fileName}} · 千私云文件分享</title>
<style>
  :root { --bg:#f5f7fb; --card:#fff; --border:#e5e9f2; --text:#1b2330; --muted:#6b7686; --accent:#316bff; }
  * { box-sizing: border-box; }
  body { margin:0; background:var(--bg); color:var(--text);
         font-family:"Noto Sans CJK SC","Segoe UI",system-ui,sans-serif; }
  main { max-width:640px; margin:0 auto; padding:28px 18px 48px; }
  header { color:var(--muted); font-size:14px; margin-bottom:18px; }
  header .brand { color:var(--accent); font-weight:600; }
  header .dot { margin:0 6px; }
  h1 { font-size:20px; line-height:1.4; word-break:break-all; margin:0 0 10px; }
  .meta { color:var(--muted); font-size:13px; margin:0 0 18px; line-height:1.7; }
  .meta .sep { margin:0 6px; color:#c3cad6; }
  .notice { background:#fff7e6; border:1px solid #ffe0a3; color:#8a5a00;
            border-radius:12px; padding:10px 14px; font-size:14px; }
  .card, form, .preview { background:var(--card); border:1px solid var(--border);
            border-radius:16px; padding:16px; margin-bottom:16px; }
  form label { display:block; font-size:14px; color:var(--muted); margin-bottom:10px; }
  input { width:100%; padding:12px 14px; font-size:16px; border-radius:10px;
          border:1px solid var(--border); outline:none; }
  input:focus { border-color:var(--accent); }
  button, .download { display:block; width:100%; margin-top:12px; padding:12px 14px;
          border:0; border-radius:10px; background:var(--accent); color:#fff;
          font-size:16px; text-align:center; text-decoration:none; font-weight:600; }
  .preview { padding:0; overflow:hidden; text-align:center; }
  .preview img, .preview video { display:block; width:100%; max-height:70vh; object-fit:contain; background:#000; }
  .preview audio { display:block; width:100%; padding:14px; }
  .hint { color:var(--muted); font-size:14px; text-align:center; margin:0; padding:20px; }
  footer { color:var(--muted); font-size:12px; text-align:center; margin-top:22px; }
</style>
</head>
<body>
<main>
{{body}}
</main>
</body>
</html>
""";
    }

    public static string RenderError(string title, string message) => $$"""
<!doctype html>
<html lang="zh">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex, nofollow">
<title>{{WebUtility.HtmlEncode(title)}} · 千私云</title>
<style>
  body { margin:0; background:#f5f7fb; color:#1b2330;
         font-family:"Noto Sans CJK SC","Segoe UI",system-ui,sans-serif; }
  main { max-width:520px; margin:0 auto; padding:64px 20px; text-align:center; }
  h1 { font-size:20px; margin:0 0 12px; }
  p { color:#6b7686; font-size:14px; line-height:1.7; margin:0; }
</style>
</head>
<body>
<main>
<h1>{{WebUtility.HtmlEncode(title)}}</h1>
<p>{{WebUtility.HtmlEncode(message)}}</p>
</main>
</body>
</html>
""";

    private static void AppendPreview(
        System.Text.StringBuilder body, string token, ShareMediaKind kind)
    {
        switch (kind)
        {
            case ShareMediaKind.Image:
                body.Append("<div class=\"preview\"><img src=\"/s/")
                    .Append(token).Append("/file\" alt=\"\"></div>");
                break;
            case ShareMediaKind.Audio:
                body.Append("<div class=\"preview\"><audio controls preload=\"metadata\" src=\"/s/")
                    .Append(token).Append("/file\"></audio></div>");
                break;
            case ShareMediaKind.Video:
                body.Append("<div class=\"preview\"><video controls playsinline preload=\"metadata\" src=\"/s/")
                    .Append(token).Append("/file\"></video></div>");
                break;
            default:
                body.Append("<p class=\"hint\">该文件类型不支持在线预览，请下载后查看。</p>");
                break;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0
            ? $"{value:0} {units[index]}"
            : $"{value:0.#} {units[index]}";
    }
}
