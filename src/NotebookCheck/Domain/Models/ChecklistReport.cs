using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Domain.Models;

/// <summary>
/// Relatório consolidado do checklist completo, agregando identificação do
/// equipamento, resultados automáticos e manuais, identificação do técnico e
/// metadados de geração.
/// </summary>
/// <param name="TestId">Identificador único universal (UUID v4) da execução.</param>
/// <param name="TestedAt">Data e hora local em que o checklist foi concluído.</param>
/// <param name="TechnicianName">Nome ou matrícula do técnico responsável.</param>
/// <param name="Machine">Identificação completa do equipamento.</param>
/// <param name="Storage">Lista imutável de discos físicos coletados (até 16).</param>
/// <param name="Battery">Informações da bateria, quando aplicável.</param>
/// <param name="Tests">
/// Dicionário imutável de resultados de testes automáticos chaveado por
/// <c>TestKey</c> em snake_case.
/// </param>
/// <param name="ManualChecklist">
/// Dicionário imutável de itens do checklist físico chaveado por <c>ItemKey</c>.
/// </param>
/// <param name="GeneralNotes">Observações gerais (até 2000 caracteres).</param>
/// <param name="AssetTag">
/// Etiqueta de patrimônio interno opcional (até 100 caracteres), distinta do
/// código NTB armazenado em <see cref="MachineInfo.NtbCode"/>.
/// </param>
/// <param name="FinalClassification">Classificação final consolidada.</param>
/// <param name="FinalClassificationOverrideReason">
/// Justificativa textual obrigatória quando o técnico altera manualmente a
/// classificação final automática.
/// </param>
public record ChecklistReport(
    Guid TestId,
    DateTime TestedAt,
    string TechnicianName,
    MachineInfo Machine,
    IReadOnlyList<StorageInfo> Storage,
    BatteryInfo? Battery,
    IReadOnlyDictionary<string, TestResult> Tests,
    IReadOnlyDictionary<string, ManualCheckItem> ManualChecklist,
    string GeneralNotes,
    string AssetTag,
    FinalClassification FinalClassification,
    string? FinalClassificationOverrideReason,
    ChecklistMode Mode = ChecklistMode.Padrao,
    StressSnapshot? Stress = null,
    IReadOnlyList<InspectionPhoto>? InspectionPhotos = null,
    string? InspectionSlug = null,
    bool? HasNumericKeypad = null);

/// <summary>
/// Snapshot do resultado da nova benchmark suite, propagado para o payload
/// da API para alimentar o ranking por specs. Campos opcionais ficam em 0
/// quando o teste correspondente não foi executado.
/// </summary>
public record StressSnapshot(
    int FinalScore,
    // CPU
    int CpuSingleThread,
    int CpuMultiThread,
    int CpuEfficiency,
    int CpuThreads,
    // GPU
    int GpuGraphics,
    int GpuCompute,
    int GpuBandwidth,
    string? GpuName,
    string? GpuFeatureLevel,
    // Disco
    int DiskScore,
    double DiskReadMbPerSec,
    double DiskWriteMbPerSec,
    // VRAM
    bool VramOk,
    int VramAllocatedMb,
    long VramMismatchCount,
    // RAM
    bool RamOk = true,
    int RamAllocatedMb = 0,
    long RamErrorCount = 0,
    double RamBandwidthGbs = 0,
    // Geekbench 6 (opcional)
    int GeekbenchSingle = 0,
    int GeekbenchMulti = 0,
    string? GeekbenchVersion = null);
