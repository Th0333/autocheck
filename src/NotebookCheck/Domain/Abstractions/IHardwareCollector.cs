using NotebookCheck.Domain.Models;

namespace NotebookCheck.Domain.Abstractions;

/// <summary>
/// Coletor responsável pela identificação completa do equipamento (hardware,
/// armazenamento, bateria, recursos de segurança e displays).
/// </summary>
/// <remarks>
/// Cada operação tem timeout específico (Requirements 2.1, 3.1, 3.3, 3.4) e em
/// caso de erro/timeout retorna sentinelas <c>Indisponível</c> sem propagar
/// exceções para o orquestrador (Requirement 26).
/// </remarks>
public interface IHardwareCollector
{
    /// <summary>Coleta a identificação completa da máquina (timeout total 30 s).</summary>
    Task<MachineInfo> CollectMachineAsync(CancellationToken ct);

    /// <summary>Coleta os discos físicos com SMART e tipo de mídia (até 16 itens).</summary>
    Task<IReadOnlyList<StorageInfo>> CollectStorageAsync(CancellationToken ct);

    /// <summary>
    /// Coleta as informações da bateria, ou <c>null</c> quando o equipamento
    /// não possui bateria.
    /// </summary>
    Task<BatteryInfo?> CollectBatteryAsync(CancellationToken ct);

    /// <summary>Coleta TPM, Secure Boot e estado de Autopilot.</summary>
    Task<SecurityFeatures> CollectSecurityAsync(CancellationToken ct);

    /// <summary>Coleta resolução, adaptador gráfico e monitores conectados.</summary>
    Task<DisplayInfo> CollectDisplayAsync(CancellationToken ct);
}
