using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NAudio.Wave;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Painel embutido do teste de microfone: VU meter AO VIVO via NAudio. O áudio
/// captado fica num buffer WAV (<see cref="Wav"/>) para reprodução posterior.
/// Portado do antigo MicTestWindow para rodar dentro do TestActionWindow.
/// </summary>
public partial class MicTestControl : UserControl, ITestInlineControl
{
    /// <summary>Buffer WAV capturado durante o teste.</summary>
    public byte[]? Wav { get; private set; }

    private const int Segments = 24;
    private static readonly WaveFormat Format = new(44100, 16, 1);
    private readonly Rectangle[] _seg = new Rectangle[Segments];

    private static readonly Brush Unlit = new SolidColorBrush(Color.FromRgb(0x33, 0x3A, 0x46));
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

    private WaveInEvent? _capture;
    private MemoryStream? _ms;
    private WaveFileWriter? _writer;
    private bool _hasDevice;
    private bool _soundDetected;
    private bool _clipping;
    private float _peakOverall;
    private float _peakHold;
    private bool _stopped;

    public MicTestControl()
    {
        InitializeComponent();
        Freeze(Unlit); Freeze(Green); Freeze(Yellow); Freeze(Red);
        BuildMeter();
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => StopTest();
    }

    public string Summary
    {
        get
        {
            if (!_hasDevice) return "Sem dispositivo de captura";
            var pk = (int)Math.Round(MeterFrac(_peakOverall) * 100);
            var db = ToDb(_peakOverall);
            var dbTxt = double.IsNegativeInfinity(db) ? "silêncio" : $"{db:0} dBFS";
            var heard = _clipping ? "clipando" : _soundDetected ? "som detectado" : "nenhum som captado";
            return $"Microfone — pico {pk}% ({dbTxt}, {heard})";
        }
    }

    private static void Freeze(Brush b) { if (b.CanFreeze && !b.IsFrozen) b.Freeze(); }

    private void BuildMeter()
    {
        meterGrid.Children.Clear();
        for (var i = 0; i < Segments; i++)
        {
            var r = new Rectangle { Margin = new Thickness(1, 0, 1, 0), RadiusX = 2, RadiusY = 2, Fill = Unlit };
            _seg[i] = r;
            meterGrid.Children.Add(r);
        }
    }

    private void Start()
    {
        var count = WaveInEvent.DeviceCount;
        _hasDevice = count > 0;
        if (!_hasDevice)
        {
            noDevicePanel.Visibility = Visibility.Visible;
            devicePanel.Visibility = Visibility.Collapsed;
            statusText.Text = "Sem dispositivo de captura";
            return;
        }

        deviceBox.Items.Clear();
        for (var i = 0; i < count; i++)
        {
            var name = WaveInEvent.GetCapabilities(i).ProductName;
            deviceBox.Items.Add(string.IsNullOrWhiteSpace(name) ? $"Dispositivo {i}" : name);
        }
        devicePanel.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        deviceBox.SelectedIndex = 0;
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_hasDevice) return;
        StartCapture(Math.Max(0, deviceBox.SelectedIndex));
    }

    private void StartCapture(int device)
    {
        StopTest();
        try
        {
            _stopped = false;
            _ms = new MemoryStream();
            _writer = new WaveFileWriter(_ms, Format);
            _capture = new WaveInEvent { DeviceNumber = device, WaveFormat = Format, BufferMilliseconds = 50 };
            _capture.DataAvailable += OnData;
            _capture.StartRecording();
        }
        catch (Exception)
        {
            noDevicePanel.Visibility = Visibility.Visible;
            statusText.Text = "Não foi possível abrir o microfone";
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try { _writer?.Write(e.Buffer, 0, e.BytesRecorded); } catch { /* ignore */ }

        float peak = 0;
        for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            short s = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
            var f = Math.Abs(s) / 32768f;
            if (f > peak) peak = f;
        }

        if (peak > _peakOverall) _peakOverall = peak;
        if (peak > 0.04f) _soundDetected = true;
        if (peak >= 0.985f) _clipping = true;

        if (!Dispatcher.CheckAccess())
            Dispatcher.BeginInvoke(new Action(() => UpdateMeter(peak)));
        else
            UpdateMeter(peak);
    }

    private const double FloorDb = -54.0;
    private static double ToDb(float lin) => lin > 1e-5f ? 20.0 * Math.Log10(lin) : double.NegativeInfinity;
    private static double MeterFrac(float lin)
    {
        var db = ToDb(lin);
        if (double.IsNegativeInfinity(db)) return 0;
        return Math.Clamp((db - FloorDb) / (0 - FloorDb), 0, 1);
    }

    private void UpdateMeter(float level)
    {
        if (_stopped) return;
        var frac = (float)MeterFrac(level);

        _peakHold = Math.Max(frac, _peakHold - 0.02f);
        if (_peakHold < 0) _peakHold = 0;

        var lit = (int)Math.Round(frac * Segments);
        var holdIdx = Math.Min(Segments - 1, (int)Math.Round(_peakHold * Segments) - 1);
        for (var i = 0; i < Segments; i++)
        {
            if (i < lit || i == holdIdx) _seg[i].Fill = ColorFor(i);
            else _seg[i].Fill = Unlit;
        }

        levelText.Text = $"{(int)Math.Round(frac * 100)}%";
        var db = ToDb(level);
        var pkPercent = (int)Math.Round(MeterFrac(_peakOverall) * 100);
        peakText.Text = double.IsNegativeInfinity(db)
            ? $"Pico: {pkPercent}%  •  silêncio"
            : $"Pico: {pkPercent}%  •  agora {db:0} dBFS";

        if (_clipping)
        {
            statusDot.Fill = Red; statusText.Text = "Clipando (0 dBFS) ⚠"; statusText.Foreground = Red;
        }
        else if (_soundDetected)
        {
            statusDot.Fill = Green; statusText.Text = "Som detectado ✓"; statusText.Foreground = Green;
        }
    }

    private Brush ColorFor(int index)
    {
        var frac = (index + 1) / (double)Segments;
        if (frac <= 0.78) return Green;
        if (frac <= 0.94) return Yellow;
        return Red;
    }

    public void StopTest()
    {
        _stopped = true;
        try
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnData;
                _capture.StopRecording();
                _capture.Dispose();
                _capture = null;
            }
        }
        catch { /* ignore */ }

        try
        {
            _writer?.Flush();
            if (_ms is not null && _ms.Length > 0) Wav = _ms.ToArray();
            _writer?.Dispose();
            _writer = null;
            _ms?.Dispose();
            _ms = null;
        }
        catch { /* ignore */ }
    }
}
