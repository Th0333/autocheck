using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Pergunta quantas horas de humanização rodar (1–48; padrão 12, lembrando a
/// última escolha da sessão). <see cref="Hours"/> fica null quando cancelado.
/// </summary>
public partial class HumanizationDialog : Window
{
    private const int MinHours = 1;
    private const int MaxHours = 99;

    /// <summary>Última escolha do técnico nesta sessão do app.</summary>
    private static int _lastHours = 12;

    /// <summary>Horas escolhidas; null se o técnico cancelou.</summary>
    public int? Hours { get; private set; }

    public HumanizationDialog()
    {
        InitializeComponent();
        HoursBox.Text = _lastHours.ToString();
        Loaded += (_, _) => { HoursBox.Focus(); HoursBox.SelectAll(); };
    }

    private int? ParseHours()
        => int.TryParse(HoursBox.Text.Trim(), out var h) && h >= MinHours && h <= MaxHours ? h : null;

    private void Nudge(int delta)
    {
        var current = int.TryParse(HoursBox.Text.Trim(), out var h) ? h : _lastHours;
        HoursBox.Text = System.Math.Clamp(current + delta, MinHours, MaxHours).ToString();
    }

    private void OnMinus(object sender, RoutedEventArgs e) => Nudge(-1);
    private void OnPlus(object sender, RoutedEventArgs e) => Nudge(+1);

    /// <summary>Aceita apenas dígitos no campo de horas.</summary>
    private void OnHoursInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string t) HoursBox.Text = t;
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        var h = ParseHours();
        if (h is null)
        {
            hintText.Text = $"Valor inválido — informe um número inteiro entre {MinHours} e {MaxHours} horas.";
            if (TryFindResource("DangerFgBrush") is Brush danger) hintText.Foreground = danger;
            HoursBox.Focus();
            HoursBox.SelectAll();
            return;
        }
        Hours = h;
        _lastHours = h.Value;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
