using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Domain.Models;

/// <summary>
/// Progresso reportado pelo <c>IRetestController</c> ao consumir
/// <c>IProgress&lt;RetestProgress&gt;</c> durante a execução do reteste.
/// </summary>
/// <param name="Current">
/// Posição 1-based do componente em execução dentro da lista selecionada
/// (entre 1 e <paramref name="Total"/>, inclusive).
/// </param>
/// <param name="Total">Quantidade total de componentes selecionados para reteste.</param>
/// <param name="CurrentComponent">
/// Identificador do componente atualmente em execução, ou <c>null</c> antes do
/// primeiro teste / após o último.
/// </param>
public record RetestProgress(
    int Current,
    int Total,
    ComponentId? CurrentComponent);
