using System.Windows;

namespace NotebookCheck.Presentation.Views;

/// <summary>Janela dos testes avulsos de componentes (memória, SSD, bateria).</summary>
public partial class TesteComponentesWindow : Window
{
    public TesteComponentesWindow(TesteComponentesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.InitializeAsync();
        // cancela um teste de descarga em andamento ao fechar
        Closed += (_, _) => vm.Cleanup();
    }
}
