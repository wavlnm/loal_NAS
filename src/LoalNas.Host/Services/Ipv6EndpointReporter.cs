using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LoalNas.Host.Services;

public readonly record struct Ipv6EndpointInfo(string Category, string Url);

public static class Ipv6EndpointReporter
{
	public static IReadOnlyList<Ipv6EndpointInfo> GetAvailableAddresses(IEnumerable<string> boundUrls)
	{
		var urls = boundUrls
			.Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null)
			.OfType<Uri>()
			.ToArray();

		var reportedUrls = urls
			.Where(uri => IsSpecificIpv6Host(uri.Host))
			.Select(uri => new Ipv6EndpointInfo("Configured IPv6", BuildUrl(uri.Scheme, uri.Host, uri.Port)))
			.ToList();

		foreach (var wildcardBinding in urls.Where(uri => IsWildcardIpv6Host(uri.Host)))
		{
			reportedUrls.AddRange(GetCandidateAddresses()
				.Select(address => new Ipv6EndpointInfo(GetCategory(address), BuildUrl(wildcardBinding.Scheme, address.ToString(), wildcardBinding.Port))));
		}

		return reportedUrls
			.DistinctBy(item => item.Url)
			.OrderBy(item => CategoryOrder(item.Category))
			.ThenBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	public static void LogAvailableAddresses(ILogger logger, IEnumerable<string> boundUrls)
	{
		var distinctUrls = GetAvailableAddresses(boundUrls).ToArray();

		if (distinctUrls.Length == 0)
		{
			logger.LogWarning("No usable non-loopback IPv6 address was detected. Remote IPv6 access may be unavailable on this PC.");
			return;
		}

		logger.LogInformation("Available IPv6 endpoints for this PC:");

		foreach (var endpoint in distinctUrls)
		{
			logger.LogInformation("  {Category}: {Url}", endpoint.Category, endpoint.Url);
		}

		if (distinctUrls.Any(item => item.Category == "Link-local IPv6"))
		{
			logger.LogInformation("  Note: link-local IPv6 addresses only work on the same LAN and may require the scope suffix kept in the URL.");
		}
	}

	private static IEnumerable<IPAddress> GetCandidateAddresses()
	{
		return NetworkInterface.GetAllNetworkInterfaces()
			.Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
			.Where(networkInterface => networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
			.SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
			.Select(unicastAddress => unicastAddress.Address)
			.Where(address => address.AddressFamily == AddressFamily.InterNetworkV6)
			.Where(address => !IPAddress.IsLoopback(address))
			.Where(address => !address.IsIPv6Multicast)
			.DistinctBy(address => address.ToString(), StringComparer.OrdinalIgnoreCase);
	}

	private static bool IsSpecificIpv6Host(string host)
	{
		var normalizedHost = NormalizeHost(host);

		return IPAddress.TryParse(normalizedHost, out var address)
			&& address.AddressFamily == AddressFamily.InterNetworkV6
			&& !IPAddress.IPv6Any.Equals(address);
	}

	private static bool IsWildcardIpv6Host(string host)
	{
		var normalizedHost = NormalizeHost(host);

		return normalizedHost is "::" or "[::]";
	}

	private static string NormalizeHost(string host)
	{
		return host.Trim('[', ']');
	}

	private static string BuildUrl(string scheme, string hostOrAddress, int port)
	{
		var normalizedHost = NormalizeHost(hostOrAddress).Replace("%", "%25", StringComparison.Ordinal);
		return $"{scheme}://[{normalizedHost}]:{port}";
	}

	private static string GetCategory(IPAddress address)
	{
		if (address.IsIPv6LinkLocal)
		{
			return "Link-local IPv6";
		}

		if (IsUniqueLocalAddress(address))
		{
			return "Unique local IPv6";
		}

		return "Global IPv6";
	}

	private static bool IsUniqueLocalAddress(IPAddress address)
	{
		var bytes = address.GetAddressBytes();
		return bytes.Length > 0 && (bytes[0] & 0xFE) == 0xFC;
	}

	private static int CategoryOrder(string category)
	{
		return category switch
		{
			"Global IPv6" => 0,
			"Unique local IPv6" => 1,
			"Link-local IPv6" => 2,
			_ => 3
		};
	}
}