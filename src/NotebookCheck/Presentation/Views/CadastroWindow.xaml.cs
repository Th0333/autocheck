using System.Windows;
using NotebookCheck.Presentation;

namespace NotebookCheck.Presentation.Views;

public partial class CadastroWindow : Window
{
    public CadastroWindow(CadastroViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += (_, _) => Close();
        Loaded += async (_, _) => await vm.InitializeAsync();
    }
}
