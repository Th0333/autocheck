using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Janela de teste manual de touchpad. Verifica:
/// - Cobertura de movimento (grid 8×6)
/// - Clique esquerdo, clique direito, duplo clique
/// - Scroll (mouse wheel emulado pelo gesto de dois dedos)
/// </summary>
public partial class TouchpadTestWindow : TestResultWindow
{
    private const int Cols = 8;
    private const int Rows = 6;
    private readonly bool[,] _coverage = new bool[Rows, Cols];
    private readonly Rectangle[,] _cells = new Rectangle[Rows, Cols];
    public ObservableCollection<GestureRow> Gestures { get; } = new()
    {
        new GestureRow("Movimento", "Cubra toda a área com o cursor", "Pendente"),
        new GestureRow("Clique esquerdo", "Toque com 1 dedo / botão esquerdo", "Pendente"),
        new GestureRow("Clique direito", "Botão direito / toque com 2 dedos", "Pendente"),
        new GestureRow("Duplo clique", "Dois cliques rápidos no mesmo ponto", "Pendente"),
        new GestureRow("Scroll", "Deslize com 2 dedos para cima/baixo", "Pendente"),
    };

    private GestureRow? Find(string name)
    {
        foreach (var g in Gestures) if (g.Name == name) return g;
        return null;
    }

    public TouchpadTestWindow()
    {
        InitializeComponent();
        gestureList.ItemsSource = Gestures;
        BuildCoverage();
        UpdateCoverageUi();
    }

    private void BuildCoverage()
    {
        var panel = (UniformGrid)coverageGrid;
        panel.Children.Clear();
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                var rect = new Rectangle
                {
                    Margin = new Thickness(2),
                    RadiusX = 4,
                    RadiusY = 4,
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EEF0F4")),
                    IsHitTestVisible = false,
                };
                _cells[r, c] = rect;
                panel.Children.Add(rect);
            }
        }
    }

    private readonly Queue<DateTime> _moveTimes = new();
    private int _peakRateHz;

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Taxa de relatório observada: nº de eventos de movimento na última
        // janela de 1 s. O WPF/Windows coalesce mensagens (~125 Hz típico),
        // então é uma APROXIMAÇÃO, não o polling rate de hardware real.
        var now = DateTime.UtcNow;
        _moveTimes.Enqueue(now);
        while (_moveTimes.Count > 0 && (now - _moveTimes.Peek()).TotalMilliseconds > 1000)
            _moveTimes.Dequeue();
        var rate = _moveTimes.Count;
        if (rate > _peakRateHz)
        {
            _peakRateHz = rate;
            pollingText.Text = $"{_peakRateHz} Hz";
        }

        var pos = e.GetPosition(touchArea);
        var w = touchArea.ActualWidth;
        var h = touchArea.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var col = Math.Min(Cols - 1, (int)(pos.X / (w / Cols)));
        var row = Math.Min(Rows - 1, (int)(pos.Y / (h / Rows)));
        if (row < 0 || col < 0) return;

        if (!_coverage[row, col])
        {
            _coverage[row, col] = true;
            _cells[row, col].Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#86EFAC"));
            UpdateCoverageUi();
        }
    }

    private void OnLeftClick(object sender, MouseButtonEventArgs e)
    {
        SetGesture("Clique esquerdo", "OK");
        if (e.ClickCount >= 2) SetGesture("Duplo clique", "OK");
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        SetGesture("Clique direito", "OK");
    }

    private void OnScroll(object sender, MouseWheelEventArgs e)
    {
        SetGesture("Scroll", "OK");
    }

    private void SetGesture(string name, string detected)
    {
        var g = Find(name);
        if (g != null) g.Detected = detected;
    }

    private void UpdateCoverageUi()
    {
        var count = 0;
        for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
                if (_coverage[r, c]) count++;

        coverageBar.Value = count;
        coverageText.Text = $"{count}/{Rows * Cols} células ({count * 100 / (Rows * Cols)}%)";

        if (count >= (Rows * Cols * 80 / 100))
        {
            SetGesture("Movimento", "OK");
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
            {
                _coverage[r, c] = false;
                _cells[r, c].Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EEF0F4"));
            }
        foreach (var g in Gestures) g.Detected = "Pendente";
        _moveTimes.Clear();
        _peakRateHz = 0;
        pollingText.Text = "— Hz";
        UpdateCoverageUi();
    }

    private void OnComplete(object sender, RoutedEventArgs e)
    {
        var ok = 0;
        foreach (var g in Gestures) if (g.Detected == "OK") ok++;
        var message = $"{ok}/{Gestures.Count} verificações ok";
        if (_peakRateHz > 0) message += $" • taxa ~{_peakRateHz} Hz";
        Approve(message);
    }

    private void OnFailed(object sender, RoutedEventArgs e)
    {
        Reject("Marcado como falho pelo técnico");
    }
}

public sealed partial class GestureRow : ObservableObject
{
    public string Name { get; }
    public string Hint { get; }
    [ObservableProperty] private string detected;

    public GestureRow(string name, string hint, string detected)
    {
        Name = name;
        Hint = hint;
        this.detected = detected;
    }
}
