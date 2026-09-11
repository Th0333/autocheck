using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Infrastructure.Persistence;

/// <summary>
/// Relatório completo do checklist que NÃO conseguiu subir para o ERP na hora
/// do envio. Um arquivo por máquina, chaveado pelo <c>test_id</c> (reenviar o
/// mesmo test_id substitui no ERP, então guardar só o último basta).
/// </summary>
/// <param name="TestId">test_id do relatório (nome do arquivo).</param>
/// <param name="Ntb">Código NTB, só para o log ficar legível.</param>
/// <param name="PayloadJson">O JSON exato que seria enviado.</param>
/// <param name="EnqueuedAt">Quando entrou na fila (UTC).</param>
/// <param name="Attempts">Tentativas já feitas pelo sync.</param>
public sealed record PendingErpReport(
    string TestId,
    string? Ntb,
    string PayloadJson,
    DateTime EnqueuedAt,
    int Attempts);

/// <summary>
/// Fila em disco dos relatórios que ainda não chegaram ao ERP.
///
/// Existe porque a <see cref="FileOfflineQueue"/> só reenvia para o painel
/// antigo: quando o ERP estava fora no momento do envio, o relatório (e as
/// fotos embutidas nele) nunca mais era tentado lá — o check aparecia no
/// painel e sumia do ERP. Mesma mecânica da <see cref="FileAudioQueue"/>:
/// pasta própria, um JSON por item, contador de tentativas e descarte por idade.
/// </summary>
public sealed class FileErpReportQueue
{
    /// <summary>Depois disso o item para de tentar (mas não é apagado).</summary>
    public const int MaxAttempts = 20;

    /// <summary>Itens mais velhos que isto são descartados na próxima varredura.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly string _dir;
    private readonly ILogger<FileErpReportQueue> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public FileErpReportQueue(ILogger<FileErpReportQueue> logger, string? baseDir = null)
    {
        _dir = Path.Combine(baseDir ?? AppContext.BaseDirectory, "erp-queue");
        _logger = logger;
        Directory.CreateDirectory(_dir);
    }

    public int Count
    {
        get
        {
            try { return Directory.EnumerateFiles(_dir, "erp_*.json").Count(); }
            catch { return 0; }
        }
    }

    /// <summary>Guarda (ou substitui) o relatório pendente daquele test_id.</summary>
    public async Task EnqueueAsync(string testId, string? ntb, string payloadJson, CancellationToken ct)
    {
        var item = new PendingErpReport(testId, ntb, payloadJson, DateTime.UtcNow, 0);
        var json = JsonSerializer.Serialize(item, JsonOpts);
        await File.WriteAllTextAsync(PathFor(testId), json, Encoding.UTF8, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Relatório {TestId} (NTB {Ntb}) enfileirado para o ERP ({Kb:0} KB)",
            testId, ntb ?? "—", payloadJson.Length / 1024.0);
    }

    public async Task<IReadOnlyList<PendingErpReport>> ListAsync(CancellationToken ct)
    {
        var list = new List<PendingErpReport>();
        if (!Directory.Exists(_dir)) return list;

        foreach (var file in Directory.EnumerateFiles(_dir, "erp_*.json").ToList())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, Encoding.UTF8, ct).ConfigureAwait(false);
                var item = JsonSerializer.Deserialize<PendingErpReport>(json, JsonOpts);
                if (item is null || string.IsNullOrWhiteSpace(item.TestId) || string.IsNullOrWhiteSpace(item.PayloadJson))
                {
                    TryDelete(file);
                    continue;
                }
                if (DateTime.UtcNow - item.EnqueuedAt > MaxAge)
                {
                    _logger.LogWarning("Relatório {TestId} na fila do ERP há mais de {Days} dias — descartado", item.TestId, MaxAge.TotalDays);
                    TryDelete(file);
                    continue;
                }
                list.Add(item);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Item ilegível na fila do ERP: {File}", file);
                TryDelete(file);
            }
        }
        return list.OrderBy(i => i.EnqueuedAt).ToList();
    }

    public Task RemoveAsync(string testId, CancellationToken ct)
    {
        TryDelete(PathFor(testId));
        return Task.CompletedTask;
    }

    public async Task IncrementAttemptsAsync(string testId, CancellationToken ct)
    {
        var path = PathFor(testId);
        if (!File.Exists(path)) return;
        try
        {
            var json = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
            var item = JsonSerializer.Deserialize<PendingErpReport>(json, JsonOpts);
            if (item is null) return;
            var updated = item with { Attempts = item.Attempts + 1 };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(updated, JsonOpts), Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Não deu para incrementar tentativas de {TestId}", testId);
        }
    }

    private string PathFor(string testId)
    {
        var safe = new string(testId.Where(ch => char.IsLetterOrDigit(ch) || ch == '-').ToArray());
        return Path.Combine(_dir, $"erp_{safe}.json");
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Falha apagando {Path}", path); }
    }
}
