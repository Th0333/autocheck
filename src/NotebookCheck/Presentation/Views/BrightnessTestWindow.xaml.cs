using System;
using System.Windows;
using Microsoft.Extensions.Logging;
using NotebookCheck.Infrastructure.Hardware;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Teste manual de brilho do painel: um slider ajusta o brilho ao vivo (via
/// <see cref="BrightnessController"/>) para o técnico confirmar que a tela
/// responde do mínimo ao máximo. O brilho original é restaurado ao fechar.
/// </summary>
public partial class BrightnessTestWindow : TestResultWindow
{
    private readonly BrightnessController _ctrl;
    private readonly bool _supported;
    private readonly int _original;
    private bool _changing;          // suprime ValueChanged durante init programática
    private int _minSeen = 101, _maxSeen = -1;
    private bool _restored;

    public BrightnessTestWindow(ILogger? logger = null)
    {
        InitializeComponent();
        _ctrl = new BrightnessController(logger);
        _supported = _ctrl.TryReadCurrent(out var cur, out _);
        _original = cur;

        if (_supported)
        {
            supportedPanel.Visibility = Visibility.Visible;
            unsupportedPanel.Visibility = Visibility.Collapsed;
            _changing = true;
            slider.Value = cur;
            valueText.Text = $"{cur}%";
            _changing = false;
            _minSeen = _maxSeen = cur;
        }
        else
        {
            supportedPanel.Visibility = Visibility.Collapsed;
            unsupportedPanel.Visibility = Visibility.Visible;
        }

        Closed += (_, _) => Restore();
    }

    private void Apply(int percent)
    {
        var applied = _ctrl.Set(percent);
        if (applied >= 0)
        {
            if (applied < _minSeen) _minSeen = applied;
            if (applied > _maxSeen) _maxSeen = applied;
        }
        valueText.Text = $"{percent}%";
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_changing || !_supported) return;
        Apply((int)Math.Round(e.NewValue));
    }

    private void SetSlider(int percent)
    {
        if (!_supported) return;
        _changing = true;
        slider.Value = percent;       // não dispara Apply
        _changing = false;
        Apply(percent);
    }

    private void OnMin(object sender, RoutedEventArgs e) => SetSlider(0);
    private void OnMid(object sender, RoutedEventArgs e) => SetSlider(50);
    private void OnMax(object sender, RoutedEventArgs e) => SetSlider(100);

    private void Restore()
    {
        if (_restored) return;
        _restored = true;
        if (_supported && _original >= 0) _ctrl.Set(_original);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Restore();
        Approve(_supported
            ? $"Brilho responde (testado de {_minSeen}% a {_maxSeen}%) — aprovado pelo técnico"
            : "Aprovado pelo técnico (ajuste manual via Fn)");
    }

    private void OnFailed(object sender, RoutedEventArgs e)
    {
        Restore();
        Reject(_supported
            ? "Brilho não responde corretamente — marcado como falho"
            : "Marcado como falho pelo técnico");
    }
}
