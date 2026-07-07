using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NotebookCheck.Application.Bench;

/// <summary>
/// Stress test contínuo de GPU. Roda compute kernels pesados em loop por
/// uma janela longa, alternando entre 3 tipos de carga a cada 30s pra
/// evitar otimização do driver. Coleta snapshots térmicos e detecta
/// throttling.
/// </summary>
public sealed class GpuStress
{
    private readonly ILogger _logger;

    public GpuStress(ILogger logger) { _logger = logger; }

    public async Task<GpuStressDetailedResult> RunAsync(
        TimeSpan duration,
        IProgress<BenchProgress>? progress,
        CancellationToken ct)
    {
        return await Task.Run(() => RunCore(duration, progress, ct), ct).ConfigureAwait(false);
    }

    private GpuStressDetailedResult RunCore(TimeSpan duration, IProgress<BenchProgress>? progress, CancellationToken ct)
    {
        var hr = D3D11.D3D11CreateDevice(null,
            DriverType.Hardware, DeviceCreationFlags.None,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out ID3D11Device? device, out _, out ID3D11DeviceContext? context);

        if (hr.Failure || device is null || context is null)
        {
            return new GpuStressDetailedResult(
                duration.TotalSeconds, Array.Empty<double>(),
                double.NaN, double.NaN, double.NaN, false,
                Array.Empty<HardwareSnapshot>(), 0, "D3D11 indisponível");
        }

        try
        {
            // Compila os 3 kernels de stress
            var kComputeHeavy = CompileKernel(device, KernelComputeHeavy, 64);
            var kBandwidthHeavy = CompileKernel(device, KernelBandwidthHeavy, 64);
            var kCacheHeavy = CompileKernel(device, KernelCacheHeavy, 64);

            using var monitor = new HardwareMonitor(_logger);
            var samples = new List<HardwareSnapshot>();
            var buckets = new List<double>();

            // Buffer de 64 MB pra todos
            const int elementCount = 16 * 1024 * 1024;
            var initData = new float[elementCount];
            for (var i = 0; i < initData.Length; i++) initData[i] = 1.0001f + i * 0.0000001f;

            var bufferDesc = new BufferDescription
            {
                ByteWidth = elementCount * 4u,
                BindFlags = BindFlags.UnorderedAccess,
                Usage = ResourceUsage.Default,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = 4,
            };
            using var buffer = device.CreateBuffer(initData.AsSpan(), bufferDesc);
            var uavDesc = new UnorderedAccessViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = elementCount },
            };
            using var uav = device.CreateUnorderedAccessView(buffer, uavDesc);

            context.CSSetUnorderedAccessView(0, uav);

            var threadGroups = (uint)(elementCount / 64);
            var stop = DateTime.UtcNow + duration;

            long bucketIters = 0;
            var bucketStart = DateTime.UtcNow;
            const int bucketMs = 5000;

            using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var monitorTask = Task.Run(async () =>
            {
                while (!monitorCts.IsCancellationRequested && DateTime.UtcNow < stop)
                {
                    try { samples.Add(monitor.Sample()); } catch { }
                    try { await Task.Delay(500, monitorCts.Token).ConfigureAwait(false); } catch { break; }
                }
            }, monitorCts.Token);

            var kernels = new[] { kComputeHeavy, kBandwidthHeavy, kCacheHeavy };
            var kernelNames = new[] { "Compute heavy", "Bandwidth heavy", "Cache heavy" };

            while (DateTime.UtcNow < stop && !ct.IsCancellationRequested)
            {
                var elapsedSec = (DateTime.UtcNow - (stop - duration)).TotalSeconds;
                var idx = (int)(elapsedSec / 30) % kernels.Length;
                context.CSSetShader(kernels[idx]);
                context.Dispatch(threadGroups, 1, 1);
                context.Flush();
                bucketIters++;

                if ((DateTime.UtcNow - bucketStart).TotalMilliseconds >= bucketMs)
                {
                    var sec = (DateTime.UtcNow - bucketStart).TotalSeconds;
                    buckets.Add(bucketIters / sec);
                    bucketIters = 0;
                    bucketStart = DateTime.UtcNow;

                    var pct = (int)Math.Min(100, elapsedSec / duration.TotalSeconds * 100);
                    progress?.Report(new BenchProgress($"GPU stress: {kernelNames[idx]} ({pct}%)", pct, 100));
                }
            }

            monitorCts.Cancel();
            try { monitorTask.Wait(2000); } catch { }

            // Cleanup kernels
            foreach (var k in kernels) k.Dispose();

            var avgIter = buckets.Count > 0 ? buckets.Average() : 0;
            var stability = CalcStability(buckets);
            var clockStab = SnapshotAnalysis.GpuClockStability(samples);
            var maxTemp = SnapshotAnalysis.MaxGpuTemp(samples);
            var throttled = (!double.IsNaN(clockStab) && clockStab < 90) || stability < 90;

            return new GpuStressDetailedResult(
                DurationSec: duration.TotalSeconds,
                ThroughputBuckets: buckets.ToArray(),
                StabilityScore: stability,
                ClockStabilityScore: clockStab,
                MaxTempC: maxTemp,
                ThrottlingDetected: throttled,
                Snapshots: samples,
                AvgIterPerSec: avgIter,
                Notes: null);
        }
        finally
        {
            context.Dispose();
            device.Dispose();
        }
    }

    private static ID3D11ComputeShader CompileKernel(ID3D11Device device, string hlsl, int threadCount)
    {
        var hr = Compiler.Compile(hlsl, "main", "kernel.hlsl", "cs_5_0", out var bc, out _);
        if (hr.Failure || bc is null) throw new InvalidOperationException("Falha compilando kernel: " + hr);
        return device.CreateComputeShader(bc.AsBytes());
    }

    private static double CalcStability(List<double> buckets)
    {
        if (buckets.Count < 4) return double.NaN;
        var head = buckets.Take(buckets.Count / 4).Average();
        var tail = buckets.Skip(buckets.Count * 3 / 4).Average();
        if (head <= 0) return double.NaN;
        return Math.Clamp(tail / head * 100, 0, 100);
    }

    // Kernels de stress
    private const string KernelComputeHeavy = @"
RWStructuredBuffer<float> B : register(u0);
[numthreads(64,1,1)]
void main(uint3 dtid : SV_DispatchThreadID) {
    float v = B[dtid.x];
    [unroll(128)] for (int k = 0; k < 2048; k++) {
        v = sqrt(v * 1.0001 + 0.5);
        v = sin(v) + cos(v) * 0.5;
    }
    B[dtid.x] = v;
}";

    private const string KernelBandwidthHeavy = @"
RWStructuredBuffer<float> B : register(u0);
[numthreads(64,1,1)]
void main(uint3 dtid : SV_DispatchThreadID) {
    uint i = dtid.x;
    float v = B[i] + B[(i + 1024) & 16777215] * 0.001;
    B[i] = v;
}";

    private const string KernelCacheHeavy = @"
RWStructuredBuffer<float> B : register(u0);
[numthreads(64,1,1)]
void main(uint3 dtid : SV_DispatchThreadID) {
    uint i = dtid.x;
    float sum = 0;
    [unroll(32)] for (int k = 0; k < 64; k++) {
        sum += B[(i + k * 65536) & 16777215];
    }
    B[i] = sum * 0.0001;
}";
}

public record GpuStressDetailedResult(
    double DurationSec,
    double[] ThroughputBuckets,
    double StabilityScore,
    double ClockStabilityScore,
    double MaxTempC,
    bool ThrottlingDetected,
    IReadOnlyList<HardwareSnapshot> Snapshots,
    double AvgIterPerSec,
    string? Notes);
