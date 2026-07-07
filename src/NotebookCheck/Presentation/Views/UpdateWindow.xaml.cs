using System.Windows;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Janela mostrada durante a verificação/instalação de atualizações, para que
/// o técnico saiba que o app está atualizando (não fica vago).
/// </summary>
public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
    }

    public void SetPhase(string phase)
    {
        Dispatcher.Invoke(() => phaseText.Text = phase);
    }

    public void SetProgress(int pct)
    {
        Dispatcher.Invoke(() =>
        {
            bar.IsIndeterminate = false;
            bar.Value = pct;
            pctText.Text = pct + "%";
        });
    }

    public void SetIndeterminate()
    {
        Dispatcher.Invoke(() =>
        {
            bar.IsIndeterminate = true;
            pctText.Text = "";
        });
    }
}
