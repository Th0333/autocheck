using System;
using System.Collections.Generic;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using NotebookCheck.Infrastructure.Abstractions;

namespace NotebookCheck.Infrastructure.Wmi;

/// <summary>
/// Implementação real de <see cref="IWmiQueryRunner"/> usando
/// <see cref="ManagementObjectSearcher"/>. Cada consulta é executada em uma
/// task em background com timeout explícito.
/// </summary>
public sealed class WmiQueryRunner : IWmiQueryRunner
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string scope,
        string wql,
        TimeSpan timeout,
        CancellationToken ct)
    {
        return Task.Run<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(() =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                cts.Token.ThrowIfCancellationRequested();

                using var searcher = new ManagementObjectSearcher(scope, wql);
                var results = new List<IReadOnlyDictionary<string, object?>>();

                using var collection = searcher.Get();
                foreach (ManagementBaseObject obj in collection)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData p in obj.Properties)
                    {
                        try { row[p.Name] = p.Value; } catch { row[p.Name] = null; }
                    }
                    results.Add(row);
                    obj.Dispose();
                }

                return (IReadOnlyList<IReadOnlyDictionary<string, object?>>)results;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new WmiQueryException(WmiFailureCause.Timeout, scope, wql);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new WmiQueryException(WmiFailureCause.AccessDenied, scope, wql, innerException: ex);
            }
            catch (ManagementException ex)
            {
                var cause = ex.ErrorCode switch
                {
                    ManagementStatus.AccessDenied => WmiFailureCause.AccessDenied,
                    ManagementStatus.NotFound => WmiFailureCause.NotPresent,
                    ManagementStatus.InvalidQuery => WmiFailureCause.InvalidQuery,
                    ManagementStatus.InvalidNamespace => WmiFailureCause.InvalidQuery,
                    ManagementStatus.InvalidClass => WmiFailureCause.NotPresent,
                    _ => WmiFailureCause.Unknown,
                };
                throw new WmiQueryException(cause, scope, wql, innerException: ex);
            }
            catch (Exception ex) when (ex is not WmiQueryException)
            {
                throw new WmiQueryException(WmiFailureCause.Unknown, scope, wql, innerException: ex);
            }
        }, ct);
    }
}
