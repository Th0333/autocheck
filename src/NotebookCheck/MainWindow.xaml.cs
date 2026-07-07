using System.Windows;
using System.Windows.Controls;
using NotebookCheck.Presentation;

namespace NotebookCheck;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateMaximizeIcon();
    }

    /// <summary>
    /// Quando o técnico escolhe um status diferente no ComboBox da grade de
    /// testes, propaga a mudança para o ViewModel que persiste o override
    /// na sessão.
    /// </summary>
    private void OnStatusOverrideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is FrameworkElement fe
            && fe.DataContext is TestResultRow row
            && DataContext is MainViewModel vm)
        {
            vm.OverrideTestStatusCommand.Execute(row);
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateMaximizeIcon()
    {
        // Padding extra evita que a janela maximizada fique colada na borda
        // do monitor (efeito do WindowChrome em alguns Windows).
        if (WindowState == WindowState.Maximized)
        {
            BorderThickness = new Thickness(7);
        }
        else
        {
            BorderThickness = new Thickness(0);
        }
    }
}
