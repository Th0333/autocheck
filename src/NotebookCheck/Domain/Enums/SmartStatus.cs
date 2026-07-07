namespace NotebookCheck.Domain.Enums;

/// <summary>
/// Estado de saúde S.M.A.R.T. reportado pelo dispositivo de armazenamento.
/// </summary>
public enum SmartStatus
{
    OK,
    Aviso,
    Falha,
    Indisponivel
}
