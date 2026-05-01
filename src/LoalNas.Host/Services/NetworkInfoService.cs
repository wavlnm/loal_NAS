using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LoalNas.Host.Services;

/// <summary>
/// 获取网络地址信息，专为 UI 展示设计：
/// - 稳定公网 IPv6：排除隐私扩展随机地址（SuffixOrigin == Random），
///   优先返回 EUI-64 派生地址（SuffixOrigin == LinkLayerAddress）。
/// - 局域网 IPv4：私有段地址（10/8、172.16/12、192.168/16）。
/// </summary>
public static class NetworkInfoService
{
	/// <summary>
	/// 返回唯一的稳定公网 IPv6 地址；找不到时返回 null。
	/// </summary>
	public static IPAddress? GetStablePublicIpv6()
	{
		return NetworkInterface.GetAllNetworkInterfaces()
			.Where(ni => ni.OperationalStatus == OperationalStatus.Up
				&& ni.NetworkInterfaceType is not NetworkInterfaceType.Loopback
					and not NetworkInterfaceType.Tunnel)
			.SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
			.Where(ua =>
				ua.Address.AddressFamily == AddressFamily.InterNetworkV6
				&& !ua.Address.IsIPv6LinkLocal
				&& !ua.Address.IsIPv6Multicast
				&& !IPAddress.IsLoopback(ua.Address)
				&& !IsUniqueLocal(ua.Address)
				&& ua.SuffixOrigin != SuffixOrigin.Random)
			// EUI-64 派生地址排在最前
			.OrderBy(ua => ua.SuffixOrigin == SuffixOrigin.LinkLayerAddress ? 0 : 1)
			.Select(ua => ua.Address)
			.FirstOrDefault();
	}

	/// <summary>
	/// 返回所有局域网 IPv4 地址（私有段）。
	/// </summary>
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
