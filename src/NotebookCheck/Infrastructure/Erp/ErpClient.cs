using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Infrastructure.Erp;

/// <summary>
/// Cliente HTTP do ERP de estoque (recebimento via app). Autentica na conta de
/// serviço pelo Supabase Auth (grant de senha), guarda o <c>access_token</c> e o
/// usa como Bearer nas chamadas a <c>/api/integracao/...</c>. Reautentica sozinho
/// quando o token expira ou a API responde 401.
/// </summary>
public sealed class ErpClient
{
    private readonly IHttpClientFactory _factory;
    private readonly ErpConfig _config;
    private readonly ILogger<ErpClient> _logger;
    private readonly SemaphoreSlim _authGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private string? _token;
    private DateTime _tokenExpiresUtc = DateTime.MinValue;

    public ErpClient(IHttpClientFactory factory, ErpConfig config, ILogger<ErpClient> logger)
    {
        _factory = factory;
        _config = config;
        _logger = logger;
    }

    public bool IsConfigured => _config.IsConfigured;

    // ---------------------------------------------------------------- auth ---

    private async Task EnsureTokenAsync(CancellationToken ct, bool force = false)
    {
        if (!force && _token is not null && DateTime.UtcNow < _tokenExpiresUtc)
            return;

        await _authGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && _token is not null && DateTime.UtcNow < _tokenExpiresUtc)
                return;

            if (!_config.IsConfigured)
                throw new ErpException("Integração com o ERP não configurada (URL/credenciais ausentes).");

            var client = _factory.CreateClient("erp");
            var url = $"{_config.SupabaseUrl}/auth/v1/token?grant_type=password";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { email = _config.Email, password = _config.Password }),
            };
            req.Headers.TryAddWithoutValidation("apikey", _config.SupabaseAnonKey);

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            SupabaseTokenResponse? parsed = null;
            try { parsed = JsonSerializer.Deserialize<SupabaseTokenResponse>(body, JsonOpts); } catch { /* ignore */ }

            if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(parsed?.AccessToken))
            {
                var msg = parsed?.ErrorDescription ?? parsed?.Msg ?? parsed?.Error
                          ?? $"Falha ao autenticar no ERP (HTTP {(int)resp.StatusCode}).";
                _logger.LogWarning("ERP auth falhou: {Status} {Body}", (int)resp.StatusCode, body);
                throw new ErpException(msg, (int)resp.StatusCode);
            }

            _token = parsed!.AccessToken;
            // Renova 60s antes de expirar de fato.
            var ttl = parsed.ExpiresIn > 120 ? parsed.ExpiresIn - 60 : Math.Max(30, parsed.ExpiresIn);
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(ttl);
            _logger.LogInformation("ERP autenticado; token válido por ~{Ttl}s", ttl);
        }
        finally
        {
            _authGate.Release();
        }
    }

    /// <summary>Força um login agora (usado no "Conectar" da tela de cadastro).</summary>
    public Task ConnectAsync(CancellationToken ct) => EnsureTokenAsync(ct, force: true);

    // ------------------------------------------------------------ requests ---

    private async Task<HttpResponseMessage> SendAuthedAsync(
        Func<HttpRequestMessage> build, CancellationToken ct)
    {
        await EnsureTokenAsync(ct).ConfigureAwait(false);
        var client = _factory.CreateClient("erp");

        using var first = build();
        first.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        var resp = await client.SendAsync(first, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.Unauthorized)
            return resp;

        // 401: token pode ter expirado — reautentica uma vez e repete.
        resp.Dispose();
        await EnsureTokenAsync(ct, force: true).ConfigureAwait(false);
        using var retry = build();
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return await client.SendAsync(retry, ct).ConfigureAwait(false);
    }

    private async Task<string> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString() ?? body;
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString() ?? body;
        }
        catch { /* corpo não-JSON */ }
        return string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)resp.StatusCode}" : body;
    }

    private async Task<IReadOnlyList<ErpRef>> ListAsync(string resource, string? q, CancellationToken ct)
    {
        var url = $"{_config.BaseUrl}/api/integracao/{resource}?limit=1000";
        if (!string.IsNullOrWhiteSpace(q)) url += $"&q={Uri.EscapeDataString(q)}";

        using var resp = await SendAuthedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ErpException(await ReadErrorAsync(resp, ct).ConfigureAwait(false), (int)resp.StatusCode);

        var parsed = await resp.Content.ReadFromJsonAsync<ErpListResponse>(JsonOpts, ct).ConfigureAwait(false);
        return parsed?.Items ?? new List<ErpRef>();
    }

    public Task<IReadOnlyList<ErpRef>> GetMarcasAsync(string? q, CancellationToken ct) => ListAsync("marcas", q, ct);
    public Task<IReadOnlyList<ErpRef>> GetFornecedoresAsync(string? q, CancellationToken ct) => ListAsync("fornecedores", q, ct);
    public Task<IReadOnlyList<ErpRef>> GetLocalizacoesAsync(string? q, CancellationToken ct) => ListAsync("localizacoes", q, ct);

    /// <summary>
    /// Pedidos de compra abertos (aguardando recebimento de máquinas). Todo
    /// cadastro parte de um pedido — ver docs/relatorio-api-pedido-compra.md.
    /// </summary>
    public async Task<IReadOnlyList<ErpPedidoCompra>> GetPedidosCompraAsync(string? q, CancellationToken ct)
    {
        var url = $"{_config.BaseUrl}/api/integracao/pedidos-compra?status=aberto&limit=200";
        if (!string.IsNullOrWhiteSpace(q)) url += $"&q={Uri.EscapeDataString(q)}";

        using var resp = await SendAuthedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ErpException(await ReadErrorAsync(resp, ct).ConfigureAwait(false), (int)resp.StatusCode);

        var parsed = await resp.Content.ReadFromJsonAsync<ErpPedidoListResponse>(JsonOpts, ct).ConfigureAwait(false);
        return parsed?.Items ?? new List<ErpPedidoCompra>();
    }

    /// <summary>Máquinas de um pedido com a etapa do kanban (em branco, fila, execução).</summary>
    public async Task<ErpPedidoMaquinasResponse> GetPedidoMaquinasAsync(string pedidoId, CancellationToken ct)
    {
        var url = $"{_config.BaseUrl}/api/integracao/pedidos-compra/{Uri.EscapeDataString(pedidoId)}/maquinas";
        using var resp = await SendAuthedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ErpException(await ReadErrorAsync(resp, ct).ConfigureAwait(false), (int)resp.StatusCode);

        var parsed = await resp.Content.ReadFromJsonAsync<ErpPedidoMaquinasResponse>(JsonOpts, ct).ConfigureAwait(false);
        return parsed ?? new ErpPedidoMaquinasResponse();
    }

    /// <summary>
    /// Avança a máquina para a próxima etapa do kanban (endpoint PROPOSTO —
    /// responde 404 até o ERP implementar; ver docs/relatorio-api-kanban.md).
    /// </summary>
    public async Task<ErpKanbanAvancarResponse> AvancarKanbanAsync(
        ErpKanbanAvancarRequest req, string idempotencyKey, CancellationToken ct)
    {
        var url = $"{_config.BaseUrl}/api/integracao/kanban/avancar";
        using var resp = await SendAuthedAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(req, options: JsonOpts),
            };
            m.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            return m;
        }, ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ErpException(await ExtractErrorAsync(body, resp), (int)resp.StatusCode);

        var parsed = JsonSerializer.Deserialize<ErpKanbanAvancarResponse>(body, JsonOpts);
        if (parsed is null)
            throw new ErpException("Resposta inválida do avanço de etapa.", (int)resp.StatusCode);
        return parsed;
    }

    /// <summary>
    /// Confirma que a mercadoria chegou: a máquina sai de "aguardando recebimento"
    /// e entra no check de entrada. Corpo: { asset_id }.
    /// </summary>
    public async Task<ErpConfirmarRecebimentoResponse> ConfirmarRecebimentoAsync(
        string assetId, CancellationToken ct)
    {
        var url = $"{_config.BaseUrl}/api/integracao/confirmar-recebimento";
        using var resp = await SendAuthedAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { asset_id = assetId }, options: JsonOpts),
        }, ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ErpException(await ExtractErrorAsync(body, resp), (int)resp.StatusCode);

        var parsed = JsonSerializer.Deserialize<ErpConfirmarRecebimentoResponse>(body, JsonOpts);
        if (parsed is null)
            throw new ErpException("Resposta inválida da confirmação de recebimento.", (int)resp.StatusCode);
        return parsed;
    }

    /// <summary>
    /// Volta a máquina uma etapa no kanban. Ao voltar para fila/check o ERP
    /// limpa o assumido_por (o chamador limpa o cache local de técnico também).
    /// </summary>
    public async Task<ErpKanbanRetrocederResponse> RetrocederKanbanAsync(
        ErpKanbanRetrocederRequest req, string idempotencyKey, CancellationToken ct)
    {
        var url = $"{_config.BaseUrl}/api/integracao/kanban/retroceder";
        using var resp = await SendAuthedAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(req, options: JsonOpts),
            };
            m.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            return m;
        }, ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ErpException(await ExtractErrorAsync(body, resp), (int)resp.StatusCode);

        var parsed = JsonSerializer.Deserialize<ErpKanbanRetrocederResponse>(body, JsonOpts);
        if (parsed is null)
            throw new ErpException("Resposta inválida do retrocesso de etapa.", (int)resp.StatusCode);
        return parsed;
    }

    /// <summary>
    /// Fila de teste completo da organização (ordens de diagnóstico na fila ou
    /// em execução), independente do pedido de compra de origem.
    /// </summary>
    public async Task<IReadOnlyList<ErpFilaTesteMaquina>> GetFilaTesteAsync(CancellationToken ct)
    {
        var url = $"{_config.BaseUrl}/api/integracao/fila-teste";
        using var resp = await SendAuthedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ErpException(await ReadErrorAsync(resp, ct).ConfigureAwait(false), (int)resp.StatusCode);

        var parsed = await resp.Content.ReadFromJsonAsync<ErpFilaTesteResponse>(JsonOpts, ct).ConfigureAwait(false);
        return parsed?.Maquinas ?? new List<ErpFilaTesteMaquina>();
    }

    /// <summary>
    /// Reporta o check automático (teste completo): specs + detalhe por teste. A
    /// ordem de diagnóstico fica em <c>em_andamento</c> — quem a conclui é o
    /// "Dar OK" no ERP, depois que alguém confere o que foi reportado.
    /// </summary>
    public async Task<ErpAutocheckResponse> CreateAutocheckAsync(
        ErpAutocheckRequest req, string idempotencyKey, CancellationToken ct)
    {
        var url = $"{_config.BaseUrl}/api/integracao/autocheck";
        using var resp = await SendAuthedAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(req, options: JsonOpts),
            };
            m.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            return m;
        }, ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ErpException(await ExtractErrorAsync(body, resp), (int)resp.StatusCode);

        var parsed = JsonSerializer.Deserialize<ErpAutocheckResponse>(body, JsonOpts);
        if (parsed is null)
            throw new ErpException("Resposta inválida do autocheck.", (int)resp.StatusCode);
        return parsed;
    }

    public async Task<ErpFornecedorResponse> CreateFornecedorAsync(ErpFornecedorRequest req, CancellationToken ct)
    {
        var url = $"{_config.BaseUrl}/api/integracao/fornecedores";
        using var resp = await SendAuthedAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(req, options: JsonOpts),
        }, ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ErpException(await ExtractErrorAsync(body, resp), (int)resp.StatusCode);

        var parsed = JsonSerializer.Deserialize<ErpFornecedorResponse>(body, JsonOpts);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Id))
            throw new ErpException("Resposta inválida ao cadastrar fornecedor.", (int)resp.StatusCode);
        return parsed;
    }

    public async Task<ErpRecebimentoResponse> CreateRecebimentoAsync(
        ErpRecebimentoRequest req, string idempotencyKey, CancellationToken ct)
    {
        var url = $"{_config.BaseUrl}/api/integracao/recebimentos";
        using var resp = await SendAuthedAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(req, options: JsonOpts),
            };
            m.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            return m;
        }, ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ErpException(await ExtractErrorAsync(body, resp), (int)resp.StatusCode);

        var parsed = JsonSerializer.Deserialize<ErpRecebimentoResponse>(body, JsonOpts);
        if (parsed is null)
            throw new ErpException("Resposta inválida do recebimento.", (int)resp.StatusCode);
        return parsed;
    }

    private static Task<string> ExtractErrorAsync(string body, HttpResponseMessage resp)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return Task.FromResult(e.GetString() ?? body);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return Task.FromResult(m.GetString() ?? body);
        }
        catch { /* não-JSON */ }
        return Task.FromResult(string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)resp.StatusCode}" : body);
    }
}
