using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NotebookCheck.Bootstrap;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;
using NotebookCheck.Infrastructure.Api;

namespace NotebookCheck.Application.Orchestration;

/// <summary>
/// Orquestra o reteste pós-reparo "inteligente":
///   1. Coleta hardware atual (pra obter o serial).
///   2. Busca histórico do equipamento no painel via GET /api/reports/by-serial.
///   3. Identifica quais testes/itens falharam ou ficaram em "Atenção" antes.
///   4. Executa TODOS os testes automáticos novamente, com ênfase nos
///      componentes problemáticos: rodam 2x ou em janela mais longa.
///   5. Sobrescreve o relatório original via POST /api/reports/&lt;testId&gt;/retest.
///
/// O foco do reteste é validar que o reparo (cabo flat de áudio, fita do
/// teclado, conexão da bateria, etc.) foi feito corretamente — por isso os
/// testes que falharam antes são os mais importantes.
/// </summary>
public sealed class PostRepairRetest
{
    private readonly IHardwareCollector _collector;
    private readonly ITestEngine _engine;
    private readonly IPortCollector _ports;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AppConfig _config;
    private readonly ILogger<PostRepairRetest> _logger;

    public PostRepairRetest(
        IHardwareCollector collector,
        ITestEngine engine,
        IPortCollector ports,
        IHttpClientFactory httpFactory,
        AppConfig config,
        ILogger<PostRepairRetest> logger)
    {
        _collector = collector;
        _engine = engine;
        _ports = ports;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Executa o fluxo completo: coleta hardware, busca histórico,
    /// roda testes com ênfase nas falhas anteriores e devolve o resultado.
    /// </summary>
    public async Task<RetestOutcome> RunAsync(
        IProgress<string>? progress,
        CancellationToken ct)
        => await RunAsync(progress, null, ct).ConfigureAwait(false);

    /// <summary>
    /// Variante que permite escolher explicitamente qual relatório anterior
    /// usar como base (selecionado pelo técnico), em vez do mais recente.
    /// </summary>
    public async Task<RetestOutcome> RunAsync(
        IProgress<string>? progress,
        string? historyTestIdOverride,
        CancellationToken ct)
    {
        progress?.Report("Coletando identificação do equipamento...");
        var machine = await _collector.CollectMachineAsync(ct).ConfigureAwait(false);
        var storage = await _collector.CollectStorageAsync(ct).ConfigureAwait(false);
        var battery = await _collector.CollectBatteryAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(machine.Serial))
        {
            return RetestOutcome.NoSerial();
        }

        progress?.Report("Buscando histórico no painel...");
        var history = historyTestIdOverride is not null
            ? await TryFetchHistoryByTestIdAsync(historyTestIdOverride, ct).ConfigureAwait(false)
            : await TryFetchHistoryAsync(machine.Serial!, ct).ConfigureAwait(false);

        if (history is null)
        {
            // Não tem histórico — o reteste vira um checklist normal (nenhum teste
            // anterior pra dar ênfase).
            progress?.Report("Sem histórico no painel — rodando bateria padrão...");
        }
        else
        {
            progress?.Report($"Histórico encontrado: relatório de {history.TestedAt:dd/MM/yyyy}.");
        }

        var problemKeys = ExtractProblemTestKeys(history).ToHashSet(StringComparer.OrdinalIgnoreCase);

        progress?.Report(problemKeys.Count > 0
            ? $"Ênfase em: {string.Join(", ", problemKeys)}"
            : "Sem testes prioritários identificados — bateria completa.");

        var probeUrl = string.IsNullOrWhiteSpace(_config.Options.InternetTestUrl)
            ? "https://www.gstatic.com/generate_204"
            : _config.Options.InternetTestUrl;

        // Mapa de runners. Cada teste roda 1x normalmente; testes "prioritários"
        // (que falharam antes) rodam 2x e ficamos com o pior resultado, pra
        // pegar falhas intermitentes do reparo.
        var runners = new Dictionary<string, Func<Task<TestResult>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ram"] = () => _engine.RunRamAsync(machine, ct),
            ["armazenamento"] = () => _engine.RunStorageAsync(storage, ct),
            ["saude_disco"] = () => _engine.RunStorageHealthAsync(storage, ct),
            ["bateria"] = () => _engine.RunBatteryAsync(battery, ct),
            ["carregador"] = () => _engine.RunChargerAsync(ct),
            ["hdmi"] = () => _engine.RunHdmiAsync(ct),
            ["wifi"] = () => _engine.RunWifiAsync(ct),
            ["bluetooth"] = () => _engine.RunBluetoothAsync(ct),
            ["internet"] = () => _engine.RunInternetAsync(probeUrl, ct),
            ["audio"] = () => _engine.RunAudioAsync(ct),
        };

        // Ordem de execução: prioritários primeiro.
        var orderedKeys = runners.Keys
            .OrderByDescending(k => problemKeys.Contains(k))
            .ThenBy(k => k)
            .ToList();

        var results = new Dictionary<string, TestResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in orderedKeys)
        {
            ct.ThrowIfCancellationRequested();
            var label = LabelFor(key);
            var emphasized = problemKeys.Contains(key);
            progress?.Report(emphasized
                ? $"⚠️ Reteste com ênfase: {label} (rodando 2x)..."
                : $"Executando: {label}...");

            var first = await runners[key]().ConfigureAwait(false);
            if (emphasized)
            {
                // Roda novamente e fica com o pior dos dois — assim falhas
                // intermitentes (cabo solto que só falha em frio) são pegas.
                var second = await runners[key]().ConfigureAwait(false);
                first = WorstOf(first, second);
            }
            results[key] = first;
        }

        return new RetestOutcome(
            Ok: true,
            Reason: null,
            Machine: machine,
            Storage: storage,
            Battery: battery,
            History: history,
            ProblemKeys: problemKeys,
            Tests: results);
    }

    /// <summary>
    /// Atualiza no painel o relatório encontrado anteriormente, sobrescrevendo
    /// os resultados dos testes com os do reteste atual.
    /// </summary>
    public async Task<bool> ApplyRetestAsync(string testId, ApiPayload payload, CancellationToken ct)
    {
        if (!ConfigBootstrap.TryBuildApiUri(_config, out var apiUri) || apiUri is null)
        {
            return false;
        }

        // /api/reports/<testId>/retest
        var url = $"{apiUri.GetLeftPart(UriPartial.Path)}/{Uri.EscapeDataString(testId)}/retest";
        try
        {
            using var client = _httpFactory.CreateClient("api");
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload),
            };
            if (!string.IsNullOrWhiteSpace(_config.AuthToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            }
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha aplicando reteste em {TestId}", testId);
            return false;
        }
    }

    /// <summary>
    /// Busca o relatório-base do reteste: por test_id quando o técnico escolheu
    /// um específico, senão o mais recente do serial. Público para o novo fluxo
    /// de reteste (que mostra as falhas ANTES de rodar qualquer teste).
    /// </summary>
    public Task<HistoricReport?> FetchHistoryAsync(string serial, string? testIdOverride, CancellationToken ct)
        => testIdOverride is not null
            ? TryFetchHistoryByTestIdAsync(testIdOverride, ct)
            : TryFetchHistoryAsync(serial, ct);

    /// <summary>
    /// Roda APENAS os testes automáticos selecionados (chaves que falharam no
    /// relatório anterior), cada um 2x ficando com o pior resultado — pega
    /// falhas intermitentes pós-reparo. Coleta hardware/storage/bateria para o
    /// payload. Testes manuais (câmera, teclado...) ficam a cargo da UI.
    /// </summary>
    public async Task<RetestOutcome> RunSelectedAsync(
        HistoricReport history,
        IReadOnlyCollection<string> keys,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report("Coletando identificação do equipamento...");
        var machine = await _collector.CollectMachineAsync(ct).ConfigureAwait(false);
        var storage = await _collector.CollectStorageAsync(ct).ConfigureAwait(false);
        var battery = await _collector.CollectBatteryAsync(ct).ConfigureAwait(false);

        var probeUrl = string.IsNullOrWhiteSpace(_config.Options.InternetTestUrl)
            ? "https://www.gstatic.com/generate_204"
            : _config.Options.InternetTestUrl;

        var runners = BuildRunners(machine, storage, battery, probeUrl, ct);
        var results = new Dictionary<string, TestResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            if (!runners.TryGetValue(key, out var runner)) continue;   // manuais ficam pra UI
            ct.ThrowIfCancellationRequested();
            progress?.Report($"⚠️ Retestando falha: {LabelFor(key)} (2x)...");
            var first = await runner().ConfigureAwait(false);
            var second = await runner().ConfigureAwait(false);
            results[key] = WorstOf(first, second);
        }

        return new RetestOutcome(
            Ok: true,
            Reason: null,
            Machine: machine,
            Storage: storage,
            Battery: battery,
            History: history,
            ProblemKeys: keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Tests: results);
    }

    /// <summary>Chaves de teste que este orquestrador sabe rodar sozinho (sem UI).</summary>
    public static readonly IReadOnlySet<string> AutoRunnableKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ram", "armazenamento", "saude_disco", "bateria", "carregador", "hdmi",
        "wifi", "bluetooth", "internet", "audio",
        "usb", "taxa_atualizacao", "biometria", "leitor_cartao",
    };

    private Dictionary<string, Func<Task<TestResult>>> BuildRunners(
        MachineInfo machine, IReadOnlyList<StorageInfo> storage, BatteryInfo? battery,
        string probeUrl, CancellationToken ct)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["ram"] = () => _engine.RunRamAsync(machine, ct),
            ["armazenamento"] = () => _engine.RunStorageAsync(storage, ct),
            ["saude_disco"] = () => _engine.RunStorageHealthAsync(storage, ct),
            ["bateria"] = () => _engine.RunBatteryAsync(battery, ct),
            ["carregador"] = () => _engine.RunChargerAsync(ct),
            ["hdmi"] = () => _engine.RunHdmiAsync(ct),
            ["wifi"] = () => _engine.RunWifiAsync(ct),
            ["bluetooth"] = () => _engine.RunBluetoothAsync(ct),
            ["internet"] = () => _engine.RunInternetAsync(probeUrl, ct),
            ["audio"] = () => _engine.RunAudioAsync(ct),
            ["usb"] = () => _engine.RunUsbPortsAsync(ct),
            ["taxa_atualizacao"] = () => _engine.RunRefreshRateAsync(ct),
            ["biometria"] = () => _engine.RunBiometricsAsync(ct),
            ["leitor_cartao"] = () => _engine.RunCardReaderAsync(ct),
        };

    private async Task<HistoricReport?> TryFetchHistoryAsync(string serial, CancellationToken ct)
    {
        if (!ConfigBootstrap.TryBuildApiUri(_config, out var apiUri) || apiUri is null)
        {
            _logger.LogWarning("Painel não configurado — não é possível buscar histórico");
            return null;
        }

        var url = $"{apiUri.GetLeftPart(UriPartial.Authority)}/api/reports/by-serial/{Uri.EscapeDataString(serial)}";
        try
        {
            using var client = _httpFactory.CreateClient("api");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_config.AuthToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            }
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseHistoric(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha buscando histórico do serial {Serial}", serial);
            return null;
        }
    }

    /// <summary>Busca um relatório específico por test_id para usar como base do reteste.</summary>
    private async Task<HistoricReport?> TryFetchHistoryByTestIdAsync(string testId, CancellationToken ct)
    {
        if (!ConfigBootstrap.TryBuildApiUri(_config, out var apiUri) || apiUri is null) return null;
        var url = $"{apiUri.GetLeftPart(UriPartial.Authority)}/api/reports/{Uri.EscapeDataString(testId)}";
        try
        {
            using var client = _httpFactory.CreateClient("api");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_config.AuthToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseHistoric(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha buscando relatório {TestId}", testId);
            return null;
        }
    }

    /// <summary>
    /// Lista os relatórios anteriores de um serial para o técnico escolher
    /// qual usar como base do reteste. Retorna (test_id, descrição).
    /// </summary>
    public async Task<IReadOnlyList<(string TestId, string Label)>> ListReportsBySerialAsync(
        string serial, CancellationToken ct)
    {
        var list = new List<(string, string)>();
        if (!ConfigBootstrap.TryBuildApiUri(_config, out var apiUri) || apiUri is null) return list;
        var url = $"{apiUri.GetLeftPart(UriPartial.Authority)}/api/reports/by-serial/{Uri.EscapeDataString(serial)}?all=1";
        try
        {
            using var client = _httpFactory.CreateClient("api");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_config.AuthToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return list;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in items.EnumerateArray())
                {
                    var id = it.TryGetProperty("test_id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(id)) continue;
                    var when = it.TryGetProperty("tested_at", out var w) ? w.GetString() ?? "" : "";
                    DateTime.TryParse(when, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt);
                    var cls = it.TryGetProperty("final_classification", out var c) ? c.GetString() ?? "" : "";
                    var tech = it.TryGetProperty("technician_name", out var t) ? t.GetString() ?? "" : "";
                    var ntb = it.TryGetProperty("ntb_code", out var n) ? n.GetString() ?? "" : "";
                    var label = $"{(dt == default ? "?" : dt.ToString("dd/MM/yyyy HH:mm"))} • {cls} • {ntb} • {tech}";
                    list.Add((id, label));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha listando relatórios do serial {Serial}", serial);
        }
        return list;
    }

    /// <summary>Coleta apenas o serial da máquina (para listar relatórios antes do reteste).</summary>
    public async Task<string?> GetCurrentSerialAsync(CancellationToken ct)
    {
        try
        {
            var machine = await _collector.CollectMachineAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(machine.Serial) ? null : machine.Serial;
        }
        catch { return null; }
    }

    private static HistoricReport? ParseHistoric(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("test_id", out _)) return null;
        var testId = root.GetProperty("test_id").GetString() ?? "";
        var testedAtStr = root.TryGetProperty("tested_at", out var ta) ? ta.GetString() ?? "" : "";
        DateTime.TryParse(testedAtStr, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var testedAt);
        var classification = root.TryGetProperty("final_classification", out var fc) ? fc.GetString() ?? "" : "";

        var failingTests = new List<(string Key, string Status, string Details)>();
        if (root.TryGetProperty("tests", out var tests) && tests.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in tests.EnumerateObject())
            {
                var status = entry.Value.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                var details = entry.Value.TryGetProperty("details", out var d) ? d.GetString() ?? "" : "";
                failingTests.Add((entry.Name, status, details));
            }
        }

        var failingManual = new List<(string Key, string Status, string Notes)>();
        if (root.TryGetProperty("manual_checklist", out var manual) && manual.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in manual.EnumerateObject())
            {
                var status = entry.Value.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                var notes = entry.Value.TryGetProperty("notes", out var nt) ? nt.GetString() ?? "" : "";
                failingManual.Add((entry.Name, status, notes));
            }
        }

        // Identificação do relatório antigo — usada para pré-preencher o
        // "refazer por cima" (NTB, localização, etiqueta, técnico).
        string ntb = "", location = "", assetTag = "", technician = "", backlight = "", generalNotes = "";
        bool? numpad = null, touch = null;
        if (root.TryGetProperty("machine", out var mEl) && mEl.ValueKind == JsonValueKind.Object)
        {
            if (mEl.TryGetProperty("ntb_code", out var nEl)) ntb = nEl.GetString() ?? "";
            if (mEl.TryGetProperty("location", out var lEl)) location = lEl.GetString() ?? "";
            if (mEl.TryGetProperty("keyboard_backlight", out var kEl)) backlight = kEl.GetString() ?? "";
            if (mEl.TryGetProperty("has_numeric_keypad", out var hEl))
            {
                if (hEl.ValueKind == JsonValueKind.True) numpad = true;
                else if (hEl.ValueKind == JsonValueKind.False) numpad = false;
            }
            if (mEl.TryGetProperty("has_touch_screen", out var tsEl))
            {
                if (tsEl.ValueKind == JsonValueKind.True) touch = true;
                else if (tsEl.ValueKind == JsonValueKind.False) touch = false;
            }
        }
        if (root.TryGetProperty("asset_tag", out var aEl)) assetTag = aEl.GetString() ?? "";
        if (root.TryGetProperty("technician_name", out var tEl)) technician = tEl.GetString() ?? "";
        if (root.TryGetProperty("general_notes", out var gEl)) generalNotes = gEl.GetString() ?? "";

        // Problemas registrados na INSPEÇÃO FÍSICA (fotos com status "problema")
        // — o checklist manual antigo foi substituído por esse fluxo.
        var inspectionProblems = new List<(string Key, string Label, string Note)>();
        if (root.TryGetProperty("inspection_photos", out var photos) && photos.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in photos.EnumerateArray())
            {
                var st = p.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "" : "";
                if (!string.Equals(st, "problema", StringComparison.OrdinalIgnoreCase)) continue;
                var key = p.TryGetProperty("item_key", out var kEl2) ? kEl2.GetString() ?? "" : "";
                var label = p.TryGetProperty("label", out var lEl2) ? lEl2.GetString() ?? key : key;
                var note = p.TryGetProperty("note", out var noEl) ? noEl.GetString() ?? "" : "";
                if (key.Length > 0) inspectionProblems.Add((key, label, note));
            }
        }

        // Stress antigo — preservado no "refazer por cima" (o upsert substitui o
        // documento inteiro; sem isso a nota de benchmark sumia do painel).
        StressSnapshot? stress = null;
        if (root.TryGetProperty("stress", out var st2) && st2.ValueKind == JsonValueKind.Object)
        {
            double D(string n) => st2.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
            int I(string n) => (int)Math.Round(D(n));
            long L(string n) => (long)Math.Round(D(n));
            bool B(string n, bool dft) => st2.TryGetProperty(n, out var e)
                ? e.ValueKind == JsonValueKind.True : dft;
            string? S(string n) => st2.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

            stress = new StressSnapshot(
                FinalScore: I("final_score"),
                CpuSingleThread: I("cpu_single_thread"), CpuMultiThread: I("cpu_multi_thread"),
                CpuEfficiency: I("cpu_efficiency"), CpuThreads: I("cpu_threads"),
                GpuGraphics: I("gpu_graphics"), GpuCompute: I("gpu_compute"), GpuBandwidth: I("gpu_bandwidth"),
                GpuName: S("gpu_name"), GpuFeatureLevel: S("gpu_feature_level"),
                DiskScore: I("disk_score"),
                DiskReadMbPerSec: D("disk_read_mb_s"), DiskWriteMbPerSec: D("disk_write_mb_s"),
                VramOk: B("vram_ok", true), VramAllocatedMb: I("vram_allocated_mb"), VramMismatchCount: L("vram_mismatch_count"),
                RamOk: B("ram_ok", true), RamAllocatedMb: I("ram_allocated_mb"), RamErrorCount: L("ram_error_count"),
                RamBandwidthGbs: D("ram_bandwidth_gbs"),
                GeekbenchSingle: I("geekbench_single"), GeekbenchMulti: I("geekbench_multi"),
                GeekbenchVersion: S("geekbench_version"));

            // Stress "vazio" (nunca rodou) não vale a pena preservar.
            if (stress.FinalScore == 0 && stress.CpuMultiThread == 0 && stress.DiskScore == 0
                && stress.GeekbenchMulti == 0)
            {
                stress = null;
            }
        }

        return new HistoricReport(testId, testedAt == default ? DateTime.MinValue : testedAt,
            classification, failingTests, failingManual,
            NtbCode: ntb, Location: location, AssetTag: assetTag, TechnicianName: technician,
            KeyboardBacklight: backlight, HasNumericKeypad: numpad, HasTouchScreen: touch, GeneralNotes: generalNotes,
            Stress: stress, InspectionProblems: inspectionProblems);
    }

    /// <summary>
    /// Identifica testes/itens que falharam ou ficaram em atenção no relatório
    /// anterior — esses ganham ênfase no reteste.
    /// </summary>
    private static IEnumerable<string> ExtractProblemTestKeys(HistoricReport? history)
    {
        if (history is null) yield break;

        foreach (var t in history.Tests)
        {
            if (string.Equals(t.Status, "Falha", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Status, "Atenção", StringComparison.OrdinalIgnoreCase))
            {
                yield return t.Key;
            }
        }

        // Itens manuais que estavam com defeito também sinalizam reparo a verificar
        // (ex.: dobradiça, carcaça, tela com pixel morto).
        foreach (var m in history.ManualChecklist)
        {
            if (string.Equals(m.Status, "Com defeito", StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Status, "Observação", StringComparison.OrdinalIgnoreCase))
            {
                // Mapeia chaves manuais que correspondem a testes automáticos.
                // Carregador, HDMI e teclado têm contraparte automática.
                yield return m.Key switch
                {
                    "carregador" => "carregador",
                    "hdmi" => "hdmi",
                    "teclado" => "teclado",
                    "tela" => "tela",
                    _ => m.Key,
                };
            }
        }
    }

    private static TestResult WorstOf(TestResult a, TestResult b)
    {
        // Severidade: NaoAplicavel < NaoTestado < OK < Atencao < Falha
        int Score(AutoStatus s) => s switch
        {
            AutoStatus.NaoAplicavel => 0,
            AutoStatus.NaoTestado => 1,
            AutoStatus.OK => 2,
            AutoStatus.Atencao => 3,
            AutoStatus.Falha => 4,
            _ => 1,
        };
        return Score(a.Status) >= Score(b.Status) ? a : b;
    }

    private static string LabelFor(string key) => key switch
    {
        "ram" => "Memória RAM",
        "armazenamento" => "Armazenamento",
        "saude_disco" => "Saúde do disco",
        "bateria" => "Bateria",
        "carregador" => "Carregador",
        "hdmi" => "HDMI / monitor externo",
        "wifi" => "Wi-Fi",
        "bluetooth" => "Bluetooth",
        "internet" => "Internet",
        "audio" => "Áudio",
        _ => key,
    };
}

/// <summary>Resultado consolidado de um reteste pós-reparo.</summary>
public record RetestOutcome(
    bool Ok,
    string? Reason,
    MachineInfo? Machine,
    IReadOnlyList<StorageInfo>? Storage,
    BatteryInfo? Battery,
    HistoricReport? History,
    IReadOnlySet<string>? ProblemKeys,
    IReadOnlyDictionary<string, TestResult>? Tests)
{
    public static RetestOutcome NoSerial() => new(false, "Serial não detectado", null, null, null, null, null, null);
}

/// <summary>Snapshot do relatório anterior usado pra orientar a ênfase.</summary>
public record HistoricReport(
    string TestId,
    DateTime TestedAt,
    string FinalClassification,
    List<(string Key, string Status, string Details)> Tests,
    List<(string Key, string Status, string Notes)> ManualChecklist,
    string NtbCode = "",
    string Location = "",
    string AssetTag = "",
    string TechnicianName = "",
    string KeyboardBacklight = "",
    bool? HasNumericKeypad = null,
    bool? HasTouchScreen = null,
    string GeneralNotes = "",
    StressSnapshot? Stress = null,
    List<(string Key, string Label, string Note)>? InspectionProblems = null)
{
    /// <summary>
    /// Itens problemáticos do relatório: testes em Falha/Atenção, itens manuais
    /// Com defeito/Observação e fotos da INSPEÇÃO FÍSICA marcadas "Com problema"
    /// — a base do "retestar só as falhas".
    /// </summary>
    public List<(string Key, string Status, string Details)> Failures()
    {
        var list = new List<(string, string, string)>();
        foreach (var t in Tests)
        {
            if (string.Equals(t.Status, "Falha", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Status, "Atenção", StringComparison.OrdinalIgnoreCase))
            {
                list.Add((t.Key, t.Status, t.Details));
            }
        }
        foreach (var m in ManualChecklist)
        {
            if (string.Equals(m.Status, "Com defeito", StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Status, "Observação", StringComparison.OrdinalIgnoreCase))
            {
                list.Add((m.Key, m.Status, m.Notes));
            }
        }
        foreach (var (key, label, note) in InspectionProblems ?? new())
        {
            list.Add((key, "Com problema",
                string.IsNullOrWhiteSpace(note) ? $"{label} (inspeção física)" : $"{label}: {note}"));
        }
        return list;
    }
}
