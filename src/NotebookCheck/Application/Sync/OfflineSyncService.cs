using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotebookCheck.Bootstrap;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Infrastructure.Abstractions;

namespace NotebookCheck.Application.Sync;

/// <summary>
/// BackgroundService que tenta enviar payloads pendentes a cada 60s quando há
/// conectividade. Notifica observadores via evento <see cref="PendingChanged"/>.
/// </summary>
public sealed class OfflineSyncService : BackgroundService
{
    private readonly IOfflineQueue _queue;
    private readonly IApiClient _api;
    private readonly INetworkProbe _probe;
    private readonly AppConfig _config;
    private readonly IReportArchive _archive;
    private readonly ILogger<OfflineSyncService> _logger;

    public event EventHandler<int>? PendingChanged;

    public int CurrentPendingCount => _queue.Count;

    public OfflineSyncService(
        IOfflineQueue queue,
        IApiClient api,
        INetworkProbe probe,
        AppConfig config,
        IReportArchive archive,
        ILogger<OfflineSyncService> logger)
    {
        _queue = queue;
        _api = api;
        _probe = probe;
        _config = config;
        _archive = archive;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Notify();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_queue.Count > 0)
                {
                    var ok = false;
                    if (!string.IsNullOrWhiteSpace(_config.Options.InternetTestUrl))
                    {
                        var probe = await _probe.ProbeAsync(_config.Options.InternetTestUrl, TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                        ok = probe.Outcome == ProbeOutcome.Success;
                    }
                    else
                    {
                        ok = true;
                    }

                    if (ok)
                    {
                        var pending = await _queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
                        foreach (var item in pending)
                        {
                            stoppingToken.ThrowIfCancellationRequested();
                            var result = await _api.SendAsync(item.Payload, stoppingToken).ConfigureAwait(false);
                            switch (result.Outcome)
                            {
                                case ApiOutcome.Sent:
                                    await _queue.RemoveAsync(item.Payload.TestId, stoppingToken).ConfigureAwait(false);
                                    await _archive.MarkSyncedAsync(item.Payload.TestId, stoppingToken).ConfigureAwait(false);
                                    break;
                                case ApiOutcome.ValidationError:
                                    await _queue.RemoveAsync(item.Payload.TestId, stoppingToken).ConfigureAwait(false);
                                    break;
                                default:
                                    await _queue.IncrementAttemptsAsync(item.Payload.TestId, stoppingToken).ConfigureAwait(false);
                                    break;
                            }
                        }
                    }
                }
                Notify();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Loop de sync falhou");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Notify()
    {
        try { PendingChanged?.Invoke(this, _queue.Count); }
        catch { /* ignore */ }
    }
}
