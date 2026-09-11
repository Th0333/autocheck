using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotebookCheck.Infrastructure.Inspection;

/// <summary>
/// Reduz uma foto para viajar DENTRO do relatório (base64 no JSON). Foto de
/// celular vem com 3–8 MB; nove delas estourariam o limite de corpo do
/// servidor (Vercel corta em ~4,5 MB) e o relatório inteiro seria recusado —
/// junto com todos os testes. Em 1280 px de lado maior e qualidade 78 cada
/// foto fica em ~150–250 KB, o suficiente para ver risco, trinca e etiqueta.
///
/// Respeita a orientação EXIF (celular em pé grava a imagem deitada com a
/// tag de rotação); sem isso a foto embutida apareceria de lado no painel.
/// Qualquer falha devolve os bytes originais: pior caso é a foto grande,
/// nunca a foto perdida.
/// </summary>
public static class PhotoShrinker
{
    /// <summary>Lado maior da foto embutida, em pixels.</summary>
    public const int MaxSide = 1280;

    /// <summary>Qualidade JPEG da foto embutida.</summary>
    public const int Quality = 78;

    /// <summary>Abaixo disto, e já dentro do tamanho, não recomprime (evita perder qualidade à toa).</summary>
    private const int SmallEnoughBytes = 400_000;

    public static byte[] ToReportJpeg(byte[] source)
    {
        if (source is null || source.Length == 0) return Array.Empty<byte>();
        try
        {
            using var input = new MemoryStream(source);
            var decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            var rotation = ReadExifRotation(frame);
            var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
            var needsResize = longest > MaxSide;

            if (!needsResize && rotation == Rotation.Rotate0 && source.Length <= SmallEnoughBytes)
                return source;

            BitmapSource result = frame;
            if (needsResize)
            {
                var scale = (double)MaxSide / longest;
                result = new TransformedBitmap(result, new ScaleTransform(scale, scale));
            }
            if (rotation != Rotation.Rotate0)
            {
                var angle = rotation switch
                {
                    Rotation.Rotate90 => 90,
                    Rotation.Rotate180 => 180,
                    Rotation.Rotate270 => 270,
                    _ => 0,
                };
                result = new TransformedBitmap(result, new RotateTransform(angle));
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = Quality };
            encoder.Frames.Add(BitmapFrame.Create(result));
            using var output = new MemoryStream();
            encoder.Save(output);
            var bytes = output.ToArray();
            return bytes.Length > 0 ? bytes : source;
        }
        catch
        {
            return source;
        }
    }

    /// <summary>Tag EXIF 274 (Orientation): 3 = 180°, 6 = 90° horário, 8 = 270°.</summary>
    private static Rotation ReadExifRotation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata meta) return Rotation.Rotate0;
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/app1/{ushort=0}/{ushort=274}" })
            {
                if (!meta.ContainsQuery(query)) continue;
                var value = meta.GetQuery(query);
                var orientation = value switch
                {
                    ushort u => u,
                    short s => s,
                    int i => i,
                    _ => 1,
                };
                return orientation switch
                {
                    3 => Rotation.Rotate180,
                    6 => Rotation.Rotate90,
                    8 => Rotation.Rotate270,
                    _ => Rotation.Rotate0,
                };
            }
        }
        catch
        {
            // metadado ilegível — segue sem rotação
        }
        return Rotation.Rotate0;
    }
}
