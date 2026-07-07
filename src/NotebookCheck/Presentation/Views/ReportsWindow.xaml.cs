using System.Windows;
using NotebookCheck.Presentation;

namespace NotebookCheck.Presentation.Views;

public partial class ReportsWindow : Window
{
    public ReportsWindow(ReportsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.RefreshCommand.ExecuteAsync(null);
    }
}
