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

namespace NotebookCheck.Infrastructure.Persistence;

/// <summary>
/// Fila em disco das gravações de microfone que ainda não subiram para o ERP.
///
/// Separada da <see cref="FileOfflineQueue"/> de propósito: aquela guarda um
/// JSON por máquina (o relatório) e é indexada pelo <c>test_id</c>; aqui o
/// conteúdo é binário e pode haver mais de uma gravação para a mesma máquina.
///
/// Cada item é um par de arquivos — o <c>.json</c> com o carimbo da máquina
/// (<c>test_id</c>, NTB, serial) e o <c>.bin</c> com o áudio já comprimido. É o
/// carimbo que garante o que o dono pediu: <b>cada áudio vai para o check do
/// seu próprio PC</b>, mesmo tendo sido gravado dias antes.
/// </summary>
public sealed class FileAudioQueue : IAudioQueue
{
    /// <summary>Depois disso o item para de tentar (mas não é apagado).</summary>
    public const int MaxAttempts = 5;

    /// <summary>Itens mais velhos que isto são descartados na próxima varredura.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly string _dir;
    private readonly ILogger<FileAudioQueue> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public FileAudioQueue(ILogger<FileAudioQueue> logger, string? baseDir = null)
    {
        _dir = Path.Combine(baseDir ?? AppContext.BaseDirectory, "audio-queue");
        _logger = logger;
        Directory.CreateDirectory(_dir);
    }

    public int Count
    {
        get
        {
            try { return Directory.EnumerateFiles(_dir, "audio_*.json").Count(); }
            catch { return 0; }
        }
    }

    public async Task EnqueueAsync(PendingAudio item, byte[] bytes, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(item, JsonOpts);
        await File.WriteAllBytesAsync(BinPath(item.Id), bytes, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(JsonPath(item.Id), json, Encoding.UTF8, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Áudio {Id} enfileirado para o test {TestId} (NTB {Ntb}, {Bytes} bytes)",
            item.Id, item.TestId, item.Ntb ?? "—", bytes.Length);
    }

    public async Task<IReadOnlyList<PendingAudio>> ListAsync(CancellationToken ct)
    {
        var list = new List<PendingAudio>();
        if (!Directory.Exists(_dir)) return list;

        foreach (var file in Directory.EnumerateFiles(_dir, "audio_*.json").ToList())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var raw = await File.ReadAllTextAsync(file, Encoding.UTF8, ct).ConfigureAwait(false);
                var item = JsonSerializer.Deserialize<PendingAudio>(raw, JsonOpts);
                if (item is null) continue;

                // Velho demais ou sem o binário do lado: não adianta guardar.
                if (DateTime.UtcNow - item.EnqueuedAt > MaxAge || !File.Exists(BinPath(item.Id)))
                {
                    _logger.LogInformation("Áudio {Id} descartado (expirado ou sem binário)", item.Id);
                    Delete(item.Id);
                    continue;
                }
                list.Add(item);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha lendo item de áudio {File}", file);
            }
        }
        return list.OrderBy(i => i.EnqueuedAt).ToList();
    }

    public async Task<byte[]?> ReadBytesAsync(string id, CancellationToken ct)
    {
        try
        {
            var path = BinPath(id);
            if (!File.Exists(path)) return null;
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha lendo o áudio {Id}", id);
            return null;
        }
    }

    public Task RemoveAsync(string id, CancellationToken ct)
    {
        Delete(id);
        return Task.CompletedTask;
    }

    public async Task IncrementAttemptsAsync(string id, CancellationToken ct)
    {
        var path = JsonPath(id);
        if (!File.Exists(path)) return;
        try
        {
            var raw = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
            var item = JsonSerializer.Deserialize<PendingAudio>(raw, JsonOpts);
            if (item is null) return;
            var updated = item with { Attempts = item.Attempts + 1 };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(updated, JsonOpts), Encoding.UTF8, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha atualizando tentativas do áudio {Id}", id);
        }
    }

    private void Delete(string id)
    {
        foreach (var path in new[] { JsonPath(id), BinPath(id) })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { _logger.LogWarning(ex, "Falha removendo {File}", path); }
        }
    }

    private string JsonPath(string id) => Path.Combine(_dir, $"audio_{id}.json");
    private string BinPath(string id) => Path.Combine(_dir, $"audio_{id}.bin");
}
