using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace NotebookCheck.Presentation.Converters;

/// <summary>
/// Converte um <see cref="WizardStep"/> em <see cref="Visibility"/> comparando
/// com o nome passado como parâmetro.
/// </summary>
public sealed class StepConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is not string target) return Visibility.Collapsed;
        return string.Equals(value.ToString(), target, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

/// <summary>Converte bool (foto recebida) em check verde / círculo vazio.</summary>
public sealed class PhotoCheckConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? "✅" : "⬜";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SimNaoConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? (b ? "Sim" : "Não") : "Indeterminado";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converte uma string de status (OK / Atenção / Falha / Não testado / etc.)
/// no <see cref="Brush"/> de fundo do badge correspondente.
/// </summary>
public sealed class StatusBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value?.ToString() ?? "") switch
        {
            "OK" or "Sincronizado" => "StatusOkBgBrush",
            "Atenção" or "Observação" or "Pendente" => "StatusWarnBgBrush",
            "Falha" or "Com defeito" => "StatusBadBgBrush",
            _ => "StatusMuteBgBrush",
        };
        // Brush compartilhado do tema: o ThemeManager muta a Color, então o
        // chip acompanha o claro/escuro sem re-binding.
        return System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StatusForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value?.ToString() ?? "") switch
        {
            "OK" or "Sincronizado" => "StatusOkFgBrush",
            "Atenção" or "Observação" or "Pendente" => "StatusWarnFgBrush",
            "Falha" or "Com defeito" => "StatusBadFgBrush",
            _ => "StatusMuteFgBrush",
        };
        return System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value is bool b && b;
        if (parameter is string p && string.Equals(p, "invert", StringComparison.OrdinalIgnoreCase))
        {
            v = !v;
        }
        return v ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// True → borda vermelha (campo obrigatório não preenchido); False → transparente.
/// Usado para destacar campos pendentes na etapa de inspeção.
/// </summary>
public sealed class BoolToErrorBrushConverter : IValueConverter
{
    private static readonly System.Windows.Media.Brush None =
        System.Windows.Media.Brushes.Transparent;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b)
            ? System.Windows.Application.Current?.TryFindResource("DangerBorderBrush") as Brush ?? Brushes.Red
            : None;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var n = 0;
        if (value is int i) n = i;
        else if (value != null && int.TryParse(value.ToString(), out var parsed)) n = parsed;
        return n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converte um <see cref="WizardStep"/> no índice numérico para destacar a
/// etapa atual no rail lateral.
/// </summary>
public sealed class StepIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is not string targetName) return Brushes.Transparent;
        var current = value?.ToString() ?? "";
        return string.Equals(current, targetName, StringComparison.OrdinalIgnoreCase)
            ? System.Windows.Application.Current?.FindResource("BrandBrush") ?? Brushes.SteelBlue
            : System.Windows.Application.Current?.FindResource("Ink200Brush") ?? Brushes.Gainsboro;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}


/// <summary>
/// Converte um <see cref="WizardStep"/> no título amigável da etapa para o
/// cabeçalho da janela.
/// </summary>
public sealed class StepTitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value?.ToString() ?? "") switch
        {
            "Start" => "Bem-vindo",
            "ModeSelect" => "Modo do checklist",
            "Identification" => "Identificação do equipamento",
            "Hardware" => "Coleta de hardware",
            "AutoTests" => "Testes automáticos",
            "Inputs" => "Inputs e portas",
            "Manual" => "Inspeção física",
            "Summary" => "Resumo",
            "Done" => "Concluído",
            _ => "",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}


/// <summary>
/// True → cor verde, false → cinza claro. Usado no fundo dos blocos de portas.
/// </summary>
public sealed class PortBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Application.Current?.TryFindResource(
               value is bool b && b ? "PortActiveBgBrush" : "PortInactiveBgBrush") as Brush
           ?? Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PortBorderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Application.Current?.TryFindResource(
               value is bool b && b ? "PortActiveBorderBrush" : "PortInactiveBorderBrush") as Brush
           ?? Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PortLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Application.Current?.TryFindResource(
               value is bool b && b ? "PortActiveLabelBrush" : "PortInactiveLabelBrush") as Brush
           ?? Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Calcula a largura preenchida de uma barra de progresso: recebe [0]=percentual
/// (0–100) e [1]=largura total disponível, e devolve a largura proporcional.
/// Usado na barra de carga da bateria.
/// </summary>
public sealed class PercentWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return 0d;
        double pct = ToDouble(values[0]);
        double total = ToDouble(values[1]);
        if (double.IsNaN(total) || total <= 0) return 0d;
        pct = Math.Clamp(pct, 0d, 100d);
        return total * pct / 100d;
    }

    private static double ToDouble(object? v)
    {
        return v switch
        {
            double d => d,
            int i => i,
            float f => f,
            _ => v != null && double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0d,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
