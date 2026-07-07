using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Infrastructure.Abstractions;

namespace NotebookCheck.Infrastructure.Hardware;

/// <summary>
/// Implementação heurística do <see cref="IKeyboardBacklightDetector"/>.
/// Estratégia em camadas (timeout total 10s), retornando <c>Indisponivel</c>
/// na primeira falha.
/// </summary>
public sealed class KeyboardBacklightDetector : IKeyboardBacklightDetector
{
    private readonly IPowerShellRunner _ps;
    private readonly ILogger<KeyboardBacklightDetector> _logger;

    public KeyboardBacklightDetector(IPowerShellRunner ps, ILogger<KeyboardBacklightDetector> logger)
    {
        _ps = ps;
        _logger = logger;
    }

    public async Task<KeyboardBacklight> DetectAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var token = cts.Token;

        // Camada 1: Registry HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Lighting
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Lighting");
            if (key is not null)
            {
                return KeyboardBacklight.Sim;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Registry Lighting falhou");
        }

        // Camada 2: PowerShell PnP devices com nome contendo "backlight"/"keyboard"
        try
        {
            var rows = await _ps.InvokeAsync(
                "Get-PnpDevice -PresentOnly | Where-Object { $_.FriendlyName -match 'backlight|retroilumin|illumination' } | Select-Object -First 5 FriendlyName",
                null, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);

            if (rows.Count > 0)
            {
                return KeyboardBacklight.Sim;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Get-PnpDevice backlight check falhou");
        }

        // Camada 3: heurísticas OEM via WMI/Registry (best-effort, sempre indisponível)
        return KeyboardBacklight.Indisponivel;
    }
}
