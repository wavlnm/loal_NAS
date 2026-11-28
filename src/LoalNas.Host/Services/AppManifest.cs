using System.Text.RegularExpressions;

namespace LoalNas.Host.Services;

/// <summary>
/// 一个可托管应用/游戏的清单（apps/{id}/app.json）。
/// 平台只理解启动方式、健康检查和公开路由，不理解应用内部规则。
/// </summary>
public sealed class AppManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int ContractVersion { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    /// <summary>可执行文件，例如 dotnet、node、./server。相对路径按应用目录解析。</summary>
    public string Command { get; set; } = "";

    /// <summary>启动参数，支持 {port} / {basePath} / {controlToken} / {appDir} / {dataDir} 占位符。</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>工作目录，默认应用目录；支持同一组占位符。</summary>
    public string WorkingDirectory { get; set; } = "";

    public string HealthPath { get; set; } = "/health";

    public int StartupTimeoutSeconds { get; set; }

    /// <summary>公开 URL 前缀，固定以 /apps/ 开头，例如 /apps/gomoku。</summary>
    public string PublicPrefix { get; set; } = "";

    /// <summary>
    /// 公开路由模板（相对 publicPrefix），支持 {token}（22 位令牌）、{any}（单段）、{path...}（剩余路径）。
    /// 不在列表里的路径一律要求手机端签名头。
    /// </summary>
    public List<string> PublicRoutes { get; set; } = new();

    /// <summary>managed（应用内多房间）或 single（一房一进程，预留）。</summary>
    public string RoomModel { get; set; } = "managed";

    public int MaxRooms { get; set; }

    /// <summary>建房默认有效期（小时），0 表示使用平台默认值。</summary>
    public int DefaultRoomLifetimeHours { get; set; }

    /// <summary>建房允许的最长有效期（小时），0 表示使用平台默认值。</summary>
    public int MaxRoomLifetimeHours { get; set; }

    public string DataDirectoryName { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string AppDirectory { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string DataDirectory { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public List<AppRoute> Routes { get; } = new();

    public AppRouteMatch? Match(string restPath)
    {
        foreach (var route in Routes)
        {
            var match = route.Regex.Match(restPath);
            if (match.Success)
            {
                var token = route.HasToken && match.Groups["token"].Success
                    ? match.Groups["token"].Value
                    : null;
                return new AppRouteMatch(route, token);
            }
        }

        return null;
    }
}

public sealed record AppRouteMatch(AppRoute Route, string? Token);

/// <summary>编译后的公开路由（把 {token} 等占位符转成正则）。</summary>
public sealed class AppRoute
{
    private static readonly Regex Placeholder = new(
        @"\{(token|path\.\.\.|any)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public Regex Regex { get; }
    public bool HasToken { get; }
    public string Template { get; }

    private AppRoute(Regex regex, bool hasToken, string template)
    {
        Regex = regex;
        HasToken = hasToken;
        Template = template;
    }

    public static AppRoute Compile(string template)
    {
        var hasToken = template.Contains("{token}", StringComparison.Ordinal);
        var builder = new System.Text.StringBuilder("^");
        var cursor = 0;

        foreach (Match match in Placeholder.Matches(template))
        {
            builder.Append(Regex.Escape(template[cursor..match.Index]));
            builder.Append(match.Groups[1].Value switch
            {
                "token" => "(?<token>[A-Za-z0-9_-]{22})",
                "path..." => "(?<rest>.+)",
                _ => "[^/]+",
            });
            cursor = match.Index + match.Length;
        }

        builder.Append(Regex.Escape(template[cursor..]));
        builder.Append('$');
        return new AppRoute(
            new Regex(builder.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant),
            hasToken,
            template);
    }
}
