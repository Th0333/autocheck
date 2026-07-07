namespace NotebookCheck.Domain.Enums;

/// <summary>
/// Sinalizador de disponibilidade/estado para recursos como TPM, Secure Boot,
/// Autopilot e ativação do Windows.
/// </summary>
public enum AvailabilityFlag
{
    Presente,
    Ausente,
    Indisponivel,
    Habilitado,
    Desabilitado,
    NaoPronto,
    Registrado,
    NaoRegistrado,
    NaoDeterminado,
    Ativado,
    NaoAtivado
}
