using NotebookCheck.Infrastructure.Api;

namespace NotebookCheck.Domain.Abstractions;

/// <summary>
/// Cliente HTTP que envia o relatório serializado para a API REST configurada,
/// com políticas Polly de retry exponencial e timeout (Requirements 24.1–24.4).
/// </summary>
public interface IApiClient
{
    /// <summary>
    /// Envia <paramref name="payload"/> via POST para o endpoint configurado em
    /// <c>config.json</c> e mapeia a resposta HTTP para um <see cref="ApiResult"/>.
    /// </summary>
    Task<ApiResult> SendAsync(ApiPayload payload, CancellationToken ct);
}

/// <summary>
/// Resultado classificado de uma tentativa de envio à API.
/// </summary>
public enum ApiOutcome
{
    /// <summary>HTTP 2xx — relatório aceito pela API.</summary>
    Sent,

    /// <summary>HTTP 4xx — payload inválido; body registrado em log.</summary>
    ValidationError,

    /// <summary>HTTP 5xx — erro do servidor; relatório vai para a fila offline.</summary>
    ServerError,

    /// <summary>Exceção de transporte (DNS, TCP, TLS, etc.).</summary>
    NetworkFailure,

    /// <summary>URL configurada inválida ou ausente.</summary>
    ConfigInvalid
}

/// <summary>
/// Resultado consolidado do envio à API, contendo o <see cref="ApiOutcome"/>
/// classificado e (quando disponível) o status HTTP e o corpo da resposta.
/// </summary>
/// <param name="Outcome">Classificação do resultado.</param>
/// <param name="StatusCode">Código HTTP retornado, quando houve resposta.</param>
/// <param name="Body">Corpo da resposta (texto), quando recebido.</param>
public record ApiResult(
    ApiOutcome Outcome,
    int? StatusCode,
    string? Body);
