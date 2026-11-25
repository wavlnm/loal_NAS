using QRCoder;

namespace LoalNas.Host.Services;

/// <summary>
/// 二维码生成。使用 PngByteQRCode，纯托管输出 PNG，不依赖 System.Drawing / libgdiplus。
/// </summary>
public static class QrService
{
    public static byte[] CreatePng(string payload, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(qrData);
        return qrCode.GetGraphic(pixelsPerModule);
    }
}
