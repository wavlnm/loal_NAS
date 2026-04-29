namespace LoalNas.Host.Configuration;

public sealed class PublicHostOptions
{
    public const string SectionName = "LoalNas:Host";

    public string Url { get; set; } = "http://[::]:5034";
}