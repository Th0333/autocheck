using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Threading.Tasks;
using NotebookCheck.Infrastructure.Abstractions;

namespace NotebookCheck.Infrastructure.PowerShell;

/// <summary>
/// Implementação real de <see cref="IPowerShellRunner"/> usando
/// <see cref="System.Management.Automation"/> (Microsoft.PowerShell.SDK).
/// </summary>
public sealed class PowerShellRunner : IPowerShellRunner
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> InvokeAsync(
        string script,
        IReadOnlyDictionary<string, object?>? parameters,
        TimeSpan timeout,
        CancellationToken ct)
    {
        return Task.Run<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(() =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var iss = InitialSessionState.CreateDefault2();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();

            using var ps = System.Management.Automation.PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(script);
            if (parameters is not null)
            {
                foreach (var kvp in parameters)
                {
                    ps.AddParameter(kvp.Key, kvp.Value);
                }
            }

            var asyncResult = ps.BeginInvoke();
            try
            {
                if (!asyncResult.AsyncWaitHandle.WaitOne(timeout))
                {
                    try { ps.Stop(); } catch { /* ignore */ }
                    throw new PowerShellExecutionException(script, Array.Empty<string>(), timedOut: true);
                }

                var output = ps.EndInvoke(asyncResult);
                if (ps.HadErrors)
                {
                    var errs = ps.Streams.Error
                        .Select(e => e.Exception?.Message ?? e.ToString())
                        .ToList();
                    throw new PowerShellExecutionException(script, errs, timedOut: false);
                }

                var rows = new List<IReadOnlyDictionary<string, object?>>();
                foreach (var item in output)
                {
                    if (item is null) continue;
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    PSMemberInfoCollection<PSPropertyInfo>? props = null;
                    try { props = item.Properties; } catch { /* algumas instâncias do SDK lançam aqui */ }
                    if (props is not null)
                    {
                        foreach (var p in props)
                        {
                            try { row[p.Name] = p.Value; } catch { row[p.Name] = null; }
                        }
                    }
                    rows.Add(row);
                }
                return rows;
            }
            catch (PowerShellExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PowerShellExecutionException(script, new[] { ex.Message }, timedOut: false, ex);
            }
        }, ct);
    }
}
