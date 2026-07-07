using System.Collections.Generic;
using System.Windows;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Janela de seleção do relatório anterior usado como base do reteste.
/// </summary>
public partial class RetestSelectWindow : Window
{
    /// <summary>test_id escolhido. null = usar o mais recente (automático).</summary>
    public string? SelectedTestId { get; private set; }

    private sealed record Row(string TestId, string Label);

    public RetestSelectWindow(IReadOnlyList<(string TestId, string Label)> reports)
    {
        InitializeComponent();
        var rows = new List<Row>();
        foreach (var (id, label) in reports) rows.Add(new Row(id, label));
        list.ItemsSource = rows;
        if (rows.Count > 0) list.SelectedIndex = 0;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (list.SelectedItem is Row r)
        {
            SelectedTestId = r.TestId;
            DialogResult = true;
            Close();
        }
        else
        {
            // Nada selecionado — trata como "mais recente".
            SelectedTestId = null;
            DialogResult = true;
            Close();
        }
    }

    private void OnUseLatest(object sender, RoutedEventArgs e)
    {
        SelectedTestId = null; // automático: mais recente
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
