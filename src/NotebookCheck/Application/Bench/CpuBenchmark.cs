using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Application.Bench;

/// <summary>
/// Benchmark moderno de CPU com 6 workloads heterogêneos. Inspirado em
/// CPU-Z bench, mas usando carga mais variada (Cinebench-style fp + JPEG-style
/// integer + LZ4-style compressão). Roda cada workload em ST e MT,
/// produzindo scores reproduzíveis.
///
/// Workloads:
///   1. INTEGER MATH      — multiplicação/divisão grande sobre buffer
///   2. FLOATING POINT    — Mandelbrot iterativo
///   3. SIMD (Vector{T})  — soma vetorial usando SSE/AVX automático
///   4. COMPRESSION       — Deflate sobre buffer determinístico
///   5. CRYPTO            — SHA-256 sobre buffer
///   6. MEMORY            — varredura de array gigante (cache miss)
///
/// Cada workload roda 4s ST + 6s MT. O score final pondera todos.
/// </summary>
public sealed class CpuBenchmark
{
    private readonly ILogger _logger;

    public CpuBenchmark(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<CpuBenchResult> RunAsync(IProgress<BenchProgress>? progress, CancellationToken ct)
    {
        var threads = Math.Max(1, Environment.ProcessorCount);
        var stOps = new long[6];
        var mtOps = new long[6];
        var totalSteps = 12; // 6 workloads × 2 fases

        var workloads = new (string Label, Func<long, long> Run)[]
        {
            ("Integer math", IntegerMathWorkload),
            ("Floating point (Mandelbrot)", MandelbrotWorkload),
            ("SIMD vector ops", SimdWorkload),
            ("Deflate compression", CompressionWorkload),
            ("SHA-256 crypto", CryptoWorkload),
            ("Memory bandwidth", MemoryWorkload),
        };

        for (var i = 0; i < workloads.Length; i++)
        {
            var (label, run) = workloads[i];
            ct.ThrowIfCancellationRequested();

            // Progresso contínuo: cada fase ocupa uma fatia de 1/totalSteps da
            // barra e avança suavemente conforme o tempo decorre, em vez de só
            // saltar ao terminar (o que fazia a barra "travar" por segundos).
            var stPhase = i * 2;
            var mtPhase = i * 2 + 1;

            // ---- ST ----
            stOps[i] = await RunOnThreadsAsync(run, threads: 1, TimeSpan.FromSeconds(4), ct,
                frac => Report(progress, $"CPU ST: {label}", stPhase, totalSteps, frac)).ConfigureAwait(false);

            // ---- MT ----
            mtOps[i] = await RunOnThreadsAsync(run, threads, TimeSpan.FromSeconds(6), ct,
                frac => Report(progress, $"CPU MT: {label}", mtPhase, totalSteps, frac)).ConfigureAwait(false);
        }

        // Cálculo de score: cada workload tem peso/divisor próprio, calibrado
        // contra Ryzen 7 7840HS = referência ~600 ST / ~5000 MT por workload.
        // Os divisores foram aumentados em 1e6× (vs. versão anterior) pra manter
        // os scores dentro de uma escala 0..2000 por workload e evitar overflow.
        // CPUs mais lentas têm scores menores (proporcional) e top de linha
        // chegam até a faixa dos 1500 ST / 15000 MT total.
        var stDivisors = new double[]
        {
            /* 1 Integer math     */ 8_000_000_000d,
            /* 2 Mandelbrot       */ 2_000_000d,
            /* 3 SIMD             */ 4_000_000_000d,
            /* 4 Compression      */ 30_000_000d,
            /* 5 Crypto SHA-256   */ 800_000d,
            /* 6 Memory bandwidth */ 1_500_000_000d,
        };
        var mtDivisors = stDivisors;

        double stTotal = 0, mtTotal = 0;
        var details = new (string Workload, long StOps, long MtOps, int StScore, int MtScore)[6];
        for (var i = 0; i < 6; i++)
        {
            var stPerSec = stOps[i] / 4.0;
            var mtPerSec = mtOps[i] / 6.0;

            // Clamp por workload: nenhum workload pode dominar o score final.
            // ST: até 50 (×1000 = 50k). MT: até 500 (×1000 = 500k).
            var stScore = Math.Clamp(stPerSec / stDivisors[i], 0, 50);
            var mtScore = Math.Clamp(mtPerSec / mtDivisors[i], 0, 500);
            stTotal += stScore;
            mtTotal += mtScore;
            details[i] = (workloads[i].Label, stOps[i], mtOps[i],
                (int)Math.Round(stScore * 1000), (int)Math.Round(mtScore * 1000));
        }

        // Score final = média ponderada das 6 workloads × 1000 (escala legível).
        // Limites duros pra blindar contra overflow: ST até 99999, MT até 999999.
        var stScoreFinal = (int)Math.Clamp(Math.Round(stTotal / 6 * 1000), 0, 99_999);
        var mtScoreFinal = (int)Math.Clamp(Math.Round(mtTotal / 6 * 1000), 0, 999_999);

        // Eficiência = MT / (ST × threads) × 100 → 100% = escala perfeita SMT.
        // Cap em 150 evita ruído quando ST é muito baixo (divisão por número pequeno).
        var ipcRaw = (stScoreFinal > 0 && threads > 0)
            ? (double)mtScoreFinal / ((double)stScoreFinal * threads) * 100.0
            : 0.0;
        var efficiencyScore = (int)Math.Clamp(Math.Round(ipcRaw), 0, 150);

        return new CpuBenchResult(
            SingleThreadScore: stScoreFinal,
            MultiThreadScore: mtScoreFinal,
            EfficiencyScore: efficiencyScore,
            Threads: threads,
            WorkloadDetails: details);
    }

    /// <summary>
    /// Reporta progresso global 0..100 dado a fase atual (0..totalSteps-1) e a
    /// fração concluída dentro dela (0..1). Garante avanço suave e contínuo.
    /// </summary>
    private static void Report(IProgress<BenchProgress>? progress, string phase, int phaseIndex, int totalSteps, double fracWithinPhase)
    {
        var f = Math.Clamp(fracWithinPhase, 0, 1);
        var overall = (phaseIndex + f) / totalSteps * 100.0;
        progress?.Report(new BenchProgress(phase, (int)Math.Round(overall), 100));
    }

    private static Task<long> RunOnThreadsAsync(Func<long, long> work, int threads, TimeSpan window, CancellationToken ct,
        Action<double>? onProgress = null)
    {
        return Task.Run(() =>
        {
            var start = DateTime.UtcNow;
            var stop = start + window;
            var counters = new long[threads];
            var tasks = new Task[threads];
            for (var i = 0; i < threads; i++)
            {
                var idx = i;
                tasks[idx] = Task.Run(() =>
                {
                    long n = 0;
                    while (DateTime.UtcNow < stop && !ct.IsCancellationRequested)
                    {
                        n = work(n);
                    }
                    counters[idx] = n;
                }, ct);
            }

            // Loop de progresso baseado em tempo enquanto os workers rodam.
            if (onProgress is not null)
            {
                while (!Task.WaitAll(tasks, 100))
                {
                    var elapsed = (DateTime.UtcNow - start).TotalSeconds;
                    onProgress(elapsed / window.TotalSeconds);
                    if (ct.IsCancellationRequested) break;
                }
                onProgress(1.0);
            }
            else
            {
                Task.WaitAll(tasks, ct);
            }

            long total = 0;
            for (var i = 0; i < threads; i++) total += counters[i];
            return total;
        }, ct);
    }

    // ---------------- Workloads individuais ----------------

    private static long IntegerMathWorkload(long counter)
    {
        // Carga mista: mul/div/mod/xor sobre uint32 — força ALU sem cache miss.
        unchecked
        {
            uint x = (uint)counter + 1u;
            for (var i = 0; i < 200_000; i++)
            {
                x = x * 1664525u + 1013904223u;
                x ^= (x >> 13);
                x += (uint)(i * 7);
            }
            // Persiste algo pra evitar JIT eliminar
            if (x == 0) Console.Write(' ');
        }
        return counter + 200_000;
    }

    private static long MandelbrotWorkload(long counter)
    {
        // Mandelbrot 64×64 com 100 iterações max. FP64 intensivo.
        const int size = 64;
        const int maxIter = 100;
        long pixels = 0;
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                double x0 = px / (double)size * 3.5 - 2.5;
                double y0 = py / (double)size * 2.0 - 1.0;
                double x = 0, y = 0;
                int iter = 0;
                while (x * x + y * y <= 4 && iter < maxIter)
                {
                    var xt = x * x - y * y + x0;
                    y = 2 * x * y + y0;
                    x = xt;
                    iter++;
                }
                pixels++;
                if (iter < 0) Console.Write(' '); // anti-elide
            }
        }
        return counter + pixels;
    }

    private static long SimdWorkload(long counter)
    {
        // SIMD via Vector<float> — JIT escolhe SSE2/AVX2/AVX-512 sozinho.
        var lanes = Vector<float>.Count;
        var arrA = new float[lanes * 1024];
        var arrB = new float[lanes * 1024];
        for (var i = 0; i < arrA.Length; i++)
        {
            arrA[i] = i * 0.001f;
            arrB[i] = (i + 1) * 0.0007f;
        }
        long ops = 0;
        // 50 passadas mul+add
        for (var pass = 0; pass < 50; pass++)
        {
            for (var i = 0; i < arrA.Length; i += lanes)
            {
                var va = new Vector<float>(arrA, i);
                var vb = new Vector<float>(arrB, i);
                var vc = Vector.Multiply(va, vb) + va;
                vc.CopyTo(arrA, i);
                ops += lanes;
            }
        }
        return counter + ops;
    }

    private static readonly byte[] _compressionInput = MakeCompressionBuffer();
    private static byte[] MakeCompressionBuffer()
    {
        var buf = new byte[256 * 1024];
        var rnd = new Random(42);
        // Mistura de regiões aleatórias (incompressíveis) e regiões repetitivas
        // (alta compressão). Dá um workload realista.
        for (var i = 0; i < buf.Length; i++)
        {
            buf[i] = (byte)((i % 1024 < 256) ? rnd.Next(256) : (i % 64));
        }
        return buf;
    }

    private static long CompressionWorkload(long counter)
    {
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(_compressionInput, 0, _compressionInput.Length);
        }
        return counter + ms.Length;
    }

    [ThreadStatic] private static SHA256? _sha;
    private static readonly byte[] _cryptoBuffer = new byte[64 * 1024];
    static CpuBenchmark()
    {
        new Random(7).NextBytes(_cryptoBuffer);
    }

    private static long CryptoWorkload(long counter)
    {
        _sha ??= SHA256.Create();
        for (var i = 0; i < 50; i++)
        {
            _sha.ComputeHash(_cryptoBuffer);
        }
        return counter + 50;
    }

    private static long MemoryWorkload(long counter)
    {
        // Buffer de 32 MB — força cache miss em L1/L2/L3. JIT não consegue
        // eliminar porque escrevemos no array.
        const int size = 8 * 1024 * 1024;
        var arr = new int[size];
        for (var i = 0; i < arr.Length; i++) arr[i] = i;
        long sum = 0;
        for (var i = 0; i < arr.Length; i += 16)
        {
            sum += arr[i];
        }
        if (sum < 0) Console.Write(' ');
        return counter + arr.Length / 16;
    }
}

public record BenchProgress(string Phase, int Current, int Total);

public record CpuBenchResult(
    int SingleThreadScore,
    int MultiThreadScore,
    int EfficiencyScore,
    int Threads,
    (string Workload, long StOps, long MtOps, int StScore, int MtScore)[] WorkloadDetails);
