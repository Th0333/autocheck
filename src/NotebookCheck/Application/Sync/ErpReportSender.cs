using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NotebookCheck.Infrastructure.Erp;
using NotebookCheck.Infrastructure.Persistence;

namespace NotebookCheck.Application.Sync;

/// <summary>
/// Envio do relatório COMPLETO do checklist para o ERP com fila de reenvio.
///
/// Antes, o envio para o ERP era "tenta uma vez e esquece": ERP fora do ar na
/// hora do clique = check que nunca chegava lá (e as fotos da inspeção, que
/// viajam dentro dele, sumiam junto). Agora a falha vai para a
/// <see cref="FileErpReportQueue"/> e o <see cref="OfflineSyncService"/>
/// insiste a cada minuto, como já faz com o painel antigo e com os áudios.
/// </summary>
public sealed class ErpReportSender
{
    private readonly ErpClient _erp;
    private readonly FileErpReportQueue _queue;
    private readonly ILogger<ErpReportSender> _logger;

    public ErpReportSender(ErpClient erp, FileErpReportQueue queue, ILogger<ErpReportSender> logger)
    {
        _erp = erp;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>Quantos relatórios estão esperando para subir ao ERP.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// Tenta enviar agora; em falha de rede/servidor enfileira. Nunca lança por
    /// problema de rede — o relatório não se perde de qualquer jeito.
    /// </summary>
    /// <returns><c>Enviado</c> = chegou no ERP; false = ficou na fila.</returns>
    public async Task<(bool Enviado, string Mensagem)> SendAsync(
        string testId, string? ntb, string payloadJson, CancellationToken ct)
    {
        try
        {
            await _erp.SendChecklistReportAsync(payloadJson, ct).ConfigureAwait(false);
            // Se uma versão antiga deste test_id estava na fila, ela já foi
            // superada pelo que acabou de subir.
            await _queue.RemoveAsync(testId, ct).ConfigureAwait(false);
            _logger.LogInformation("Checklist {TestId} (NTB {Ntb}) salvo no ERP", testId, ntb ?? "—");
            return (true, "salvo no ERP");
        }
        catch (ErpException ex) when (NaoAdiantaRepetir(ex.StatusCode))
        {
            // O ERP recusou o conteúdo: repetir dá no mesmo. Fica no log para
            // investigar, mas não trava a fila.
            _logger.LogWarning("ERP recusou o checklist {TestId} ({Status}: {Msg})", testId, ex.StatusCode, ex.Message);
            return (false, $"ERP recusou o checklist ({ex.Message})");
        }
        catch (Exception ex)
        {
            try
            {
                await _queue.EnqueueAsync(testId, ntb, payloadJson, ct).ConfigureAwait(false);
                _logger.LogWarning(ex, "Checklist {TestId} não subiu pro ERP; ficou na fila", testId);
                return (false, $"ERP indisponível — o checklist ficou na fila ({_queue.Count}) e sobe sozinho depois");
            }
            catch (Exception queueEx)
            {
                _logger.LogError(queueEx, "Não foi possível nem enviar nem enfileirar o checklist {TestId}", testId);
                return (false, $"ERP indisponível e não deu para guardar na fila ({ex.Message})");
            }
        }
    }

    /// <summary>Tenta subir tudo que está na fila.</summary>
    public async Task DrainAsync(CancellationToken ct)
    {
        if (!_erp.IsConfigured || _queue.Count == 0) return;

        var pendentes = await _queue.ListAsync(ct).ConfigureAwait(false);
        foreach (var item in pendentes)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Attempts >= FileErpReportQueue.MaxAttempts) continue;

            try
            {
                await _erp.SendChecklistReportAsync(item.PayloadJson, ct).ConfigureAwait(false);
                await _queue.RemoveAsync(item.TestId, ct).ConfigureAwait(false);
                _logger.LogInformation("Checklist {TestId} (NTB {Ntb}) da fila subiu pro ERP", item.TestId, item.Ntb ?? "—");
            }
            catch (ErpException ex) when (NaoAdiantaRepetir(ex.StatusCode))
            {
                _logger.LogWarning(
                    "Checklist {TestId} recusado pelo ERP ({Status}: {Msg}) — descartado da fila",
                    item.TestId, ex.StatusCode, ex.Message);
                await _queue.RemoveAsync(item.TestId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Checklist {TestId} falhou de novo; fica na fila", item.TestId);
                await _queue.IncrementAttemptsAsync(item.TestId, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 4xx é culpa do conteúdo, então repetir dá no mesmo — exceto 401 (token
    /// expirado), 408 e 429, que passam com o tempo.
    /// </summary>
    private static bool NaoAdiantaRepetir(int? status) =>
        status is >= 400 and < 500 && status is not (401 or 408 or 429);
}
