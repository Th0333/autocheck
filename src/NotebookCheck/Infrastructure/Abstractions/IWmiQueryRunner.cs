using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NotebookCheck.Infrastructure.Abstractions;

/// <summary>
/// Abstração sobre <c>System.Management</c> para execução de consultas WQL com
/// timeout explícito. Encapsular o WMI por trás desta interface permite que o
/// <c>WmiHardwareCollector</c> seja substituído em testes unitários e
/// property-based por implementações em memória, conforme estratégia de
/// testabilidade descrita no design.
/// </summary>
public interface IWmiQueryRunner
{
    /// <summary>
    /// Executa uma consulta WQL contra o escopo informado e devolve as linhas
    /// como dicionários nome → valor. Em caso de timeout, exceção do WMI ou
    /// negação de permissão, a implementação SHALL lançar
    /// <see cref="WmiQueryException"/> contendo a causa textual classificada.
    /// </summary>
    /// <param name="scope">
    /// Caminho do namespace WMI (ex.: <c>root\\cimv2</c>, <c>root\\wmi</c>).
    /// </param>
    /// <param name="wql">
    /// Texto WQL a ser executado (ex.: <c>SELECT * FROM Win32_BIOS</c>).
    /// </param>
    /// <param name="timeout">
    /// Tempo máximo total para a operação. Após esse intervalo a tarefa SHALL
    /// ser cancelada e <see cref="WmiQueryException"/> SHALL ser lançada com
    /// causa <see cref="WmiFailureCause.Timeout"/>.
    /// </param>
    /// <param name="ct">Token externo que pode antecipar o cancelamento.</param>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string scope,
        string wql,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>
/// Causa classificada de falha numa consulta WMI, registrada nos logs e usada
/// para diferenciar valores <c>Indisponível</c> do tipo "permissão",
/// "ausência do recurso" ou "tempo limite excedido" exigidos pelo
/// Requirement 3.5.
/// </summary>
public enum WmiFailureCause
{
    /// <summary>Falha genérica ou exceção inesperada.</summary>
    Unknown,
    /// <summary>O recurso consultado não existe na máquina alvo.</summary>
    NotPresent,
    /// <summary>Acesso negado (privilégios insuficientes).</summary>
    AccessDenied,
    /// <summary>Tempo limite excedido pelo <c>timeout</c> informado.</summary>
    Timeout,
    /// <summary>Sintaxe ou namespace inválido.</summary>
    InvalidQuery,
}

/// <summary>
/// Exceção lançada por <see cref="IWmiQueryRunner"/> quando a consulta WMI não
/// pode ser concluída. Mantém a causa classificada e o WQL original para
/// permitir log estruturado.
/// </summary>
public sealed class WmiQueryException : Exception
{
    public WmiQueryException(WmiFailureCause cause, string scope, string wql, string? message = null, Exception? innerException = null)
        : base(message ?? $"Falha WMI ({cause}) em {scope}: {wql}", innerException)
    {
        Cause = cause;
        Scope = scope;
        Query = wql;
    }

    /// <summary>Categoria de falha responsável pelo erro.</summary>
    public WmiFailureCause Cause { get; }

    /// <summary>Namespace WMI alvo da consulta.</summary>
    public string Scope { get; }

    /// <summary>Texto WQL que falhou.</summary>
    public string Query { get; }
}
