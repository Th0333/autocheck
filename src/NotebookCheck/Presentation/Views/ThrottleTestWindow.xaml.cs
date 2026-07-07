using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NotebookCheck.Application.Bench;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Teste de throttling térmico AO VIVO: pega todos os núcleos da CPU com carga
/// SIMD por ~60 s enquanto mostra temperatura e clock efetivo em tempo real. Ao
/// final, compara o clock do início vs do fim (via <see cref="SnapshotAnalysis"/>)
/// para decidir se houve queda sob calor (throttling).
/// </summary>
public partial class ThrottleTestWindow : TestResultWindow
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(60);

    private readonly HardwareMonitor _monitor;
    private readonly DispatcherTimer _timer;
    private readonly List<HardwareSnapshot> _samples = new();
    private CancellationTokenSource? _loadCts;
    private Task[] _workers = Array.Empty<Task>();
    private DateTime _start;
    private double _peakTemp = double.NaN;
    private bool _finalized;

    public ThrottleTestWindow(ILogger logger)
    {
        InitializeComponent();
        _monitor = new HardwareMonitor(logger);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => StartLoad();
        Closed += (_, _) => Cleanup();
    }

    private void StartLoad()
    {
        _start = DateTime.UtcNow;
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        var n = Math.Max(1, Environment.ProcessorCount);
        _workers = new Task[n];
        for (var i = 0; i < n; i++)
            _workers[i] = Task.Run(() => Burn(ct), ct);
        _timer.Start();
    }

    /// <summary>Carga SIMD contínua: mantém a unidade vetorial ocupada (gera calor).</summary>
    private static void Burn(CancellationToken ct)
    {
        var lanes = Vector<float>.Count;
        var arr = new float[lanes * 1024];
        for (var i = 0; i < arr.Length; i++) arr[i] = i * 0.0001f;
        while (!ct.IsCancellationRequested)
        {
            for (var i = 0; i + lanes <= arr.Length; i += lanes)
            {
                var v = new Vector<float>(arr, i);
                v = System.Numerics.Vector.SquareRoot(v * v + Vector<float>.One);
                v.CopyTo(arr, i);
            }
        }
    }

    private void Tick()
    {
        HardwareSnapshot s;
        try { s = _monitor.Sample(); }
        catch { return; }
        _samples.Add(s);

        var temp = !double.IsNaN(s.CpuTempMax) ? s.CpuTempMax : s.CpuTempPackage;
        if (!double.IsNaN(temp) && (double.IsNaN(_peakTemp) || temp > _peakTemp)) _peakTemp = temp;

        tempText.Text = double.IsNaN(temp) ? "—" : $"{temp:0} °C";
        tempPeakText.Text = double.IsNaN(_peakTemp) ? "Pico: —" : $"Pico: {_peakTemp:0} °C";
        clockText.Text = double.IsNaN(s.CpuClockMaxMhz) ? "—" : $"{s.CpuClockMaxMhz / 1000.0:0.00} GHz";
        loadText.Text = double.IsNaN(s.CpuLoadPercent) ? "—" : $"{s.CpuLoadPercent:0}%";
        powerText.Text = double.IsNaN(s.CpuPowerWatts) ? "—" : $"{s.CpuPowerWatts:0} W";

        var elapsed = DateTime.UtcNow - _start;
        var remaining = Duration - elapsed;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        timeText.Text = $"{remaining.TotalSeconds:0} s";
        progress.Value = Math.Min(100, elapsed.TotalSeconds / Duration.TotalSeconds * 100);

        // Clock inicial (média dos primeiros segundos) para comparação visual.
        if (_samples.Count == 4 && !double.IsNaN(s.CpuClockMaxMhz))
        {
            double sum = 0; int c = 0;
            foreach (var sm in _samples)
                if (!double.IsNaN(sm.CpuClockMaxMhz)) { sum += sm.CpuClockMaxMhz; c++; }
            if (c > 0) clockBaseText.Text = $"Inicial: {sum / c / 1000.0:0.00} GHz";
        }

        if (elapsed >= Duration) Evaluate();
    }

    private void StopLoad()
    {
        _timer.Stop();
        try { _loadCts?.Cancel(); } catch { /* ignore */ }
        try { Task.WaitAll(_workers, 2000); } catch { /* ignore */ }
    }

    private void Evaluate()
    {
        if (_finalized) return;
        _finalized = true;
        StopLoad();

        var stability = SnapshotAnalysis.CpuClockStability(_samples);   // 100 = sem queda
        var maxTemp = SnapshotAnalysis.MaxCpuTemp(_samples);

        // Clock início vs fim para a mensagem.
        double startClk = double.NaN, endClk = double.NaN;
        var valid = _samples.FindAll(s => !double.IsNaN(s.CpuClockMaxMhz));
        if (valid.Count >= 5)
        {
            startClk = Avg(valid, 0, valid.Count / 5);
            endClk = Avg(valid, valid.Count * 4 / 5, valid.Count);
        }

        var hasStability = !double.IsNaN(stability);
        var throttled = hasStability && stability < 90;   // caiu > 10%

        string clockMsg = (!double.IsNaN(startClk) && !double.IsNaN(endClk))
            ? $"clock {startClk / 1000.0:0.00}→{endClk / 1000.0:0.00} GHz"
            : "clock indisponível";
        string stabMsg = hasStability ? $"estabilidade {stability:0}%" : "estabilidade n/d";
        string tempMsg = double.IsNaN(maxTemp) ? "temp. n/d" : $"pico {maxTemp:0} °C";

        Message = $"{clockMsg} ({stabMsg}), {tempMsg} — " +
                  (throttled ? "throttling térmico detectado" : "sem throttling significativo");

        verdictPanel.Visibility = Visibility.Visible;
        verdictText.Text = throttled
            ? $"⚠ Possível throttling: o clock caiu sob carga ({stabMsg}). {tempMsg}. Verifique refrigeração/pasta térmica."
            : !hasStability
                ? $"Não foi possível ler o clock para avaliar a estabilidade. {tempMsg}. Avalie pela temperatura e decida manualmente."
                : $"✓ Clock se manteve estável sob carga ({stabMsg}). {tempMsg}.";

        // Sugere veredito, mas o técnico decide.
        runBar.Visibility = Visibility.Collapsed;
        decisionBar.Visibility = Visibility.Visible;
    }

    private static double Avg(List<HardwareSnapshot> list, int from, int to)
    {
        double sum = 0; int c = 0;
        for (var i = from; i < to && i < list.Count; i++) { sum += list[i].CpuClockMaxMhz; c++; }
        return c > 0 ? sum / c : double.NaN;
    }

    private void OnStop(object sender, RoutedEventArgs e) => Evaluate();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Approve(string.IsNullOrEmpty(Message) ? "Aprovado pelo técnico" : Message);
    }

    private void OnFailed(object sender, RoutedEventArgs e)
    {
        Reject(string.IsNullOrEmpty(Message) ? "Throttling marcado pelo técnico" : Message);
    }

    private void Cleanup()
    {
        StopLoad();
        try { _monitor.Dispose(); } catch { /* ignore */ }
    }
}
