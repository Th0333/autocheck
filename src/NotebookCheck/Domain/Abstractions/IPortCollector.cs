using NotebookCheck.Domain.Models;

namespace NotebookCheck.Domain.Abstractions;

/// <summary>
/// Coletor responsável por enumerar portas/conectores físicos do equipamento
/// e o estado atual de uso de cada um, alimentando a etapa "Inputs e portas".
/// </summary>
public interface IPortCollector
{
    Task<IReadOnlyList<PortInfo>> CollectAsync(CancellationToken ct);
}
