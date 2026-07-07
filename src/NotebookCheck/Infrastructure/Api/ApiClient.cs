using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NotebookCheck.Bootstrap;
using NotebookCheck.Domain.Abstractions;

namespace NotebookCheck.Infrastructure.Api;

/// <summary>
/// Implementação simples do <see cref="IApiClient"/> sobre <see cref="HttpClient"/>.
/// Mapeia status HTTP para <see cref="ApiOutcome"/>.
/// </summary>
public sealed class ApiClient : IApiClient
{
    private readonly IHttpClientFactory _factory;
    private readonly AppConfig _config;
    private readonly ILogger<ApiClient> _logger;

    public ApiClient(IHttpClientFactory factory, AppConfig config, ILogger<ApiClient> logger)
    {
        _factory = factory;
        _config = config;
        _logger = logger;
    }

    public async Task<ApiResult> SendAsync(ApiPayload payload, CancellationToken ct)
    {
        if (!ConfigBootstrap.TryBuildApiUri(_config, out var uri) || uri is null)
        {
            return new ApiResult(ApiOutcome.ConfigInvalid, null, null);
        }

        try
        {
            var client = _factory.CreateClient("api");
            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(payload),
            };
            if (!string.IsNullOrWhiteSpace(_config.AuthToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            }
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            var outcome = code switch
            {
                >= 200 and < 300 => ApiOutcome.Sent,
                >= 400 and < 500 => ApiOutcome.ValidationError,
                >= 500 and < 600 => ApiOutcome.ServerError,
                _ => ApiOutcome.NetworkFailure,
            };
            if (outcome == ApiOutcome.ValidationError)
            {
                _logger.LogWarning("API recusou payload: {Status} {Body}", code, body);
            }
            return new ApiResult(outcome, code, body);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha enviando para API");
            return new ApiResult(ApiOutcome.NetworkFailure, null, ex.Message);
        }
    }
}
