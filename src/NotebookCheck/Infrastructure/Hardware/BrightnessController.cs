using System;
using System.Linq;
using System.Management;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Infrastructure.Hardware;

/// <summary>
/// Lê e ajusta o brilho do painel integrado via WMI (namespace <c>root\wmi</c>):
/// <c>WmiMonitorBrightness</c> para o valor atual + níveis suportados e
/// <c>WmiMonitorBrightnessMethods.WmiSetBrightness</c> para alterar.
///
/// Só funciona para o display interno de notebooks cujo driver expõe o controle
/// (a maioria expõe). Em desktops / monitores externos (que usam DDC/CI) o WMI
/// não responde e <see cref="IsSupported"/> fica <c>false</c>.
/// </summary>
public sealed class BrightnessController
{
    private readonly ILogger? _logger;

    public BrightnessController(ILogger? logger = null) => _logger = logger;

    /// <summary>Há um painel cujo brilho pode ser lido/ajustado por software?</summary>
    public bool IsSupported
    {
        get
        {
            try { return TryReadCurrent(out _, out _); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Brilho atual em 0–100 (ou -1 se indisponível) e os níveis suportados
    /// pelo driver (subconjunto de 0–100, em ordem crescente).
    /// </summary>
    public bool TryReadCurrent(out int current, out byte[] levels)
    {
        current = -1;
        levels = Array.Empty<byte>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\wmi", "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementObject mo in searcher.Get())
            {
                current = Convert.ToInt32(mo["CurrentBrightness"]);
                if (mo["Level"] is byte[] lv && lv.Length > 0)
                    levels = lv;
                mo.Dispose();
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "WmiMonitorBrightness indisponível");
        }
        return false;
    }

    /// <summary>
    /// Define o brilho (0–100). Faz "snap" para o nível suportado mais próximo
    /// quando o driver publica uma lista de níveis. Retorna o valor aplicado, ou
    /// -1 em falha.
    /// </summary>
    public int Set(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            // Aproxima do nível suportado mais próximo, se houver lista.
            if (TryReadCurrent(out _, out var levels) && levels.Length > 0)
                percent = levels.OrderBy(l => Math.Abs(l - percent)).First();

            using var searcher = new ManagementObjectSearcher(
                "root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject mo in searcher.Get())
            {
                // WmiSetBrightness(uint Timeout (s), byte Brightness)
                mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
                mo.Dispose();
                return percent;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Falha ao ajustar brilho para {Percent}%", percent);
        }
        return -1;
    }
}
