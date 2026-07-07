using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Application.Bench;

/// <summary>
/// Teste de integridade de RAM estilo MemTest86, em vez de só medir
/// bandwidth. Aloca um bloco grande (default 1 GB ou o que couber),
/// escreve padrões conhecidos, lê de volta e compara byte a byte. Detecta
/// células defeituosas, bits presos (stuck-at) e erros de endereçamento —
/// comuns em pentes usados ou mal encaixados.
///
/// Padrões aplicados (rodadas):
///   1. 0x00000000  (all zeros)
///   2. 0xFFFFFFFF  (all ones)
///   3. 0xAAAAAAAA / 0x55555555 (checkerboard — detecta bits presos)
///   4. Walking bits (1,2,4,8,...) — detecta erros de endereçamento
///   5. Own-address (cada palavra = seu próprio endereço) — detecta aliasing
///
/// Também mede bandwidth (GB/s) como subproduto.
/// </summary>
public sealed class RamStress
{
    private readonly ILogger _logger;

    public RamStress(ILogger logger) { _logger = logger; }

    public async Task<RamStressResult> RunAsync(
        long requestedBytes,
        IProgress<BenchProgress>? progress,
        CancellationToken ct)
    {
        return await Task.Run(() => RunCore(requestedBytes, progress, ct), ct).ConfigureAwait(false);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// Calcula quanto da RAM física disponível podemos testar com segurança.
    /// Estratégia: usa o total informado (do WMI, confiável) e mira ~70% dele,
    /// deixando folga pro SO. Se vier 0, tenta GlobalMemoryStatusEx; se ainda
    /// falhar, usa um teto conservador.
    /// </summary>
    private static long ComputeSafeTestBytes(long totalRamBytes)
    {
        // 1ª escolha: total informado pelo caller (machine.RamGb via WMI).
        if (totalRamBytes > 0)
        {
            var reserve = 3L * 1024 * 1024 * 1024; // 3 GB pro SO/apps
            var safe = (long)((totalRamBytes - reserve) * 0.70);
            if (safe < 512L * 1024 * 1024) safe = 512L * 1024 * 1024;
            return safe;
        }

        // 2ª escolha: API do Windows.
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0)
            {
                var reserve = 3UL * 1024 * 1024 * 1024;
                var total = (long)status.ullTotalPhys;
                var safe = (long)((total - (long)reserve) * 0.70);
                if (safe < 512L * 1024 * 1024) safe = 512L * 1024 * 1024;
                return safe;
            }
        }
        catch { }

        // 3ª escolha: teto conservador (4 GB).
        return 4L * 1024 * 1024 * 1024;
    }

    private unsafe RamStressResult RunCore(long totalRamBytes, IProgress<BenchProgress>? progress, CancellationToken ct)
    {
        // Aloca em blocos de 512 MB até atingir o alvo seguro OU até a alocação
        // falhar (o que vier primeiro). Assim testamos quase toda a RAM sem
        // arriscar BSOD por exaustão.
        var targetBytes = ComputeSafeTestBytes(totalRamBytes);
        const long blockBytes = 512L * 1024 * 1024;
        var maxBlocks = (int)Math.Max(1, targetBytes / blockBytes);

        var blocks = new System.Collections.Generic.List<IntPtr>();
        long allocated = 0;
        try
        {
            for (var b = 0; b < maxBlocks; b++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new BenchProgress(
                    $"RAM alocando: {(b + 1) * 512} MB / {maxBlocks * 512} MB", b * 10 / maxBlocks, 100));
                try
                {
                    var p = Marshal.AllocHGlobal((nint)blockBytes);
                    blocks.Add(p);
                    allocated += blockBytes;
                }
                catch
                {
                    break; // memória esgotada — para com o que já temos
                }
            }

            if (blocks.Count == 0)
                return new RamStressResult(0, 0, 0, false, double.NaN, "Falha ao alocar memória de teste");

            long totalErrors = 0;
            var sw = Stopwatch.StartNew();
            long bytesProcessed = 0;

            // Conjunto completo de padrões MemTest86.
            var patterns = new ulong[]
            {
                0x0000000000000000UL,
                0xFFFFFFFFFFFFFFFFUL,
                0xAAAAAAAAAAAAAAAAUL,
                0x5555555555555555UL,
            };
            var totalPhases = patterns.Length + 2; // + walking bits + own-address
            var phase = 0;

            void RunFixed(ulong pat)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new BenchProgress(
                    $"RAM padrão 0x{pat:X16} ({phase + 1}/{totalPhases})",
                    phase * 90 / totalPhases + 10, 100));
                foreach (var blk in blocks)
                {
                    ct.ThrowIfCancellationRequested();
                    var up = (ulong*)blk;
                    var count = blockBytes / 8;
                    for (long i = 0; i < count; i++) up[i] = pat;
                    bytesProcessed += blockBytes;
                }
                foreach (var blk in blocks)
                {
                    ct.ThrowIfCancellationRequested();
                    var up = (ulong*)blk;
                    var count = blockBytes / 8;
                    for (long i = 0; i < count; i++) if (up[i] != pat) totalErrors++;
                    bytesProcessed += blockBytes;
                }
                phase++;
            }

            foreach (var pat in patterns) RunFixed(pat);

            // Walking bits.
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BenchProgress($"RAM walking bits ({phase + 1}/{totalPhases})", phase * 90 / totalPhases + 10, 100));
            foreach (var blk in blocks)
            {
                var up = (ulong*)blk;
                var count = blockBytes / 8;
                for (long i = 0; i < count; i++) up[i] = 1UL << (int)(i & 63);
                bytesProcessed += blockBytes;
            }
            foreach (var blk in blocks)
            {
                var up = (ulong*)blk;
                var count = blockBytes / 8;
                for (long i = 0; i < count; i++) if (up[i] != (1UL << (int)(i & 63))) totalErrors++;
                bytesProcessed += blockBytes;
            }
            phase++;

            // Own-address (cada palavra = seu índice global).
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BenchProgress($"RAM own-address ({phase + 1}/{totalPhases})", phase * 90 / totalPhases + 10, 100));
            ulong globalIdx = 0;
            foreach (var blk in blocks)
            {
                var up = (ulong*)blk;
                var count = blockBytes / 8;
                for (long i = 0; i < count; i++) up[i] = globalIdx++;
                bytesProcessed += blockBytes;
            }
            globalIdx = 0;
            foreach (var blk in blocks)
            {
                var up = (ulong*)blk;
                var count = blockBytes / 8;
                for (long i = 0; i < count; i++) if (up[i] != globalIdx++) totalErrors++;
                bytesProcessed += blockBytes;
            }
            phase++;

            sw.Stop();
            progress?.Report(new BenchProgress("RAM concluído", 100, 100));

            var bandwidthGbs = bytesProcessed / sw.Elapsed.TotalSeconds / 1e9;
            var allocatedMb = (int)(allocated / (1024 * 1024));
            var ok = totalErrors == 0;
            var notes = ok
                ? $"{allocatedMb} MB testados • {totalPhases} padrões • sem erros"
                : $"{totalErrors} erro(s) de memória em {allocatedMb} MB — RAM defeituosa";

            return new RamStressResult(
                AllocatedMb: allocatedMb,
                PatternsRun: totalPhases,
                ErrorCount: totalErrors,
                Ok: ok,
                BandwidthGbs: bandwidthGbs,
                Notes: notes);
        }
        finally
        {
            foreach (var p in blocks) Marshal.FreeHGlobal(p);
        }
    }
}

public record RamStressResult(
    int AllocatedMb,
    int PatternsRun,
    long ErrorCount,
    bool Ok,
    double BandwidthGbs,
    string? Notes);
