using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Janela de teste manual de teclado. Renderiza um layout de teclado de
/// notebook ABNT2-like e pinta cada tecla de verde quando pressionada.
/// O técnico pode marcar como concluído (com X de Y teclas detectadas) ou
/// como falho.
/// </summary>
public partial class KeyboardTestWindow : TestResultWindow
{
    public int PressedCount => _pressed.Count;
    public int TotalKeys => _layout.Count;

    /// <summary>Todas as teclas do layout (ids), na ordem do teclado.</summary>
    public IReadOnlyList<string> AllKeys =>
        _layout.ConvertAll(k => k.Id);

    /// <summary>Teclas que NÃO foram pressionadas — candidatas a defeito.</summary>
    public IReadOnlyList<string> UnpressedKeys
    {
        get
        {
            var list = new List<string>();
            foreach (var k in _layout) if (!_pressed.Contains(k.Id)) list.Add(k.Id);
            return list;
        }
    }

    /// <summary>Teclas marcadas como defeituosas (clicadas) pelo técnico.</summary>
    public IReadOnlyList<string> FailedKeys => new List<string>(_failed);

    private readonly Dictionary<string, Border> _keyButtons = new();
    private readonly HashSet<string> _pressed = new();
    private readonly HashSet<string> _failed = new();
    private List<KeyDef> _layout = new();   // principal + numpad (para contagem)
    private List<KeyDef> _main = new();
    private List<KeyDef> _numpad = new();   // vazio quando o checkbox está desmarcado
    private bool _ready;                    // evita rebuild durante InitializeComponent

    private static readonly System.Windows.Media.Color PressedBg = (System.Windows.Media.Color)ColorConverter.ConvertFromString("#DCFCE7");
    private static readonly System.Windows.Media.Color PressedBorder = (System.Windows.Media.Color)ColorConverter.ConvertFromString("#15803D");
    private static readonly System.Windows.Media.Color FailedBg = (System.Windows.Media.Color)ColorConverter.ConvertFromString("#FEE2E2");
    private static readonly System.Windows.Media.Color FailedBorder = (System.Windows.Media.Color)ColorConverter.ConvertFromString("#DC2626");

    public enum LayoutKind { Abnt2, UsAnsi }

    public KeyboardTestWindow() : this(LayoutKind.Abnt2) { }

    public KeyboardTestWindow(LayoutKind kind)
    {
        InitializeComponent();
        layoutBox.SelectedIndex = kind == LayoutKind.UsAnsi ? 1 : 0;
        numpadCheck.IsChecked = true;
        _ready = true;
        Rebuild();
    }

    /// <summary>Troca de layout no dropdown → reconstrói o teclado mantendo as marcações.</summary>
    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e) => Rebuild();

    private void OnNumpadToggled(object sender, RoutedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        if (!_ready) return;
        var kind = layoutBox.SelectedIndex == 1 ? LayoutKind.UsAnsi : LayoutKind.Abnt2;
        _main = kind == LayoutKind.UsAnsi ? BuildUsAnsiLayout() : BuildAbnt2Layout();
        _numpad = numpadCheck.IsChecked == true ? BuildNumpadLayout() : new List<KeyDef>();
        _layout = new List<KeyDef>(_main.Count + _numpad.Count);
        _layout.AddRange(_main);
        _layout.AddRange(_numpad);

        // Remove marcações de teclas que não existem mais no layout atual.
        var valid = new HashSet<string>(_layout.ConvertAll(k => k.Id));
        _pressed.RemoveWhere(id => !valid.Contains(id));
        _failed.RemoveWhere(id => !valid.Contains(id));

        BuildLayoutUi();
        foreach (var id in _pressed) RepaintKey(id);
        foreach (var id in _failed) RepaintKey(id);
        UpdateCounter();
        UpdateFailSummary();
        root.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Trata teclas modificadoras pelo seu nome canônico
        var id = MapKeyToId(e.Key, e.SystemKey);
        if (id is null) return;
        if (_pressed.Add(id))
        {
            RepaintKey(id);
            UpdateCounter();
        }
        e.Handled = true;
    }

    /// <summary>Clique numa tecla → alterna a marcação de "defeituosa" (vermelho).</summary>
    private void OnKeyClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string id) return;
        if (!_failed.Add(id)) _failed.Remove(id);
        RepaintKey(id);
        UpdateFailSummary();
        // Mantém o foco no host para continuar capturando teclas.
        root.Focus();
        e.Handled = true;
    }

    /// <summary>Pinta a tecla conforme prioridade: defeito (vermelho) > pressionada (verde) > neutra.</summary>
    private void RepaintKey(string id)
    {
        if (!_keyButtons.TryGetValue(id, out var border)) return;
        if (_failed.Contains(id))
        {
            border.Background = new SolidColorBrush(FailedBg);
            border.BorderBrush = new SolidColorBrush(FailedBorder);
        }
        else if (_pressed.Contains(id))
        {
            border.Background = new SolidColorBrush(PressedBg);
            border.BorderBrush = new SolidColorBrush(PressedBorder);
        }
        else
        {
            border.Background = Brushes.White;
            border.BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Ink200Brush");
        }
    }

    private void UpdateFailSummary()
    {
        failSummary.Text = _failed.Count == 0
            ? ""
            : $"Teclas marcadas com defeito: {string.Join(", ", _failed)}";
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _pressed.Clear();
        _failed.Clear();
        foreach (var (_, b) in _keyButtons)
        {
            b.Background = Brushes.White;
            b.BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Ink200Brush");
        }
        UpdateCounter();
        UpdateFailSummary();
        root.Focus();
    }

    private void OnComplete(object sender, RoutedEventArgs e)
    {
        // Teclas marcadas em vermelho ⇒ Falha, mesmo no botão "Concluir".
        if (_failed.Count > 0)
        {
            Reject(BuildFailMessage());
        }
        else
        {
            Approve($"{_pressed.Count}/{_layout.Count} teclas detectadas");
        }
    }

    private void OnFailed(object sender, RoutedEventArgs e)
    {
        Reject(BuildFailMessage());
    }

    private string BuildFailMessage()
    {
        if (_failed.Count > 0)
            return $"Tecla(s) com defeito: {string.Join(", ", _failed)}";
        return "Marcado como falho pelo técnico";
    }

    private void UpdateCounter()
    {
        counterText.Text = _pressed.Count.ToString();
        totalText.Text = _layout.Count.ToString();
    }

    private void BuildLayoutUi()
    {
        _keyButtons.Clear();

        // Teclado principal + bloco numpad lado a lado (numpad alinhado embaixo,
        // acompanhando as fileiras de números/letras).
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(BuildStack(_main));
        if (_numpad.Count > 0)
        {
            var np = BuildStack(_numpad);
            np.Margin = new Thickness(18, 0, 0, 0);
            np.VerticalAlignment = VerticalAlignment.Bottom;
            panel.Children.Add(np);
        }

        layoutHost.Children.Clear();
        layoutHost.Children.Add(panel);
    }

    /// <summary>Monta a pilha de fileiras de um bloco de teclas (principal ou numpad).</summary>
    private StackPanel BuildStack(List<KeyDef> keys)
    {
        var rows = new List<List<KeyDef>>();
        var current = new List<KeyDef>();
        var currentRow = -1;
        foreach (var k in keys)
        {
            if (k.Row != currentRow)
            {
                currentRow = k.Row;
                if (current.Count > 0) rows.Add(current);
                current = new List<KeyDef>();
            }
            current.Add(k);
        }
        if (current.Count > 0) rows.Add(current);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var row in rows)
        {
            var rowStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
            };
            foreach (var k in row)
            {
                var border = new Border
                {
                    Width = k.Width,
                    Height = 44,
                    CornerRadius = new CornerRadius(6),
                    Background = Brushes.White,
                    BorderBrush = (Brush)System.Windows.Application.Current.FindResource("Ink200Brush"),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(3),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = k.Id,
                    Child = new TextBlock
                    {
                        Text = k.Label,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        FontSize = k.FontSize,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)System.Windows.Application.Current.FindResource("Ink800Brush"),
                    },
                };
                border.MouseLeftButtonDown += OnKeyClicked;
                _keyButtons[k.Id] = border;
                rowStack.Children.Add(border);
            }
            stack.Children.Add(rowStack);
        }
        return stack;
    }

    /// <summary>
    /// Bloco do teclado numérico (numpad). Os ids "Num*" são mapeados em
    /// <see cref="MapKeyToId"/>; o Num Lock precisa estar LIGADO para as teclas
    /// numéricas serem reportadas como NumPad0–9.
    /// </summary>
    private static List<KeyDef> BuildNumpadLayout()
    {
        const double w = 44;
        return new List<KeyDef>
        {
            new("NumLk", "Nlk", 0, w, 11), new("Num/", "/", 0, w), new("Num*", "*", 0, w), new("Num-", "-", 0, w),
            new("Num7", "7", 1, w), new("Num8", "8", 1, w), new("Num9", "9", 1, w), new("Num+", "+", 1, w),
            new("Num4", "4", 2, w), new("Num5", "5", 2, w), new("Num6", "6", 2, w),
            new("Num1", "1", 3, w), new("Num2", "2", 3, w), new("Num3", "3", 3, w),
            new("Num0", "0", 4, w * 2.2, 12), new("Num.", ".", 4, w),
        };
    }

    /// <summary>
    /// Mapeia <see cref="Key"/> para o identificador usado em <see cref="KeyDef"/>.
    /// Retorna null para teclas fora do layout.
    /// </summary>
    private static string? MapKeyToId(Key key, Key systemKey)
    {
        // SystemKey é usado quando Alt está segurando — Key vira System.
        var k = key == Key.System ? systemKey : key;
        return k switch
        {
            Key.Escape => "Esc",
            Key.F1 => "F1", Key.F2 => "F2", Key.F3 => "F3", Key.F4 => "F4",
            Key.F5 => "F5", Key.F6 => "F6", Key.F7 => "F7", Key.F8 => "F8",
            Key.F9 => "F9", Key.F10 => "F10", Key.F11 => "F11", Key.F12 => "F12",
            Key.OemTilde or Key.Oem8 => "`",
            Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4", Key.D5 => "5",
            Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9", Key.D0 => "0",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.Back => "Backspace",
            Key.Tab => "Tab",
            Key.Q => "Q", Key.W => "W", Key.E => "E", Key.R => "R", Key.T => "T",
            Key.Y => "Y", Key.U => "U", Key.I => "I", Key.O => "O", Key.P => "P",
            Key.OemOpenBrackets => "[",
            Key.Oem6 => "]",
            Key.Oem5 or Key.OemBackslash => "\\",
            Key.CapsLock => "Caps",
            Key.A => "A", Key.S => "S", Key.D => "D", Key.F => "F", Key.G => "G",
            Key.H => "H", Key.J => "J", Key.K => "K", Key.L => "L",
            Key.OemSemicolon or Key.Oem1 => "Ç",
            Key.OemQuotes => "´",
            Key.Enter => "Enter",
            Key.LeftShift => "ShiftL",
            Key.RightShift => "ShiftR",
            Key.Z => "Z", Key.X => "X", Key.C => "C", Key.V => "V", Key.B => "B",
            Key.N => "N", Key.M => "M",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion or Key.Oem2 => "/",
            Key.LeftCtrl => "CtrlL",
            Key.RightCtrl => "CtrlR",
            Key.LWin => "Win",
            Key.LeftAlt => "AltL",
            Key.RightAlt => "AltR",
            Key.Space => "Space",
            Key.Apps => "Menu",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            // Numpad (com Num Lock ligado)
            Key.NumLock => "NumLk",
            Key.Divide => "Num/",
            Key.Multiply => "Num*",
            Key.Subtract => "Num-",
            Key.Add => "Num+",
            Key.Decimal => "Num.",
            Key.NumPad0 => "Num0", Key.NumPad1 => "Num1", Key.NumPad2 => "Num2",
            Key.NumPad3 => "Num3", Key.NumPad4 => "Num4", Key.NumPad5 => "Num5",
            Key.NumPad6 => "Num6", Key.NumPad7 => "Num7", Key.NumPad8 => "Num8",
            Key.NumPad9 => "Num9",
            _ => null,
        };
    }

    /// <summary>
    /// Layout simplificado de teclado de notebook ABNT2 com 75 teclas.
    /// </summary>
    private static List<KeyDef> BuildAbnt2Layout()
    {
        const double w = 44; // largura padrão
        return new List<KeyDef>
        {
            // Linha 0 — Escape + Funções
            new("Esc",   "Esc",  0, w),
            new("F1",    "F1",   0, w),  new("F2", "F2", 0, w),  new("F3", "F3", 0, w),  new("F4", "F4", 0, w),
            new("F5",    "F5",   0, w),  new("F6", "F6", 0, w),  new("F7", "F7", 0, w),  new("F8", "F8", 0, w),
            new("F9",    "F9",   0, w),  new("F10","F10",0, w),  new("F11","F11",0, w),  new("F12","F12",0, w),

            // Linha 1 — Números
            new("`","`", 1, w),
            new("1","1", 1, w), new("2","2", 1, w), new("3","3", 1, w), new("4","4", 1, w),
            new("5","5", 1, w), new("6","6", 1, w), new("7","7", 1, w), new("8","8", 1, w),
            new("9","9", 1, w), new("0","0", 1, w),
            new("-","-", 1, w), new("=","=", 1, w),
            new("Backspace","⌫ Backspace", 1, w * 2.2, 12),

            // Linha 2 — QWERTY
            new("Tab","Tab", 2, w * 1.5, 12),
            new("Q","Q", 2, w), new("W","W", 2, w), new("E","E", 2, w), new("R","R", 2, w), new("T","T", 2, w),
            new("Y","Y", 2, w), new("U","U", 2, w), new("I","I", 2, w), new("O","O", 2, w), new("P","P", 2, w),
            new("´","´", 2, w), new("[","[", 2, w),
            new("Enter","↵ Enter", 2, w * 1.7, 12),

            // Linha 3 — ASDF
            new("Caps","Caps", 3, w * 1.7, 11),
            new("A","A", 3, w), new("S","S", 3, w), new("D","D", 3, w), new("F","F", 3, w), new("G","G", 3, w),
            new("H","H", 3, w), new("J","J", 3, w), new("K","K", 3, w), new("L","L", 3, w),
            new("Ç","Ç", 3, w),
            new("\\","\\", 3, w),
            // segunda Enter linha (compactada na nossa visualização)

            // Linha 4 — ZXCV
            new("ShiftL","⇧ Shift", 4, w * 2.0, 12),
            new("Z","Z", 4, w), new("X","X", 4, w), new("C","C", 4, w), new("V","V", 4, w), new("B","B", 4, w),
            new("N","N", 4, w), new("M","M", 4, w),
            new(",",",", 4, w), new(".",".", 4, w), new("/","/", 4, w),
            new("ShiftR","⇧ Shift", 4, w * 2.4, 12),

            // Linha 5 — modificadoras + space + setas
            new("CtrlL","Ctrl", 5, w * 1.4, 12),
            new("Win","⊞", 5, w),
            new("AltL","Alt", 5, w),
            new("Space","Espaço", 5, w * 5.5, 12),
            new("AltR","AltGr", 5, w, 11),
            new("Menu","☰", 5, w),
            new("CtrlR","Ctrl", 5, w * 1.4, 12),
            new("Left","◀", 5, w),
            new("Up","▲", 5, w),
            new("Down","▼", 5, w),
            new("Right","▶", 5, w),
        };
    }

    private sealed record KeyDef(string Id, string Label, int Row, double Width, double FontSize = 13);
}


// Layout US ANSI estendido — exposto via partial helper para manter o arquivo navegável.
public partial class KeyboardTestWindow
{
    private static List<KeyDef> BuildUsAnsiLayout()
    {
        const double w = 44;
        return new List<KeyDef>
        {
            // Linha 0
            new("Esc","Esc", 0, w),
            new("F1","F1", 0, w), new("F2","F2", 0, w), new("F3","F3", 0, w), new("F4","F4", 0, w),
            new("F5","F5", 0, w), new("F6","F6", 0, w), new("F7","F7", 0, w), new("F8","F8", 0, w),
            new("F9","F9", 0, w), new("F10","F10", 0, w), new("F11","F11", 0, w), new("F12","F12", 0, w),

            // Linha 1
            new("`","`", 1, w),
            new("1","1", 1, w), new("2","2", 1, w), new("3","3", 1, w), new("4","4", 1, w),
            new("5","5", 1, w), new("6","6", 1, w), new("7","7", 1, w), new("8","8", 1, w),
            new("9","9", 1, w), new("0","0", 1, w),
            new("-","-", 1, w), new("=","=", 1, w),
            new("Backspace","⌫ Backspace", 1, w * 2.0, 12),

            // Linha 2 (QWERTY)
            new("Tab","Tab", 2, w * 1.5, 12),
            new("Q","Q", 2, w), new("W","W", 2, w), new("E","E", 2, w), new("R","R", 2, w), new("T","T", 2, w),
            new("Y","Y", 2, w), new("U","U", 2, w), new("I","I", 2, w), new("O","O", 2, w), new("P","P", 2, w),
            new("[","[", 2, w), new("]","]", 2, w),
            new("\\","\\", 2, w * 1.5, 12),

            // Linha 3 (ASDF) — ANSI tem Enter horizontal e sem Ç
            new("Caps","Caps", 3, w * 1.7, 11),
            new("A","A", 3, w), new("S","S", 3, w), new("D","D", 3, w), new("F","F", 3, w), new("G","G", 3, w),
            new("H","H", 3, w), new("J","J", 3, w), new("K","K", 3, w), new("L","L", 3, w),
            new("Ç",";", 3, w),  // mesmo Id "Ç" pra reuso do mapeamento
            new("´","'", 3, w),
            new("Enter","↵ Enter", 3, w * 2.0, 12),

            // Linha 4 (ZXCV)
            new("ShiftL","⇧ Shift", 4, w * 2.2, 12),
            new("Z","Z", 4, w), new("X","X", 4, w), new("C","C", 4, w), new("V","V", 4, w), new("B","B", 4, w),
            new("N","N", 4, w), new("M","M", 4, w),
            new(",",",", 4, w), new(".",".", 4, w), new("/","/", 4, w),
            new("ShiftR","⇧ Shift", 4, w * 2.6, 12),

            // Linha 5
            new("CtrlL","Ctrl", 5, w * 1.4, 12),
            new("Win","⊞", 5, w),
            new("AltL","Alt", 5, w),
            new("Space","Space", 5, w * 6.0, 12),
            new("AltR","Alt", 5, w, 11),
            new("Menu","☰", 5, w),
            new("CtrlR","Ctrl", 5, w * 1.4, 12),
            new("Left","◀", 5, w),
            new("Up","▲", 5, w),
            new("Down","▼", 5, w),
            new("Right","▶", 5, w),
        };
    }
}
