namespace NotebookCheck.Domain.Enums;

/// <summary>
/// Status resultante de itens automáticos do checklist (executados pelo motor de testes).
/// </summary>
public enum AutoStatus
{
    OK,
    Atencao,
    Falha,
    NaoTestado,
    NaoAplicavel
}
