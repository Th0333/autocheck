using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;

namespace NotebookCheck.Domain.Abstractions;

/// <summary>
/// Orquestrador do modo reteste pós-reparo: coleta silenciosa de identificação,
/// execução dos testes mapeados em <c>ComponentTestMap</c> apenas para os
/// componentes selecionados, e geração do <see cref="RetestReport"/>
/// (Requirement 32).
/// </summary>
public interface IRetestController
{
    /// <summary>
    /// Executa o reteste para os componentes em <paramref name="selected"/> na
    /// ordem de seleção, expondo progresso por componente via
    /// <paramref name="progress"/>.
    /// </summary>
    /// <param name="selected">
    /// Lista não-vazia de <see cref="ComponentId"/> escolhidos pelo técnico.
    /// </param>
    /// <param name="repairNotes">
    /// Nota textual do reparo (será truncada a 500 caracteres no payload).
    /// </param>
    /// <param name="progress">
    /// Canal para reportar <see cref="RetestProgress"/> com posição
    /// <c>(current, total)</c>.
    /// </param>
    /// <param name="ct">Token de cancelamento.</param>
    Task<RetestReport> RunAsync(
        IReadOnlyList<ComponentId> selected,
        string repairNotes,
        IProgress<RetestProgress> progress,
        CancellationToken ct);
}
