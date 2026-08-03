using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Wave;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Painel embutido do teste de microfone: VU meter AO VIVO via NAudio, mais o
/// ciclo de <b>gravar → encerrar → ouvir</b> dentro do próprio modal, para o
/// técnico julgar o áudio antes de marcar o resultado.
///
/// A captação fica ligada desde que o painel abre (é ela que move a barra); a
/// gravação é só um interruptor que manda o mesmo fluxo para um buffer WAV.
/// Durante a reprodução a captação é PAUSADA — sem isso o alto-falante
/// realimenta o microfone e a barra dispara sozinha.
/// </summary>
public partial class MicTestControl : UserControl, ITestInlineControl
{
    /// <summary>Fase atual do painel; manda no rótulo e no estado dos botões.</summary>
    private enum Fase { Ouvindo, Gravando, Pronto, Reproduzindo }

    /// <summary>Buffer WAV da última gravação (null enquanto nada foi gravado).</summary>
    public byte[]? Wav { get; private set; }

    /// <summary>Duração, em segundos, da última gravação.</summary>
    public double RecordedSeconds { get; private set; }

    /// <summary>
    /// Envia a gravação para fora (ERP). Injetado pela ViewModel, que é quem
    /// conhece o <c>test_id</c>/NTB da máquina — o painel não fala com o ERP.
    /// Recebe (wav, duração) e devolve se realmente subiu mais a mensagem a
    /// mostrar. Nulo esconde o botão.
    /// </summary>
    public Func<byte[], double, Task<(bool Enviado, string Mensagem)>>? UploadAsync
    {
        get => _uploadAsync;
        set
        {
            _uploadAsync = value;
            sendBtn.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private const int Segments = 24;
    private const double MaxSeconds = 30.0;

    /// <summary>16 kHz mono: voz clara com 1/3 do tamanho de 44,1 kHz.</summary>
    private static readonly WaveFormat Format = new(16000, 16, 1);

    private readonly Rectangle[] _seg = new Rectangle[Segments];

    private static readonly Brush Unlit = new SolidColorBrush(Color.FromRgb(0x33, 0x3A, 0x46));
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

    private WaveInEvent? _capture;
    private MemoryStream? _ms;
    private WaveFileWriter? _writer;
    private WaveOutEvent? _player;
    private WaveFileReader? _reader;

    private Func<byte[], double, Task<(bool Enviado, string Mensagem)>>? _uploadAsync;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _recWatch = new();

    private Fase _fase = Fase.Ouvindo;
    private bool _recording;
    private bool _hasDevice;
    private bool _soundDetected;
    private bool _clipping;
    private bool _sent;
    private float _peakOverall;
    private float _peakHold;
    private bool _stopped;

    public MicTestControl()
    {
        InitializeComponent();
        Freeze(Unlit); Freeze(Green); Freeze(Yellow); Freeze(Red);
        BuildMeter();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += OnTimerTick;

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
            var texto = $"Microfone — pico {pk}% ({dbTxt}, {heard})";
            if (Wav is { Length: > 0 })
            {
                texto += $", gravação de {RecordedSeconds:0}s";
                if (_sent) texto += " enviada ao ERP";
            }
            return texto;
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
            recorderPanel.Visibility = Visibility.Collapsed;
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
        deviceBox.SelectedIndex = 0; // dispara OnDeviceChanged, que abre a captura
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_hasDevice) return;

        // Trocar de microfone no meio da gravação invalida o que foi gravado:
        // metade seria de um aparelho e metade de outro.
        if (_fase == Fase.Gravando) DiscardRecording("Dispositivo trocado — a gravação anterior foi descartada.");
        if (_fase == Fase.Reproduzindo) StopPlayback();

        StartCapture(Math.Max(0, deviceBox.SelectedIndex));
    }

    // ----------------------------------------------------------- captura ---

    /// <summary>Abre o microfone para o VU meter. Não grava nada por si só.</summary>
    private void StartCapture(int device)
    {
        StopCapture();
        try
        {
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

    private void StopCapture()
    {
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
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_recording)
        {
            try { _writer?.Write(e.Buffer, 0, e.BytesRecorded); } catch { /* ignore */ }
        }

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
        if (_stopped || _fase == Fase.Reproduzindo) return;
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

    private void ClearMeter()
    {
        for (var i = 0; i < Segments; i++) _seg[i].Fill = Unlit;
        levelText.Text = "0%";
    }

    private Brush ColorFor(int index)
    {
        var frac = (index + 1) / (double)Segments;
        if (frac <= 0.78) return Green;
        if (frac <= 0.94) return Yellow;
        return Red;
    }

    // ---------------------------------------------------------- gravação ---

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (_fase == Fase.Gravando) EndRecording(porLimite: false);
        else BeginRecording();
    }

    private void BeginRecording()
    {
        if (!_hasDevice) return;

        // Regravar descarta a anterior — dito na tela, para não haver surpresa.
        Wav = null;
        RecordedSeconds = 0;
        _sent = false;
        sendBtn.Content = "☁ Enviar pro ERP"; // senão fica "✓ Enviado" da gravação velha
        HideSendMessage();

        try
        {
            _ms = new MemoryStream();
            _writer = new WaveFileWriter(_ms, Format);
        }
        catch (Exception ex)
        {
            ShowSendMessage($"Não foi possível iniciar a gravação: {ex.Message}", Red);
            return;
        }

        _recording = true;
        _recWatch.Restart();
        _timer.Start();
        SetFase(Fase.Gravando);
    }

    /// <param name="porLimite">true quando parou sozinha nos 30 s.</param>
    private void EndRecording(bool porLimite)
    {
        if (!_recording) return;

        _recording = false;
        _recWatch.Stop();
        _timer.Stop();
        RecordedSeconds = Math.Min(_recWatch.Elapsed.TotalSeconds, MaxSeconds);

        try
        {
            // Flush() reescreve os tamanhos no cabeçalho RIFF; sem ele o WAV
            // sai com comprimento zero e nenhum player toca.
            _writer?.Flush();
            if (_ms is not null && _ms.Length > 44) Wav = _ms.ToArray();
        }
        catch { /* ignore */ }
        finally
        {
            try { _writer?.Dispose(); } catch { /* ignore */ }
            try { _ms?.Dispose(); } catch { /* ignore */ }
            _writer = null;
            _ms = null;
        }

        SetFase(Fase.Pronto);
        if (Wav is null)
        {
            recStateText.Text = "Nada foi capturado. Tente gravar de novo.";
            playBtn.IsEnabled = false;
            sendBtn.IsEnabled = false;
        }
        else if (porLimite)
        {
            recStateText.Text = $"Gravação de {RecordedSeconds:0}s (limite de 30s atingido). Ouça antes de marcar o resultado.";
        }
    }

    /// <summary>Joga fora a gravação em andamento sem virar resultado.</summary>
    private void DiscardRecording(string motivo)
    {
        _recording = false;
        _recWatch.Reset();
        _timer.Stop();
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _ms?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _ms = null;
        Wav = null;
        RecordedSeconds = 0;
        SetFase(Fase.Ouvindo);
        recStateText.Text = motivo;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var s = _recWatch.Elapsed.TotalSeconds;
        if (s >= MaxSeconds)
        {
            EndRecording(porLimite: true);
            return;
        }
        recTimeText.Text = $"{Fmt(s)} / 0:30";
    }

    private static string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{t.Minutes}:{t.Seconds:00}";
    }

    // -------------------------------------------------------- reprodução ---

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_fase == Fase.Reproduzindo) StopPlayback();
        else StartPlayback();
    }

    private void StartPlayback()
    {
        if (Wav is not { Length: > 0 }) return;

        // Pausa a captação: sem isso o alto-falante volta pro microfone e a
        // barra reage ao próprio áudio (na pior das hipóteses, microfonia).
        StopCapture();
        ClearMeter();

        try
        {
            _reader = new WaveFileReader(new MemoryStream(Wav, writable: false));
            _player = new WaveOutEvent();
            _player.Init(_reader);
            _player.PlaybackStopped += OnPlaybackStopped;
            _player.Play();
        }
        catch (Exception ex)
        {
            DisposePlayback();
            ResumeCapture();
            SetFase(Fase.Pronto);
            ShowSendMessage($"Não foi possível reproduzir: {ex.Message}", Red);
            return;
        }

        SetFase(Fase.Reproduzindo);
    }

    private void StopPlayback()
    {
        try { _player?.Stop(); } catch { /* o PlaybackStopped faz a limpeza */ }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Vem da thread de áudio; toda a UI tem que voltar pro dispatcher.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            DisposePlayback();
            if (_stopped) return;
            ResumeCapture();
            SetFase(Fase.Pronto);
        }));
    }

    private void DisposePlayback()
    {
        try
        {
            if (_player is not null)
            {
                _player.PlaybackStopped -= OnPlaybackStopped;
                _player.Dispose();
                _player = null;
            }
        }
        catch { /* ignore */ }
        try { _reader?.Dispose(); } catch { /* ignore */ }
        _reader = null;
    }

    private void ResumeCapture()
    {
        if (!_hasDevice || _stopped) return;
        StartCapture(Math.Max(0, deviceBox.SelectedIndex));
    }

    // ------------------------------------------------------------- envio ---

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (_uploadAsync is null || Wav is not { Length: > 0 } wav) return;

        sendBtn.IsEnabled = false;
        sendBtn.Content = "Enviando...";
        HideSendMessage();
        try
        {
            var (enviado, msg) = await _uploadAsync(wav, RecordedSeconds);

            // Ficou na fila também trava o botão: reenviar duplicaria o áudio no
            // ERP, já que a cópia enfileirada sobe sozinha depois.
            _sent = true;
            sendBtn.Content = enviado ? "✓ Enviado" : "☁ Na fila";
            // Ficar na fila não é erro (nada se perdeu), mas também não é
            // sucesso — âmbar em vez de verde ou vermelho.
            if (!string.IsNullOrWhiteSpace(msg)) ShowSendMessage(msg, enviado ? Green : Yellow);
        }
        catch (Exception ex)
        {
            sendBtn.Content = "☁ Enviar pro ERP";
            sendBtn.IsEnabled = true;
            ShowSendMessage($"Não foi possível enviar: {ex.Message}", Red);
        }
    }

    private void ShowSendMessage(string texto, Brush cor)
    {
        sendMsgText.Text = texto;
        sendMsgText.Foreground = cor;
        sendMsgText.Visibility = Visibility.Visible;
    }

    private void HideSendMessage()
    {
        sendMsgText.Visibility = Visibility.Collapsed;
        sendMsgText.Text = "";
    }

    // ------------------------------------------------------------ estado ---

    /// <summary>Único lugar que mexe nos rótulos e no habilitado dos botões.</summary>
    private void SetFase(Fase fase)
    {
        _fase = fase;
        var temAudio = Wav is { Length: > 0 };

        switch (fase)
        {
            case Fase.Ouvindo:
                recBtn.Content = "● Gravar";
                recBtn.IsEnabled = _hasDevice;
                playBtn.Content = "▶ Ouvir";
                playBtn.IsEnabled = false;
                sendBtn.IsEnabled = false;
                recTimeText.Text = "0:00 / 0:30";
                break;

            case Fase.Gravando:
                recBtn.Content = "■ Encerrar";
                recBtn.IsEnabled = true;
                playBtn.Content = "▶ Ouvir";
                playBtn.IsEnabled = false;
                sendBtn.IsEnabled = false;
                recStateText.Text = "Gravando... fale perto do microfone e aperte Encerrar quando terminar.";
                recTimeText.Text = "0:00 / 0:30";
                break;

            case Fase.Pronto:
                recBtn.Content = "● Gravar de novo";
                recBtn.IsEnabled = _hasDevice;
                playBtn.Content = "▶ Ouvir";
                playBtn.IsEnabled = temAudio;
                sendBtn.IsEnabled = temAudio && _uploadAsync is not null && !_sent;
                if (temAudio)
                {
                    recStateText.Text = $"Gravação de {RecordedSeconds:0}s pronta. Ouça antes de marcar o resultado.";
                    recTimeText.Text = $"{Fmt(RecordedSeconds)} gravados";
                }
                break;

            case Fase.Reproduzindo:
                recBtn.IsEnabled = false;
                playBtn.Content = "■ Parar";
                playBtn.IsEnabled = true;
                sendBtn.IsEnabled = false;
                recStateText.Text = "Reproduzindo a gravação — a captação está pausada.";
                statusDot.Fill = Unlit;
                statusText.Text = "Reproduzindo...";
                if (TryFindResource("Ink600Brush") is Brush ink) statusText.Foreground = ink;
                break;
        }
    }

    public void StopTest()
    {
        _stopped = true;
        _timer.Stop();

        // Fechar o modal gravando ainda aproveita o que foi captado.
        if (_recording) EndRecording(porLimite: false);

        StopPlayback();
        DisposePlayback();
        StopCapture();

        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _ms?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _ms = null;
    }
}
