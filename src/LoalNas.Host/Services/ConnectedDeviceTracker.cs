using System.Collections.Concurrent;

namespace LoalNas.Host.Services;

/// <summary>
/// 追踪最近有过请求的客户端 IP，供 UI 展示"已连接设备"。
/// </summary>
public sealed class ConnectedDeviceTracker
{
	private static readonly TimeSpan ActiveThreshold = TimeSpan.FromMinutes(5);

	private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new();

	public void RecordActivity(string? ipAddress)
	{
		if (string.IsNullOrWhiteSpace(ipAddress))
		{
			return;
		}

		_lastSeen[ipAddress] = DateTimeOffset.UtcNow;
	}

	public IReadOnlyList<(string IpAddress, DateTimeOffset LastSeen)> GetActiveDevices()
	{
		var threshold = DateTimeOffset.UtcNow - ActiveThreshold;
		return _lastSeen
			.Where(kv => kv.Value >= threshold)
			.OrderByDescending(kv => kv.Value)
			.Select(kv => (kv.Key, kv.Value))
			.ToList();
	}
}
