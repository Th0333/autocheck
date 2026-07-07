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
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Infrastructure.Api;

namespace NotebookCheck.Infrastructure.Persistence;

/// <summary>
/// Implementação do arquivo consolidado em <c>reports.json</c>. Toda
/// gravação é serializada via um <see cref="SemaphoreSlim"/> e feita de forma
/// atômica (escreve em <c>.tmp</c> e renomeia) para preservar o arquivo
/// quando o pendrive é removido durante uma operação.
/// </summary>
public sealed class JsonReportArchive : IReportArchive
{
    private readonly ILogger<JsonReportArchive> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ArchivePath { get; }

    public JsonReportArchive(ILogger<JsonReportArchive> logger, string? baseDir = null)
    {
        _logger = logger;
        var dir = baseDir ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(dir);
        ArchivePath = Path.Combine(dir, "reports.json");
    }

    public async Task AppendAsync(ApiPayload payload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await LoadInternalAsync(ct).ConfigureAwait(false);

            // Idempotência: se test_id já existe, não duplica.
            var existing = doc.Reports.FindIndex(e => e.TestId == payload.TestId);
            if (existing >= 0)
            {
                // Atualiza o payload (caso o relatório tenha sido sobrescrito antes do envio)
                doc.Reports[existing] = doc.Reports[existing] with { Payload = payload };
            }
            else
            {
                doc.Reports.Add(new LocalReportEntry(
                    TestId: payload.TestId,
                    ReceivedAt: DateTime.Now.ToString("o"),
                    SyncedAt: null,
                    Payload: payload));
            }

            await SaveInternalAsync(doc, ct).ConfigureAwait(false);
            _logger.LogInformation("Relatório {TestId} arquivado em {File}", payload.TestId, ArchivePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkSyncedAsync(string testId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(testId)) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await LoadInternalAsync(ct).ConfigureAwait(false);
            var idx = doc.Reports.FindIndex(e => e.TestId == testId);
            if (idx < 0) return;

            doc.Reports[idx] = doc.Reports[idx] with { SyncedAt = DateTime.Now.ToString("o") };
            await SaveInternalAsync(doc, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalReportEntry>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await LoadInternalAsync(ct).ConfigureAwait(false);
            return doc.Reports
                .OrderByDescending(e => e.ReceivedAt, StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ArchiveDoc> LoadInternalAsync(CancellationToken ct)
    {
        if (!File.Exists(ArchivePath)) return new ArchiveDoc();

        try
        {
            var raw = await File.ReadAllTextAsync(ArchivePath, Encoding.UTF8, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return new ArchiveDoc();
            return JsonSerializer.Deserialize<ArchiveDoc>(raw, _options) ?? new ArchiveDoc();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Arquivo de relatórios corrompido — começando do zero");
            return new ArchiveDoc();
        }
    }

    private async Task SaveInternalAsync(ArchiveDoc doc, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(doc, _options);
        var tmp = ArchivePath + ".tmp";
        await File.WriteAllTextAsync(tmp, json, Encoding.UTF8, ct).ConfigureAwait(false);

        // Move atômico — em pendrives funciona como rename.
        File.Move(tmp, ArchivePath, overwrite: true);
    }

    /// <summary>Documento raiz serializado em <c>reports.json</c>.</summary>
    private sealed class ArchiveDoc
    {
        public int Version { get; set; } = 1;
        public List<LocalReportEntry> Reports { get; set; } = new();
    }
}
