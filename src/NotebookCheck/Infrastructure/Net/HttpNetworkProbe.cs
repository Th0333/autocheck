using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NotebookCheck.Infrastructure.Abstractions;

namespace NotebookCheck.Infrastructure.Net;

/// <summary>
/// Implementação de <see cref="INetworkProbe"/> sobre <see cref="HttpClient"/>.
/// </summary>
public sealed class HttpNetworkProbe : INetworkProbe
{
    private readonly IHttpClientFactory _factory;

    public HttpNetworkProbe(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    public async Task<ProbeResult> ProbeAsync(string probeUrl, TimeSpan timeout, CancellationToken ct)
    {
        if (!Uri.TryCreate(probeUrl, UriKind.Absolute, out var uri))
        {
            return new ProbeResult(ProbeOutcome.InvalidUrl, null, 0, "URL inválida");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var sw = Stopwatch.StartNew();
        try
        {
            var client = _factory.CreateClient("probe");
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            sw.Stop();
            var code = (int)resp.StatusCode;
            var outcome = code switch
            {
                >= 200 and < 300 => ProbeOutcome.Success,
                >= 400 and < 500 => ProbeOutcome.ClientError,
                >= 500 and < 600 => ProbeOutcome.ServerError,
                _ => ProbeOutcome.NetworkFailure,
            };
            return new ProbeResult(outcome, code, sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new ProbeResult(ProbeOutcome.Timeout, null, sw.ElapsedMilliseconds, "Tempo limite excedido");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult(ProbeOutcome.NetworkFailure, null, sw.ElapsedMilliseconds, ex.Message);
        }
    }
}
