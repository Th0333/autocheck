using System;
using System.Windows;

namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Faz toda janela nascer dentro da tela da bancada.
///
/// Os tamanhos do XAML foram desenhados num monitor grande. Nos notebooks de
/// 1366x768 — e em qualquer tela com escala de 125%, que derruba a área útil
/// para ~1090x580 — a janela nascia mais alta que a área de trabalho e o rodapé
/// com "Avançar"/"Cadastrar" ficava embaixo da barra de tarefas: o técnico tinha
/// que redimensionar na mão toda vez que abria o cadastro.
///
/// O ajuste roda uma vez por janela, no <c>Loaded</c>, e só encosta em quem não
/// cabe. Janela que já cabe não muda de tamanho nem de lugar.
/// </summary>
public static class WindowSizing
{
    /// <summary>Folga para a janela não colar nas bordas da área de trabalho.</summary>
    private const double Folga = 40;

    /// <summary>
    /// Liga o ajuste para TODA janela do app (inclusive as abertas por diálogos
    /// e a de atualização). Chamar uma vez no startup, antes da primeira janela.
    /// </summary>
    public static void Register() =>
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((remetente, _) =>
            {
                if (remetente is Window janela) Ajustar(janela);
            }));

    /// <summary>Encolhe e reposiciona a janela para caber na área útil da tela.</summary>
    public static void Ajustar(Window janela)
    {
        // Maximizada já respeita a área de trabalho; minimizada não tem tamanho.
        if (janela.WindowState != WindowState.Normal) return;

        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;

        var maxLargura = Math.Max(360, area.Width - Folga);
        var maxAltura = Math.Max(360, area.Height - Folga);

        // O mínimo do XAML precisa caber na tela ANTES de qualquer outra coisa:
        // enquanto ele for maior que a área útil, o WPF ignora o tamanho que a
        // gente pedir e a janela volta a nascer maior que o monitor.
        if (janela.MinWidth > maxLargura) janela.MinWidth = maxLargura;
        if (janela.MinHeight > maxAltura) janela.MinHeight = maxAltura;

        var larguraAuto = janela.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
        var alturaAuto = janela.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;

        // Em janela que se dimensiona pelo conteúdo o tamanho não pode ser
        // fixado (o SizeToContent vence), então o limite vai no teto.
        if (larguraAuto && janela.MaxWidth > maxLargura) janela.MaxWidth = maxLargura;
        if (alturaAuto && janela.MaxHeight > maxAltura) janela.MaxHeight = maxAltura;

        var largura = Efetivo(janela.Width, janela.ActualWidth);
        var altura = Efetivo(janela.Height, janela.ActualHeight);
        var encolheuLargura = largura > maxLargura + 0.5;
        var encolheuAltura = altura > maxAltura + 0.5;
        if (!encolheuLargura && !encolheuAltura) return; // já cabia: não mexe na posição

        if (encolheuLargura)
        {
            largura = maxLargura;
            if (!larguraAuto) janela.Width = maxLargura;
        }
        if (encolheuAltura)
        {
            altura = maxAltura;
            if (!alturaAuto) janela.Height = maxAltura;
        }

        // Ao encolher no Loaded a posição calculada pelo CenterOwner/CenterScreen
        // deixa de centralizar — recentraliza na área útil.
        if (largura > 0) janela.Left = area.Left + Math.Max(0, (area.Width - largura) / 2);
        if (altura > 0) janela.Top = area.Top + Math.Max(0, (area.Height - altura) / 2);
    }

    /// <summary>Tamanho declarado no XAML; se for Auto, o que a janela mediu.</summary>
    private static double Efetivo(double declarado, double atual) =>
        double.IsNaN(declarado) ? atual : declarado;
}
