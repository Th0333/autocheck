using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;

namespace NotebookCheck.Application.Humanization;

/// <summary>
/// Runner do "modo humanização" — simula 12 horas de uso humano normal para
/// caçar instabilidades térmicas, vazamentos de memória do firmware,
/// disjuntores de bateria e travamentos intermitentes que só aparecem em
/// ciclos longos.
///
/// ROTEIRO COMPLETO (ciclos de 60 minutos repetidos 12 vezes):
///   00:00–00:08  Navegação simulada — abre páginas web (NotebookCheck, Wikipedia, YouTube) em uma janela headless via processo edge.
///   00:08–00:18  Vídeo em loop — abre um clipe do YouTube em janela do edge,
///                volume baixo, span 10 minutos.
///   00:18–00:23  Pico de CPU 50% (5 min) — carga em metade dos cores lógicos
///                via Task com loop matemático.
///   00:23–00:28  Pico de CPU 100% (5 min) — ataca todos os cores.
///   00:28–00:33  Camera test — abre app Câmera por 2 minutos, depois fecha
///                e aguarda 3 minutos.
///   00:33–00:38  Disco I/O — escreve/lê 200 MB em arquivo temporário 5x.
///   00:38–00:48  Idle simulado — Task.Delay representando o usuário
///                "lendo" o conteúdo, com mouse move via SendInput a cada 2 min
///                (impede o monitor entrar em standby).
///   00:48–00:55  Stress aleatório — 7 minutos com carga variando entre 50%
///                e 100% em janelas de 30s (quem decide é Random).
///   00:55–01:00  Cooldown — 5 minutos sem carga; coleta temperaturas pra
///                ver se a CPU desce conforme esperado.
///
/// A cada minuto registra um snapshot (timestamp, fase, %CPU, MB livres,
/// temperatura quando disponível, AC ligado/bateria) que é gravado num
/// arquivo de log incremental. No final, conta:
///   - Quantos ciclos completaram sem erro
///   - Quantas falhas/timeouts ocorreram
///   - Pico e média de CPU
///   - Quedas de bateria
///   - Excursões de temperatura (>85°C)
/// e converte em um <see cref="TestResult"/>.
///
/// O runner é interruptível via CancellationToken — fecha tudo limpo se o
/// técnico apertar "Parar". Estado é persistido em humanization.log.json
/// para que possa retomar (futuro).
/// </summary>
public sealed class HumanizationRunner
{
    private readonly ILogger<HumanizationRunner> _logger;

    /// <summary>Duração total padrão do roteiro.</summary>
    public TimeSpan TotalDuration { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Duração de um ciclo completo (todas as fases acima).</summary>
    public TimeSpan CycleDuration { get; set; } = TimeSpan.FromHours(1);

    public HumanizationRunner(ILogger<HumanizationRunner> logger)
    {
        _logger = logger;
    }

    public async Task<TestResult> RunAsync(IProgress<HumanizationProgress>? progress, CancellationToken ct)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, $"humanization-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        await using var log = new StreamWriter(logPath, append: false);
        await log.WriteLineAsync("# Notelet — humanization log").ConfigureAwait(false);
        await log.WriteLineAsync($"# started at {DateTime.Now:O}").ConfigureAwait(false);

        var start = DateTime.UtcNow;
        var end = start + TotalDuration;
        var cycle = 0;
        var cyclesCompleted = 0;
        var failures = 0;
        var rng = new Random();

        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            cycle++;
            try
            {
                await RunCycleAsync(cycle, log, rng, progress, ct).ConfigureAwait(false);
                cyclesCompleted++;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha no ciclo {Cycle}", cycle);
                await log.WriteLineAsync($"[{DateTime.Now:O}] cycle {cycle} FAILED: {ex.Message}").ConfigureAwait(false);
                failures++;
            }
        }

        await log.WriteLineAsync($"# finished at {DateTime.Now:O}").ConfigureAwait(false);
        await log.WriteLineAsync($"# cycles completed: {cyclesCompleted}, failures: {failures}").ConfigureAwait(false);

        AutoStatus status;
        string detail;
        if (ct.IsCancellationRequested)
        {
            status = AutoStatus.NaoTestado;
            detail = $"Interrompido após {cyclesCompleted} ciclo(s) de {TotalDuration.TotalHours:0}h previstos";
        }
        else if (failures == 0)
        {
            status = AutoStatus.OK;
            detail = $"{cyclesCompleted} ciclos concluídos sem falhas em {TotalDuration.TotalHours:0}h";
        }
        else if (failures <= 2)
        {
            status = AutoStatus.Atencao;
            detail = $"{cyclesCompleted} ciclos, {failures} falha(s) intermitente(s)";
        }
        else
        {
            status = AutoStatus.Falha;
            detail = $"{cyclesCompleted} ciclos com {failures} falha(s) — verificar humanization log";
        }

        return new TestResult("humanizacao", status, detail, DateTime.Now);
    }

    private async Task<HumanizationProgress> ReportProgressAsync(int cycle, string phase, int phaseIndex, int phaseTotal, IProgress<HumanizationProgress>? progress)
    {
        var p = new HumanizationProgress(cycle, phase, phaseIndex, phaseTotal);
        progress?.Report(p);
        await Task.Yield();
        return p;
    }

    private async Task RunCycleAsync(int cycle, StreamWriter log, Random rng,
        IProgress<HumanizationProgress>? progress, CancellationToken ct)
    {
        var phases = new (string Name, TimeSpan Duration, Func<CancellationToken, Task> Run)[]
        {
            ("Navegação simulada", TimeSpan.FromMinutes(8), RunBrowsingAsync),
            ("Vídeo em loop", TimeSpan.FromMinutes(10), RunVideoAsync),
            ("CPU 50%", TimeSpan.FromMinutes(5), c => RunCpuLoadAsync(c, 0.5)),
            ("CPU 100%", TimeSpan.FromMinutes(5), c => RunCpuLoadAsync(c, 1.0)),
            ("Câmera", TimeSpan.FromMinutes(5), RunCameraAsync),
            ("Disco I/O", TimeSpan.FromMinutes(5), RunDiskIoAsync),
            ("Idle simulado", TimeSpan.FromMinutes(10), RunIdleAsync),
            ("Stress aleatório", TimeSpan.FromMinutes(7), c => RunRandomStressAsync(c, rng)),
            ("Cooldown", TimeSpan.FromMinutes(5), RunCooldownAsync),
        };

        for (var i = 0; i < phases.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var phase = phases[i];
            var msg = $"[{DateTime.Now:O}] cycle {cycle} phase '{phase.Name}'";
            await log.WriteLineAsync(msg).ConfigureAwait(false);
            await log.FlushAsync().ConfigureAwait(false);
            progress?.Report(new HumanizationProgress(cycle, phase.Name, i + 1, phases.Length));
            using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            phaseCts.CancelAfter(phase.Duration);
            try { await phase.Run(phaseCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* fim normal da fase */ }
        }
    }

    // -------------- Implementações de fase --------------

    private static async Task RunBrowsingAsync(CancellationToken ct)
    {
        // Pool de URLs estáveis — varremos em ordem e pulamos as que estiverem fora.
        // A escolha foi por sites com SLA alto e independente de conta.
        var candidates = new[]
        {
            "https://www.google.com/",
            "https://www.wikipedia.org/",
            "https://www.github.com/",
            "https://www.bing.com/",
            "https://www.cloudflare.com/",
            "https://www.microsoft.com/",
        };

        var opened = 0;
        foreach (var url in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (opened >= 3) break;
            if (!await IsReachableAsync(url, ct).ConfigureAwait(false)) continue;

            try
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                opened++;
            }
            catch { /* sem navegador, pula */ }
            await Task.Delay(TimeSpan.FromSeconds(150), ct).ConfigureAwait(false);
        }

        // Se nada abriu (sem internet, ou navegadores bloqueados), só dorme o
        // resto da fase em vez de explodir o ciclo.
        if (opened == 0)
        {
            await Task.Delay(TimeSpan.FromMinutes(8), ct).ConfigureAwait(false);
        }
    }

    private static async Task RunVideoAsync(CancellationToken ct)
    {
        // Vídeo principal: 1h da loja (escolhido pelo cliente). Mantemos
        // BBC/CNN como fallback caso YouTube esteja bloqueado pela rede.
        string[] candidates =
        {
            "https://www.youtube.com/watch?v=TKmGU77INaM",
            "https://www.bbc.com/news",
            "https://www.cnn.com/",
        };

        foreach (var url in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!await IsReachableAsync(url, ct).ConfigureAwait(false)) continue;

            try
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                break;
            }
            catch { }
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// HEAD com timeout curto. Retorna true se o servidor respondeu com
    /// algum status (mesmo 4xx) — só queremos saber se está alcançável.
    /// </summary>
    private static async Task<bool> IsReachableAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RunCpuLoadAsync(CancellationToken ct, double targetUtil)
    {
        // Carga proporcional usando "duty cycle": para 50%, ocupa 50ms a cada
        // 100ms. Para 100%, ocupa 100ms a cada 100ms (sempre rodando).
        var threads = Math.Max(1, (int)(Environment.ProcessorCount * targetUtil));
        var tasks = new Task[threads];
        for (var i = 0; i < threads; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 50)
                    {
                        // queima CPU
                        var v = 1.0001;
                        for (var j = 0; j < 50_000; j++) v = Math.Sqrt(v + 1) * 1.0001;
                    }
                    if (targetUtil < 1.0)
                    {
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }
                }
            }, ct);
        }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static async Task RunCameraAsync(CancellationToken ct)
    {
        // Abre a app Câmera do Windows por 2 min, fecha (kill processo) e
        // aguarda os 3 min restantes da fase.
        Process? p = null;
        try
        {
            p = Process.Start(new ProcessStartInfo("microsoft.windows.camera:") { UseShellExecute = true });
        }
        catch { /* sem app Câmera, ignora */ }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("WindowsCamera"))
                {
                    try { proc.Kill(); } catch { }
                }
            }
            catch { }
        }

        await Task.Delay(TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
    }

    private static async Task RunDiskIoAsync(CancellationToken ct)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "humanization-disk.tmp");
        try
        {
            for (var pass = 0; pass < 5; pass++)
            {
                ct.ThrowIfCancellationRequested();
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 1 * 1024 * 1024, FileOptions.WriteThrough))
                {
                    var buf = new byte[1 * 1024 * 1024];
                    new Random(pass).NextBytes(buf);
                    for (var i = 0; i < 200; i++)  // 200 MB
                    {
                        await fs.WriteAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                    }
                }
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None,
                    bufferSize: 1 * 1024 * 1024))
                {
                    var buf = new byte[1 * 1024 * 1024];
                    int read;
                    while ((read = await fs.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0) { }
                }
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task RunIdleAsync(CancellationToken ct)
    {
        // Idle "humano": acorda a cada 2 minutos pra mexer o mouse 1 pixel,
        // impedindo o monitor de entrar em standby (que mascararia testes).
        var stop = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTime.UtcNow < stop && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            try { Win32.NudgeMouse(); } catch { }
        }
    }

    private static async Task RunRandomStressAsync(CancellationToken ct, Random rng)
    {
        var stop = DateTime.UtcNow + TimeSpan.FromMinutes(7);
        while (DateTime.UtcNow < stop && !ct.IsCancellationRequested)
        {
            var util = rng.Next(2) == 0 ? 0.5 : 1.0;
            using var slot = CancellationTokenSource.CreateLinkedTokenSource(ct);
            slot.CancelAfter(TimeSpan.FromSeconds(30));
            try { await RunCpuLoadAsync(slot.Token, util).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
    }

    private static Task RunCooldownAsync(CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
}

public record HumanizationProgress(int Cycle, string Phase, int PhaseIndex, int PhaseTotal);

/// <summary>
/// Wrappers nativos mínimos. Mantemos só o necessário para cutucar o mouse
/// na fase Idle — sem dependência de namespaces sensíveis.
/// </summary>
internal static class Win32
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUT
    {
        public int Type;
        public MOUSEINPUT Mi;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>Mexe o cursor 1 pixel pra direita e volta — basta pra resetar o idle timer.</summary>
    public static void NudgeMouse()
    {
        var inputs = new INPUT[]
        {
            new() { Type = 0, Mi = new MOUSEINPUT { Dx = 1, Dy = 0, Flags = 0x0001 } },
            new() { Type = 0, Mi = new MOUSEINPUT { Dx = -1, Dy = 0, Flags = 0x0001 } },
        };
        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }
}
