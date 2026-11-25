using System.Security.Cryptography;
using System.Text;

namespace LoalNas.Host.Services;

/// <summary>
/// 设备身份：名称、稳定 ID、用于二维码配对的 secret。
/// Linux 版：ID 来源 /etc/machine-id，secret 持久化在 LocalApplicationData（~/.local/share/loal-nas）。
/// </summary>
public sealed class DeviceIdentityService
{
    public string DeviceName { get; }
    public string DeviceId { get; }
    /// <summary>随机 32 字节 hex，首次启动生成后持久化。二维码里传给手机端作为信任标识。</summary>
    public string DeviceSecret { get; }

    public DeviceIdentityService()
    {
        DeviceName = Environment.MachineName;
        DeviceId = GetOrCreateStableDeviceId();
        DeviceSecret = GetOrCreateDeviceSecret();
    }

    private static string DataDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "loal-nas");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string GetOrCreateStableDeviceId()
    {
        var machineId = TryReadMachineId();
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            return HashToDisplayId(machineId!);
        }

        var idFilePath = Path.Combine(DataDirectory(), "device-id.txt");
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

            var created = HashToDisplayId(Guid.NewGuid().ToString("N"));
            File.WriteAllText(idFilePath, created);
            return created;
        }
        catch
        {
            return HashToDisplayId($"{Environment.MachineName}-{Environment.UserName}");
        }
    }

    private static string? TryReadMachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                if (File.Exists(path))
                {
                    var content = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return content;
                    }
                }
            }
            catch
            {
                // 忽略，继续尝试下一个来源
            }
        }

        return null;
    }

    private static string GetOrCreateDeviceSecret()
    {
        var secretFilePath = Path.Combine(DataDirectory(), "device-secret.txt");
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
    /// 对 secret 做简单混淆再落盘（防明文一眼可读）。
    /// 规则：交换 index 2 与 index 6，再在 index 8 插入 '5'，最终长 65。
    /// </summary>
    private static string EncodeSecret(string secret)
    {
        var chars = secret.ToCharArray();
        (chars[2], chars[6]) = (chars[6], chars[2]);
        var result = new char[chars.Length + 1];
        chars[..8].CopyTo(result, 0);
        result[8] = '5';
        chars[8..].CopyTo(result, 9);
        return new string(result);
    }

    private static string DecodeSecret(string encoded)
    {
        if (encoded.Length != 65)
            throw new FormatException($"SecretStore: expected 65 chars, got {encoded.Length}");
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
