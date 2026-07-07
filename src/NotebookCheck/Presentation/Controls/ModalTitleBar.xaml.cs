using System.Windows;
using System.Windows.Controls;

namespace NotebookCheck.Presentation.Controls;

/// <summary>
/// Barra de título customizada usada em todas as janelas modais. Substitui
/// o frame padrão do Windows. A janela hospedeira deve usar:
///   <c>WindowStyle="None"</c> e configurar <c>WindowChrome</c> com
///   <c>CaptionHeight="36"</c> e <c>UseAeroCaptionButtons="False"</c>.
/// </summary>
public partial class ModalTitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(ModalTitleBar),
            new PropertyMetadata("Notelet"));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public ModalTitleBar()
    {
        InitializeComponent();
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        var w = Window.GetWindow(this);
        if (w is not null) w.WindowState = WindowState.Minimized;
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        var w = Window.GetWindow(this);
        if (w is null) return;
        w.WindowState = w.WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        var w = Window.GetWindow(this);
        if (w is not null) w.Close();
    }
}
