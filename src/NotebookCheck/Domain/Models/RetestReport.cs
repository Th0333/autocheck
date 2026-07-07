using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Domain.Models;

/// <summary>
/// Relatório do modo reteste pós-reparo, contendo apenas os componentes
/// retestados e a nota de reparo associada.
/// </summary>
/// <param name="TestId">Identificador único universal (UUID v4) da execução.</param>
/// <param name="TestedAt">Data e hora local em que o reteste foi concluído.</param>
/// <param name="TechnicianName">Nome ou matrícula do técnico responsável.</param>
/// <param name="Machine">Identificação do equipamento coletada silenciosamente.</param>
/// <param name="RetestedComponents">
/// Lista imutável dos componentes selecionados pelo técnico, na ordem de seleção.
/// </param>
/// <param name="RepairNotes">
/// Nota textual descrevendo o reparo realizado (até 500 caracteres no payload).
/// </param>
/// <param name="Tests">
/// Dicionário imutável dos resultados dos testes executados, chaveado por
/// <c>TestKey</c> em snake_case.
/// </param>
/// <param name="FinalClassification">Classificação final consolidada do reteste.</param>
public record RetestReport(
    Guid TestId,
    DateTime TestedAt,
    string TechnicianName,
    MachineInfo Machine,
    IReadOnlyList<ComponentId> RetestedComponents,
    string RepairNotes,
    IReadOnlyDictionary<string, TestResult> Tests,
    FinalClassification FinalClassification);
