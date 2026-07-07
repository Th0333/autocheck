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
/// Fila offline FIFO baseada em arquivos JSON em <c>./queue/</c>.
/// </summary>
public sealed class FileOfflineQueue : IOfflineQueue
{
    private readonly string _dir;
    private readonly ILogger<FileOfflineQueue> _logger;
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public FileOfflineQueue(ILogger<FileOfflineQueue> logger, string? baseDir = null)
    {
        _dir = Path.Combine(baseDir ?? AppContext.BaseDirectory, "queue");
        _logger = logger;
        Directory.CreateDirectory(_dir);
    }

    public int Count
    {
        get
        {
            try { return Directory.EnumerateFiles(_dir, "pending_*.json").Count(); }
            catch { return 0; }
        }
    }

    public async Task EnqueueAsync(ApiPayload payload, CancellationToken ct)
    {
        var envelope = new Envelope(payload, DateTime.UtcNow, 0);
        var json = JsonSerializer.Serialize(envelope, _options);
        var path = Path.Combine(_dir, $"pending_{payload.TestId}.json");
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct).ConfigureAwait(false);
        _logger.LogInformation("Payload enfileirado em {File}", path);
    }

    public async Task<IReadOnlyList<QueuedPayload>> DequeueAsync(CancellationToken ct)
    {
        var list = new List<QueuedPayload>();
        if (!Directory.Exists(_dir)) return list;

        var files = Directory.EnumerateFiles(_dir, "pending_*.json").ToList();
        foreach (var file in files)
        {
            try
            {
                var raw = await File.ReadAllTextAsync(file, Encoding.UTF8, ct).ConfigureAwait(false);
                var env = JsonSerializer.Deserialize<Envelope>(raw, _options);
                if (env is not null)
                {
                    list.Add(new QueuedPayload(env.Payload, env.EnqueuedAt, env.Attempts));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha lendo {File}", file);
            }
        }
        return list.OrderBy(q => q.EnqueuedAt).ToList();
    }

    public Task RemoveAsync(string testId, CancellationToken ct)
    {
        var path = Path.Combine(_dir, $"pending_{testId}.json");
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Falha removendo {File}", path); }
        return Task.CompletedTask;
    }

    public async Task IncrementAttemptsAsync(string testId, CancellationToken ct)
    {
        var path = Path.Combine(_dir, $"pending_{testId}.json");
        if (!File.Exists(path)) return;
        try
        {
            var raw = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
            var env = JsonSerializer.Deserialize<Envelope>(raw, _options);
            if (env is null) return;
            var updated = env with { Attempts = env.Attempts + 1 };
            var json = JsonSerializer.Serialize(updated, _options);
            await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha atualizando attempts {File}", path);
        }
    }

    private sealed record Envelope(ApiPayload Payload, DateTime EnqueuedAt, int Attempts);
}
