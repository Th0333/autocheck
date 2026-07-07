using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Application.Bench;

/// <summary>
/// Stress test contínuo de CPU. Roda por uma janela longa (default 5 min)
/// rotando workloads a cada 30s pra evitar otimizações específicas. Coleta
/// snapshots de hardware via <see cref="HardwareMonitor"/> e calcula um
/// score de estabilidade ao final.
///
/// Score de estabilidade: compara throughput dos primeiros 30s vs últimos 30s.
/// Se cair >5% → throttling detectado.
/// </summary>
public sealed class CpuStress
{
    private readonly ILogger _logger;

    public CpuStress(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<CpuStressDetailedResult> RunAsync(
        TimeSpan duration,
        IProgress<BenchProgress>? progress,
        CancellationToken ct)
    {
        var threads = Math.Max(1, Environment.ProcessorCount);
        using var monitor = new HardwareMonitor(_logger);
        var samples = new List<HardwareSnapshot>();

        var workloads = new (string Name, Func<long, long> Run)[]
        {
            ("AVX SIMD", SimdHeavy),
            ("Floating point", FpHeavy),
            ("Integer ALU", IntegerHeavy),
            ("Memory pressure", MemoryHeavy),
            ("Crypto", CryptoHeavy),
        };

        // Buckets de throughput por janela de 5s (pra detectar degradação).
        var bucketDurationMs = 5000;
        var buckets = new List<(int IndexBucket, double OpsPerSec, DateTime At)>();

        var stop = DateTime.UtcNow + duration;
        var totalOps = 0L;
        var bucketStart = DateTime.UtcNow;
        long bucketOps = 0;
        int bucketIdx = 0;

        // Monitor sampler em paralelo — coleta snapshot a cada 500ms
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = Task.Run(async () =>
        {
            while (!monitorCts.IsCancellationRequested && DateTime.UtcNow < stop)
            {
                try { samples.Add(monitor.Sample()); } catch { /* ignore */ }
                try { await Task.Delay(500, monitorCts.Token).ConfigureAwait(false); } catch { break; }
            }
        }, monitorCts.Token);

        // Workers: cada thread roda o mesmo workload simultaneamente. A cada
        // 30s mudamos o workload pra forçar variedade.
        var currentWorkload = 0;
        var counters = new long[threads];
        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workerTasks = new Task[threads];
        for (var t = 0; t < threads; t++)
        {
            var idx = t;
            workerTasks[idx] = Task.Run(() =>
            {
                long localOps = 0;
                while (DateTime.UtcNow < stop && !workerCts.IsCancellationRequested)
                {
                    var workload = workloads[currentWorkload].Run;
                    localOps = workload(localOps);
                    Interlocked.Increment(ref bucketOps);
                }
                counters[idx] = localOps;
            }, workerCts.Token);
        }

        // Loop de progresso + rotação de workload + bucketing.
        while (DateTime.UtcNow < stop && !ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);

            // Rotação de workload a cada 30s
            var elapsedSec = (DateTime.UtcNow - (stop - duration)).TotalSeconds;
            currentWorkload = (int)(elapsedSec / 30) % workloads.Length;

            // Bucket: se passou bucketDurationMs, fecha
            var bucketElapsed = DateTime.UtcNow - bucketStart;
            if (bucketElapsed.TotalMilliseconds >= bucketDurationMs)
            {
                var ops = Interlocked.Exchange(ref bucketOps, 0);
                buckets.Add((bucketIdx++, ops / bucketElapsed.TotalSeconds, DateTime.UtcNow));
                bucketStart = DateTime.UtcNow;
                totalOps += ops;
            }

            var pct = (int)Math.Min(100, (DateTime.UtcNow - (stop - duration)).TotalSeconds / duration.TotalSeconds * 100);
            progress?.Report(new BenchProgress(
                $"CPU stress: {workloads[currentWorkload].Name} ({pct}%)",
                pct, 100));
        }

        workerCts.Cancel();
        try { await Task.WhenAll(workerTasks).ConfigureAwait(false); } catch (OperationCanceledException) { }
        monitorCts.Cancel();
        try { await monitorTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

        // Análise
        var avgOpsPerSec = buckets.Count > 0 ? buckets.Average(b => b.OpsPerSec) : 0;
        var stabilityScore = CalculateStability(buckets);
        var clockStability = SnapshotAnalysis.CpuClockStability(samples);
        var maxTemp = SnapshotAnalysis.MaxCpuTemp(samples);

        // Throttling = clock caiu E temp passou 95°C, OU clock caiu E throughput caiu.
        var throttled = (!double.IsNaN(clockStability) && clockStability < 90)
                     || stabilityScore < 90;

        return new CpuStressDetailedResult(
            DurationSec: duration.TotalSeconds,
            ThroughputBuckets: buckets.Select(b => b.OpsPerSec).ToArray(),
            StabilityScore: stabilityScore,
            ClockStabilityScore: clockStability,
            MaxTempC: maxTemp,
            ThrottlingDetected: throttled,
            Snapshots: samples,
            AvgOpsPerSec: avgOpsPerSec);
    }

    private static double CalculateStability(List<(int IndexBucket, double OpsPerSec, DateTime At)> buckets)
    {
        if (buckets.Count < 4) return double.NaN;
        var head = buckets.Take(buckets.Count / 4).Average(b => b.OpsPerSec);
        var tail = buckets.Skip(buckets.Count * 3 / 4).Average(b => b.OpsPerSec);
        if (head <= 0) return double.NaN;
        return Math.Clamp(tail / head * 100, 0, 100);
    }

    // Workloads herdados do benchmark (mesma assinatura)
    [ThreadStatic] private static SHA256? _sha;
    private static readonly byte[] _crypto = MakeBuffer(64 * 1024);
    private static readonly byte[] _mem = MakeBuffer(8 * 1024 * 1024);
    private static byte[] MakeBuffer(int size)
    {
        var b = new byte[size];
        new Random(42).NextBytes(b);
        return b;
    }

    private static long SimdHeavy(long c)
    {
        var lanes = Vector<float>.Count;
        var arr = new float[lanes * 2048];
        for (var i = 0; i < arr.Length; i++) arr[i] = i * 0.001f;
        for (var pass = 0; pass < 100; pass++)
        {
            for (var i = 0; i < arr.Length; i += lanes)
            {
                var v = new Vector<float>(arr, i);
                v = Vector.SquareRoot(v * v + Vector<float>.One);
                v.CopyTo(arr, i);
            }
        }
        return c + arr.Length;
    }

    private static long FpHeavy(long c)
    {
        double acc = 1.0001;
        for (var i = 0; i < 500_000; i++)
        {
            acc = Math.Sqrt(acc * 1.000001 + 0.5);
            acc = Math.Sin(acc) + 1.5;
        }
        if (acc == 0.0) Console.Write(' ');
        return c + 500_000;
    }

    private static long IntegerHeavy(long c)
    {
        unchecked
        {
            uint x = (uint)c + 1u;
            for (var i = 0; i < 1_000_000; i++)
            {
                x = x * 1664525u + 1013904223u;
                x ^= x >> 13;
            }
            if (x == 0) Console.Write(' ');
            return c + 1_000_000;
        }
    }

    private static long MemoryHeavy(long c)
    {
        long s = 0;
        for (var i = 0; i < _mem.Length; i += 64) s += _mem[i];
        if (s < 0) Console.Write(' ');
        return c + _mem.Length / 64;
    }

    private static long CryptoHeavy(long c)
    {
        _sha ??= SHA256.Create();
        for (var i = 0; i < 30; i++) _sha.ComputeHash(_crypto);
        return c + 30;
    }
}

public record CpuStressDetailedResult(
    double DurationSec,
    double[] ThroughputBuckets,
    double StabilityScore,
    double ClockStabilityScore,
    double MaxTempC,
    bool ThrottlingDetected,
    IReadOnlyList<HardwareSnapshot> Snapshots,
    double AvgOpsPerSec);
