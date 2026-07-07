using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Application.Bench;

/// <summary>
/// Benchmark de disco rápido. Aloca um arquivo de 64 MB no diretório do app
/// e mede leitura aleatória + escrita aleatória em janelas curtas. Devolve
/// score derivado de (read + write) MB/s.
///
/// Calibração:
///   HDD 5400 → ~150 MB/s combinado → score ~15
///   SATA SSD → ~1000 MB/s combinado → score ~100
///   NVMe Gen3 → ~3500 MB/s → score ~350
///   NVMe Gen4 → ~7000 MB/s → score ~700
///   NVMe Gen5 → ~14000 MB/s → score ~1400
/// </summary>
public sealed class DiskBenchmark
{
    private readonly ILogger _logger;

    public DiskBenchmark(ILogger logger) { _logger = logger; }

    public async Task<DiskBenchResult> RunAsync(
        TimeSpan duration,
        IProgress<BenchProgress>? progress,
        CancellationToken ct)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "stress.tmp");
        const int totalSize = 64 * 1024 * 1024; // 64 MB
        const int blockSize = 1 * 1024 * 1024;  // 1 MB

        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                bufferSize: blockSize, FileOptions.WriteThrough);
            fs.SetLength(totalSize);

            var buffer = new byte[blockSize];
            new Random(42).NextBytes(buffer);

            // Janela 1 — escrita pura
            var half = TimeSpan.FromTicks(duration.Ticks / 2);
            var halfSecs = Math.Max(0.001, half.TotalSeconds);
            var writeStop = DateTime.UtcNow + half;
            long bytesWritten = 0;
            var rnd = new Random();
            var swWrite = Stopwatch.StartNew();
            while (DateTime.UtcNow < writeStop && !ct.IsCancellationRequested)
            {
                var offset = (long)rnd.Next(0, (totalSize / blockSize) - 1) * blockSize;
                fs.Position = offset;
                await fs.WriteAsync(buffer.AsMemory(0, blockSize), ct).ConfigureAwait(false);
                bytesWritten += blockSize;
                progress?.Report(new BenchProgress("Disco (escrita)",
                    (int)Math.Min(50, swWrite.Elapsed.TotalSeconds / halfSecs * 50), 100));
            }
            swWrite.Stop();
            await fs.FlushAsync(ct).ConfigureAwait(false);

            // Janela 2 — leitura pura
            var readStop = DateTime.UtcNow + half;
            long bytesRead = 0;
            var swRead = Stopwatch.StartNew();
            while (DateTime.UtcNow < readStop && !ct.IsCancellationRequested)
            {
                var offset = (long)rnd.Next(0, (totalSize / blockSize) - 1) * blockSize;
                fs.Position = offset;
                await fs.ReadAsync(buffer.AsMemory(0, blockSize), ct).ConfigureAwait(false);
                bytesRead += blockSize;
                progress?.Report(new BenchProgress("Disco (leitura)",
                    50 + (int)Math.Min(50, swRead.Elapsed.TotalSeconds / halfSecs * 50), 100));
            }
            swRead.Stop();

            var writeMbPerSec = bytesWritten / 1024.0 / 1024.0 / Math.Max(0.001, swWrite.Elapsed.TotalSeconds);
            var readMbPerSec = bytesRead / 1024.0 / 1024.0 / Math.Max(0.001, swRead.Elapsed.TotalSeconds);
            var combined = writeMbPerSec + readMbPerSec;
            var score = (int)Math.Min(9999, Math.Round(combined / 10.0));
            return new DiskBenchResult(score, readMbPerSec, writeMbPerSec, bytesRead, bytesWritten);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha no disk benchmark");
            return new DiskBenchResult(0, 0, 0, 0, 0);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

public record DiskBenchResult(
    int Score,
    double ReadMbPerSec,
    double WriteMbPerSec,
    long BytesRead,
    long BytesWritten);
