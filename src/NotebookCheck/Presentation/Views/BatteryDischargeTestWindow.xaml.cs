using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NotebookCheck.Infrastructure.Hardware;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Teste de descarga da bateria AO VIVO. Lê a API nativa de bateria
/// (<see cref="BatteryDeviceReader"/>) a cada ~1 s e mostra carga, taxa de
/// descarga (W) e autonomia estimada. O técnico desconecta o carregador e
/// confirma que o notebook se mantém ligado e descarrega num ritmo plausível.
/// </summary>
public partial class BatteryDischargeTestWindow : TestResultWindow
{
    private static readonly Brush OnAc = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));   // ink
    private static readonly Brush OnBatt = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)); // green

    private readonly DispatcherTimer _timer;
    private bool _hasBattery = true;
    private bool _wentToBattery;
    private int _onBatterySamples;
    private double _lastDischargeW = double.NaN;
    private double _lastRuntimeH = double.NaN;
    private double _peakDischargeW = double.NaN;

    public BatteryDischargeTestWindow()
    {
        InitializeComponent();
        if (OnAc.CanFreeze) OnAc.Freeze();
        if (OnBatt.CanFreeze) OnBatt.Freeze();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { Tick(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void Tick()
    {
        BatteryDeviceData? d;
        try { d = BatteryDeviceReader.ReadAll().FirstOrDefault(); }
        catch { d = null; }

        if (d is null)
        {
            _hasBattery = false;
            noBatteryPanel.Visibility = Visibility.Visible;
            acText.Text = "Sem bateria detectada";
            statusText.Text = "Não há bateria legível neste equipamento. Marque o resultado manualmente.";
            okBtn.IsEnabled = false;
            return;
        }

        // Estado AC e descarga.
        var onBattery = d.Discharging || !d.OnLine;
        acBanner.Background = onBattery ? OnBatt : OnAc;
        acText.Foreground = Brushes.White;
        acText.Text = onBattery ? "🔋 Rodando na bateria" : "🔌 Conectado à tomada (AC)";

        // Carga %.
        if (d.RemainingCapacityMwh is int rem && d.FullChargeCapacityMwh is int full && full > 0)
            chargeText.Text = $"{Math.Clamp(rem * 100.0 / full, 0, 100):0}%";
        else
            chargeText.Text = "—";

        // Taxa de descarga (Rate negativo = descarregando).
        double dischargeW = double.NaN, runtimeH = double.NaN;
        if (d.RateMw is int rate && rate < 0)
        {
            dischargeW = -rate / 1000.0;
            if (d.RemainingCapacityMwh is int r2 && r2 > 0)
                runtimeH = r2 / (double)(-rate);
        }

        rateText.Text = double.IsNaN(dischargeW) ? (onBattery ? "~0 W" : "— (na tomada)") : $"{dischargeW:0.0} W";
        runtimeText.Text = double.IsNaN(runtimeH) ? "—" : FormatRuntime(runtimeH);
        voltText.Text = d.VoltageMv is int mv && mv > 0 ? $"{mv / 1000.0:0.00} V" : "—";

        if (onBattery)
        {
            _wentToBattery = true;
            _onBatterySamples++;
            if (!double.IsNaN(dischargeW))
            {
                _lastDischargeW = dischargeW;
                _lastRuntimeH = runtimeH;
                if (double.IsNaN(_peakDischargeW) || dischargeW > _peakDischargeW) _peakDischargeW = dischargeW;
            }

            if (_onBatterySamples < 6)
                statusText.Text = "Na bateria — medindo a descarga. Aguarde alguns segundos para a leitura estabilizar...";
            else if (!double.IsNaN(_lastDischargeW))
                statusText.Text = $"Descarga medida: {_lastDischargeW:0.0} W" +
                    (double.IsNaN(_lastRuntimeH) ? "" : $", autonomia ~{FormatRuntime(_lastRuntimeH)}") +
                    ". Se o notebook se mantém ligado e descarrega normalmente, aprove.";
            else
                statusText.Text = "Na bateria, mas sem leitura de taxa de descarga. Confirme visualmente que a % cai e decida.";
        }
        else
        {
            statusText.Text = _wentToBattery
                ? "Carregador reconectado. Você já mediu a descarga — pode aprovar ou reprovar."
                : "Agora DESCONECTE o carregador para o notebook passar a rodar na bateria.";
        }
    }

    private static string FormatRuntime(double hours)
    {
        if (double.IsNaN(hours) || hours <= 0) return "—";
        var h = (int)hours;
        var m = (int)Math.Round((hours - h) * 60);
        if (m == 60) { h++; m = 0; }
        return h > 0 ? $"{h}h {m}min" : $"{m}min";
    }

    private string BuildMessage(bool ok)
    {
        if (!_hasBattery) return ok ? "Aprovado pelo técnico (sem leitura de bateria)" : "Sem bateria detectada";
        if (!_wentToBattery)
            return ok ? "Aprovado pelo técnico (não chegou a rodar na bateria)" : "Marcado como falho pelo técnico";

        var rate = !double.IsNaN(_lastDischargeW) ? $"{_lastDischargeW:0.0} W" : "n/d";
        var run = !double.IsNaN(_lastRuntimeH) ? FormatRuntime(_lastRuntimeH) : "n/d";
        return ok
            ? $"Mantém-se na bateria — descarga ~{rate}, autonomia estimada ~{run}"
            : $"Falha na descarga — descarga {rate}, autonomia {run}";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Approve(BuildMessage(true));
    }

    private void OnFailed(object sender, RoutedEventArgs e)
    {
        Reject(BuildMessage(false));
    }
}
