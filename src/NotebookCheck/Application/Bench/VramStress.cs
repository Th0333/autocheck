using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NotebookCheck.Application.Bench;

/// <summary>
/// Stress isolado de VRAM: aloca múltiplos buffers grandes, escreve um
/// padrão conhecido, lê de volta e compara. Detecta corrupção de memória
/// (raríssimo em silício saudável; comum em GPUs com VRAM defeituosa
/// vendidas em segunda mão).
/// </summary>
public sealed class VramStress
{
    private readonly ILogger _logger;

    public VramStress(ILogger logger) { _logger = logger; }

    public async Task<VramStressResult> RunAsync(IProgress<BenchProgress>? progress, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                return RunCore(progress, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Qualquer falha do driver/D3D (inclusive reset de GPU) vira um
                // resultado de erro em vez de derrubar o app.
                _logger.LogError(ex, "VRAM stress falhou");
                return new VramStressResult(0, 0, 0, 0, double.NaN, false, "Falha no teste de VRAM: " + ex.Message);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True se o dispositivo D3D11 foi removido/resetado (TDR). Mapear/ler
    /// recursos após isso aponta para memória inválida e crasha o processo.
    /// </summary>
    private static bool IsDeviceLost(ID3D11Device device)
    {
        try { return device.DeviceRemovedReason.Failure; }
        catch { return false; }
    }

    private VramStressResult RunCore(IProgress<BenchProgress>? progress, CancellationToken ct)
    {
        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out ID3D11Device? device, out _, out ID3D11DeviceContext? context);

        if (hr.Failure || device is null || context is null)
        {
            return new VramStressResult(0, 0, 0, 0, double.NaN, false, "D3D11 indisponível");
        }

        try
        {
            // Lê a VRAM dedicada da GPU principal (mesmo método usado no
            // GpuBenchmark, comprovadamente funcional). Em WDDM, o D3D11 NÃO
            // falha ao exceder a VRAM (ele pagina para a RAM), então precisamos
            // limitar pela VRAM real para o teste ser fiel.
            long dedicatedVramBytes = 0;
            string gpuName = "GPU";
            try
            {
                using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                var adapter = dxgiDevice.GetAdapter();
                try
                {
                    var desc = adapter.Description;
                    dedicatedVramBytes = (long)(ulong)desc.DedicatedVideoMemory;
                    gpuName = desc.Description ?? "GPU";
                }
                finally { adapter.Dispose(); }
            }
            catch { }
            _logger.LogInformation("VRAM stress: GPU {Gpu}, dedicada {Mb} MB", gpuName, dedicatedVramBytes / (1024 * 1024));

            const long chunkBytes = 128L * 1024 * 1024;
            // IMPORTANTE: alocar muita VRAM derruba o driver (TDR) e reseta a
            // GPU — e o reset, no meio de uma leitura mapeada, causava CRASH do
            // app em GPUs fortes (mais VRAM ⇒ alocação maior ⇒ TDR). O objetivo
            // aqui é só detectar VRAM defeituosa, e para isso NÃO é preciso
            // encher a placa: testar ~1 GB já cobre células ruins. Miramos no
            // MENOR entre:
            //   • 25% da VRAM dedicada (deixa 3/4 livre pro driver/desktop)
            //   • um teto absoluto de 1,5 GB (evita TDR mesmo em GPUs grandes)
            const long hardCapBytes = 1536L * 1024 * 1024;
            var targetBytes = dedicatedVramBytes > 0
                ? Math.Min((long)(dedicatedVramBytes * 0.25), hardCapBytes)
                : 1024L * 1024 * 1024;
            var maxChunks = (int)Math.Clamp(targetBytes / chunkBytes, 2, 12);
            const int floatsPerChunk = (int)(chunkBytes / 4);
            var chunkMb = (int)(chunkBytes / (1024 * 1024));

            var buffers = new ID3D11Buffer[maxChunks];
            var allocated = 0;

            // Buffers gerenciados reutilizados (256 MB cada) — evita alocar/coletar
            // ~256 MB no LOH a cada chunk, o que fragmentava a heap e podia
            // derrubar o app.
            var writeData = new uint[floatsPerChunk];

            // Buffer de staging único reutilizável (256 MB) — economiza VRAM.
            ID3D11Buffer? staging = null;

            try
            {
                // Padrões de teste, aplicados em passadas sucessivas. Detectam
                // bits presos (0x00/0xFF), acoplamento (checkerboard) e
                // endereçamento (own-address).
                var patterns = new (string Name, Func<int, int, uint> Gen)[]
                {
                    ("own-address", (chunk, i) => (uint)(chunk * floatsPerChunk + i)),
                    ("0xFFFFFFFF",  (_, _) => 0xFFFFFFFFu),
                    ("0x00000000",  (_, _) => 0x00000000u),
                    ("0xAAAA5555",  (_, i) => (i & 1) == 0 ? 0xAAAAAAAAu : 0x55555555u),
                };

                var sd = new BufferDescription
                {
                    ByteWidth = (uint)chunkBytes,
                    BindFlags = BindFlags.None,
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Read,
                };

                long bytesVerified = 0;
                long mismatchCount = 0;
                int firstMismatchChunk = -1;

                // Fase 1: aloca o máximo possível (com o 1º padrão).
                var firstPat = patterns[0];
                for (var chunk = 0; chunk < maxChunks; chunk++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new BenchProgress(
                        $"VRAM alocando: {(chunk + 1) * chunkMb} MB / {maxChunks * chunkMb} MB",
                        chunk * 25 / maxChunks, 100));
                    try
                    {
                        for (var i = 0; i < writeData.Length; i++) writeData[i] = firstPat.Gen(chunk, i);
                        var bd = new BufferDescription
                        {
                            ByteWidth = (uint)chunkBytes,
                            BindFlags = BindFlags.None,
                            Usage = ResourceUsage.Default,
                            MiscFlags = ResourceOptionFlags.None,
                        };
                        buffers[chunk] = device.CreateBuffer(writeData.AsSpan(), bd);
                        allocated++;
                    }
                    catch
                    {
                        break; // VRAM esgotada
                    }
                }

                if (allocated == 0)
                    return new VramStressResult(0, 0, 0, 0, double.NaN, false, "Falha ao alocar VRAM");

                staging = device.CreateBuffer(sd);

                // Verifica/reescreve cada padrão em todos os chunks alocados.
                for (var p = 0; p < patterns.Length; p++)
                {
                    var pat = patterns[p];

                    // Reescreve (exceto o 1º, que já foi escrito na alocação).
                    if (p > 0)
                    {
                        for (var chunk = 0; chunk < allocated; chunk++)
                        {
                            ct.ThrowIfCancellationRequested();
                            for (var i = 0; i < writeData.Length; i++) writeData[i] = pat.Gen(chunk, i);
                            context.UpdateSubresource(writeData.AsSpan(), buffers[chunk]);
                        }
                    }

                    // Lê de volta e compara.
                    for (var chunk = 0; chunk < allocated; chunk++)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Se o driver resetou (TDR), NÃO podemos mapear/ler — o
                        // ponteiro mapeado apontaria para memória inválida e o
                        // app crasharia (access violation). Encerra com um
                        // resultado parcial em vez de derrubar tudo.
                        if (IsDeviceLost(device))
                        {
                            var doneMb = allocated * chunkMb;
                            return new VramStressResult(
                                AllocatedMb: doneMb,
                                BytesVerified: bytesVerified,
                                MismatchCount: mismatchCount,
                                FirstMismatchChunk: firstMismatchChunk,
                                CorruptionRatePercent: 0,
                                Ok: mismatchCount == 0,
                                Notes: "Teste parcial: o driver da GPU reiniciou durante a leitura. Feche jogos/apps de vídeo pesados e rode de novo.");
                        }

                        var pct = 25 + (p * allocated + chunk) * 70 / (patterns.Length * allocated);
                        progress?.Report(new BenchProgress(
                            $"VRAM padrão {pat.Name} • chunk {chunk + 1}/{allocated}", pct, 100));

                        context.CopyResource(staging, buffers[chunk]);
                        var mapped = context.Map(staging, 0, MapMode.Read);
                        try
                        {
                            unsafe
                            {
                                var ptr = (uint*)mapped.DataPointer;
                                if (ptr != null)
                                {
                                    for (var i = 0; i < floatsPerChunk; i++)
                                    {
                                        if (ptr[i] != pat.Gen(chunk, i))
                                        {
                                            mismatchCount++;
                                            if (firstMismatchChunk < 0) firstMismatchChunk = chunk;
                                        }
                                        bytesVerified += 4;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            context.Unmap(staging, 0);
                        }
                    }
                }

                progress?.Report(new BenchProgress("VRAM concluído", 100, 100));

                var allocatedMb = allocated * chunkMb;
                var corruptionRate = bytesVerified > 0 ? mismatchCount * 100.0 / (bytesVerified / 4) : 0;
                var ok = mismatchCount == 0;
                var notes = ok
                    ? $"{allocatedMb} MB testados em {patterns.Length} padrões, sem corrupção"
                    : $"{mismatchCount} erros ({corruptionRate:F4}%) — 1º chunk @ {firstMismatchChunk * chunkMb} MB";

                return new VramStressResult(
                    AllocatedMb: allocatedMb,
                    BytesVerified: bytesVerified,
                    MismatchCount: mismatchCount,
                    FirstMismatchChunk: firstMismatchChunk,
                    CorruptionRatePercent: corruptionRate,
                    Ok: ok,
                    Notes: notes);
            }
            finally
            {
                staging?.Dispose();
                for (var i = 0; i < buffers.Length; i++) buffers[i]?.Dispose();
            }
        }
        finally
        {
            context.Dispose();
            device.Dispose();
        }
    }
}

public record VramStressResult(
    int AllocatedMb,
    long BytesVerified,
    long MismatchCount,
    int FirstMismatchChunk,
    double CorruptionRatePercent,
    bool Ok,
    string? Notes);
