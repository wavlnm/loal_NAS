using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LoalNas.Host.Services;

/// <summary>
/// 网络地址探测（Linux 容错版）。
/// - 公网 IPv6：排除回环/链路本地/ULA/组播；优先 EUI-64（LinkLayerAddress），
///   其次非临时地址，最后回退到任意全局地址（Linux 上 SuffixOrigin 可能不完整）。
/// - 局域网 IPv4：私有段（10/8、172.16/12、192.168/16）。
/// </summary>
public static class NetworkInfoService
{
    public static IPAddress? GetStablePublicIpv6()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                && ni.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                    and not NetworkInterfaceType.Tunnel)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
            .Where(ua =>
                ua.Address.AddressFamily == AddressFamily.InterNetworkV6
                && !ua.Address.IsIPv6LinkLocal
                && !ua.Address.IsIPv6Multicast
                && !IPAddress.IsLoopback(ua.Address)
                && !IsUniqueLocal(ua.Address))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderBy(SuffixRank)
            .Select(ua => ua.Address)
            .First();
    }

    /// <summary>
    /// 地址优先级：EUI-64（LinkLayerAddress）最优，临时隐私地址（Random）最次。
    /// 注意 SuffixOrigin 仅 Windows 支持，Linux 上访问会抛 PlatformNotSupportedException，
    /// 因此非 Windows 平台统一返回 0（取首个全局地址）。
    /// </summary>
    private static int SuffixRank(UnicastIPAddressInformation ua)
    {
        if (OperatingSystem.IsWindows())
        {
            return ua.SuffixOrigin switch
            {
                SuffixOrigin.LinkLayerAddress => 0,
                SuffixOrigin.Random => 2,
                _ => 1
            };
        }

        return 0;
    }

    public static IReadOnlyList<IPAddress> GetLanIpv4Addresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                && ni.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                    and not NetworkInterfaceType.Tunnel)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
            .Where(ua =>
                ua.Address.AddressFamily == AddressFamily.InterNetwork
                && IsPrivateIpv4(ua.Address))
            .Select(ua => ua.Address)
            .ToList();
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168);
    }

    private static bool IsUniqueLocal(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length > 0 && (b[0] & 0xFE) == 0xFC;
    }
}
