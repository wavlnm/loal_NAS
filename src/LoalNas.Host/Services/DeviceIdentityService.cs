using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace LoalNas.Host.Services;

public sealed class DeviceIdentityService
{
	public string DeviceName { get; }
	public string DeviceId { get; }
	/// <summary>随机 32 字节 hex 字符串，首次启动生成后持久化保存。用于二维码中对手机端的信任标识。</summary>
	public string DeviceSecret { get; }

	public DeviceIdentityService()
	{
		DeviceName = Environment.MachineName;
		DeviceId = GetOrCreateStableDeviceId();
		DeviceSecret = GetOrCreateDeviceSecret();
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

	private static string GetOrCreateDeviceSecret()
	{
		var dataDirectory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"loal_NAS");
		var secretFilePath = Path.Combine(dataDirectory, "device-secret.txt");

		try
		{
			if (File.Exists(secretFilePath))
			{
				var existing = File.ReadAllText(secretFilePath).Trim();
				if (existing.Length == 65)
				{
					return DecodeSecret(existing);
				}
				// 长度不对说明文件损坏，重新生成
			}

			Directory.CreateDirectory(dataDirectory);
			var bytes = RandomNumberGenerator.GetBytes(32);
			var secret = Convert.ToHexString(bytes).ToLowerInvariant();
			File.WriteAllText(secretFilePath, EncodeSecret(secret));
			return secret;
		}
		catch
		{
			// 回退：由机器名哈希派生，稳定但非随机
			return Convert.ToHexString(SHA256.HashData(
				Encoding.UTF8.GetBytes("secret-" + Environment.MachineName + Environment.UserName)))
				.ToLowerInvariant()[..64];
		}
	}

	/// <summary>
	/// 对 secret 进行简单混淆再落盘（防止明文一眼可读）。
	/// 规则：交换 index 2 与 index 6，然后在 index 8 位置插入字符 '5'。
	/// 最终存储字符串长 65。
	/// </summary>
	private static string EncodeSecret(string secret)
	{
		var chars = secret.ToCharArray();
		(chars[2], chars[6]) = (chars[6], chars[2]);
		// 在 index 8 插入 '5'
		var result = new char[chars.Length + 1];
		chars[..8].CopyTo(result, 0);
		result[8] = '5';
		chars[8..].CopyTo(result, 9);
		return new string(result);
	}

	/// <summary>从混淆格式还原明文 secret。<br/>
	/// encoded 应为 65 字符；黄令格式时抛出异常。</summary>
	private static string DecodeSecret(string encoded)
	{
		if (encoded.Length != 65)
			throw new FormatException($"SecretStore: expected 65 chars, got {encoded.Length}");
		// 删除 index 8 的 '5'
		var chars = (encoded[..8] + encoded[9..]).ToCharArray();
		(chars[2], chars[6]) = (chars[6], chars[2]);
		return new string(chars);
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