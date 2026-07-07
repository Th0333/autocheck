using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Janela em tela cheia que cicla por cores sólidas para o técnico identificar
/// pixels mortos/presos. Inicia em preto, segue para branco, vermelho, verde,
/// azul, magenta, ciano e amarelo. Setas avançam/retrocedem; ESC fecha.
/// </summary>
public partial class PixelTestWindow : Window
{
    private enum PatternKind { Solid, Gradient, Checkerboard, Text }

    private static readonly (string Name, PatternKind Kind, Color Color)[] Patterns =
    {
        ("Preto (luz de fundo / pixels acesos)", PatternKind.Solid, System.Windows.Media.Colors.Black),
        ("Branco (uniformidade / poeira)", PatternKind.Solid, System.Windows.Media.Colors.White),
        ("Cinza 50% (uniformidade)", PatternKind.Solid, Color.FromRgb(128, 128, 128)),
        ("Vermelho", PatternKind.Solid, System.Windows.Media.Colors.Red),
        ("Verde", PatternKind.Solid, System.Windows.Media.Colors.Lime),
        ("Azul", PatternKind.Solid, System.Windows.Media.Colors.Blue),
        ("Gradiente (banding)", PatternKind.Gradient, System.Windows.Media.Colors.Black),
        ("Xadrez fino (nitidez)", PatternKind.Checkerboard, System.Windows.Media.Colors.Black),
        ("Texto (nitidez / foco)", PatternKind.Text, System.Windows.Media.Colors.White),
    };

    private int _index;

    /// <summary>Quantos padrões o técnico viu antes de fechar a janela.</summary>
    public int CompletedColors { get; private set; }

    /// <summary>Total de padrões disponíveis no teste.</summary>
    public int TotalPatterns => Patterns.Length;

    public PixelTestWindow()
    {
        InitializeComponent();
        textOverlay.Text = BuildSharpnessText();
        UpdateColor();
        KeyDown += OnKey;
        Focus();
    }

    private static string BuildSharpnessText()
    {
        // Texto denso para avaliar nitidez/foco e convergência de subpixels.
        const string line = "O rato roeu a roupa do rei de Roma — 0123456789 — ABCDEFGHIJKLMNOPQRSTUVWXYZ — abcdefghijklmnopqrstuvwxyz — .,;:!?@#$%&*()[]{}/\\|";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 60; i++) sb.AppendLine(line);
        return sb.ToString();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right:
            case Key.Space:
                Next();
                break;
            case Key.Left:
                Previous();
                break;
            case Key.Escape:
                Close();
                break;
        }
    }

    private void Next()
    {
        _index = (_index + 1) % Patterns.Length;
        if (_index + 1 > CompletedColors) CompletedColors = _index + 1;
        UpdateColor();
    }

    private void Previous()
    {
        _index = (_index - 1 + Patterns.Length) % Patterns.Length;
        UpdateColor();
    }

    private void UpdateColor()
    {
        var (name, kind, color) = Patterns[_index];

        textOverlay.Visibility = kind == PatternKind.Text ? Visibility.Visible : Visibility.Collapsed;

        switch (kind)
        {
            case PatternKind.Gradient:
                colorRect.Fill = new LinearGradientBrush(
                    System.Windows.Media.Colors.Black, System.Windows.Media.Colors.White, 0);
                break;
            case PatternKind.Checkerboard:
                colorRect.Fill = BuildCheckerboard();
                break;
            case PatternKind.Text:
                colorRect.Fill = new SolidColorBrush(System.Windows.Media.Colors.White);
                break;
            default:
                colorRect.Fill = new SolidColorBrush(color);
                break;
        }

        // Texto da dica em cor contrastante (no padrão de texto, sempre escuro).
        var contrast = kind == PatternKind.Text || (color.R + color.G + color.B) > 380
            ? System.Windows.Media.Colors.Black
            : System.Windows.Media.Colors.White;
        hint.Foreground = new SolidColorBrush(contrast);
        status.Text = $"{_index + 1}/{Patterns.Length} — {name}";
    }

    /// <summary>Xadrez fino 2×2 px (preto/branco) para avaliar nitidez/foco.</summary>
    private static DrawingBrush BuildCheckerboard()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(System.Windows.Media.Colors.White),
            null, new RectangleGeometry(new Rect(0, 0, 2, 2))));
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(System.Windows.Media.Colors.Black), null,
            new GeometryGroup
            {
                Children =
                {
                    new RectangleGeometry(new Rect(0, 0, 1, 1)),
                    new RectangleGeometry(new Rect(1, 1, 1, 1)),
                },
            }));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 2, 2),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
    }
}
