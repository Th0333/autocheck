using System.Windows;
using NotebookCheck.Presentation;

namespace NotebookCheck.Presentation.Views;

public partial class TesteCompletoWindow : Window
{
    public TesteCompletoWindow(TesteCompletoViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += (_, _) => Close();
        Loaded += async (_, _) => await vm.InitializeAsync();
    }
}
