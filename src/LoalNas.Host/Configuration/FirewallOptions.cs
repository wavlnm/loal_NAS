namespace LoalNas.Host.Configuration;

/// <summary>
/// Linux 防火墙自动放行配置。
/// Tool 取值：auto（自动探测 ufw/firewalld/iptables）、ufw、firewalld、iptables、none（关闭）。
/// </summary>
public sealed class FirewallOptions
{
    public const string SectionName = "LoalNas:Firewall";

    public bool AutoConfigureOnStartup { get; set; } = true;

    public string Tool { get; set; } = "auto";

    public int EnsureTimeoutSeconds { get; set; } = 30;
}
