namespace LoalNas.Host.Configuration;

public sealed class PublicHostOptions
{
    public const string SectionName = "LoalNas:Host";

    public string Url { get; set; } = "http://[::]:5034";

    /// <summary>公网访问地址（如 http://vps.example.com:5034），用于二维码下发外网入口；留空则不下发。</summary>
    public string PublicBaseUrl { get; set; } = "";
}
