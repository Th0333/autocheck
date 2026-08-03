using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Infrastructure.Audio;
using NotebookCheck.Infrastructure.Erp;
using NotebookCheck.Infrastructure.Persistence;

namespace NotebookCheck.Application.Sync;

/// <summary>
/// Junta as três pontas do envio da gravação do microfone: comprimir, tentar
/// subir e, se não der, guardar na fila com o carimbo da máquina.
///
/// Existe para a <c>MainViewModel</c> e o <see cref="OfflineSyncService"/>
/// compartilharem a mesma regra — o painel do microfone só chama
/// <see cref="SendAsync"/> e mostra a frase que voltar.
/// </summary>
public sealed class ChecklistAudioSender
{
    private readonly ErpClient _erp;
    private readonly IAudioQueue _queue;
    private readonly ILogger<ChecklistAudioSender> _logger;

    public ChecklistAudioSender(ErpClient erp, IAudioQueue queue, ILogger<ChecklistAudioSender> logger)
    {
        _erp = erp;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>Quantas gravações estão esperando para subir.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// Comprime e tenta enviar agora. Em falha, enfileira — nunca lança por
    /// problema de rede, porque a gravação não se perde de qualquer jeito.
    /// </summary>
    /// <returns>
    /// <c>Enviado</c> distingue "chegou no ERP" de "ficou na fila": o painel
    /// precisa disso para não escrever "enviado" num áudio que só foi guardado.
    /// </returns>
    public async Task<(bool Enviado, string Mensagem)> SendAsync(
        Guid testId, string? ntb, string? serial, byte[] wav, double duracaoSeg, CancellationToken ct)
    {
        var pronto = AudioCompressor.ForUpload(wav);
        var kb = pronto.Bytes.Length / 1024.0;

        try
        {
            await _erp.UploadChecklistAudioAsync(
                testId.ToString(), ntb, serial,
                pronto.Bytes, pronto.Mime, pronto.Extension, duracaoSeg, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Áudio do microfone enviado ao ERP (test {TestId}, {Kb:0} KB, {Fmt})",
                testId, kb, pronto.Extension);
            return (true, $"Enviado ao ERP ({kb:0} KB).");
        }
        catch (Exception ex)
        {
            var item = new PendingAudio(
                Id: Guid.NewGuid().ToString("N"),
                TestId: testId.ToString(),
                Ntb: ntb,
                Serial: serial,
                Mime: pronto.Mime,
                Extension: pronto.Extension,
                DuracaoSeg: duracaoSeg,
                EnqueuedAt: DateTime.UtcNow,
                Attempts: 0);

            try
            {
                await _queue.EnqueueAsync(item, pronto.Bytes, ct).ConfigureAwait(false);
                _logger.LogWarning(ex, "Envio do áudio falhou; ficou na fila (test {TestId})", testId);
                return (false,
                    $"Sem conexão com o ERP — a gravação ficou na fila ({_queue.Count}) e sobe sozinha depois.");
            }
            catch (Exception queueEx)
            {
                // Falhou enviar E falhou guardar: aí sim o técnico precisa saber.
                _logger.LogError(queueEx, "Não foi possível nem enviar nem enfileirar o áudio");
                throw new InvalidOperationException(
                    $"não deu para enviar nem guardar na fila ({ex.Message})", queueEx);
            }
        }
    }

    /// <summary>
    /// Tenta subir tudo que está na fila. Cada item já sabe de que máquina é,
    /// então vai para o check certo mesmo tendo sido gravado dias antes.
    /// </summary>
    public async Task DrainAsync(CancellationToken ct)
    {
        if (!_erp.IsConfigured || _queue.Count == 0) return;

        var pendentes = await _queue.ListAsync(ct).ConfigureAwait(false);
        foreach (var item in pendentes)
        {
            ct.ThrowIfCancellationRequested();

            // Já tentou demais: fica guardado, mas para de insistir a cada minuto.
            if (item.Attempts >= FileAudioQueue.MaxAttempts) continue;

            var bytes = await _queue.ReadBytesAsync(item.Id, ct).ConfigureAwait(false);
            if (bytes is null or { Length: 0 })
            {
                await _queue.RemoveAsync(item.Id, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await _erp.UploadChecklistAudioAsync(
                    item.TestId, item.Ntb, item.Serial,
                    bytes, item.Mime, item.Extension, item.DuracaoSeg, ct)
                    .ConfigureAwait(false);

                await _queue.RemoveAsync(item.Id, ct).ConfigureAwait(false);
                _logger.LogInformation("Áudio {Id} da fila subiu para o test {TestId}", item.Id, item.TestId);
            }
            catch (ErpException ex) when (NaoAdiantaRepetir(ex.StatusCode))
            {
                // Arquivo recusado, formato errado, test_id inválido: insistir
                // não conserta, e o item ficaria travando a fila para sempre.
                _logger.LogWarning(
                    "Áudio {Id} recusado pelo ERP ({Status}: {Msg}) — descartado",
                    item.Id, ex.StatusCode, ex.Message);
                await _queue.RemoveAsync(item.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Áudio {Id} falhou de novo; fica na fila", item.Id);
                await _queue.IncrementAttemptsAsync(item.Id, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 4xx é culpa do que foi mandado, então repetir dá no mesmo — exceto 401
    /// (token expirado), 408 e 429, que passam com o tempo.
    /// </summary>
    private static bool NaoAdiantaRepetir(int? status) =>
        status is >= 400 and < 500 && status is not (401 or 408 or 429);
}
