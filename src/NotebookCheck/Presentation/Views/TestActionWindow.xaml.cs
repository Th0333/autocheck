using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Modal ÚNICO de teste: roda o teste DENTRO dele (painel inline para áudio,
/// microfone e brilho) ou abre o teste em tela cheia a partir de um botão
/// (câmera, pixels, teclado, touchpad...). Tudo num lugar só: rodar, ver o
/// resultado, trocar o resultado (dropdown), comentar e salvar/cancelar.
/// </summary>
public partial class TestActionWindow : Window
{
    public enum Action { Cancel, Save }

    public Action Result { get; private set; } = Action.Cancel;
    public string ChosenStatus { get; private set; } = "Não testado";
    public string Comment { get; private set; } = "";
    /// <summary>Detalhe final mostrado (atualizado pelo runner/inline).</summary>
    public string FinalDetails { get; private set; } = "";
    /// <summary>Áudio capturado, quando o teste é o microfone.</summary>
    public byte[]? CapturedWav { get; private set; }

    private readonly UserControl? _inline;
    private readonly Func<Window, Task<(string? statusDisplay, string details)>>? _runner;

    /// <param name="label">Nome do teste.</param>
    /// <param name="currentStatus">Status atual (texto) para pré-seleção.</param>
    /// <param name="details">Detalhes do resultado atual.</param>
    /// <param name="comment">Comentário atual.</param>
    /// <param name="hasResult">Se já há resultado (muda o rótulo do botão de rodar).</param>
    /// <param name="inline">Painel de teste embutido (ou null).</param>
    /// <param name="runner">Função que roda o teste em tela cheia e devolve (status, detalhe).</param>
    /// <param name="runLabel">Rótulo do botão de rodar (testes em tela cheia).</param>
    public TestActionWindow(
        string label, string currentStatus, string details, string comment, bool hasResult,
        UserControl? inline,
        Func<Window, Task<(string?, string)>>? runner,
        string runLabel)
    {
        InitializeComponent();
        titleText.Text = label;
        FinalDetails = details ?? "";
        detailsText.Text = string.IsNullOrWhiteSpace(details) ? "Ainda não testado." : details;
        commentBox.Text = comment ?? "";
        SelectStatus(currentStatus);

        _inline = inline;
        _runner = runner;

        if (inline is not null)
        {
            testHost.Content = inline;
        }
        if (runner is not null)
        {
            runArea.Visibility = Visibility.Visible;
            runBtn.Content = hasResult ? "↻ Refazer do zero" : "▶ " + runLabel;
        }

        Closed += (_, _) => StopInline();
    }

    private void StopInline()
    {
        if (_inline is ITestInlineControl t) t.StopTest();
        if (_inline is MicTestControl m) CapturedWav = m.Wav;
    }

    private void SelectStatus(string status)
    {
        foreach (var obj in statusBox.Items)
        {
            if (obj is ComboBoxItem item && (string)item.Content == status)
            {
                statusBox.SelectedItem = item;
                return;
            }
        }
        statusBox.SelectedIndex = 3; // "Não testado"
    }

    /// <summary>
    /// Espelha o status escolhido no <c>Tag</c>: é dele que o template do
    /// <c>StatusCombo</c> tira a cor da bolinha. Só aparência — o texto do item
    /// continua sendo a fonte de verdade do resultado.
    /// </summary>
    private void OnStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        statusBox.Tag = (statusBox.SelectedItem as ComboBoxItem)?.Content as string ?? "";
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_runner is null) return;
        var prev = runBtn.Content;
        runBtn.IsEnabled = false;
        runBtn.Content = "Rodando...";
        try
        {
            var (status, det) = await _runner(this);
            if (!string.IsNullOrEmpty(status)) SelectStatus(status);
            if (!string.IsNullOrWhiteSpace(det)) { detailsText.Text = det; FinalDetails = det; }
        }
        catch (Exception ex)
        {
            detailsText.Text = "Erro: " + ex.Message;
        }
        finally
        {
            runBtn.IsEnabled = true;
            runBtn.Content = "↻ Refazer do zero";
            _ = prev; // o rótulo passa a ser "refazer" após a primeira execução
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ChosenStatus = (statusBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Não testado";
        Comment = (commentBox.Text ?? "").Trim();
        // Resumo do painel inline entra no detalhe.
        if (_inline is ITestInlineControl t)
        {
            var s = t.Summary;
            if (!string.IsNullOrWhiteSpace(s)) FinalDetails = s;
        }
        Result = Action.Save;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = Action.Cancel;
        DialogResult = false;
        Close();
    }
}
