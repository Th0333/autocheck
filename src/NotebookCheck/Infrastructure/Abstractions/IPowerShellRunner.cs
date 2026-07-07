using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NotebookCheck.Infrastructure.Abstractions;

/// <summary>
/// Abstração sobre <c>System.Management.Automation</c> (Microsoft.PowerShell.SDK)
/// para execução de cmdlets isolados (ex.: <c>Get-Tpm</c>,
/// <c>Confirm-SecureBootUEFI</c>, <c>Get-PnpDevice</c>,
/// <c>Get-NetAdapter</c>) com timeout configurável.
/// </summary>
public interface IPowerShellRunner
{
    /// <summary>
    /// Executa um pipeline PowerShell e devolve cada objeto de saída como um
    /// dicionário nome → valor (resultado de <c>PSObject.Properties</c>).
    /// Falhas, exceções ou estouro do <paramref name="timeout"/> são
    /// reportados via <see cref="PowerShellExecutionException"/>.
    /// </summary>
    /// <param name="script">Bloco de script ou cmdlet a executar.</param>
    /// <param name="parameters">
    /// Parâmetros nomeados a injetar via <c>AddParameter</c>; pode ser nulo.
    /// </param>
    /// <param name="timeout">Tempo máximo total da execução.</param>
    /// <param name="ct">Token externo que pode antecipar o cancelamento.</param>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> InvokeAsync(
        string script,
        IReadOnlyDictionary<string, object?>? parameters,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>
/// Exceção lançada por <see cref="IPowerShellRunner"/> quando um pipeline
/// retorna erros não-terminantes coletados de <c>$Error</c>, lança exceção
/// terminante ou excede o timeout configurado.
/// </summary>
public sealed class PowerShellExecutionException : Exception
{
    public PowerShellExecutionException(string script, IReadOnlyList<string> errors, bool timedOut, Exception? innerException = null)
        : base(BuildMessage(script, errors, timedOut), innerException)
    {
        Script = script;
        Errors = errors;
        TimedOut = timedOut;
    }

    /// <summary>Script ou cmdlet que falhou.</summary>
    public string Script { get; }

    /// <summary>Mensagens de erro coletadas do stream de erro do PowerShell.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Indica se a execução foi abortada por timeout.</summary>
    public bool TimedOut { get; }

    private static string BuildMessage(string script, IReadOnlyList<string> errors, bool timedOut)
    {
        if (timedOut)
        {
            return $"Tempo limite excedido ao executar PowerShell: {script}";
        }

        return errors.Count == 0
            ? $"Falha ao executar PowerShell: {script}"
            : $"Falha ao executar PowerShell ({script}): {string.Join("; ", errors)}";
    }
}
