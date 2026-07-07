namespace NotebookCheck.Domain.Enums;

/// <summary>
/// Resultado da detecção e/ou confirmação manual da presença de teclado retroiluminado
/// no equipamento inspecionado.
/// </summary>
/// <remarks>
/// A serialização para o payload da API converte:
/// <c>Sim → "sim"</c>, <c>Nao → "nao"</c>, <c>Indisponivel → "indisponivel"</c>.
/// </remarks>
public enum KeyboardBacklight
{
    Sim,
    Nao,
    Indisponivel
}
