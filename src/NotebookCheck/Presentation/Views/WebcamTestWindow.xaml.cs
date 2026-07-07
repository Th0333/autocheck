using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Janela do teste de câmera. Lista as câmeras detectadas e abre a app Câmera
/// do Windows para o técnico inspecionar a imagem ao vivo. O resultado é
/// registrado a partir do clique do técnico em "Câmera OK" ou "Não funciona".
/// </summary>
public partial class WebcamTestWindow : TestResultWindow
{
    public WebcamTestWindow(IReadOnlyList<string> detectedCameras)
    {
        InitializeComponent();
        if (detectedCameras.Count == 0)
        {
            emptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            camList.ItemsSource = detectedCameras;
        }
    }

    private void OnOpenCamera(object sender, RoutedEventArgs e)
    {
        try
        {
            // URI da app integrada Câmera do Windows (Windows 10+)
            Process.Start(new ProcessStartInfo
            {
                FileName = "microsoft.windows.camera:",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            lastError.Text = "Falha abrindo a câmera: " + ex.Message
                + ". Verifique se a app Câmera do Windows está instalada (ms-availablecomms).";
            lastError.Visibility = Visibility.Visible;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Approve("Imagem confirmada pelo técnico");
    }

    private void OnFailed(object sender, RoutedEventArgs e)
    {
        Reject("Câmera marcada como não funcional pelo técnico");
    }
}
