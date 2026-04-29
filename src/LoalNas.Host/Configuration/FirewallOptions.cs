namespace LoalNas.Host.Configuration;

public sealed class FirewallOptions
{
    public const string SectionName = "LoalNas:Firewall";

    public bool AutoConfigureOnStartup { get; set; } = true;

    public string RelativeScriptPath { get; set; } = "scripts/Enable-LoalNasFirewallRule.ps1";

    public string[] Profiles { get; set; } = ["Private"];

    public int EnsureTimeoutSeconds { get; set; } = 30;
}