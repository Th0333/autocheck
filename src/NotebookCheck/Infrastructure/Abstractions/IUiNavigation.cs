using System.Threading;
using System.Threading.Tasks;

namespace NotebookCheck.Infrastructure.Abstractions;

/// <summary>
/// Abstração sobre a navegação WPF (<c>Frame.Navigate</c>) e os diálogos
/// modais usados pelos orquestradores (<c>ChecklistOrchestrator</c> e
/// <c>RetestController</c>). Mantê-la fora dos ViewModels permite que o
/// orquestrador seja exercitado em testes de integração sem instanciar a
/// janela real.
/// </summary>
public interface IUiNavigation
{
    /// <summary>
    /// Navega para a página identificada pela chave informada. O mapeamento
    /// chave → <c>Page</c> é resolvido pelo container DI da implementação.
    /// </summary>
    /// <param name="pageKey">
    /// Chave estável da página (ex.: <c>"start"</c>, <c>"identification"</c>,
    /// <c>"summary"</c>).
    /// </param>
    /// <param name="parameter">
    /// Parâmetro opcional passado ao ViewModel da próxima página
    /// (ex.: lista de componentes selecionados no reteste).
    /// </param>
    void NavigateTo(string pageKey, object? parameter = null);

    /// <summary>
    /// Retorna à página anterior do <c>Frame</c>, se houver. Retorna
    /// <c>false</c> quando a pilha está vazia.
    /// </summary>
    bool GoBack();

    /// <summary>
    /// Apresenta um diálogo modal de confirmação Sim/Não. Resolvido com o
    /// resultado escolhido pelo Técnico.
    /// </summary>
    /// <param name="title">Título da janela.</param>
    /// <param name="message">Texto principal exibido ao Técnico.</param>
    /// <param name="ct">Token externo que pode fechar o diálogo.</param>
    Task<bool> ConfirmAsync(string title, string message, CancellationToken ct);

    /// <summary>
    /// Apresenta um diálogo modal de notificação (apenas botão "OK").
    /// </summary>
    /// <param name="title">Título da janela.</param>
    /// <param name="message">Texto principal exibido ao Técnico.</param>
    /// <param name="severity">Severidade visual da mensagem.</param>
    /// <param name="ct">Token externo que pode fechar o diálogo.</param>
    Task ShowMessageAsync(string title, string message, NotificationSeverity severity, CancellationToken ct);
}

/// <summary>
/// Severidade visual aplicada ao diálogo de notificação. Permite à
/// implementação WPF escolher cor e ícone apropriados sem expor
/// <c>MessageBoxImage</c> aos ViewModels.
/// </summary>
public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
}
