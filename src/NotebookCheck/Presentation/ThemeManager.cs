using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using NotebookCheck.Bootstrap;

namespace NotebookCheck.Presentation;

/// <summary>
/// Alterna o app entre tema claro e escuro em tempo de execução.
///
/// Estratégia: os estilos inteiros (Theme.xaml e janelas) referenciam os
/// brushes compartilhados via StaticResource. Como todos apontam para a MESMA
/// instância de <see cref="SolidColorBrush"/>, mutar a <c>Color</c> de cada
/// brush repinta a interface inteira na hora — sem DynamicResource, sem
/// recarregar janelas. Brushes fora do mapa (Brand, Ok/Warn/Bad,
/// DarkPanelBrush) são fixos e valem para os dois temas.
///
/// A escolha persiste em <c>theme.txt</c> ao lado do .exe (com fallback para
/// %LOCALAPPDATA%\Notelet quando o pendrive está somente-leitura).
/// </summary>
public static class ThemeManager
{
    private const string FileName = "theme.txt";

    /// <summary>Mapa chave do brush → (cor no claro, cor no escuro).</summary>
    private static readonly Dictionary<string, (string Light, string Dark)> Map = new()
    {
        // Escala neutra (texto usa tons altos, fundos/bordas usam tons baixos;
        // no escuro a rampa inverte).
        ["Ink50Brush"] = ("#F7F8FA", "#141824"),
        ["Ink100Brush"] = ("#EEF0F4", "#232A3C"),
        ["Ink200Brush"] = ("#DDE1EA", "#303852"),
        ["Ink300Brush"] = ("#C3C9D6", "#414A66"),
        ["Ink400Brush"] = ("#9099AD", "#7C87A3"),
        ["Ink500Brush"] = ("#5F6779", "#98A2BC"),
        ["Ink600Brush"] = ("#444B5C", "#B3BCD2"),
        ["Ink700Brush"] = ("#2F3543", "#CAD2E5"),
        ["Ink800Brush"] = ("#1C2030", "#E1E6F2"),
        ["Ink900Brush"] = ("#0F1320", "#F2F5FC"),

        // Superfície de cards/inputs.
        ["SurfaceBrush"] = ("#FFFFFF", "#1E2433"),

        // Semânticos de erro/aviso/ok.
        ["DangerFgBrush"] = ("#C62828", "#F87171"),
        ["DangerBgBrush"] = ("#FDECEC", "#351B1E"),
        ["DangerBorderBrush"] = ("#E53935", "#C24B45"),
        ["DangerTintBorderBrush"] = ("#FECACA", "#5C2A2C"),
        ["WarnTintBrush"] = ("#FFFBEB", "#33290F"),
        ["OkTintBrush"] = ("#ECFDF5", "#10291C"),

        // Chips de status.
        ["StatusOkBgBrush"] = ("#DCFCE7", "#173222"),
        ["StatusWarnBgBrush"] = ("#FEF3C7", "#362B10"),
        ["StatusBadBgBrush"] = ("#FEE2E2", "#391D20"),
        ["StatusMuteBgBrush"] = ("#EEF0F4", "#262D3F"),
        ["StatusOkFgBrush"] = ("#15803D", "#4ADE80"),
        ["StatusWarnFgBrush"] = ("#B45309", "#FBBF24"),
        ["StatusBadFgBrush"] = ("#B91C1C", "#F87171"),
        ["StatusMuteFgBrush"] = ("#444B5C", "#B6BFD3"),

        // Blocos de portas.
        ["PortActiveBgBrush"] = ("#DCFCE7", "#173222"),
        ["PortInactiveBgBrush"] = ("#F3F4F6", "#202638"),
        ["PortActiveBorderBrush"] = ("#15803D", "#2E9E5B"),
        ["PortInactiveBorderBrush"] = ("#D1D5DB", "#3A4258"),
        ["PortActiveLabelBrush"] = ("#15803D", "#4ADE80"),
        ["PortInactiveLabelBrush"] = ("#6B7280", "#97A0B5"),
    };

    /// <summary>True quando o tema escuro está ativo.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>
    /// Disparado após aplicar um tema. Controles que constroem visual em
    /// código (ex.: StepRail) escutam para se reconstruir com os brushes novos.
    /// </summary>
    public static event Action? ThemeChanged;

    /// <summary>Carrega a preferência salva e aplica (chamar no startup).</summary>
    public static void Initialize()
    {
        var dark = false;
        try
        {
            var path = Path.Combine(AppPaths.ResolveWritable(AppPaths.ExeDir), FileName);
            if (File.Exists(path))
                dark = string.Equals(File.ReadAllText(path).Trim(), "dark", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* sem preferência legível: fica no claro */ }
        Apply(dark);
    }

    /// <summary>Alterna o tema e persiste a escolha.</summary>
    public static void Toggle() => Apply(!IsDark);

    /// <summary>Aplica o tema mutando a Color de cada brush compartilhado.</summary>
    public static void Apply(bool dark)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        foreach (var (key, colors) in Map)
        {
            var color = (Color)ColorConverter.ConvertFromString(dark ? colors.Dark : colors.Light);
            if (app.TryFindResource(key) is SolidColorBrush brush && !brush.IsFrozen)
            {
                brush.Color = color;
            }
            else
            {
                // Fallback defensivo (brush congelado ou ausente): substitui o
                // recurso. Referências StaticResource antigas não atualizam ao
                // vivo nesse caso, mas novas janelas nascem certas.
                app.Resources[key] = new SolidColorBrush(color);
            }
        }

        IsDark = dark;
        Save(dark);
        ThemeChanged?.Invoke();
    }

    private static void Save(bool dark)
    {
        try
        {
            var path = Path.Combine(AppPaths.ResolveWritable(AppPaths.ExeDir), FileName);
            File.WriteAllText(path, dark ? "dark" : "light");
        }
        catch { /* sem onde gravar: preferência vale só para a sessão */ }
    }
}
