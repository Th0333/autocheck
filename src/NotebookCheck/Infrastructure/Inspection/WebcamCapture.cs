using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

// WPF e WinRT têm um BitmapEncoder cada; aqui o que vale é o do WinRT.
using WinBitmapEncoder = Windows.Graphics.Imaging.BitmapEncoder;

namespace NotebookCheck.Infrastructure.Inspection;

/// <summary>Câmera disponível no sistema.</summary>
public sealed record CameraOption(string Id, string Nome)
{
    public override string ToString() => Nome;
}

/// <summary>
/// Captura fotos de uma webcam (a da bancada, apontada para a máquina) usando as
/// APIs WinRT de captura, disponíveis pelo alvo <c>net8.0-windows10.0.*</c>.
///
/// Não há preview do WPF pronto para MediaCapture — o CaptureElement é do UWP.
/// Então lemos os frames com um <see cref="MediaFrameReader"/> e devolvemos cada
/// um como <see cref="BitmapSource"/> em <see cref="FrameReady"/>; a janela só
/// pinta num Image. A foto é o frame que está na tela, codificado em JPEG —
/// assim o que o técnico vê é exatamente o que sobe pro ERP.
/// </summary>
public sealed class WebcamCapture : IAsyncDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private SoftwareBitmap? _ultimoFrame;
    private readonly object _frameLock = new();

    /// <summary>Frame novo da câmera, já congelado e pronto para exibir (thread da UI).</summary>
    public event EventHandler<BitmapSource>? FrameReady;

    public bool EstaLigada => _reader is not null;

    /// <summary>Câmeras vistas pelo sistema. Lista vazia = nenhuma conectada.</summary>
    public static async Task<IReadOnlyList<CameraOption>> ListarCamerasAsync()
    {
        try
        {
            var grupos = await MediaFrameSourceGroup.FindAllAsync();
            return grupos.Select(g => new CameraOption(g.Id, g.DisplayName)).ToList();
        }
        catch
        {
            return Array.Empty<CameraOption>();
        }
    }

    /// <summary>
    /// Liga a câmera escolhida e começa a entregar frames. Lança
    /// <see cref="InvalidOperationException"/> com motivo legível quando não dá
    /// (sem câmera, privacidade do Windows bloqueando, câmera em uso por outro app).
    /// </summary>
    public async Task IniciarAsync(string? cameraId, CancellationToken ct = default)
    {
        await PararAsync().ConfigureAwait(false);

        var grupos = await MediaFrameSourceGroup.FindAllAsync();
        if (grupos.Count == 0)
            throw new InvalidOperationException("Nenhuma câmera encontrada neste computador.");

        var grupo = cameraId is null
            ? grupos[0]
            : grupos.FirstOrDefault(g => g.Id == cameraId) ?? grupos[0];

        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = grupo,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });
        }
        catch (UnauthorizedAccessException)
        {
            capture.Dispose();
            throw new InvalidOperationException(
                "O Windows bloqueou o acesso à câmera. Abra Configurações → Privacidade → Câmera e permita apps de desktop.");
        }
        catch (Exception ex)
        {
            capture.Dispose();
            throw new InvalidOperationException($"Não foi possível abrir a câmera: {ex.Message}", ex);
        }

        ct.ThrowIfCancellationRequested();

        var source = capture.FrameSources.Values
            .FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
            ?? capture.FrameSources.Values.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
            ?? capture.FrameSources.Values.FirstOrDefault();

        if (source is null)
        {
            capture.Dispose();
            throw new InvalidOperationException("A câmera não expôs nenhum fluxo de vídeo.");
        }

        // maior resolução disponível: a foto vai ser recortada da prévia
        var melhor = source.SupportedFormats
            .Where(f => f.VideoFormat.Width > 0 && f.VideoFormat.Height > 0)
            .OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
            .FirstOrDefault();
        if (melhor is not null)
            await source.SetFormatAsync(melhor);

        var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        reader.FrameArrived += OnFrameArrived;
        await reader.StartAsync();

        _capture = capture;
        _reader = reader;
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var bmp = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bmp is null) return;

        // o SoftwareBitmap do frame morre com o frame: guardamos uma cópia
        var copia = SoftwareBitmap.Copy(bmp);
        SoftwareBitmap? anterior;
        lock (_frameLock)
        {
            anterior = _ultimoFrame;
            _ultimoFrame = copia;
        }
        anterior?.Dispose();

        var handler = FrameReady;
        if (handler is null) return;

        var origem = ParaBitmapSource(copia);
        if (origem is not null) handler(this, origem);
    }

    /// <summary>Converte o frame em algo que o WPF sabe pintar (congelado: cruza threads).</summary>
    private static BitmapSource? ParaBitmapSource(SoftwareBitmap bmp)
    {
        try
        {
            // o reader entrega Bgra8, então o stride é 4 bytes por pixel
            var stride = bmp.PixelWidth * 4;
            var bytes = new byte[stride * bmp.PixelHeight];
            bmp.CopyToBuffer(bytes.AsBuffer());

            var origem = BitmapSource.Create(
                bmp.PixelWidth, bmp.PixelHeight, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, bytes, stride);
            origem.Freeze();
            return origem;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Congela o frame atual em JPEG. Devolve null se ainda não chegou nenhum frame.
    /// </summary>
    public async Task<byte[]?> TirarFotoJpegAsync()
    {
        SoftwareBitmap? frame;
        lock (_frameLock)
        {
            frame = _ultimoFrame is null ? null : SoftwareBitmap.Copy(_ultimoFrame);
        }
        if (frame is null) return null;

        try
        {
            using var ms = new InMemoryRandomAccessStream();
            var encoder = await WinBitmapEncoder.CreateAsync(WinBitmapEncoder.JpegEncoderId, ms);
            // o encoder JPEG não aceita canal alpha
            using var semAlpha = SoftwareBitmap.Convert(frame, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            encoder.SetSoftwareBitmap(semAlpha);
            await encoder.FlushAsync();

            var bytes = new byte[ms.Size];
            await ms.ReadAsync(bytes.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);
            return bytes;
        }
        finally
        {
            frame.Dispose();
        }
    }

    public async Task PararAsync()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            try { await _reader.StopAsync(); } catch { /* já parado */ }
            _reader.Dispose();
            _reader = null;
        }
        _capture?.Dispose();
        _capture = null;

        lock (_frameLock)
        {
            _ultimoFrame?.Dispose();
            _ultimoFrame = null;
        }
    }

    public async ValueTask DisposeAsync() => await PararAsync().ConfigureAwait(false);
}
