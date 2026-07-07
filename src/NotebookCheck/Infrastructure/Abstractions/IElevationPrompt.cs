using System.Threading;
using System.Threading.Tasks;

namespace NotebookCheck.Infrastructure.Abstractions;

/// <summary>
/// Abstração sobre o fluxo de elevação UAC (Requirements 1.6 a 1.8).
/// Implementações reais combinam <c>WindowsIdentity.GetCurrent()</c> +
/// reinicialização com <c>verb=runas</c> via <c>Process.Start</c>; em testes
/// unitários a interface é substituída por uma fake determinística.
/// </summary>
public interface IElevationPrompt
{
    /// <summary>
    /// Indica se o processo atual já está em execução com privilégios
    /// administrativos (sem necessidade de UAC).
    /// </summary>
    bool IsRunningAsAdministrator { get; }

    /// <summary>
    /// Solicita elevação ao usuário e aguarda a resposta por até 60 segundos
    /// (Requirement 1.6). Em caso de timeout ou recusa, retorna
    /// <see cref="ElevationOutcome.Denied"/> sem encerrar a sessão.
    /// </summary>
    /// <param name="reason">
    /// Texto curto exibido ao Técnico explicando o motivo da elevação
    /// (ex.: "Coletar TPM via Get-Tpm").
    /// </param>
    /// <param name="timeout">
    /// Tempo máximo de espera pela resposta do usuário. Quando nulo, usa o
    /// padrão de 60 segundos.
    /// </param>
    /// <param name="ct">Token externo que pode antecipar o cancelamento.</param>
    Task<ElevationOutcome> RequestElevationAsync(string reason, TimeSpan? timeout, CancellationToken ct);
}

/// <summary>Resultado consolidado de uma tentativa de elevação UAC.</summary>
public enum ElevationOutcome
{
    /// <summary>O processo já estava elevado; nada foi feito.</summary>
    AlreadyElevated,
    /// <summary>O usuário aceitou a elevação.</summary>
    Granted,
    /// <summary>O usuário recusou a elevação.</summary>
    Denied,
    /// <summary>O usuário não respondeu dentro do timeout configurado.</summary>
    TimedOut,
    /// <summary>O serviço UAC está indisponível ou desabilitado.</summary>
    Unavailable,
}
