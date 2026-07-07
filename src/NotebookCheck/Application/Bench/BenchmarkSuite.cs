using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;

namespace NotebookCheck.Application.Bench;

/// <summary>
/// Quais testes da suite rodar. Combinável via flags — o modal monta a
/// seleção a partir dos checkboxes do técnico.
/// </summary>
[Flags]
public enum BenchTests
{
    None = 0,
    Cpu = 1,
    Gpu = 2,
    Disk = 4,
    Vram = 8,
    Ram = 16,
    All = Cpu | Gpu | Disk | Vram | Ram,
}

/// <summary>
/// Fachada que orquestra a suite completa de benchmark + stress.
/// Cada teste é independente — o caller escolhe quais rodar via
/// <see cref="BenchTests"/>.
/// </summary>
public sealed class BenchmarkSuite
{
    private readonly ILogger _logger;
    public CpuBenchmark Cpu { get; }
    public CpuStress CpuStressTest { get; }
    public GpuBenchmark Gpu { get; }
    public GpuStress GpuStressTest { get; }
    public VramStress VramStressTest { get; }
    public DiskBenchmark Disk { get; }
    public RamStress RamStressTest { get; }

    public BenchmarkSuite(ILogger<BenchmarkSuite> logger)
    {
        _logger = logger;
        Cpu = new CpuBenchmark(logger);
        CpuStressTest = new CpuStress(logger);
        Gpu = new GpuBenchmark(logger);
        GpuStressTest = new GpuStress(logger);
        VramStressTest = new VramStress(logger);
        Disk = new DiskBenchmark(logger);
        RamStressTest = new RamStress(logger);
    }

    /// <summary>
    /// Roda os testes selecionados em <paramref name="tests"/>, na ordem
    /// CPU → GPU → Disco → VRAM → RAM. Os testes não selecionados ficam null
    /// no resultado.
    /// </summary>
    public async Task<BenchmarkOutcome> RunSelectedAsync(
        BenchTests tests,
        IProgress<BenchProgress>? progress,
        CancellationToken ct)
    {
        CpuBenchResult? cpu = null;
        GpuBenchResult? gpu = null;
        DiskBenchResult? disk = null;
        VramStressResult? vram = null;
        RamStressResult? ram = null;

        if (tests.HasFlag(BenchTests.Cpu))
        {
            progress?.Report(new BenchProgress("Iniciando CPU benchmark", 0, 100));
            cpu = await Cpu.RunAsync(progress, ct).ConfigureAwait(false);
        }
        if (tests.HasFlag(BenchTests.Gpu))
        {
            progress?.Report(new BenchProgress("Iniciando GPU benchmark", 0, 100));
            gpu = await Gpu.RunAsync(progress, ct).ConfigureAwait(false);
        }
        if (tests.HasFlag(BenchTests.Disk))
        {
            progress?.Report(new BenchProgress("Iniciando teste de disco", 0, 100));
            disk = await Disk.RunAsync(TimeSpan.FromSeconds(20), progress, ct).ConfigureAwait(false);
        }
        if (tests.HasFlag(BenchTests.Vram))
        {
            progress?.Report(new BenchProgress("Iniciando VRAM stress", 0, 100));
            vram = await VramStressTest.RunAsync(progress, ct).ConfigureAwait(false);
        }
        if (tests.HasFlag(BenchTests.Ram))
        {
            progress?.Report(new BenchProgress("Iniciando teste de RAM", 0, 100));
            // 1 GB de teste — equilíbrio entre cobertura e tempo (~15s).
            ram = await RamStressTest.RunAsync(1024L * 1024 * 1024, progress, ct).ConfigureAwait(false);
        }

        var finalScore = ComputeFinalScore(cpu, gpu, disk, vram, ram, out var status, out var summary);
        var result = new TestResult("stress", status, summary, DateTime.Now);
        return new BenchmarkOutcome(result, cpu, gpu, disk, vram, ram, finalScore);
    }

    /// <summary>Atalho que roda tudo (compatível com o fluxo do modo Detalhado).</summary>
    public Task<BenchmarkOutcome> RunQuickAsync(IProgress<BenchProgress>? progress, CancellationToken ct)
        => RunSelectedAsync(BenchTests.All, progress, ct);

    /// <summary>
    /// Roda TODOS os testes em sequência usando o total de RAM real informado
    /// (em bytes) para o teste de memória mirar quase toda. Devolve o outcome
    /// com a nota final consolidada.
    /// </summary>
    public async Task<BenchmarkOutcome> RunAllAsync(
        long totalRamBytes,
        IProgress<BenchProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new BenchProgress("Iniciando CPU benchmark", 0, 100));
        var cpu = await Cpu.RunAsync(progress, ct).ConfigureAwait(false);
        progress?.Report(new BenchProgress("Iniciando GPU benchmark", 0, 100));
        var gpu = await Gpu.RunAsync(progress, ct).ConfigureAwait(false);
        progress?.Report(new BenchProgress("Iniciando teste de disco", 0, 100));
        var disk = await Disk.RunAsync(TimeSpan.FromSeconds(20), progress, ct).ConfigureAwait(false);
        progress?.Report(new BenchProgress("Iniciando VRAM stress", 0, 100));
        var vram = await VramStressTest.RunAsync(progress, ct).ConfigureAwait(false);
        progress?.Report(new BenchProgress("Iniciando teste de RAM", 0, 100));
        var ram = await RamStressTest.RunAsync(totalRamBytes > 0 ? totalRamBytes : 1024L * 1024 * 1024, progress, ct).ConfigureAwait(false);

        var finalScore = ComputeFinalScore(cpu, gpu, disk, vram, ram, out var status, out var summary);
        var result = new TestResult("stress", status, summary, DateTime.Now);
        return new BenchmarkOutcome(result, cpu, gpu, disk, vram, ram, finalScore);
    }

    // ------------------------------------------------------------------
    //  Escala de NOTA NORMALIZADA (estilo UserBenchmark/CPU-Z):
    //  cada componente vira um índice onde 100 = máquina de referência,
    //  e só então os pesos são aplicados. Assim um CPU 20% mais rápido
    //  move a nota final em ~20% × peso do CPU, independente da escala
    //  bruta de cada benchmark.
    //
    //  Máquina de referência (índice 100): notebook midrange 2023 —
    //   • CPU  Ryzen 7 7840HS ........ ST ~600 / MT ~5000  (calibração do CpuBenchmark)
    //   • GPU  classe GTX 1650/780M .. ~1000 por métrica   (RTX 5070 mobile ≈ 7000; Iris Xe ≈ 230)
    //   • Disco NVMe Gen3 ............ score ~350          (SATA SSD ≈ 100; HDD ≈ 15)
    //
    //  Interpretação da nota final: 100 = referência; 120 = 20% mais
    //  rápida; 50 = metade. Teto por componente em 1000 (10× a referência)
    //  — alto o bastante para top de linha (RTX 4090/5090, Ryzen 9, Gen5)
    //  se distinguirem, e só corta outliers absurdos para um único
    //  componente monstruoso não mascarar totalmente o resto.
    //
    //  RAM/VRAM são testes de INTEGRIDADE (passa/falha) — não entram na
    //  média de desempenho; falha derruba a nota pela metade e marca Falha.
    // ------------------------------------------------------------------
    private const double RefCpuSt = 600;
    private const double RefCpuMt = 5000;
    private const double RefGpuMetric = 1000;
    private const double RefDisk = 350;
    private const double MaxComponentIndex = 1000;

    private const double WeightCpu = 0.45;
    private const double WeightGpu = 0.30;
    private const double WeightDisk = 0.25;

    /// <summary>Índice de um valor bruto contra sua referência, com teto.</summary>
    private static double Index(double value, double reference) =>
        Math.Clamp(value / reference * 100.0, 0, MaxComponentIndex);

    /// <summary>
    /// Núcleo único do cálculo da nota (compartilhado pela suite e pelo
    /// recálculo via snapshot, para nunca divergirem). Componentes ausentes
    /// (&lt;= 0) ficam fora da média e os pesos são renormalizados.
    /// </summary>
    private static int ComputeIndexScore(
        double cpuSt, double cpuMt,
        double gpuGraphics, double gpuCompute, double gpuBandwidth,
        double diskScore,
        bool vramRan, bool vramFailed,
        bool ramRan, bool ramFailed,
        out AutoStatus status)
    {
        double weightedSum = 0, weightTotal = 0;

        if (cpuSt > 0 || cpuMt > 0)
        {
            // ST pesa 40% e MT 60% dentro do bloco de CPU: responsividade
            // importa, mas throughput multi-núcleo importa um pouco mais.
            var cpuIdx = Index(cpuSt, RefCpuSt) * 0.40 + Index(cpuMt, RefCpuMt) * 0.60;
            weightedSum += cpuIdx * WeightCpu; weightTotal += WeightCpu;
        }
        if (gpuGraphics > 0 || gpuCompute > 0 || gpuBandwidth > 0)
        {
            var gpuIdx = (Index(gpuGraphics, RefGpuMetric)
                        + Index(gpuCompute, RefGpuMetric)
                        + Index(gpuBandwidth, RefGpuMetric)) / 3.0;
            weightedSum += gpuIdx * WeightGpu; weightTotal += WeightGpu;
        }
        if (diskScore > 0)
        {
            weightedSum += Index(diskScore, RefDisk) * WeightDisk; weightTotal += WeightDisk;
        }

        var finalScore = weightTotal > 0
            ? (int)Math.Clamp(Math.Round(weightedSum / weightTotal), 0, 9_999)
            : 0;

        // Integridade de memória: falha corta a nota pela metade e reprova.
        var integrityFailure = (vramRan && vramFailed) || (ramRan && ramFailed);
        if (integrityFailure) finalScore = (int)(finalScore * 0.5);

        status = integrityFailure
            ? AutoStatus.Falha
            : finalScore switch
            {
                >= 60 => AutoStatus.OK,       // ≥ 60% da referência: máquina saudável
                >= 25 => AutoStatus.Atencao,  // máquina antiga/lenta, mas funcional
                0 => AutoStatus.NaoTestado,
                _ => AutoStatus.Falha,        // abaixo de 25% da referência
            };

        return finalScore;
    }

    /// <summary>
    /// Recalcula a nota final a partir de um <see cref="StressSnapshot"/> já
    /// montado (usado quando o técnico roda os testes individualmente pela UI).
    /// Usa o mesmo núcleo de <see cref="ComputeFinalScore"/>.
    /// </summary>
    public static int ComputeFinalFromSnapshot(StressSnapshot s)
    {
        return ComputeIndexScore(
            s.CpuSingleThread, s.CpuMultiThread,
            s.GpuGraphics, s.GpuCompute, s.GpuBandwidth,
            s.DiskScore,
            vramRan: s.VramAllocatedMb > 0, vramFailed: !s.VramOk && s.VramMismatchCount > 0,
            ramRan: s.RamAllocatedMb > 0, ramFailed: !s.RamOk && s.RamErrorCount > 0,
            out _);
    }

    /// <summary>
    /// Calcula a nota final normalizada (100 = máquina de referência)
    /// considerando apenas os testes que rodaram. Pesos: CPU 45%, GPU 30%,
    /// Disco 25%, renormalizados para os componentes presentes. RAM/VRAM
    /// atuam como teste de integridade (falha = nota pela metade + Falha).
    /// </summary>
    private static int ComputeFinalScore(
        CpuBenchResult? cpu, GpuBenchResult? gpu, DiskBenchResult? disk,
        VramStressResult? vram, RamStressResult? ram,
        out AutoStatus status, out string summary)
    {
        var finalScore = ComputeIndexScore(
            cpu?.SingleThreadScore ?? 0, cpu?.MultiThreadScore ?? 0,
            gpu?.GraphicsScore ?? 0, gpu?.ComputeScore ?? 0, gpu?.BandwidthScore ?? 0,
            disk?.Score ?? 0,
            vramRan: vram is not null, vramFailed: vram is { Ok: false, MismatchCount: > 0 },
            ramRan: ram is not null, ramFailed: ram is { Ok: false, ErrorCount: > 0 },
            out status);

        var parts = new System.Collections.Generic.List<string>();
        if (cpu is not null) parts.Add($"CPU ST {cpu.SingleThreadScore} MT {cpu.MultiThreadScore}");
        if (gpu is not null) parts.Add($"GPU graf {gpu.GraphicsScore} comp {gpu.ComputeScore} bw {gpu.BandwidthScore}");
        if (disk is not null) parts.Add($"Disco {disk.Score} (↓ {disk.ReadMbPerSec:F0} ↑ {disk.WriteMbPerSec:F0} MB/s)");
        if (vram is not null) parts.Add(vram.Ok ? $"VRAM OK ({vram.AllocatedMb} MB)" : $"VRAM corrompida ({vram.MismatchCount})");
        if (ram is not null) parts.Add(ram.Ok ? $"RAM OK ({ram.AllocatedMb} MB, {ram.PatternsRun} padrões)" : $"RAM com erros ({ram.ErrorCount})");

        summary = parts.Count > 0
            ? $"Nota {finalScore} (100 = referência) • " + string.Join(" • ", parts)
            : "Nenhum teste executado";

        return finalScore;
    }
}

/// <summary>Resultado consolidado. Campos null = teste não selecionado.</summary>
public record BenchmarkOutcome(
    TestResult Result,
    CpuBenchResult? Cpu,
    GpuBenchResult? Gpu,
    DiskBenchResult? Disk,
    VramStressResult? Vram,
    RamStressResult? Ram,
    int FinalScore);
