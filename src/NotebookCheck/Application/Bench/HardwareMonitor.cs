using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Application.Bench;

/// <summary>
/// Wrapper sobre LibreHardwareMonitorLib que entrega snapshots de
/// temperatura, clocks, power e uso de CPU/GPU em tempo real.
///
/// A lib usa o driver WinRing0 embutido — sem instalação separada. O .exe
/// precisa rodar como administrador para acessar MSRs (já temos
/// requestedExecutionLevel=highestAvailable no manifest).
///
/// Uso: <code>using var mon = new HardwareMonitor(logger); mon.Update(); ...</code>
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly ILogger _logger;

    public HardwareMonitor(ILogger logger, bool monitorFans = false)
    {
        _logger = logger;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = false,
            // Placa-mãe (SuperIO) traz as ventoinhas de CPU/chassi — só habilita
            // quando pedido (tela de sensores ao vivo), pra não pesar no stress.
            IsMotherboardEnabled = monitorFans,
        };
        try
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha abrindo HardwareMonitor — driver indisponível");
        }
    }

    /// <summary>
    /// Atualiza todos os sensores e devolve um snapshot. Chamar a cada tick
    /// do loop de coleta (ex.: a cada 250ms durante stress).
    /// </summary>
    public HardwareSnapshot Sample()
    {
        var ts = DateTime.UtcNow;
        double cpuTempMax = double.NaN, cpuTempPkg = double.NaN, cpuPower = double.NaN;
        // Nominal = "Core #N" (no Zen costuma travar no alvo de boost, ~5 GHz,
        // mesmo em idle). Efetivo = "Effective Clock" (frequência real ao vivo).
        double cpuNominalMax = double.NaN, cpuEffMax = double.NaN;
        double cpuCoreSum = 0; int cpuCoreCount = 0;
        double cpuLoadAvg = double.NaN;
        var fans = new List<(string Name, double Rpm)>();

        // Por GPU: (prioridade, leitura). Prioridade: 3 = NVIDIA/AMD dedicada,
        // 1 = Intel iGPU. Pegamos a leitura com maior prioridade no final.
        // Empate de prioridade → vence quem tiver maior carga (load).
        var gpuReadings = new System.Collections.Generic.List<GpuReading>();

        try
        {
            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();

                if (hw.HardwareType == HardwareType.Cpu)
                {
                    foreach (var sensor in hw.Sensors)
                    {
                        if (sensor.Value is not float value) continue;

                        if (sensor.SensorType == SensorType.Temperature)
                        {
                            // Ignora leituras inválidas (0 ou >=130°C = sensor sem dado).
                            if (value <= 0f || value >= 130f) continue;
                            if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                                || sensor.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                            {
                                cpuTempPkg = value;
                            }
                            cpuTempMax = double.IsNaN(cpuTempMax) ? value : Math.Max(cpuTempMax, value);
                        }
                        else if (sensor.SensorType == SensorType.Clock)
                        {
                            // Aceita qualquer clock relacionado a Core/CPU
                            // — exceto o "Bus Speed" que é base FSB e não
                            // representa frequência real do core.
                            // Casos cobertos: "CPU Core #1", "Core #1",
                            // "Effective Clock 1", "CPU Clock", "Core 1".
                            if (sensor.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
                                continue;
                            // Pula clocks de memória/uncore que aparecem em CPUs
                            if (sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase)
                                || sensor.Name.Contains("Uncore", StringComparison.OrdinalIgnoreCase)
                                || sensor.Name.Contains("Fabric", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (value < 100) continue; // valor inválido
                            if (sensor.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase))
                            {
                                cpuEffMax = double.IsNaN(cpuEffMax) ? value : Math.Max(cpuEffMax, value);
                            }
                            else if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                                  || sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                            {
                                cpuNominalMax = double.IsNaN(cpuNominalMax) ? value : Math.Max(cpuNominalMax, value);
                                cpuCoreSum += value; cpuCoreCount++;
                            }
                        }
                        else if (sensor.SensorType == SensorType.Power
                            && sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        {
                            cpuPower = value;
                        }
                        else if (sensor.SensorType == SensorType.Load
                            && sensor.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase))
                        {
                            cpuLoadAvg = value;
                        }
                    }
                }
                else if (hw.HardwareType == HardwareType.GpuNvidia
                      || hw.HardwareType == HardwareType.GpuAmd
                      || hw.HardwareType == HardwareType.GpuIntel)
                {
                    var reading = ReadGpu(hw);
                    if (reading is not null) gpuReadings.Add(reading);
                }

                // Ventoinhas: CPU/chassi vêm do SuperIO (subhardware da placa-mãe);
                // a GPU também expõe fan. Coleta todas as não-zero.
                foreach (var s in hw.Sensors)
                    if (s.SensorType == SensorType.Fan && s.Value is float fv && fv > 0)
                        fans.Add((s.Name, fv));
                foreach (var sub in hw.SubHardware)
                    foreach (var s in sub.Sensors)
                        if (s.SensorType == SensorType.Fan && s.Value is float fv2 && fv2 > 0)
                            fans.Add((s.Name, fv2));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha lendo sensores");
        }

        // Escolhe a "GPU principal":
        //   1. Maior prioridade (dGPU > iGPU)
        //   2. Em caso de empate, maior carga
        //   3. Em segundo empate, maior temperatura (faz sentido pra stress)
        GpuReading? primary = null;
        foreach (var r in gpuReadings)
        {
            if (primary is null) { primary = r; continue; }
            if (r.Priority > primary.Priority) { primary = r; continue; }
            if (r.Priority == primary.Priority)
            {
                var rl = double.IsNaN(r.LoadPercent) ? -1 : r.LoadPercent;
                var pl = double.IsNaN(primary.LoadPercent) ? -1 : primary.LoadPercent;
                if (rl > pl) { primary = r; continue; }
                if (Math.Abs(rl - pl) < 0.001)
                {
                    var rt = double.IsNaN(r.TempC) ? -1 : r.TempC;
                    var pt = double.IsNaN(primary.TempC) ? -1 : primary.TempC;
                    if (rt > pt) primary = r;
                }
            }
        }

        // Clock exibido = efetivo (real) quando disponível; senão, o nominal.
        var cpuClockMax = !double.IsNaN(cpuEffMax) ? cpuEffMax : cpuNominalMax;
        var cpuClockAvg = cpuCoreCount > 0 ? cpuCoreSum / cpuCoreCount : double.NaN;

        return new HardwareSnapshot(
            Timestamp: ts,
            CpuTempPackage: cpuTempPkg,
            CpuTempMax: cpuTempMax,
            CpuClockMaxMhz: cpuClockMax,
            CpuClockAvgMhz: cpuClockAvg,
            CpuPowerWatts: cpuPower,
            CpuLoadPercent: cpuLoadAvg,
            GpuTempC: primary?.TempC ?? double.NaN,
            GpuHotspotC: primary?.HotspotC ?? double.NaN,
            GpuVramTempC: primary?.VramTempC ?? double.NaN,
            GpuPowerWatts: primary?.PowerWatts ?? double.NaN,
            GpuCoreClockMhz: primary?.CoreClockMhz ?? double.NaN,
            GpuMemClockMhz: primary?.MemClockMhz ?? double.NaN,
            GpuLoadPercent: primary?.LoadPercent ?? double.NaN,
            GpuVramUsageMb: primary?.VramUsageMb ?? double.NaN,
            GpuFanRpm: primary?.FanRpm ?? double.NaN,
            Fans: fans);
    }

    /// <summary>
    /// Lê todos os sensores de uma GPU específica e devolve um <see cref="GpuReading"/>
    /// com prioridade pra desempate. dGPU NVIDIA/AMD vence iGPU Intel.
    /// </summary>
    private static GpuReading? ReadGpu(IHardware hw)
    {
        var name = hw.Name ?? string.Empty;
        var priority = hw.HardwareType switch
        {
            HardwareType.GpuNvidia => 3,
            HardwareType.GpuAmd => name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("Vega ", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("UMA", StringComparison.OrdinalIgnoreCase)
                ? 2 // AMD iGPU em APUs (Vega, RDNA2 integrada)
                : 3, // dGPU Radeon RX/Pro
            HardwareType.GpuIntel => 1,
            _ => 0,
        };

        double tempMax = double.NaN, hotspot = double.NaN, vramTemp = double.NaN;
        double power = double.NaN, coreClk = double.NaN, memClk = double.NaN;
        double load = double.NaN, vramMb = double.NaN, fan = double.NaN;

        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Value is not float value) continue;

            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    // Descarta leituras fisicamente impossíveis: alguns drivers
                    // (sobretudo iGPU Intel) devolvem 255 (0xFF) ou 0 quando o
                    // sensor não está disponível, o que aparecia como "255°C".
                    if (value <= 0f || value >= 130f) break;
                    if (sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                        || sensor.Name.Contains("Junction", StringComparison.OrdinalIgnoreCase))
                    {
                        hotspot = value;
                    }
                    else if (sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase)
                          || sensor.Name.Contains("VRAM", StringComparison.OrdinalIgnoreCase))
                    {
                        vramTemp = value;
                    }
                    else
                    {
                        // GPU Core / GPU
                        tempMax = double.IsNaN(tempMax) ? value : Math.Max(tempMax, value);
                    }
                    break;
                case SensorType.Clock:
                    if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        coreClk = value;
                    else if (sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                        memClk = value;
                    break;
                case SensorType.Power:
                    if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                        || sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                    {
                        power = value;
                    }
                    break;
                case SensorType.Load:
                    if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                        || sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                    {
                        load = value;
                    }
                    break;
                case SensorType.SmallData:
                    if (sensor.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase))
                        vramMb = value;
                    break;
                case SensorType.Fan:
                    fan = value;
                    break;
            }
        }

        // Se a GPU não devolveu nem temperatura nem load, descarta — provavelmente
        // é um adapter virtual ou a lib não conseguiu abrir os sensores.
        if (double.IsNaN(tempMax) && double.IsNaN(hotspot) && double.IsNaN(load))
            return null;

        return new GpuReading(name, priority, tempMax, hotspot, vramTemp, power,
            coreClk, memClk, load, vramMb, fan);
    }

    private sealed record GpuReading(
        string Name, int Priority,
        double TempC, double HotspotC, double VramTempC,
        double PowerWatts, double CoreClockMhz, double MemClockMhz,
        double LoadPercent, double VramUsageMb, double FanRpm);

    public void Dispose()
    {
        try { _computer.Close(); } catch { /* ignore */ }
    }
}

/// <summary>Snapshot de uma leitura única dos sensores. NaN = sensor indisponível.</summary>
public record HardwareSnapshot(
    DateTime Timestamp,
    double CpuTempPackage,
    double CpuTempMax,
    double CpuClockMaxMhz,
    double CpuClockAvgMhz,
    double CpuPowerWatts,
    double CpuLoadPercent,
    double GpuTempC,
    double GpuHotspotC,
    double GpuVramTempC,
    double GpuPowerWatts,
    double GpuCoreClockMhz,
    double GpuMemClockMhz,
    double GpuLoadPercent,
    double GpuVramUsageMb,
    double GpuFanRpm,
    IReadOnlyList<(string Name, double Rpm)>? Fans = null);

/// <summary>Análise estatística de uma série de snapshots — usada para detectar throttling.</summary>
public static class SnapshotAnalysis
{
    /// <summary>
    /// Calcula a estabilidade dos clocks da CPU comparando a média dos
    /// primeiros 20% da série com os últimos 20%. Se cair >10%, há
    /// throttling (térmico ou power). Score 0..100 onde 100 = sem queda.
    /// </summary>
    public static double CpuClockStability(IReadOnlyList<HardwareSnapshot> samples)
    {
        if (samples.Count < 10) return double.NaN;
        var valid = samples.Where(s => !double.IsNaN(s.CpuClockMaxMhz)).ToList();
        if (valid.Count < 10) return double.NaN;

        var head = valid.Take(valid.Count / 5).Average(s => s.CpuClockMaxMhz);
        var tail = valid.Skip(valid.Count * 4 / 5).Average(s => s.CpuClockMaxMhz);
        if (head <= 0) return double.NaN;
        var ratio = tail / head;
        return Math.Clamp(ratio * 100, 0, 100);
    }

    public static double GpuClockStability(IReadOnlyList<HardwareSnapshot> samples)
    {
        if (samples.Count < 10) return double.NaN;
        var valid = samples.Where(s => !double.IsNaN(s.GpuCoreClockMhz) && s.GpuCoreClockMhz > 0).ToList();
        if (valid.Count < 10) return double.NaN;
        var head = valid.Take(valid.Count / 5).Average(s => s.GpuCoreClockMhz);
        var tail = valid.Skip(valid.Count * 4 / 5).Average(s => s.GpuCoreClockMhz);
        if (head <= 0) return double.NaN;
        return Math.Clamp(tail / head * 100, 0, 100);
    }

    public static double MaxCpuTemp(IReadOnlyList<HardwareSnapshot> samples)
        => samples.Where(s => !double.IsNaN(s.CpuTempMax)).Select(s => s.CpuTempMax).DefaultIfEmpty(double.NaN).Max();

    public static double MaxGpuTemp(IReadOnlyList<HardwareSnapshot> samples)
    {
        var hot = samples.Where(s => !double.IsNaN(s.GpuHotspotC)).Select(s => s.GpuHotspotC).DefaultIfEmpty(double.NaN).Max();
        if (!double.IsNaN(hot)) return hot;
        return samples.Where(s => !double.IsNaN(s.GpuTempC)).Select(s => s.GpuTempC).DefaultIfEmpty(double.NaN).Max();
    }
}
