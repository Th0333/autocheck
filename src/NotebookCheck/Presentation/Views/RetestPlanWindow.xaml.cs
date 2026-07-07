using System.Collections.Generic;
using System.Windows;
using NotebookCheck.Application.Orchestration;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Plano do reteste: mostra o que constou como problema no relatório anterior
/// e deixa o técnico escolher entre retestar SÓ as falhas (checklist especial)
/// ou refazer o checklist completo sobrescrevendo o relatório antigo.
/// </summary>
public partial class RetestPlanWindow : Window
{
    public enum PlanChoice { Cancel, OnlyFailures, FullOverwrite }

    public PlanChoice Choice { get; private set; } = PlanChoice.Cancel;

    /// <summary>Linha exibida na lista (Label amigável + status + detalhes).</summary>
    public sealed record FailureRow(string Key, string Label, string Status, string Details);

    public RetestPlanWindow(HistoricReport history, IReadOnlyList<FailureRow> failures)
    {
        InitializeComponent();

        var when = history.TestedAt == System.DateTime.MinValue
            ? "?" : history.TestedAt.ToString("dd/MM/yyyy HH:mm");
        var ntb = string.IsNullOrWhiteSpace(history.NtbCode) ? "" : $" • {history.NtbCode}";
        baseInfo.Text = $"Relatório-base: {when}{ntb} • Classificação anterior: {history.FinalClassification}. " +
                        "Escolha como retestar este equipamento.";

        failuresList.ItemsSource = failures;
        onlyFailuresBtn.Content = $"🔁 Retestar somente as falhas ({failures.Count})";
        if (failures.Count == 0)
        {
            onlyFailuresBtn.IsEnabled = false;
            noFailuresText.Visibility = Visibility.Visible;
        }
    }

    private void OnOnlyFailures(object sender, RoutedEventArgs e)
    {
        Choice = PlanChoice.OnlyFailures;
        DialogResult = true;
        Close();
    }

    private void OnFullOverwrite(object sender, RoutedEventArgs e)
    {
        Choice = PlanChoice.FullOverwrite;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = PlanChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
