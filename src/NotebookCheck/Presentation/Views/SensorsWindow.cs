using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NotebookCheck.Application.Bench;
using NotebookCheck.Presentation.Controls;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Janela de sensores AO VIVO: temperatura de CPU/GPU, clocks, uso e RPM das
/// ventoinhas, atualizada a cada ~700 ms via <see cref="HardwareMonitor"/>.
/// Construída em código para não exigir um XAML extra.
/// </summary>
public sealed class SensorsWindow : Window
{
    private readonly HardwareMonitor _monitor;
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _cpuTemp, _cpuClock, _cpuLoad, _cpuPower;
    private readonly TextBlock _gpuTemp, _gpuClock, _gpuLoad, _gpuFan;
    private readonly StackPanel _fansPanel;

    public SensorsWindow(ILogger logger)
    {
        Title = "Sensores ao vivo";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        // Visual do app: remove o frame padrão do Windows e usa a barra custom.
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 36,
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });
        Background = (Brush)(System.Windows.Application.Current?.FindResource("Ink50Brush") ?? Brushes.White);

        _monitor = new HardwareMonitor(logger, monitorFans: true);

        // Container: barra de título custom (topo) + conteúdo.
        var outer = new DockPanel();
        var titleBar = new ModalTitleBar { Title = "Sensores ao vivo" };
        DockPanel.SetDock(titleBar, Dock.Top);
        outer.Children.Add(titleBar);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Header("🌡 Sensores ao vivo"));
        root.Children.Add(new TextBlock
        {
            Text = "Atualiza a cada ~0,7 s. Rode um teste de carga em paralelo para ver as ventoinhas acelerarem.",
            FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var cpu = Section("PROCESSADOR");
        _cpuTemp = Row(cpu, "Temperatura");
        _cpuClock = Row(cpu, "Clock");
        _cpuLoad = Row(cpu, "Uso");
        _cpuPower = Row(cpu, "Consumo");
        Grid.SetColumn(cpu, 0);
        cpu.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(cpu);

        var gpu = Section("PLACA DE VÍDEO");
        _gpuTemp = Row(gpu, "Temperatura");
        _gpuClock = Row(gpu, "Clock");
        _gpuLoad = Row(gpu, "Uso");
        _gpuFan = Row(gpu, "Ventoinha GPU");
        Grid.SetColumn(gpu, 1);
        gpu.Margin = new Thickness(8, 0, 0, 0);
        grid.Children.Add(gpu);

        root.Children.Add(grid);

        var fansSection = Section("VENTOINHAS (PLACA-MÃE)");
        _fansPanel = new StackPanel();
        fansSection.Children.Add(_fansPanel);
        fansSection.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(fansSection);

        var close = new Button { Content = "Fechar", MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        outer.Children.Add(root); // preenche o resto (LastChildFill)
        Content = outer;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => Refresh();
        Closed += (_, _) => { _timer.Stop(); _monitor.Dispose(); };
        _timer.Start();
    }

    private void Refresh()
    {
        HardwareSnapshot s;
        try { s = _monitor.Sample(); }
        catch { return; }

        _cpuTemp.Text = Temp(s.CpuTempPackage, s.CpuTempMax);
        _cpuClock.Text = Mhz(s.CpuClockMaxMhz);
        _cpuLoad.Text = Pct(s.CpuLoadPercent);
        _cpuPower.Text = Watts(s.CpuPowerWatts);

        _gpuTemp.Text = Temp(s.GpuTempC, s.GpuHotspotC);
        _gpuClock.Text = Mhz(s.GpuCoreClockMhz);
        _gpuLoad.Text = Pct(s.GpuLoadPercent);
        _gpuFan.Text = double.IsNaN(s.GpuFanRpm) ? "—" : $"{s.GpuFanRpm:0} RPM";

        _fansPanel.Children.Clear();
        var fans = s.Fans ?? Array.Empty<(string, double)>();
        if (fans.Count == 0)
        {
            _fansPanel.Children.Add(new TextBlock
            {
                Text = "Nenhuma ventoinha lida pela placa-mãe (comum em notebooks com EC próprio).",
                FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var (name, rpm) in fans.Take(8))
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Margin = new Thickness(0, 2, 0, 2);
                row.Children.Add(new TextBlock { Text = name, FontSize = 12 });
                var v = new TextBlock { Text = $"{rpm:0} RPM", FontSize = 12, FontWeight = FontWeights.SemiBold };
                Grid.SetColumn(v, 1);
                row.Children.Add(v);
                _fansPanel.Children.Add(row);
            }
        }
    }

    private static string Temp(double a, double b)
    {
        var v = !double.IsNaN(a) ? a : b;
        return double.IsNaN(v) ? "—" : $"{v:0} °C";
    }
    private static string Mhz(double v) => double.IsNaN(v) ? "—" : $"{v / 1000.0:0.00} GHz";
    private static string Pct(double v) => double.IsNaN(v) ? "—" : $"{v:0}%";
    private static string Watts(double v) => double.IsNaN(v) ? "—" : $"{v:0} W";

    private static TextBlock Header(string text) => new()
    {
        Text = text, FontWeight = FontWeights.Bold, FontSize = 17, Margin = new Thickness(0, 0, 0, 2),
    };

    private static StackPanel Section(string title)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            Margin = new Thickness(0, 8, 0, 6),
        });
        return sp;
    }

    private static TextBlock Row(Panel parent, string label)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)) });
        var value = new TextBlock { Text = "—", FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(value, 1);
        g.Children.Add(value);
        parent.Children.Add(g);
        return value;
    }
}
