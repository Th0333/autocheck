using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Domain.Models;

/// <summary>
/// Item do checklist físico avaliado manualmente pelo técnico.
/// </summary>
/// <param name="ItemKey">
/// Identificador textual do item (ex.: "carcaca", "tela", "teclado").
/// </param>
/// <param name="Status">Classificação manual atribuída pelo técnico.</param>
/// <param name="Notes">
/// Texto descritivo (até 500 caracteres). Obrigatório quando o status é
/// <see cref="ManualStatus.ComDefeito"/> ou <see cref="ManualStatus.Observacao"/>.
/// </param>
public record ManualCheckItem(
    string ItemKey,
    ManualStatus Status,
    string Notes);
