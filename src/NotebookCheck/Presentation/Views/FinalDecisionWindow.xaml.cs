using System.Windows;
using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Diálogo modal exibido logo antes de salvar e enviar o relatório. Mostra a
/// classificação calculada pelo sistema e exige que o técnico confirme ou
/// sobrescreva. Não há aprovação automática — o relatório só sai depois que
/// o técnico clica em "Confirmar e enviar".
/// </summary>
public partial class FinalDecisionWindow : Window
{
    /// <summary>Classificação escolhida pelo técnico, ou null se ele cancelou.</summary>
    public FinalClassification? ChosenClassification { get; private set; }

    /// <summary>Justificativa textual quando o técnico altera a sugestão.</summary>
    public string Reason { get; private set; } = "";

    /// <summary>True quando o técnico mudou em relação à sugestão automática.</summary>
    public bool IsOverride { get; private set; }

    private readonly FinalClassification _suggested;

    public FinalDecisionWindow(FinalClassification suggested)
    {
        InitializeComponent();
        _suggested = suggested;

        suggestedText.Text = suggested switch
        {
            FinalClassification.Aprovado => "Aprovado",
            FinalClassification.AprovadoComRessalvas => "Aprovado com ressalvas",
            FinalClassification.Reprovado => "Reprovado",
            _ => suggested.ToString(),
        };

        // Pré-seleciona a sugestão
        switch (suggested)
        {
            case FinalClassification.Aprovado: optApproved.IsChecked = true; break;
            case FinalClassification.AprovadoComRessalvas: optWarn.IsChecked = true; break;
            case FinalClassification.Reprovado: optRejected.IsChecked = true; break;
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        FinalClassification chosen;
        if (optApproved.IsChecked == true) chosen = FinalClassification.Aprovado;
        else if (optWarn.IsChecked == true) chosen = FinalClassification.AprovadoComRessalvas;
        else if (optRejected.IsChecked == true) chosen = FinalClassification.Reprovado;
        else
        {
            MessageBox.Show("Selecione uma decisão.", "Decisão final", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsOverride = chosen != _suggested;
        Reason = (reasonBox.Text ?? "").Trim();

        if (IsOverride && string.IsNullOrWhiteSpace(Reason))
        {
            var ok = MessageBox.Show(
                "Você está alterando a classificação sugerida pelo sistema. Recomendamos justificar. Continuar mesmo assim?",
                "Justificativa em branco",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes) return;
        }

        ChosenClassification = chosen;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        ChosenClassification = null;
        DialogResult = false;
        Close();
    }
}
