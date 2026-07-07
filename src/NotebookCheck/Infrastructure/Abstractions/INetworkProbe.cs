using System.Threading;
using System.Threading.Tasks;

namespace NotebookCheck.Infrastructure.Abstractions;

/// <summary>
/// Abstração sobre <c>HttpClient</c> usada pelo teste de internet
/// (<c>RunInternetAsync</c>) e pelo <c>OfflineSyncService</c> para verificar
/// conectividade antes de tentar reenviar a fila. Mantém a assinatura mínima
/// suficiente para classificar o resultado em <see cref="ProbeOutcome"/>.
/// </summary>
public interface INetworkProbe
{
    /// <summary>
    /// Executa um <c>HEAD</c> ou <c>GET</c> contra a URL informada com timeout
    /// total especificado e retorna o resultado classificado. A implementação
    /// SHALL não lançar para o chamador — exceções de transporte são mapeadas
    /// para <see cref="ProbeOutcome.NetworkFailure"/>.
    /// </summary>
    /// <param name="probeUrl">URL completa a ser consultada.</param>
    /// <param name="timeout">Tempo máximo total da requisição.</param>
    /// <param name="ct">Token externo que pode antecipar o cancelamento.</param>
    Task<ProbeResult> ProbeAsync(string probeUrl, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Classificação compacta do resultado do probe HTTP.</summary>
public enum ProbeOutcome
{
    /// <summary>Resposta HTTP no intervalo <c>[200, 299]</c>.</summary>
    Success,
    /// <summary>Resposta HTTP <c>[400, 499]</c>.</summary>
    ClientError,
    /// <summary>Resposta HTTP <c>[500, 599]</c>.</summary>
    ServerError,
    /// <summary>Tempo limite excedido.</summary>
    Timeout,
    /// <summary>Exceção de rede ou DNS.</summary>
    NetworkFailure,
    /// <summary>URL inválida ou não absoluta.</summary>
    InvalidUrl,
}

/// <summary>Resultado retornado por <see cref="INetworkProbe.ProbeAsync"/>.</summary>
/// <param name="Outcome">Classificação do resultado.</param>
/// <param name="StatusCode">Código HTTP, quando houve resposta.</param>
/// <param name="ElapsedMilliseconds">Tempo total da requisição.</param>
/// <param name="ErrorMessage">Mensagem de erro, quando aplicável.</param>
public readonly record struct ProbeResult(
    ProbeOutcome Outcome,
    int? StatusCode,
    long ElapsedMilliseconds,
    string? ErrorMessage);
