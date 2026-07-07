using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using NotebookCheck.Presentation;

namespace NotebookCheck.Presentation.Views;

public partial class KanbanWindow : Window
{
    public KanbanWindow(KanbanViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.TecnicoPrompt = PromptTecnico;
        vm.RemoverConfirm = ConfirmRemover;
        vm.CadastroRequested += OpenCadastro;
        Loaded += async (_, _) => await vm.InitializeAsync();
    }

    /// <summary>Abre o cadastro reivindicando a máquina em branco clicada no kanban.</summary>
    private void OpenCadastro(string pedidoId, string assetId)
    {
        var host = (System.Windows.Application.Current as App)?.Host;
        if (host is null) return;
        var vm = host.Services.GetRequiredService<CadastroViewModel>();
        vm.Preselect(pedidoId, assetId);
        var window = new CadastroWindow(vm) { Owner = this };
        window.ShowDialog();
        // Ao fechar o cadastro, a máquina pode ter mudado de etapa.
        if (DataContext is KanbanViewModel kvm) kvm.RefreshCommand.Execute(null);
    }

    /// <summary>Confirma a remoção (ocultamento local) de um cartão.</summary>
    private bool ConfirmRemover(string ntb)
    {
        var r = System.Windows.MessageBox.Show(
            $"Remover {ntb} do kanban deste computador?\n\nEla continua no ERP — some só deste quadro. Use 'Mostrar ocultos' para trazer de volta.",
            "Remover do kanban", System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);
        return r == System.Windows.MessageBoxResult.OK;
    }

    /// <summary>Janelinha modal perguntando o nome do técnico.</summary>
    private string? PromptTecnico(string? nomeAtual)
    {
        var box = new TextBox { Text = nomeAtual ?? "", Margin = new Thickness(0, 8, 0, 14) };
        var dialog = BuildDialog("Quem está assumindo esta máquina?", box, out var ok);
        box.SelectAll();
        box.Focus();
        return dialog.ShowDialog() == true && ok() && !string.IsNullOrWhiteSpace(box.Text)
            ? box.Text.Trim() : null;
    }

    /// <summary>Monta uma janela modal simples (título + conteúdo + OK/Cancelar).</summary>
    private Window BuildDialog(string titulo, UIElement conteudo, out System.Func<bool> confirmado, bool comBotaoOk = true)
    {
        var okClicked = false;
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock
        {
            Text = titulo,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(conteudo);

        var dialog = new Window
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height,
            Width = 380,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            Title = "Notelet",
            Background = System.Windows.Media.Brushes.White,
            Content = root,
        };

        var botoes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (comBotaoOk)
        {
            var ok = new Button { Content = "OK", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            ok.Click += (_, _) => { okClicked = true; dialog.DialogResult = true; };
            botoes.Children.Add(ok);
        }
        var cancelar = new Button { Content = "Cancelar", Padding = new Thickness(16, 7, 16, 7), IsCancel = true };
        botoes.Children.Add(cancelar);
        root.Children.Add(botoes);

        confirmado = () => okClicked;
        return dialog;
    }
}
