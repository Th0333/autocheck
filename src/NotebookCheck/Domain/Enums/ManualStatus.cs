namespace NotebookCheck.Domain.Enums;

/// <summary>
/// Status resultante de itens de inspeção manual confirmados pelo técnico.
/// </summary>
public enum ManualStatus
{
    OK,
    ComDefeito,
    NaoTestado,
    Observacao
}
