using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace LoalNas.Host.Services;

public sealed class DeviceIdentityService
{
	public string DeviceName { get; }
	public string DeviceId { get; }

	public DeviceIdentityService()
	{
		DeviceName = Environment.MachineName;
		DeviceId = GetOrCreateStableDeviceId();
	}

	private static string GetOrCreateStableDeviceId()
	{
		var machineGuid = TryGetMachineGuid();
		if (!string.IsNullOrWhiteSpace(machineGuid))
		{
			return HashToDisplayId(machineGuid!);
		}

		var dataDirectory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"loal_NAS");
		var idFilePath = Path.Combine(dataDirectory, "device-id.txt");

		try
		{
			if (File.Exists(idFilePath))
			{
				var existing = File.ReadAllText(idFilePath).Trim();
				if (!string.IsNullOrWhiteSpace(existing))
				{
					return existing;
				}
			}

			Directory.CreateDirectory(dataDirectory);
			var created = HashToDisplayId(Guid.NewGuid().ToString("N"));
			File.WriteAllText(idFilePath, created);
			return created;
		}
		catch
		{
			return HashToDisplayId($"{Environment.MachineName}-{Environment.UserName}");
		}
	}

	private static string? TryGetMachineGuid()
	{
		try
		{
			using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
			return key?.GetValue("MachineGuid") as string;
		}
		catch
		{
			return null;
		}
	}

	private static string HashToDisplayId(string raw)
	{
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
		var hex = Convert.ToHexString(hash).ToLowerInvariant();
		var shortHex = hex[..32];
		return string.Create(36, shortHex, static (buffer, value) =>
		{
			value[..8].CopyTo(buffer);
			buffer[8] = '-';
			value.AsSpan(8, 4).CopyTo(buffer[9..]);
			buffer[13] = '-';
			value.AsSpan(12, 4).CopyTo(buffer[14..]);
			buffer[18] = '-';
			value.AsSpan(16, 4).CopyTo(buffer[19..]);
			buffer[23] = '-';
			value.AsSpan(20, 12).CopyTo(buffer[24..]);
		});
	}
}