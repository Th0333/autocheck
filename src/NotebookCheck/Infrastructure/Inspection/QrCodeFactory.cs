using System;
using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace NotebookCheck.Infrastructure.Inspection;

/// <summary>
/// Gera QR codes como <see cref="BitmapSource"/> para exibição no WPF.
/// </summary>
public static class QrCodeFactory
{
    /// <summary>
    /// Cria um QR para a URL informada, devolvendo um PNG pronto para Image.Source.
    /// </summary>
    public static BitmapSource? Create(string content, int pixelsPerModule = 8)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data);
            var bytes = png.GetGraphic(pixelsPerModule);

            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
