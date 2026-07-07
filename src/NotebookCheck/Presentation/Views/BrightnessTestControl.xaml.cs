using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using NotebookCheck.Infrastructure.Hardware;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Painel embutido do teste de brilho: slider que ajusta o brilho do painel ao
/// vivo (via <see cref="BrightnessController"/>). Restaura o brilho original ao
/// parar. Portado do antigo BrightnessTestWindow.
/// </summary>
public partial class BrightnessTestControl : UserControl, ITestInlineControl
{
    private readonly BrightnessController _ctrl;
    private readonly bool _supported;
    private readonly int _original;
    private bool _changing;
    private int _minSeen = 101, _maxSeen = -1;
    private bool _restored;

    public BrightnessTestControl(ILogger? logger = null)
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

        Unloaded += (_, _) => StopTest();
    }

    public string Summary => _supported
        ? (_maxSeen >= _minSeen ? $"Brilho testado de {_minSeen}% a {_maxSeen}%" : "Brilho verificado")
        : "Ajuste de brilho por software indisponível";

    public void StopTest()
    {
        if (_restored) return;
        _restored = true;
        if (_supported && _original >= 0) _ctrl.Set(_original);
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
        slider.Value = percent;
        _changing = false;
        Apply(percent);
    }

    private void OnMin(object sender, RoutedEventArgs e) => SetSlider(0);
    private void OnMid(object sender, RoutedEventArgs e) => SetSlider(50);
    private void OnMax(object sender, RoutedEventArgs e) => SetSlider(100);
}
