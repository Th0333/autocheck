using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace NotebookCheck.Presentation.Views;

/// <summary>Painel embutido do teste de áudio estéreo: toca a sequência L/R/ambos.</summary>
public partial class StereoTestControl : UserControl, ITestInlineControl
{
    /// <summary>Ação de reprodução (injetada pelo VM: <c>_engine.RunStereoAsync</c>).</summary>
    public Func<Task>? PlayAction { get; set; }

    public StereoTestControl()
    {
        InitializeComponent();
    }

    public string Summary => "Sequência estéreo (esquerdo, direito e ambos) reproduzida";

    public void StopTest() { /* nada a parar */ }

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (PlayAction is null) return;
        playBtn.IsEnabled = false;
        statusLine.Text = "Tocando esquerdo → direito → ambos...";
        try
        {
            await PlayAction();
            statusLine.Text = "Reprodução concluída. Confirme o resultado abaixo.";
        }
        catch (Exception ex)
        {
            statusLine.Text = "Erro ao reproduzir: " + ex.Message;
        }
        finally
        {
            playBtn.IsEnabled = true;
        }
    }
}
