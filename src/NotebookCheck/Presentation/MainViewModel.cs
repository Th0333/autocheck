using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NotebookCheck.Application.Api;
using NotebookCheck.Application.Orchestration;
using NotebookCheck.Application.Sync;
using NotebookCheck.Bootstrap;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;
using NotebookCheck.Domain.Rules;
using NotebookCheck.Domain.Validation;
using NotebookCheck.Infrastructure.Api;

namespace NotebookCheck.Presentation;

/// <summary>
/// ViewModel central que conduz o checklist em modo wizard de etapas.
/// O fluxo agora começa pela <see cref="WizardStep.Identification"/> onde o
/// técnico informa o apelido/código NTB e a localização — ambos obrigatórios
/// antes de iniciar a coleta — alinhando com o requisito de catalogação por
/// número de estoque.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IHardwareCollector _collector;
    private readonly Infrastructure.Persistence.SerialNtbStore _serialNtb;
    private readonly Infrastructure.Persistence.MachineIdentityStore _machineIdentity;
    private readonly Infrastructure.Persistence.AssumidosStore _assumidos;
    private readonly Infrastructure.Abstractions.IWmiQueryRunner _wmi;
    private readonly ITestEngine _engine;
    private readonly IPortCollector _portCollector;
    private readonly IKeyboardBacklightDetector _backlight;
    private readonly IReportRepository _repo;
    private readonly IReportArchive _archive;
    private readonly IOfflineQueue _queue;
    private readonly IApiClient _api;
    private readonly OfflineSyncService _sync;
    private readonly IRetestController _retest;
    private readonly Application.Orchestration.PostRepairRetest _postRepairRetest;
    private readonly ChecklistSession _session;
    private readonly AppConfig _config;
    private readonly ConfigBootstrap _bootstrap;
    private readonly Application.Bench.BenchmarkSuite _stress;
    private readonly Application.Humanization.HumanizationRunner _humanization;
    private readonly Infrastructure.Hardware.CrystalDiskInfoRunner _crystalDiskInfo;
    private readonly ILogger<MainViewModel> _logger;

    /// <summary>Mapa estável: chave snake_case do teste → função que reexecuta.</summary>
    private readonly Dictionary<string, Func<Task<TestResult>>> _testRunners = new();
    /// <summary>Rótulos exibidos para cada teste, para upsert na grade.</summary>
    private readonly Dictionary<string, string> _testLabels = new();

    [ObservableProperty] private WizardStep currentStep = WizardStep.Start;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private int pendingCount;
    [ObservableProperty] private string version = "1.0.0";

    public ObservableCollection<TestResultRow> TestResults { get; } = new();
    public ObservableCollection<StorageRow> StorageRows { get; } = new();
    public ObservableCollection<ManualItemRow> ManualItems { get; } = new();

    /// <summary>Itens do changelog da versão atual, exibidos na tela inicial.</summary>
    public ObservableCollection<string> Changelog { get; } = new();

    /// <summary>Linhas de resultado do benchmark exibidas no resumo.</summary>
    public ObservableCollection<BenchSummaryRow> BenchSummary { get; } = new();

    /// <summary>True se há resultado de benchmark para mostrar no resumo.</summary>
    [ObservableProperty] private bool hasBenchSummary;

    /// <summary>True se há changelog para mostrar o card de "Novidades".</summary>
    [ObservableProperty] private bool hasChangelog;

    [ObservableProperty] private string ntbCode = "";
    [ObservableProperty] private string locationField = "";
    [ObservableProperty] private string assetTag = "";
    [ObservableProperty] private string generalNotes = "";
    [ObservableProperty] private string technicianName = "";
    [ObservableProperty] private bool keyboardBacklightSim;
    [ObservableProperty] private bool keyboardBacklightNao;
    [ObservableProperty] private string keyboardBacklightDetected = "Indeterminado";
    [ObservableProperty] private bool numericKeypadSim;
    [ObservableProperty] private bool numericKeypadNao;

    /// <summary>Marcam em vermelho os campos obrigatórios da inspeção ao tentar avançar.</summary>
    [ObservableProperty] private bool keyboardBacklightInvalid;
    [ObservableProperty] private bool numericKeypadInvalid;
    /// <summary>True quando faltam fotos da inspeção física ao tentar avançar.</summary>
    [ObservableProperty] private bool inspectionIncomplete;
    /// <summary>Mensagem de aviso visível na etapa de inspeção (vazio = sem aviso).</summary>
    [ObservableProperty] private string manualWarning = "";

    [ObservableProperty] private string identificationSummary = "";
    [ObservableProperty] private string finalClassification = "";

    /// <summary>Modo do checklist escolhido na tela inicial.</summary>
    [ObservableProperty] private ChecklistMode checklistMode = ChecklistMode.Padrao;

    /// <summary>True quando o técnico escolheu explicitamente um modo (obrigatório).</summary>
    [ObservableProperty] private bool modeChosen;

    /// <summary>Marca os campos obrigatórios vazios em vermelho ao tentar avançar.</summary>
    [ObservableProperty] private bool ntbInvalid;
    [ObservableProperty] private bool technicianInvalid;

    /// <summary>Texto curto do equipamento para o cabeçalho.</summary>
    [ObservableProperty] private string deviceHeadline = "";

    /// <summary>Maior etapa já alcançada (índice do enum). Usada pelo StepRail para habilitar saltos para frente após retroceder.</summary>
    [ObservableProperty] private int highWaterMark;

    // ---- Etapa de Desempenho: cada benchmark tem botão próprio iniciar/parar ----
    [ObservableProperty] private string stressPhase = "Pronto";
    [ObservableProperty] private int stressProgress;
    [ObservableProperty] private string liveTelemetryText = "";

    // Estado e resultado por categoria
    [ObservableProperty] private bool cpuBenchRunning;
    [ObservableProperty] private string cpuBenchText = "Não testado";
    [ObservableProperty] private bool gpuBenchRunning;
    [ObservableProperty] private string gpuBenchText = "Não testado";
    [ObservableProperty] private bool diskBenchRunning;
    [ObservableProperty] private string diskBenchText = "Não testado";
    [ObservableProperty] private bool vramBenchRunning;
    [ObservableProperty] private string vramBenchText = "Não testado";
    [ObservableProperty] private bool ramBenchRunning;
    [ObservableProperty] private string ramBenchText = "Não testado";

    /// <summary>Nota final consolidada dos benchmarks (0 = sem testes).</summary>
    [ObservableProperty] private int finalBenchScore;
    /// <summary>Texto da nota final exibido na etapa de desempenho.</summary>
    [ObservableProperty] private string finalBenchText = "Rode os testes para ver a nota final";
    /// <summary>True enquanto a sequência "rodar todos" está em execução.</summary>
    [ObservableProperty] private bool allBenchRunning;
    public string AllBenchButton => AllBenchRunning ? "⏹ Parar sequência" : "▶ Rodar todos os testes";
    partial void OnAllBenchRunningChanged(bool value) { OnPropertyChanged(nameof(AllBenchButton)); AnyBenchChanged(); }

    public string CpuBenchButton => CpuBenchRunning ? "⏹ Parar" : "▶ CPU";
    public string GpuBenchButton => GpuBenchRunning ? "⏹ Parar" : "▶ GPU";
    public string DiskBenchButton => DiskBenchRunning ? "⏹ Parar" : "▶ Disco";
    public string VramBenchButton => VramBenchRunning ? "⏹ Parar" : "▶ VRAM";
    public string RamBenchButton => RamBenchRunning ? "⏹ Parar" : "▶ RAM";

    partial void OnCpuBenchRunningChanged(bool value) { OnPropertyChanged(nameof(CpuBenchButton)); AnyBenchChanged(); }
    partial void OnGpuBenchRunningChanged(bool value) { OnPropertyChanged(nameof(GpuBenchButton)); AnyBenchChanged(); }
    partial void OnDiskBenchRunningChanged(bool value) { OnPropertyChanged(nameof(DiskBenchButton)); AnyBenchChanged(); }
    partial void OnVramBenchRunningChanged(bool value) { OnPropertyChanged(nameof(VramBenchButton)); AnyBenchChanged(); }
    partial void OnRamBenchRunningChanged(bool value) { OnPropertyChanged(nameof(RamBenchButton)); AnyBenchChanged(); }

    // Limpa os indicadores de obrigatório quando o técnico responde.
    partial void OnKeyboardBacklightSimChanged(bool value) { if (value) KeyboardBacklightInvalid = false; }
    partial void OnKeyboardBacklightNaoChanged(bool value) { if (value) KeyboardBacklightInvalid = false; }
    partial void OnNumericKeypadSimChanged(bool value) { if (value) NumericKeypadInvalid = false; }
    partial void OnNumericKeypadNaoChanged(bool value) { if (value) NumericKeypadInvalid = false; }

    /// <summary>True se qualquer benchmark próprio está rodando (bloqueia os demais).</summary>
    public bool AnyBenchRunning => CpuBenchRunning || GpuBenchRunning || DiskBenchRunning
        || VramBenchRunning || RamBenchRunning || AllBenchRunning;

    private void AnyBenchChanged() => OnPropertyChanged(nameof(AnyBenchRunning));

    // ---- UserBenchmark (externo, abre no navegador) ----
    [ObservableProperty] private string userBenchmarkStatus = "";

    // ---- Inspeção física via QR + celular ----
    [ObservableProperty] private System.Windows.Media.Imaging.BitmapSource? inspectionQr;
    [ObservableProperty] private string inspectionUrl = "";
    [ObservableProperty] private string inspectionStatus = "Iniciando servidor de inspeção...";
    [ObservableProperty] private int inspectionDoneCount;
    [ObservableProperty] private int inspectionTotalCount;
    public ObservableCollection<InspectionItemRow> InspectionItems { get; } = new();

    // ---- Humanização (etapa de Desempenho) ----
    [ObservableProperty] private bool humanizationRunning;
    [ObservableProperty] private string humanizationStatus = "Não iniciada";

    public ObservableCollection<HardwareField> SystemFields { get; } = new();
    public ObservableCollection<HardwareField> SecurityFields { get; } = new();
    public ObservableCollection<HardwareField> NetworkFields { get; } = new();
    public ObservableCollection<HardwareField> DisplayFields { get; } = new();
    public ObservableCollection<HardwareField> StorageFields { get; } = new();
    public ObservableCollection<HardwareField> BatteryFields { get; } = new();
    public ObservableCollection<PortRow> Ports { get; } = new();

    // ---- Bateria: barra de carga + cabeçalho visual ----
    /// <summary>True quando há uma bateria com carga conhecida (mostra a barra).</summary>
    [ObservableProperty] private bool batteryHasCharge;
    /// <summary>Percentual de carga atual (0–100) para a barra de progresso.</summary>
    [ObservableProperty] private int batteryChargePercent;
    /// <summary>Texto sobreposto à barra (ex.: "82% • Carregando").</summary>
    [ObservableProperty] private string batteryChargeText = "";
    /// <summary>Nome/identificação da bateria exibido no topo do card.</summary>
    [ObservableProperty] private string batteryTitle = "Bateria";

    // ---- Resultados de teclado/touchpad (mostrados na etapa Inputs) ----
    [ObservableProperty] private string keyboardResult = "Não testado";
    [ObservableProperty] private string touchpadResult = "Não testado";

    // ---- Resultados térmico/bateria (mostrados na etapa Desempenho) ----
    [ObservableProperty] private string throttleResult = "Não testado";
    [ObservableProperty] private string batteryDischargeResult = "Não testado";

    public MainViewModel(
        IHardwareCollector collector,
        Infrastructure.Persistence.SerialNtbStore serialNtb,
        Infrastructure.Persistence.MachineIdentityStore machineIdentity,
        Infrastructure.Persistence.AssumidosStore assumidos,
        Infrastructure.Abstractions.IWmiQueryRunner wmi,
        ITestEngine engine,
        IPortCollector portCollector,
        IKeyboardBacklightDetector backlight,
        IReportRepository repo,
        IReportArchive archive,
        IOfflineQueue queue,
        IApiClient api,
        OfflineSyncService sync,
        IRetestController retest,
        Application.Orchestration.PostRepairRetest postRepairRetest,
        ChecklistSession session,
        AppConfig config,
        ConfigBootstrap bootstrap,
        Application.Bench.BenchmarkSuite stress,
        Application.Humanization.HumanizationRunner humanization,
        Infrastructure.Hardware.CrystalDiskInfoRunner crystalDiskInfo,
        ILogger<MainViewModel> logger)
    {
        _collector = collector;
        _serialNtb = serialNtb;
        _machineIdentity = machineIdentity;
        _assumidos = assumidos;
        _wmi = wmi;
        _engine = engine;
        _portCollector = portCollector;
        _backlight = backlight;
        _repo = repo;
        _archive = archive;
        _queue = queue;
        _api = api;
        _sync = sync;
        _retest = retest;
        _postRepairRetest = postRepairRetest;
        _session = session;
        _config = config;
        _bootstrap = bootstrap;
        _stress = stress;
        _humanization = humanization;
        _crystalDiskInfo = crystalDiskInfo;
        _logger = logger;

        try { Version = Bootstrap.AppDefaults.CurrentVersion; } catch { }

        PendingCount = _queue.Count;
        _sync.PendingChanged += (_, n) =>
        {
            if (System.Windows.Application.Current?.Dispatcher is { } d)
            {
                d.BeginInvoke(() => PendingCount = n);
            }
        };

        // Itens manuais conforme Req. 17
        foreach (var key in new[] { "carcaca", "tela", "teclado", "touchpad", "dobradicas", "usb", "hdmi", "carregador" })
        {
            ManualItems.Add(new ManualItemRow(key));
        }

        // Changelog da versão atual (obtido no startup pelo auto-updater).
        var changes = App.LatestChangelog;
        if (changes is { Length: > 0 })
        {
            foreach (var c in changes) Changelog.Add(c);
            HasChangelog = true;
        }
    }

    [RelayCommand]
    private void StartChecklist()
    {
        StatusMessage = "";
        TestResults.Clear();
        StorageRows.Clear();
        Goto(WizardStep.ModeSelect);
    }

    [RelayCommand]
    private void SelectModeAndContinue(string? mode)
    {
        ChecklistMode = (mode ?? "").ToLowerInvariant() switch
        {
            "basico" => ChecklistMode.Basico,
            "detalhado" => ChecklistMode.Detalhado,
            "desktop" => ChecklistMode.Desktop,
            _ => ChecklistMode.Padrao,
        };
        _session.Mode = ChecklistMode;
        ModeChosen = true;
        // O conjunto de testes depende do modo (Desktop é reduzido). Limpa para
        // que a grade seja re-semeada ao entrar em AutoTests.
        _testRunners.Clear();
        _testLabels.Clear();
        TestResults.Clear();
        StatusMessage = $"Modo: {ChecklistMode}";
        Goto(WizardStep.Identification);
        // Tenta achar o NTB pelo serial da máquina (cadastrada antes no estoque).
        _ = TryPrefillNtbFromSerialAsync();
    }

    /// <summary>
    /// Abre o wizard de "Cadastro no estoque" (integração com o ERP). Não faz
    /// parte do checklist — é um fluxo separado em janela própria.
    /// </summary>
    [RelayCommand]
    private void OpenCadastro()
    {
        try
        {
            var host = (System.Windows.Application.Current as App)?.Host;
            if (host is null) return;
            var window = host.Services.GetRequiredService<Views.CadastroWindow>();
            window.Owner = System.Windows.Application.Current?.MainWindow;
            window.Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha abrindo janela de cadastro no estoque");
            StatusMessage = $"Erro: {ex.Message}";
        }
    }

    /// <summary>
    /// Abre o wizard de "Teste completo" (check automático): escolhe a máquina na
    /// fila do ERP, roda os testes e reporta. Janela própria, fora do checklist.
    /// </summary>
    [RelayCommand]
    private void OpenTesteCompleto()
    {
        try
        {
            var host = (System.Windows.Application.Current as App)?.Host;
            if (host is null) return;
            var window = host.Services.GetRequiredService<Views.TesteCompletoWindow>();
            window.Owner = System.Windows.Application.Current?.MainWindow;
            window.Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha abrindo janela de teste completo");
            StatusMessage = $"Erro: {ex.Message}";
        }
    }

    /// <summary>
    /// Abre os testes avulsos de componentes (memória, SSD, bateria) —
    /// independentes do checklist e da fila do ERP.
    /// </summary>
    [RelayCommand]
    private void OpenTesteComponentes()
    {
        try
        {
            var host = (System.Windows.Application.Current as App)?.Host;
            if (host is null) return;
            var window = host.Services.GetRequiredService<Views.TesteComponentesWindow>();
            window.Owner = System.Windows.Application.Current?.MainWindow;
            window.Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha abrindo janela de teste de componentes");
            StatusMessage = $"Erro: {ex.Message}";
        }
    }

    /// <summary>Abre o kanban das máquinas dos pedidos de compra (ERP).</summary>
    [RelayCommand]
    private void OpenKanban()
    {
        try
        {
            var host = (System.Windows.Application.Current as App)?.Host;
            if (host is null) return;
            var window = host.Services.GetRequiredService<Views.KanbanWindow>();
            window.Owner = System.Windows.Application.Current?.MainWindow;
            window.Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha abrindo janela do kanban");
            StatusMessage = $"Erro: {ex.Message}";
        }
    }

    /// <summary>
    /// Preenche o NTB automaticamente: primeiro pelo arquivo de identidade
    /// gravado NA máquina no cadastro de estoque (machine-identity.json em
    /// ProgramData), depois pelo mapa serial → NTB do diretório do app.
    /// </summary>
    private async Task TryPrefillNtbFromSerialAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(NtbCode)) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var rows = await _wmi.QueryAsync(
                "root\\cimv2", "SELECT SerialNumber FROM Win32_BIOS",
                TimeSpan.FromSeconds(6), cts.Token).ConfigureAwait(true);
            var serial = rows.Count > 0 ? rows[0].TryGetValue("SerialNumber", out var v) ? v?.ToString() : null : null;

            // Arquivo de identidade da máquina (não depende do pendrive do técnico).
            var identity = _machineIdentity.TryRead();
            if (identity is not null && !string.IsNullOrWhiteSpace(identity.Ntb))
            {
                var serialConfere = string.IsNullOrWhiteSpace(serial) ||
                    string.IsNullOrWhiteSpace(identity.Serial) ||
                    string.Equals(identity.Serial!.Trim(), serial!.Trim(), StringComparison.OrdinalIgnoreCase);
                if (serialConfere && string.IsNullOrWhiteSpace(NtbCode))
                {
                    NtbCode = identity.Ntb!;

                    // Máquina assumida no kanban: preenche também o técnico.
                    var assumido = _assumidos.Get(identity.AssetId);
                    if (assumido is not null && string.IsNullOrWhiteSpace(TechnicianName))
                        TechnicianName = assumido.Tecnico;

                    StatusMessage = assumido is null
                        ? "NTB preenchido automaticamente (máquina cadastrada no estoque)."
                        : $"NTB e técnico ({assumido.Tecnico}) preenchidos automaticamente (máquina assumida no kanban).";
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(serial)) return;

            var ntb = _serialNtb.Lookup(serial);
            if (!string.IsNullOrWhiteSpace(ntb) && string.IsNullOrWhiteSpace(NtbCode))
            {
                NtbCode = ntb!;
                StatusMessage = $"NTB preenchido automaticamente (serial {serial!.Trim()} cadastrado no estoque).";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Prefill de NTB por serial falhou (ignorado)");
        }
    }

    [RelayCommand]
    private void OpenReports()
    {
        try
        {
            var host = (System.Windows.Application.Current as App)?.Host;
            if (host is null) return;
            var window = host.Services.GetRequiredService<Views.ReportsWindow>();
            window.Owner = System.Windows.Application.Current?.MainWindow;
            window.Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha abrindo janela de relatórios");
            StatusMessage = $"Erro: {ex.Message}";
        }
    }

    /// <summary>
    /// Submete os dados de identificação informados pelo técnico, valida e
    /// avança para a coleta de hardware.
    /// </summary>
    [RelayCommand]
    private async Task SubmitIdentificationAsync()
    {
        // Normaliza o código para o padrão de estoque "NTB-XXX": quem digita só
        // o número ganha o prefixo; quem já digitou "ntb" (qualquer caixa, com
        // ou sem separador) tem o prefixo padronizado sem duplicar.
        NtbCode = NormalizeNtbCode(NtbCode);

        var errors = new List<string>(ManualChecklistValidator.ValidateIdentification(NtbCode, LocationField, AssetTag));
        // Marca os campos obrigatórios vazios para destaque visual.
        NtbInvalid = string.IsNullOrWhiteSpace(NtbCode);
        TechnicianInvalid = string.IsNullOrWhiteSpace(TechnicianName);
        if (TechnicianInvalid)
        {
            errors.Add("Informe o técnico responsável (obrigatório).");
        }
        if (errors.Count > 0)
        {
            StatusMessage = string.Join(" | ", errors);
            return;
        }

        _session.NtbCode = NtbCode.Trim();
        _session.Location = LocationField.Trim();
        _session.AssetTag = AssetTag?.Trim() ?? "";
        _session.TechnicianName = TechnicianName.Trim();

        StatusMessage = "";
        Goto(WizardStep.Hardware);
        await CollectIdentificationAsync();
    }

    [RelayCommand]
    private async Task StartRetestAsync()
    {
        IsBusy = true;
        try
        {
            // 1. Coleta o serial e lista os relatórios anteriores desse equipamento.
            StatusMessage = "Identificando equipamento...";
            var serial = await _postRepairRetest.GetCurrentSerialAsync(CancellationToken.None);
            if (string.IsNullOrWhiteSpace(serial))
            {
                MessageBox.Show("Serial não detectado — não dá para localizar o relatório anterior.",
                    "Reteste", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? chosenTestId = null;
            StatusMessage = "Buscando relatórios anteriores...";
            var reports = await _postRepairRetest.ListReportsBySerialAsync(serial!, CancellationToken.None);
            if (reports.Count > 0)
            {
                var dlg = new Views.RetestSelectWindow(reports)
                {
                    Owner = System.Windows.Application.Current?.MainWindow,
                };
                var sel = dlg.ShowDialog();
                if (sel != true) { StatusMessage = "Reteste cancelado"; return; }
                chosenTestId = dlg.SelectedTestId; // null = mais recente/automático
            }

            // 2. Busca o relatório-base ANTES de rodar qualquer teste.
            StatusMessage = "Carregando relatório anterior...";
            var history = await _postRepairRetest.FetchHistoryAsync(serial!, chosenTestId, CancellationToken.None);
            if (history is null)
            {
                MessageBox.Show(
                    "Nenhum relatório anterior encontrado para este equipamento.\n\n" +
                    "Rode um checklist completo normal (botão 'Iniciar checklist').",
                    "Reteste", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusMessage = "Sem relatório anterior — use o checklist completo.";
                return;
            }

            // 3. Plano: mostra as falhas do relatório e deixa o técnico escolher.
            EnsureTestRunnersRegistered(); // garante labels para a lista
            var failures = history.Failures()
                .Select(f => new Views.RetestPlanWindow.FailureRow(f.Key, RetestLabelFor(f.Key), f.Status, f.Details))
                .ToList();

            var plan = new Views.RetestPlanWindow(history, failures)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            plan.ShowDialog();

            switch (plan.Choice)
            {
                case Views.RetestPlanWindow.PlanChoice.OnlyFailures:
                    await RunFailuresRetestAsync(history, failures);
                    break;
                case Views.RetestPlanWindow.PlanChoice.FullOverwrite:
                    BeginOverwriteChecklist(history);
                    break;
                default:
                    StatusMessage = "Reteste cancelado";
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no reteste");
            StatusMessage = $"Erro: {ex.Message}";
            MessageBox.Show($"Erro: {ex.Message}", "Reteste", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Testes do técnico que o reteste de falhas sabe reabrir (janelas).</summary>
    private static readonly HashSet<string> ManualRetestKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "estereo", "microfone", "camera", "tela", "brilho", "teclado", "touchpad",
        "descarga_bateria", "throttling",
    };

    /// <summary>
    /// OPÇÃO A — checklist especial só com o que constou como falha:
    /// roda os testes automáticos falhos (2x, pior resultado), abre as janelas
    /// dos testes manuais falhos, mescla com os resultados antigos (os OK
    /// permanecem) e sobrescreve o relatório no painel via /retest.
    /// </summary>
    private async Task RunFailuresRetestAsync(
        Application.Orchestration.HistoricReport history,
        IReadOnlyList<Views.RetestPlanWindow.FailureRow> failures)
    {
        var failureKeys = failures.Select(f => f.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var autoKeys = failureKeys
            .Where(k => Application.Orchestration.PostRepairRetest.AutoRunnableKeys.Contains(k)).ToList();
        var manualKeys = failureKeys.Where(k => ManualRetestKeys.Contains(k)).ToList();
        // Itens da inspeção física manual (carcaça, dobradiça...) não têm teste
        // automatizável — permanecem como estavam e o técnico ajusta no site.

        var progress = new Progress<string>(s => StatusMessage = s);

        // Testes automáticos falhos (também coleta hardware p/ payload).
        var outcome = await _postRepairRetest.RunSelectedAsync(history, autoKeys, progress, CancellationToken.None);

        // Testes manuais falhos: reabre as janelas; resultados caem em _session.Tests.
        _session.Tests.Clear();
        foreach (var key in manualKeys)
        {
            StatusMessage = $"Teste do técnico: {RetestLabelFor(key)}...";
            await RunManualTestByKeyAsync(key);
        }

        // Merge: status antigos → sobrescritos pelos retestados.
        var merged = new Dictionary<string, Domain.Models.TestResult>(StringComparer.OrdinalIgnoreCase);
        var oldDate = history.TestedAt == DateTime.MinValue ? DateTime.Now : history.TestedAt;
        foreach (var (key, status, details) in history.Tests)
            merged[key] = new Domain.Models.TestResult(key, ParseAutoStatus(status), details, oldDate);
        foreach (var kv in outcome.Tests!) merged[kv.Key] = kv.Value;
        foreach (var key in manualKeys)
            if (_session.Tests.TryGetValue(key, out var r)) merged[key] = r;

        // Checklist manual antigo preservado (o /retest substitui o documento).
        var manualDict = new Dictionary<string, Domain.Models.ManualCheckItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, status, notes) in history.ManualChecklist)
            manualDict[key] = new Domain.Models.ManualCheckItem(key, ParseManualStatus(status), notes ?? "");

        var finalClass = Domain.Rules.DomainRules.ClassifyFinal(
            merged.Values.Select(t => t.Status),
            manualDict.Values.Select(m => m.Status));

        // Resumo só do que foi de fato RE-EXECUTADO (problemas de inspeção
        // física não têm teste — aparecem no plano, mas não aqui).
        var executedKeys = autoKeys.Concat(manualKeys).ToList();
        var retested = merged.Where(kv => executedKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        var lines = string.Join("\n", retested.Select(kv =>
            $"  • {RetestLabelFor(kv.Key)}: {kv.Value.Status} — {kv.Value.Details}"));
        var inspectionNote = failureKeys.Count > executedKeys.Count
            ? "\n(Problemas de inspeção física não são retestáveis pelo app — ajuste as fotos/avaliações pelo celular ou site.)\n"
            : "";
        var ans = MessageBox.Show(
            $"Reteste das falhas concluído ({retested.Count} item(ns)):\n\n{lines}\n{inspectionNote}\n" +
            $"Classificação resultante: {PayloadBuilder.MapFinal(finalClass)}\n\n" +
            "Sobrescrever o relatório no painel?",
            "Reteste de falhas", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (ans != MessageBoxResult.Yes) { StatusMessage = "Reteste não enviado."; return; }

        var tech = string.IsNullOrWhiteSpace(TechnicianName) ? history.TechnicianName : TechnicianName;
        var machine = outcome.Machine! with { NtbCode = history.NtbCode, Location = history.Location };
        var report = new Domain.Models.ChecklistReport(
            TestId: Guid.TryParse(history.TestId, out var g) ? g : Guid.NewGuid(),
            TestedAt: DateTime.Now,
            TechnicianName: tech,
            Machine: machine,
            Storage: outcome.Storage!,
            Battery: outcome.Battery,
            Tests: merged,
            ManualChecklist: manualDict,
            GeneralNotes: $"Reteste de falhas — {string.Join(", ", failureKeys.Select(RetestLabelFor))}",
            AssetTag: history.AssetTag,
            FinalClassification: finalClass,
            FinalClassificationOverrideReason: null,
            Mode: ChecklistMode.Padrao);

        var payload = PayloadBuilder.Build(report);
        payload = CloneAsRetest(payload, executedKeys,
            $"Reteste de falhas sobre o relatório de {history.TestedAt:yyyy-MM-dd}");

        var ok = await _postRepairRetest.ApplyRetestAsync(history.TestId, payload, CancellationToken.None);
        StatusMessage = ok
            ? $"Relatório atualizado no painel — falhas retestadas ({history.TestId[..8]}…)"
            : "Reteste salvo localmente — falha ao atualizar o painel";
        try { await _archive.AppendAsync(payload, CancellationToken.None); } catch { /* ignore */ }
    }

    /// <summary>
    /// OPÇÃO B — refazer o checklist completo POR CIMA do relatório antigo:
    /// inicia o fluxo normal reaproveitando o test_id (o envio faz upsert e
    /// sobrescreve no painel), pré-preenche a identificação e PRESERVA tudo que
    /// o novo fluxo não re-executar — testes antigos, stress, checklist manual,
    /// retroiluminação, numpad e observações. Sem isso, o upsert apagava do
    /// painel qualquer dado que a nova passada não produzisse.
    /// </summary>
    private void BeginOverwriteChecklist(Application.Orchestration.HistoricReport history)
    {
        StartChecklist();   // limpa grades e vai para a seleção de modo

        if (Guid.TryParse(history.TestId, out var g)) _session.AdoptTestId(g);
        if (!string.IsNullOrWhiteSpace(history.NtbCode)) NtbCode = history.NtbCode;
        if (!string.IsNullOrWhiteSpace(history.Location)) LocationField = history.Location;
        if (!string.IsNullOrWhiteSpace(history.AssetTag)) AssetTag = history.AssetTag;
        if (string.IsNullOrWhiteSpace(TechnicianName) && !string.IsNullOrWhiteSpace(history.TechnicianName))
            TechnicianName = history.TechnicianName;

        // Testes antigos entram na sessão como ponto de partida: cada teste
        // re-executado sobrescreve o seu; os demais permanecem no relatório.
        _session.Tests.Clear();
        var oldDate = history.TestedAt == DateTime.MinValue ? DateTime.Now : history.TestedAt;
        foreach (var (key, status, details) in history.Tests)
            _session.Tests[key] = new Domain.Models.TestResult(key, ParseAutoStatus(status), details, oldDate);

        // Stress/benchmark antigo (a nota some do painel se não preservar).
        _session.StressResult = history.Stress;

        // Checklist manual antigo pré-preenchido nas linhas da etapa Manual.
        foreach (var (key, status, notes) in history.ManualChecklist)
        {
            var row = ManualItems.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
            if (row is null) continue;
            row.Status = status switch
            {
                "Com defeito" => "Com defeito",
                "Não testado" => "Não testado",
                "Observação" => "Observação",
                _ => "OK",
            };
            row.Notes = notes ?? "";
        }

        // Retroiluminação, numpad e observações gerais do relatório antigo.
        if (history.KeyboardBacklight.Equals("sim", StringComparison.OrdinalIgnoreCase))
        { KeyboardBacklightSim = true; KeyboardBacklightNao = false; }
        else if (history.KeyboardBacklight.Equals("nao", StringComparison.OrdinalIgnoreCase))
        { KeyboardBacklightSim = false; KeyboardBacklightNao = true; }
        if (history.HasNumericKeypad is bool np)
        { NumericKeypadSim = np; NumericKeypadNao = !np; }
        if (!string.IsNullOrWhiteSpace(history.GeneralNotes)) GeneralNotes = history.GeneralNotes;

        var when = history.TestedAt == DateTime.MinValue ? "?" : history.TestedAt.ToString("dd/MM/yyyy");
        StatusMessage = $"Refazendo checklist por cima do relatório de {when} — dados antigos preservados; " +
                        "cada teste re-executado substitui o anterior. Ao enviar, o painel é sobrescrito.";
    }

    /// <summary>Reabre o teste manual correspondente à chave (mesmos casos do ↻).</summary>
    private async Task RunManualTestByKeyAsync(string key)
    {
        switch (key.ToLowerInvariant())
        {
            case "estereo": await RunStereoAsync(); break;
            case "microfone": await RunMicAsync(); break;
            case "camera": await RunWebcamAsync(); break;
            case "tela": await RunPixelTestAsync(); break;
            case "brilho": await RunBrightnessTestAsync(); break;
            case "teclado": await RunKeyboardTestAsync(); break;
            case "touchpad": await RunTouchpadTestAsync(); break;
            case "descarga_bateria": await RunBatteryDischargeTestAsync(); break;
            case "throttling": await RunThrottleTestAsync(); break;
        }
    }

    /// <summary>Rótulo amigável de qualquer chave (teste automático, manual, item físico ou foto de inspeção).</summary>
    private string RetestLabelFor(string key)
    {
        if (_testLabels.TryGetValue(key, out var l)) return l;
        foreach (var (k, label) in ManualTestSeed)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return label;
        if (Domain.Models.InspectionCatalog.Find(key) is { } insp) return $"{insp.Label} (inspeção)";
        var mi = ManualItems.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
        return mi?.Display ?? key;
    }

    private static AutoStatus ParseAutoStatus(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "ok" => AutoStatus.OK,
        "atenção" or "atencao" => AutoStatus.Atencao,
        "falha" => AutoStatus.Falha,
        "não aplicável" or "nao aplicavel" => AutoStatus.NaoAplicavel,
        _ => AutoStatus.NaoTestado,
    };

    private static Domain.Enums.ManualStatus ParseManualStatus(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "ok" => Domain.Enums.ManualStatus.OK,
        "com defeito" => Domain.Enums.ManualStatus.ComDefeito,
        "observação" or "observacao" => Domain.Enums.ManualStatus.Observacao,
        _ => Domain.Enums.ManualStatus.NaoTestado,
    };

    /// <summary>
    /// Cópia do payload com <c>report_type = retest</c> e a lista dos itens
    /// retestados. new + init porque <c>ApiPayload</c> é classe (sem <c>with</c>).
    /// </summary>
    private static ApiPayload CloneAsRetest(ApiPayload p, IReadOnlyCollection<string> retestedKeys, string repairNotes)
    {
        return new ApiPayload
        {
            TestId = p.TestId,
            ReportType = "retest",
            TestedAt = p.TestedAt,
            TechnicianName = p.TechnicianName,
            Machine = p.Machine,
            Storage = p.Storage,
            Battery = p.Battery,
            Tests = p.Tests,
            ManualChecklist = p.ManualChecklist,
            RetestedComponents = retestedKeys.ToList(),
            RepairNotes = repairNotes,
            GeneralNotes = p.GeneralNotes,
            AssetTag = p.AssetTag,
            FinalClassification = p.FinalClassification,
            FinalClassificationOverrideReason = p.FinalClassificationOverrideReason,
            ChecklistMode = p.ChecklistMode,
            Stress = p.Stress,
        };
    }

    private async Task CollectIdentificationAsync()
    {
        IsBusy = true;
        StatusMessage = "Coletando identificação do equipamento...";
        try
        {
            var ct = CancellationToken.None;
            _session.Machine = await _collector.CollectMachineAsync(ct);
            _session.Storage = await _collector.CollectStorageAsync(ct);
            // Desktop não tem bateria — pula a leitura (o card também fica oculto).
            _session.Battery = ChecklistMode == ChecklistMode.Desktop
                ? null
                : await _collector.CollectBatteryAsync(ct);

            var detected = await _backlight.DetectAsync(ct);
            _session.KeyboardBacklightDetected = detected;
            KeyboardBacklightDetected = detected switch
            {
                KeyboardBacklight.Sim => "Sim",
                KeyboardBacklight.Nao => "Não",
                _ => "Indeterminado",
            };
            // Detecção conclusiva sobrescreve; indeterminada PRESERVA o que já
            // estiver marcado (ex.: pré-preenchido pelo reteste "refazer por cima").
            if (detected is KeyboardBacklight.Sim or KeyboardBacklight.Nao)
            {
                KeyboardBacklightSim = detected == KeyboardBacklight.Sim;
                KeyboardBacklightNao = detected == KeyboardBacklight.Nao;
            }

            var m = _session.Machine;
            DeviceHeadline = $"{(m.Manufacturer ?? "—")} {(m.Model ?? "")}".Trim();
            if (string.IsNullOrWhiteSpace(DeviceHeadline)) DeviceHeadline = m.Hostname;

            // Memoriza serial→NTB: num reteste futuro desta máquina o NTB já vem
            // preenchido (mesma lógica do cadastro no estoque).
            _serialNtb.Save(m.Serial, NtbCode);

            BuildHardwareFields(m);

            IdentificationSummary =
                $"Fabricante: {m.Manufacturer ?? "-"}\n" +
                $"Modelo: {m.Model ?? "-"}\n" +
                $"Serial: {m.Serial ?? "-"}\n" +
                $"Hostname: {m.Hostname}\n" +
                $"CPU: {m.Cpu ?? "-"}\n" +
                $"RAM: {m.RamGb:F1} GB\n" +
                $"OS: {m.Os} {m.OsVersion}\n" +
                $"Resolução: {m.ScreenResolution}\n" +
                $"MAC: {m.MacAddress ?? "-"}\n" +
                $"TPM: {m.Tpm} ({m.TpmVersion ?? "-"})\n" +
                $"SecureBoot: {m.SecureBoot}\n" +
                $"Autopilot (beta): {FormatAutopilotFlag(m.Autopilot)}\n" +
                $"Ativação: {m.WindowsActivation}";

            StorageRows.Clear();
            foreach (var s in _session.Storage)
            {
                StorageRows.Add(new StorageRow($"#{s.Index} {s.Type} {s.CapacityGb:F1} GB", s.SmartStatus.ToString()));
            }

            StatusMessage = "Identificação concluída.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha coletando identificação");
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ConfirmHardware()
    {
        Goto(WizardStep.AutoTests);
        // Pré-carrega a lista de testes vazia com "Não executado" pra que o
        // técnico veja quais existem e possa rodar individualmente.
        EnsureTestRunnersRegistered();
        SeedTestRowsAsPending();
    }

    /// <summary>
    /// Registra runners de cada teste sem executar nada. Chamado ao entrar
    /// no passo AutoTests.
    /// </summary>
    private void EnsureTestRunnersRegistered()
    {
        if (_testRunners.Count > 0) return;

        var ct = CancellationToken.None;
        var probeUrl = string.IsNullOrWhiteSpace(_config.Options.InternetTestUrl)
            ? "https://www.gstatic.com/generate_204"
            : _config.Options.InternetTestUrl;

        // Desktop: bateria reduzida — só Internet e Portas USB são automáticos
        // (o teste de som fica na seção do técnico). Os demais não se aplicam.
        if (ChecklistMode == ChecklistMode.Desktop)
        {
            Register("internet", "Internet", () => _engine.RunInternetAsync(probeUrl, ct));
            Register("usb", "Portas USB", () => _engine.RunUsbPortsAsync(ct));
        }
        else
        {
            // RAM e Disco removidos dos testes automáticos: já aparecem em detalhe
            // na etapa Hardware (quantidade, tipo, velocidade, SMART, vida útil) e o
            // teste real de integridade da RAM é o stress, na etapa Desempenho.
            Register("bateria", "Bateria", () => _engine.RunBatteryAsync(_session.Battery, ct));
            Register("carregador", "Carregador", () => _engine.RunChargerAsync(ct));
            Register("wifi", "Wi-Fi", () => _engine.RunWifiAsync(ct));
            Register("bluetooth", "Bluetooth", () => _engine.RunBluetoothAsync(ct));
            Register("internet", "Internet", () => _engine.RunInternetAsync(probeUrl, ct));
            Register("usb", "Portas USB", () => _engine.RunUsbPortsAsync(ct));
            Register("taxa_atualizacao", "Taxa de atualização", () => _engine.RunRefreshRateAsync(ct));
            Register("biometria", "Digital / Câmera IR", () => _engine.RunBiometricsAsync(ct));
            Register("leitor_cartao", "Leitor de cartão", () => _engine.RunCardReaderAsync(ct));
            Register("hdmi", "HDMI / monitor externo", () => _engine.RunHdmiAsync(ct));
        }

        void Register(string key, string label, Func<Task<TestResult>> run)
        {
            _testLabels[key] = label;
            _testRunners[key] = run;
        }
    }

    /// <summary>
    /// Testes manuais/de outras etapas, em ordem fixa. Eles NÃO são semeados
    /// como pendentes: só entram na grade quando o técnico os executa (o site
    /// acusa os que faltaram como "não realizados"). A lista existe para manter
    /// a ordem e o rótulo ao restaurar resultados da sessão na navegação.
    /// </summary>
    private static readonly (string Key, string Label)[] ManualTestSeed =
    {
        ("estereo", "Áudio estéreo"),
        ("microfone", "Microfone"),
        ("camera", "Câmera"),
        ("tela", "Tela"),
        ("brilho", "Brilho da tela"),
        ("teclado", "Teclado"),
        ("touchpad", "Touchpad"),
        ("throttling", "Throttling térmico"),
    };

    /// <summary>Testes manuais que entram na grade conforme o modo (Desktop = só som).</summary>
    private (string Key, string Label)[] ManualSeedForMode() =>
        ChecklistMode == ChecklistMode.Desktop
            ? new[] { ("estereo", "Áudio estéreo") }
            : ManualTestSeed;

    /// <summary>
    /// Popula a grade: testes automáticos aparecem como "Não executado" para o
    /// técnico rodar; os manuais só aparecem se JÁ foram executados (resultado
    /// preservado na sessão — antes eles sumiam ao navegar e voltar).
    /// </summary>
    private void SeedTestRowsAsPending()
    {
        TestResults.Clear();

        foreach (var key in _testRunners.Keys)
        {
            // Se já temos resultado da sessão (caso volte e refresque), preserva.
            if (_session.Tests.TryGetValue(key, out var existing))
            {
                TestResults.Add(new TestResultRow(key, _testLabels[key],
                    StatusDisplay(existing.Status), existing.Details, existing.Comment));
            }
            else
            {
                TestResults.Add(new TestResultRow(key, _testLabels[key],
                    "Não executado", "Clique no + para abrir este teste"));
            }
        }

        // Manuais: só os que o técnico JÁ fez, na ordem fixa do modo.
        foreach (var (key, label) in ManualSeedForMode())
        {
            if (_session.Tests.TryGetValue(key, out var done))
            {
                TestResults.Add(new TestResultRow(key, label,
                    StatusDisplay(done.Status), done.Details, done.Comment));
            }
        }

        // Catch-all: qualquer outro resultado da sessão (ex.: "audio" de versões
        // antigas, "stress") também aparece — nada some da grade.
        foreach (var (key, result) in _session.Tests)
        {
            if (TestResults.Any(t => t.TestKey == key)) continue;
            var label = _testLabels.TryGetValue(key, out var l) ? l : key;
            TestResults.Add(new TestResultRow(key, label,
                StatusDisplay(result.Status), result.Details, result.Comment));
        }
    }

    /// <summary>
    /// Executa toda a bateria de testes automáticos em sequência. Disparado
    /// apenas pelo botão "▶ Rodar todos" da tela de testes.
    /// </summary>
    [RelayCommand]
    private async Task RunAllTestsAsync()
    {
        if (_isRunningAll) return;
        _isRunningAll = true;
        IsBusy = true;
        var ct = CancellationToken.None;

        async Task RunOne(string key)
        {
            if (!_testRunners.TryGetValue(key, out var runner)) return;
            StatusMessage = $"Executando: {_testLabels[key]}";
            var r = await runner().ConfigureAwait(true);
            _session.Tests[r.TestKey] = r;
            UpsertTestRow(_testLabels[key], r);
        }

        try
        {
            foreach (var key in _testRunners.Keys.ToList())
            {
                await RunOne(key);
            }
            StatusMessage = "Testes automáticos concluídos.";

            // Stress só quando Detalhado, e ainda assim não automático aqui:
            // o botão dedicado "🔥 Stress test" abaixo já cobre.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha rodando todos os testes");
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _isRunningAll = false;
        }
    }

    private bool _isRunningAll;

    /// <summary>True depois que o relatório foi enviado/arquivado no resumo —
    /// libera a navegação para a etapa "Concluído".</summary>
    private bool _reportSent;

    private async Task RunAutomaticTestsAsync()
    {
        // Mantido só para compatibilidade interna, redireciona para RunAllTestsAsync.
        await RunAllTestsAsync();
    }

    /// <summary>
    /// Refaz um teste individual a partir do botão "Refazer" da linha. Aceita
    /// a chave snake_case do teste (mesma usada em <see cref="TestResult.TestKey"/>).
    /// Para testes que precisam de input do técnico (camera, tela, áudio,
    /// microfone, estéreo) delega para os comandos específicos.
    /// </summary>
    [RelayCommand]
    private async Task RetryTestAsync(string testKey)
    {
        if (string.IsNullOrEmpty(testKey)) return;

        // Casos especiais que abrem janelas / dependem do técnico
        switch (testKey)
        {
            case "camera": await RunWebcamAsync(); return;
            case "tela": await RunPixelTestAsync(); return;
            case "brilho": await RunBrightnessTestAsync(); return;
            case "audio": await RunAudioAsync(); return;
            case "estereo": await RunStereoAsync(); return;
            case "microfone": await RunMicAsync(); return;
            case "teclado": await RunKeyboardTestAsync(); return;
            case "touchpad": await RunTouchpadTestAsync(); return;
            case "throttling": await RunThrottleTestAsync(); return;
            case "descarga_bateria": await RunBatteryDischargeTestAsync(); return;
        }

        if (!_testRunners.TryGetValue(testKey, out var runner) ||
            !_testLabels.TryGetValue(testKey, out var label))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Refazendo: {label}";
        try
        {
            var r = await runner().ConfigureAwait(true);
            _session.Tests[r.TestKey] = r;
            UpsertTestRow(label, r);
            StatusMessage = $"{label}: {r.Status}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha refazendo {Test}", testKey);
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private void UpsertTestRow(string label, TestResult r)
    {
        var existing = TestResults.FirstOrDefault(t => t.TestKey == r.TestKey);
        if (existing != null)
        {
            // Se o técnico já fez override, preserva o status escolhido.
            if (existing.IsOverridden)
            {
                existing.Details = r.Details;
                return;
            }
            // Atualiza IN-PLACE para a linha manter a posição fixa na grade
            // (Remove+Add jogava o teste recém-rodado para o fim da lista).
            existing.Status = StatusDisplay(r.Status);
            existing.Details = r.Details;
            return;
        }
        TestResults.Add(new TestResultRow(r.TestKey, label, StatusDisplay(r.Status), r.Details));
    }

    /// <summary>
    /// Texto de exibição do status — DEVE bater com os itens do ComboBox da
    /// grade ("Atenção", "Não testado"...), senão o combo aparece vazio.
    /// </summary>
    private static string StatusDisplay(AutoStatus s) => s switch
    {
        AutoStatus.OK => "OK",
        AutoStatus.Atencao => "Atenção",
        AutoStatus.Falha => "Falha",
        AutoStatus.NaoTestado => "Não testado",
        AutoStatus.NaoAplicavel => "Não aplicável",
        _ => s.ToString(),
    };

    /// <summary>
    /// Aplica a override de status de uma linha específica diretamente na
    /// <see cref="ChecklistSession"/>. Chamada pelo ComboBox da grade.
    /// </summary>
    [RelayCommand]
    private void OverrideTestStatus(TestResultRow? row)
    {
        if (row is null) return;
        if (!_session.Tests.TryGetValue(row.TestKey, out var current)) return;

        var parsed = row.Status switch
        {
            "OK" => AutoStatus.OK,
            "Atenção" => AutoStatus.Atencao,
            "Falha" => AutoStatus.Falha,
            "Não testado" => AutoStatus.NaoTestado,
            "Não aplicável" => AutoStatus.NaoAplicavel,
            _ => current.Status,
        };
        if (parsed == current.Status) return;

        _session.Tests[row.TestKey] = current with { Status = parsed };
        row.IsOverridden = true;
        StatusMessage = $"{row.Label}: status alterado manualmente para {row.Status}";
    }

    /// <summary>
    /// Abre o modal de ação de um teste: trocar o resultado já feito (dropdown),
    /// comentar (botão "+") ou refazer do zero. Disparado pelos botões "+" da
    /// grade e pelos botões da seção "Testes que precisam do técnico".
    /// </summary>
    [RelayCommand]
    private async Task OpenTestActionAsync(string? testKey)
    {
        if (string.IsNullOrEmpty(testKey)) return;
        await Task.Yield();
        var label = RetestLabelFor(testKey);

        _session.Tests.TryGetValue(testKey, out var current);
        var hasResult = current is not null;
        var statusDisplay = hasResult ? StatusDisplay(current!.Status) : "Não testado";

        // Monta o teste DENTRO do modal: painel inline (áudio/mic/brilho) ou
        // um botão que abre o teste em tela cheia (câmera/pixels/teclado/...).
        UserControl? inline = null;
        Func<System.Windows.Window, Task<(string?, string)>>? runner = null;
        var runLabel = "Rodar teste";

        switch (testKey)
        {
            case "estereo":
                inline = new Views.StereoTestControl
                {
                    PlayAction = () => _engine.RunStereoAsync(CancellationToken.None),
                };
                break;
            case "microfone":
                inline = new Views.MicTestControl();
                break;
            case "brilho":
                inline = new Views.BrightnessTestControl(_logger);
                break;
            case "camera":
                runLabel = "Abrir câmera";
                runner = async owner =>
                {
                    var cams = await _engine.DetectCamerasAsync(CancellationToken.None).ConfigureAwait(true);
                    var d = new Views.WebcamTestWindow(cams) { Owner = owner };
                    d.ShowDialog();
                    return (BoolToStatus(d.Result), d.Message);
                };
                break;
            case "tela":
                runLabel = "Abrir padrões de tela";
                runner = owner => { var d = new Views.PixelTestWindow { Owner = owner }; d.ShowDialog(); return Task.FromResult(((string?)null, $"Visualizou {d.CompletedColors} de {d.TotalPatterns} padrões")); };
                break;
            case "teclado":
                runLabel = "Abrir teclado";
                runner = owner => { var d = new Views.KeyboardTestWindow { Owner = owner }; d.ShowDialog(); return Task.FromResult((BoolToStatus(d.Result), d.Message)); };
                break;
            case "touchpad":
                runLabel = "Abrir touchpad";
                runner = owner => { var d = new Views.TouchpadTestWindow { Owner = owner }; d.ShowDialog(); return Task.FromResult((BoolToStatus(d.Result), d.Message)); };
                break;
            case "throttling":
                runLabel = "Rodar throttling (~60s)";
                runner = owner => { var d = new Views.ThrottleTestWindow(_logger) { Owner = owner }; d.ShowDialog(); return Task.FromResult((BoolToStatus(d.Result), d.Message)); };
                break;
            case "descarga_bateria":
                runLabel = "Rodar descarga";
                runner = owner => { var d = new Views.BatteryDischargeTestWindow { Owner = owner }; d.ShowDialog(); return Task.FromResult((BoolToStatus(d.Result), d.Message)); };
                break;
            default:
                // Teste automático (motor): roda direto e devolve status/detalhe.
                if (_testRunners.TryGetValue(testKey, out var auto))
                {
                    runLabel = "Rodar teste";
                    runner = async _ =>
                    {
                        var r = await auto().ConfigureAwait(true);
                        _session.Tests[r.TestKey] = r;
                        return ((string?)StatusDisplay(r.Status), r.Details);
                    };
                }
                break;
        }

        var dlg = new Views.TestActionWindow(
            label, statusDisplay, current?.Details ?? "", current?.Comment ?? "", hasResult,
            inline, runner, runLabel)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        dlg.ShowDialog();

        if (dlg.Result != Views.TestActionWindow.Action.Save) return;

        // Microfone: guarda o áudio para o botão "Reproduzir gravação".
        if (testKey == "microfone" && dlg.CapturedWav is { Length: > 0 } wav)
        {
            _lastMicWav = wav;
            HasMicRecording = true;
        }

        var newStatus = ParseAutoStatus(dlg.ChosenStatus);
        var details = string.IsNullOrWhiteSpace(dlg.FinalDetails) ? (current?.Details ?? "") : dlg.FinalDetails;

        // Base: o resultado mais recente da sessão (o runner pode tê-lo atualizado).
        _session.Tests.TryGetValue(testKey, out var afterDialog);
        var baseRes = afterDialog ?? current
            ?? new Domain.Models.TestResult(testKey, newStatus, details, DateTime.Now);
        var prevStatus = (afterDialog ?? current)?.Status;

        var updated = baseRes with { Status = newStatus, Details = details, Comment = dlg.Comment };
        _session.Tests[testKey] = updated;

        var row = TestResults.FirstOrDefault(t => t.TestKey == testKey);
        if (row is null)
        {
            row = new TestResultRow(testKey, label, StatusDisplay(newStatus), updated.Details);
            TestResults.Add(row);
        }
        else
        {
            row.Status = StatusDisplay(newStatus);
            row.Details = updated.Details;
        }
        row.Comment = dlg.Comment;
        if (prevStatus is { } ps && newStatus != ps) row.IsOverridden = true;

        StatusMessage = $"{label}: {StatusDisplay(newStatus)}" +
            (string.IsNullOrWhiteSpace(dlg.Comment) ? "" : " • comentário salvo");
    }

    /// <summary>Mapeia o resultado bool? das janelas de teste para o texto do status.</summary>
    private static string? BoolToStatus(bool? ok) => ok switch
    {
        true => "OK",
        false => "Falha",
        _ => null,   // null = técnico não decidiu → mantém o status atual
    };

    [RelayCommand]
    private async Task RunAudioAsync()
    {
        IsBusy = true; StatusMessage = "Reproduzindo tom...";
        var r = await _engine.RunAudioAsync(CancellationToken.None);
        _session.Tests[r.TestKey] = r;
        UpsertTestRow("Áudio", r);
        StatusMessage = $"Áudio: {r.Status}";
        IsBusy = false;
    }

    [RelayCommand]
    private async Task RunStereoAsync()
    {
        IsBusy = true;
        StatusMessage = "Reproduzindo tom no canal esquerdo, depois no direito...";
        var r = await _engine.RunStereoAsync(CancellationToken.None);
        _session.Tests[r.TestKey] = r;
        UpsertTestRow("Áudio estéreo", r);
        StatusMessage = $"Estéreo: {r.Status} — confirme se ouviu os dois lados";
        IsBusy = false;
    }

    [RelayCommand]
    private async Task RunWebcamAsync()
    {
        IsBusy = true;
        StatusMessage = "Detectando câmeras...";
        try
        {
            var cams = await _engine.DetectCamerasAsync(CancellationToken.None);
            var dlg = new Views.WebcamTestWindow(cams)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            dlg.ShowDialog();

            // Confiamos no que o técnico marcou. Se ele fechou sem responder
            // (Result == null), tratamos como "não testado".
            if (dlg.Result is null)
            {
                StatusMessage = "Câmera: cancelado pelo técnico";
                return;
            }

            var r = _engine.BuildWebcamResult(dlg.Result.Value, dlg.Message);
            _session.Tests[r.TestKey] = r;
            UpsertTestRow("Câmera", r);
            StatusMessage = $"Câmera: {r.Status}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no teste de câmera");
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RunHumanizationAsync()
    {
        if (ChecklistMode == ChecklistMode.Basico)
        {
            HumanizationStatus = "Humanização não está disponível no modo Básico.";
            return;
        }
        if (HumanizationRunning) return;

        HumanizationRunning = true;
        HumanizationStatus = "Humanização iniciada — pode levar até 12 horas...";
        _humanizationCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<Application.Humanization.HumanizationProgress>(p =>
                HumanizationStatus = $"Ciclo {p.Cycle} • {p.Phase} ({p.PhaseIndex}/{p.PhaseTotal})");
            var r = await _humanization.RunAsync(progress, _humanizationCts.Token);
            _session.Tests[r.TestKey] = r;
            UpsertTestRow("Humanização (12h)", r);
            HumanizationStatus = $"{r.Status} — {r.Details}";
        }
        catch (OperationCanceledException)
        {
            HumanizationStatus = "Humanização interrompida pelo técnico.";
        }
        finally
        {
            HumanizationRunning = false;
            _humanizationCts?.Dispose();
            _humanizationCts = null;
        }
    }

    [RelayCommand]
    private void StopHumanization()
    {
        _humanizationCts?.Cancel();
    }

    [RelayCommand]
    private async Task CompareWithSameSpecsAsync()
    {
        if (_session.StressResult is null)
        {
            MessageBox.Show(
                "Esta máquina não tem stress test. Rode no modo Detalhado para gerar pontuação comparável.",
                "Comparação", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ConfigBootstrap.TryBuildApiUri(_config, out var apiUri) || apiUri is null)
        {
            MessageBox.Show("Painel não configurado.", "Comparação", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        StatusMessage = "Buscando ranking...";
        try
        {
            var cpu = _session.Machine?.Cpu ?? "";
            var gpu = _session.StressResult.GpuName ?? "";
            var url = $"{apiUri.GetLeftPart(UriPartial.Authority)}/api/ranking?cpu={Uri.EscapeDataString(cpu)}&gpu={Uri.EscapeDataString(gpu)}&by=combo";

            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                MessageBox.Show($"Painel respondeu {(int)resp.StatusCode}.", "Comparação", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var body = await resp.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("groups", out var groups) || groups.GetArrayLength() == 0)
            {
                MessageBox.Show(
                    "Nenhuma máquina com mesmo CPU + GPU foi testada ainda.",
                    "Comparação", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var first = groups[0];
            var best = first.GetProperty("best").GetInt32();
            var avg = first.GetProperty("avg").GetInt32();
            var worst = first.GetProperty("worst").GetInt32();
            var count = first.GetProperty("count").GetInt32();
            var mine = _session.StressResult.FinalScore;

            var diff = mine - avg;
            var sign = diff >= 0 ? "acima" : "abaixo";
            MessageBox.Show(
                $"Sua máquina: {mine}\n" +
                $"\n{count} máquina(s) com mesma combinação CPU + GPU já testadas:\n" +
                $"  Melhor: {best}\n" +
                $"  Média:  {avg}\n" +
                $"  Pior:   {worst}\n" +
                $"\nVocê está {Math.Abs(diff)} pontos {sign} da média.",
                "Comparação com mesmas specs",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro: {ex.Message}", "Comparação", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            StatusMessage = "";
        }
    }

    private CancellationTokenSource? _cpuCts, _gpuCts, _diskCts, _vramCts, _ramCts;
    private System.Windows.Threading.DispatcherTimer? _stressTelemetryTimer;
    private Application.Bench.HardwareMonitor? _stressMonitor;
    private int _telemetryUsers;

    private Progress<Application.Bench.BenchProgress> MakeProgress() =>
        new(p =>
        {
            StressPhase = p.Phase;
            // Normaliza para 0..100: alguns benches reportam Current/Total
            // (ex.: passo 3 de 12), outros já reportam direto em porcentagem.
            var total = p.Total <= 0 ? 100 : p.Total;
            var pct = (int)Math.Round(p.Current * 100.0 / total);
            StressProgress = Math.Clamp(pct, 0, 100);
        });

    private CancellationTokenSource? _allBenchCts;

    /// <summary>
    /// Roda todos os benchmarks em sequência (CPU → GPU → Disco → VRAM → RAM)
    /// e calcula a NOTA FINAL consolidada ao terminar. Reaproveita o
    /// BenchmarkSuite, que já pondera os testes e devolve o score final.
    /// </summary>
    // AllowConcurrentExecutions: sem isto, o AsyncRelayCommand desabilita o
    // próprio botão enquanto roda (CanExecute=false), e o clique em "Parar"
    // (mesmo botão) nunca dispara o cancelamento.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunAllBenchmarks()
    {
        if (AllBenchRunning) { _allBenchCts?.Cancel(); return; }
        if (AnyBenchRunning) { StatusMessage = "Aguarde o teste atual terminar."; return; }

        _allBenchCts = new CancellationTokenSource();
        AllBenchRunning = true;
        StressProgress = 0;
        StartStressTelemetry();
        try
        {
            var totalRamBytes = (long)((_session.Machine?.RamGb ?? 0) * 1024m * 1024m * 1024m);
            var outcome = await _stress.RunAllAsync(totalRamBytes, MakeProgress(), _allBenchCts.Token);

            // Atualiza os cartões individuais com o que rodou.
            if (outcome.Cpu is { } c)
            {
                CpuBenchText = $"ST {c.SingleThreadScore} • MT {c.MultiThreadScore} • {c.Threads}T";
                UpdateStressSnapshotCpu(c);
            }
            if (outcome.Gpu is { } g)
            {
                GpuBenchText = g.Ok ? $"Graf {g.GraphicsScore} • Comp {g.ComputeScore} • BW {g.BandwidthScore}" : "Indisponível";
                UpdateStressSnapshotGpu(g);
            }
            if (outcome.Disk is { } d)
            {
                DiskBenchText = $"Nota {d.Score} • ↓{d.ReadMbPerSec:F0} ↑{d.WriteMbPerSec:F0} MB/s";
                UpdateStressSnapshotDisk(d);
            }
            if (outcome.Vram is { } v)
            {
                VramBenchText = v.Ok ? $"OK • {v.AllocatedMb} MB" : $"Erros ({v.MismatchCount})";
                UpdateStressSnapshotVram(v);
            }
            if (outcome.Ram is { } r)
            {
                RamBenchText = r.Ok ? $"OK • {r.AllocatedMb} MB • {r.BandwidthGbs:F1} GB/s" : $"Erros ({r.ErrorCount})";
                UpdateStressSnapshotRam(r);
            }

            // Grava a nota final no snapshot e na UI.
            _session.StressResult = CurrentOrNewSnapshot() with { FinalScore = outcome.FinalScore };
            FinalBenchScore = outcome.FinalScore;
            FinalBenchText = $"Nota final: {outcome.FinalScore} (100 = referência)";
            StressPhase = "Todos os testes concluídos";
            StressProgress = 100;
        }
        catch (OperationCanceledException) { StressPhase = "Sequência cancelada"; FinalBenchText = "Sequência cancelada"; }
        catch (Exception ex) { _logger.LogError(ex, "Rodar todos os benchmarks"); FinalBenchText = "Erro ao rodar a sequência"; }
        finally { AllBenchRunning = false; StopStressTelemetry(); _allBenchCts?.Dispose(); _allBenchCts = null; }
    }

    /// <summary>Recalcula a nota final a partir dos snapshots individuais já presentes.</summary>
    private void RecomputeFinalScore()
    {
        var s = _session.StressResult;
        if (s is null) { FinalBenchScore = 0; FinalBenchText = "Rode os testes para ver a nota final"; return; }
        var score = Application.Bench.BenchmarkSuite.ComputeFinalFromSnapshot(s);
        FinalBenchScore = score;
        FinalBenchText = score > 0 ? $"Nota final parcial: {score} (100 = referência)" : "Rode os testes para ver a nota final";
        _session.StressResult = s with { FinalScore = score };
    }

    // ---------- CPU ----------
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunCpuBench()
    {
        if (CpuBenchRunning) { _cpuCts?.Cancel(); return; }
        _cpuCts = new CancellationTokenSource();
        CpuBenchRunning = true;
        StressProgress = 0;
        StartStressTelemetry();
        try
        {
            var r = await _stress.Cpu.RunAsync(MakeProgress(), _cpuCts.Token);
            CpuBenchText = $"ST {r.SingleThreadScore} • MT {r.MultiThreadScore} • {r.Threads}T";
            UpdateStressSnapshotCpu(r);
            StressPhase = "CPU concluído";
        }
        catch (OperationCanceledException) { CpuBenchText = "Cancelado"; StressPhase = "Cancelado"; }
        catch (Exception ex) { CpuBenchText = "Erro"; _logger.LogError(ex, "CPU bench"); }
        finally { CpuBenchRunning = false; StopStressTelemetry(); _cpuCts?.Dispose(); _cpuCts = null; }
    }

    // ---------- GPU ----------
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunGpuBench()
    {
        if (GpuBenchRunning) { _gpuCts?.Cancel(); return; }
        _gpuCts = new CancellationTokenSource();
        GpuBenchRunning = true;
        StressProgress = 0;
        StartStressTelemetry();
        try
        {
            var r = await _stress.Gpu.RunAsync(MakeProgress(), _gpuCts.Token);
            GpuBenchText = r.Ok
                ? $"Graf {r.GraphicsScore} • Comp {r.ComputeScore} • BW {r.BandwidthScore}"
                : "Indisponível";
            UpdateStressSnapshotGpu(r);
            StressPhase = "GPU concluído";
        }
        catch (OperationCanceledException) { GpuBenchText = "Cancelado"; StressPhase = "Cancelado"; }
        catch (Exception ex) { GpuBenchText = "Erro"; _logger.LogError(ex, "GPU bench"); }
        finally { GpuBenchRunning = false; StopStressTelemetry(); _gpuCts?.Dispose(); _gpuCts = null; }
    }

    // ---------- Disco ----------
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunDiskBench()
    {
        if (DiskBenchRunning) { _diskCts?.Cancel(); return; }
        _diskCts = new CancellationTokenSource();
        DiskBenchRunning = true;
        StressProgress = 0;
        try
        {
            var r = await _stress.Disk.RunAsync(TimeSpan.FromSeconds(20), MakeProgress(), _diskCts.Token);
            DiskBenchText = $"Nota {r.Score} • ↓{r.ReadMbPerSec:F0} ↑{r.WriteMbPerSec:F0} MB/s";
            UpdateStressSnapshotDisk(r);
            StressPhase = "Disco concluído";
        }
        catch (OperationCanceledException) { DiskBenchText = "Cancelado"; StressPhase = "Cancelado"; }
        catch (Exception ex) { DiskBenchText = "Erro"; _logger.LogError(ex, "Disk bench"); }
        finally { DiskBenchRunning = false; _diskCts?.Dispose(); _diskCts = null; }
    }

    // ---------- VRAM ----------
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunVramBench()
    {
        if (VramBenchRunning) { _vramCts?.Cancel(); return; }
        _vramCts = new CancellationTokenSource();
        VramBenchRunning = true;
        StressProgress = 0;
        try
        {
            var r = await _stress.VramStressTest.RunAsync(MakeProgress(), _vramCts.Token);
            VramBenchText = r.Ok ? $"OK • {r.AllocatedMb} MB" : $"Erros ({r.MismatchCount})";
            UpdateStressSnapshotVram(r);
            StressPhase = "VRAM concluído";
        }
        catch (OperationCanceledException) { VramBenchText = "Cancelado"; StressPhase = "Cancelado"; }
        catch (Exception ex) { VramBenchText = "Erro"; _logger.LogError(ex, "VRAM bench"); }
        finally { VramBenchRunning = false; _vramCts?.Dispose(); _vramCts = null; }
    }

    // ---------- RAM ----------
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunRamBench()
    {
        if (RamBenchRunning) { _ramCts?.Cancel(); return; }
        _ramCts = new CancellationTokenSource();
        RamBenchRunning = true;
        StressProgress = 0;
        try
        {
            // Passa o total real de RAM (do WMI) para o teste mirar quase toda.
            var totalRamBytes = (long)((_session.Machine?.RamGb ?? 0) * 1024m * 1024m * 1024m);
            var r = await _stress.RamStressTest.RunAsync(totalRamBytes, MakeProgress(), _ramCts.Token);
            RamBenchText = r.Ok ? $"OK • {r.AllocatedMb} MB • {r.BandwidthGbs:F1} GB/s" : $"Erros ({r.ErrorCount})";
            UpdateStressSnapshotRam(r);
            StressPhase = "RAM concluído";
        }
        catch (OperationCanceledException) { RamBenchText = "Cancelado"; StressPhase = "Cancelado"; }
        catch (Exception ex) { RamBenchText = "Erro"; _logger.LogError(ex, "RAM bench"); }
        finally { RamBenchRunning = false; _ramCts?.Dispose(); _ramCts = null; }
    }

    /// <summary>
    /// Abre o UserBenchmark oficial no navegador. Não pode ser embutido nem
    /// automatizado (licença + upload online), então baixamos/abrimos o site
    /// oficial e o técnico roda por fora. Os resultados são comparados com
    /// outros dispositivos no site userbenchmark.com.
    /// </summary>
    /// <summary>Abre a janela de sensores ao vivo (temperatura, clocks, ventoinhas).</summary>
    [RelayCommand]
    private void OpenSensors()
    {
        try
        {
            var win = new Views.SensorsWindow(_logger)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            win.Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha abrindo sensores ao vivo");
            StatusMessage = "Não foi possível abrir os sensores: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenUserBenchmark()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://www.userbenchmark.com/",
                UseShellExecute = true,
            });
            UserBenchmarkStatus = "UserBenchmark aberto no navegador. Rode por lá e compare com outros dispositivos.";
        }
        catch (Exception ex)
        {
            UserBenchmarkStatus = "Não foi possível abrir: " + ex.Message;
        }
    }

    // ---- Atualização incremental do snapshot por categoria ----
    private Domain.Models.StressSnapshot CurrentOrNewSnapshot() =>
        _session.StressResult ?? new Domain.Models.StressSnapshot(
            FinalScore: 0,
            CpuSingleThread: 0, CpuMultiThread: 0, CpuEfficiency: 0, CpuThreads: 0,
            GpuGraphics: 0, GpuCompute: 0, GpuBandwidth: 0, GpuName: null, GpuFeatureLevel: null,
            DiskScore: 0, DiskReadMbPerSec: 0, DiskWriteMbPerSec: 0,
            VramOk: true, VramAllocatedMb: 0, VramMismatchCount: 0,
            RamOk: true, RamAllocatedMb: 0, RamErrorCount: 0, RamBandwidthGbs: 0,
            GeekbenchSingle: 0, GeekbenchMulti: 0, GeekbenchVersion: null);

    private void EnsureStressTestRow()
    {
        if (!_session.Tests.ContainsKey("stress"))
        {
            var tr = new Domain.Models.TestResult("stress", AutoStatus.OK, "Benchmark executado", DateTime.Now);
            _session.Tests["stress"] = tr;
            UpsertTestRow("Stress test", tr);
        }
        // Atualiza a nota final parcial conforme os testes individuais terminam.
        RecomputeFinalScore();
    }

    private void UpdateStressSnapshotCpu(Application.Bench.CpuBenchResult r)
    {
        _session.StressResult = CurrentOrNewSnapshot() with
        {
            CpuSingleThread = r.SingleThreadScore,
            CpuMultiThread = r.MultiThreadScore,
            CpuEfficiency = r.EfficiencyScore,
            CpuThreads = r.Threads,
        };
        EnsureStressTestRow();
    }

    private void UpdateStressSnapshotGpu(Application.Bench.GpuBenchResult r)
    {
        _session.StressResult = CurrentOrNewSnapshot() with
        {
            GpuGraphics = r.GraphicsScore,
            GpuCompute = r.ComputeScore,
            GpuBandwidth = r.BandwidthScore,
            GpuName = r.AdapterName,
            GpuFeatureLevel = r.FeatureLevel,
        };
        EnsureStressTestRow();
    }

    private void UpdateStressSnapshotDisk(Application.Bench.DiskBenchResult r)
    {
        _session.StressResult = CurrentOrNewSnapshot() with
        {
            DiskScore = r.Score,
            DiskReadMbPerSec = r.ReadMbPerSec,
            DiskWriteMbPerSec = r.WriteMbPerSec,
        };
        EnsureStressTestRow();
    }

    private void UpdateStressSnapshotVram(Application.Bench.VramStressResult r)
    {
        _session.StressResult = CurrentOrNewSnapshot() with
        {
            VramOk = r.Ok,
            VramAllocatedMb = r.AllocatedMb,
            VramMismatchCount = r.MismatchCount,
        };
        EnsureStressTestRow();
    }

    private void UpdateStressSnapshotRam(Application.Bench.RamStressResult r)
    {
        _session.StressResult = CurrentOrNewSnapshot() with
        {
            RamOk = r.Ok,
            RamAllocatedMb = r.AllocatedMb,
            RamErrorCount = r.ErrorCount,
            RamBandwidthGbs = r.BandwidthGbs,
        };
        EnsureStressTestRow();
    }

    private void StartStressTelemetry()
    {
        _telemetryUsers++;
        if (_stressTelemetryTimer is not null) return;
        try
        {
            _stressMonitor = new Application.Bench.HardwareMonitor(
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            _stressTelemetryTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _stressTelemetryTimer.Tick += OnStressTelemetryTick;
            _stressTelemetryTimer.Start();
        }
        catch { /* sem driver — telemetria fica vazia */ }
    }

    private void StopStressTelemetry()
    {
        if (_telemetryUsers > 0) _telemetryUsers--;
        if (_telemetryUsers > 0) return;
        _stressTelemetryTimer?.Stop();
        _stressTelemetryTimer = null;
        try { _stressMonitor?.Dispose(); } catch { }
        _stressMonitor = null;
        LiveTelemetryText = "";
    }

    private void OnStressTelemetryTick(object? sender, EventArgs e)
    {
        if (_stressMonitor is null) return;
        try
        {
            var s = _stressMonitor.Sample();
            var cpuTemp = double.IsNaN(s.CpuTempPackage) ? s.CpuTempMax : s.CpuTempPackage;
            var cpuClock = s.CpuClockMaxMhz;
            var cpuLoad = s.CpuLoadPercent;
            var gpuTemp = double.IsNaN(s.GpuHotspotC) ? s.GpuTempC : s.GpuHotspotC;
            var gpuLoad = s.GpuLoadPercent;

            var cpuT = !double.IsNaN(cpuTemp) && cpuTemp > 0 ? $"{cpuTemp:F0}°C" : "--";
            var cpuC = !double.IsNaN(cpuClock) && cpuClock > 0 ? $"{cpuClock:F0} MHz" : "--";
            var cpuL = !double.IsNaN(cpuLoad) && cpuLoad > 0 ? $"{cpuLoad:F0}%" : "--";
            var gpuT = !double.IsNaN(gpuTemp) && gpuTemp > 0 ? $"{gpuTemp:F0}°C" : "--";
            var gpuL = !double.IsNaN(gpuLoad) && gpuLoad > 0 ? $"{gpuLoad:F0}%" : "--";
            LiveTelemetryText = $"CPU {cpuT} • {cpuC} • {cpuL}    GPU {gpuT} • {gpuL}";
        }
        catch { /* ignora */ }
    }

    /// <summary>
    /// Abre a interface gráfica completa do CrystalDiskInfo (binário oficial
    /// embutido) para o técnico inspecionar a saúde dos discos em detalhe.
    /// </summary>
    [RelayCommand]
    private void OpenCrystalDiskInfo()
    {
        try
        {
            StatusMessage = "Abrindo CrystalDiskInfo...";
            _crystalDiskInfo.LaunchGui();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha abrindo CrystalDiskInfo");
            StatusMessage = "Não foi possível abrir o CrystalDiskInfo: " + ex.Message;
        }
    }

    private CancellationTokenSource? _humanizationCts;

    [RelayCommand]
    private async Task RunTouchpadTestAsync()
    {
        await Task.Yield();
        IsBusy = true;
        StatusMessage = "Abrindo teste de touchpad...";
        try
        {
            var dlg = new Views.TouchpadTestWindow
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            dlg.ShowDialog();

            AutoStatus status;
            string detail;
            if (dlg.Result is true)
            {
                status = AutoStatus.OK;
                detail = dlg.Message;
            }
            else if (dlg.Result is false)
            {
                status = AutoStatus.Falha;
                detail = dlg.Message;
            }
            else
            {
                // Técnico fechou sem concluir → Não testado (não some o resultado).
                status = AutoStatus.NaoTestado;
                detail = "Teste não concluído pelo técnico";
            }

            var r = new Domain.Models.TestResult("touchpad", status, detail, DateTime.Now);
            _session.Tests[r.TestKey] = r;
            UpsertTestRow("Touchpad", r);
            TouchpadResult = $"{r.Status} — {r.Details}";
            StatusMessage = $"Touchpad: {r.Status}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no teste de touchpad");
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RunKeyboardTestAsync()
    {
        await Task.Yield();

        IsBusy = true;
        StatusMessage = "Abrindo teste de teclado...";
        try
        {
            // O idioma/layout (ABNT2 ou US ANSI) e o numpad são escolhidos no
            // dropdown DENTRO da própria janela — sem pop-up antes.
            var dlg = new Views.KeyboardTestWindow
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            dlg.ShowDialog();
            // A janela abriu: o técnico DECIDE o veredito (OK ou Falha), inclusive
            // clicando nas teclas defeituosas dentro da própria janela. Só vira
            // "Não testado" quando a janela nem abre (exceção, tratada no catch).

            bool isOk;
            if (dlg.Result is true) isOk = true;
            else if (dlg.Result is false) isOk = false;
            else
            {
                // Fechou no X sem decidir → força um veredito.
                var ans2 = MessageBox.Show(
                    "O teclado funcionou corretamente?\n\nSim = OK • Não = Falha",
                    "Resultado do teclado", MessageBoxButton.YesNo, MessageBoxImage.Question);
                isOk = ans2 == MessageBoxResult.Yes;
            }

            AutoStatus status;
            string detail;
            if (isOk)
            {
                status = AutoStatus.OK;
                detail = $"Aprovado pelo técnico ({dlg.PressedCount}/{dlg.TotalKeys} teclas detectadas)";
            }
            else
            {
                status = AutoStatus.Falha;
                // A janela já montou a mensagem com as teclas marcadas + comentário.
                detail = string.IsNullOrWhiteSpace(dlg.Message) ? "Marcado como falho pelo técnico" : dlg.Message;
            }

            var r = new Domain.Models.TestResult("teclado", status, detail, DateTime.Now);
            _session.Tests[r.TestKey] = r;
            UpsertTestRow("Teclado", r);
            KeyboardResult = $"{r.Status} — {r.Details}";
            StatusMessage = $"Teclado: {r.Status}";
        }
        catch (Exception ex)
        {
            // Janela não abriu → Não testado (único caso legítimo).
            _logger.LogError(ex, "Falha no teste de teclado");
            var r = new Domain.Models.TestResult("teclado", AutoStatus.NaoTestado,
                "Não foi possível abrir o teste de teclado", DateTime.Now);
            _session.Tests[r.TestKey] = r;
            UpsertTestRow("Teclado", r);
            KeyboardResult = "Não testado — não foi possível abrir o teste";
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RunPixelTestAsync()    {
        await Task.Yield();
        IsBusy = true;
        StatusMessage = "Abrindo teste de pixels mortos...";
        try
        {
            var dlg = new Views.PixelTestWindow
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            dlg.ShowDialog();

            var viewed = dlg.CompletedColors;
            // Pergunta ao técnico se viu defeitos.
            var msg = viewed > 0
                ? $"Você visualizou {viewed} padrões. Encontrou pixels mortos/presos, manchas ou falta de nitidez?"
                : "Você não navegou pelos padrões. Repetir?";
            var answer = MessageBox.Show(
                msg,
                "Teste de tela — confirmação",
                viewed > 0 ? MessageBoxButton.YesNoCancel : MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            bool foundDefects = answer == MessageBoxResult.Yes;
            string? note = foundDefects
                ? "Defeito de tela reportado — descrever na inspeção física"
                : null;

            var r = _engine.BuildPixelTestResult(viewed, dlg.TotalPatterns, foundDefects, note);
            _session.Tests[r.TestKey] = r;
            UpsertTestRow("Tela", r);
            StatusMessage = $"Tela: {r.Status}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no teste de tela");
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RunBrightnessTestAsync()
    {
        await Task.Yield();
        IsBusy = true;
        StatusMessage = "Abrindo teste de brilho...";
        try
        {
            // Slider ajusta o brilho do painel ao vivo; o técnico confirma que a
            // tela responde do mínimo ao máximo. O brilho original é restaurado.
            var dlg = new Views.BrightnessTestWindow(_logger)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            dlg.ShowDialog();

            AutoStatus status;
            string detail;
            if (dlg.Result is true) { status = AutoStatus.OK; detail = dlg.Message; }
            else if (dlg.Result is false) { status = AutoStatus.Falha; detail = dlg.Message; }
            else { status = AutoStatus.NaoTestado; detail = "Teste de brilho não concluído pelo técnico"; }

            var r = new Domain.Models.TestResult("brilho", status, detail, DateTime.Now);
            _session.Tests[r.TestKey] = r;
            UpsertTestRow("Brilho da tela", r);
            StatusMessage = $"Brilho: {r.Status}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no teste de brilho");
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RunThrottleTestAsync()
    {
        await Task.Yield();
        IsBusy = true;
        StatusMessage = "Abrindo teste de throttling térmico...";
        try
        {
            // Carga máxima na CPU por ~60s com monitor de temp/clock ao vivo.
            var dlg = new Views.ThrottleTestWindow(_logger)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            dlg.ShowDialog();

            AutoStatus status;
            string detail;
            if (dlg.Result is true) { status = AutoStatus.OK; detail = dlg.Message; }
            else if (dlg.Result is false) { status = AutoStatus.Falha; detail = dlg.Message; }
            else { status = AutoStatus.NaoTestado; detail = "Teste de throttling não concluído pelo técnico"; }

            var r = new Domain.Models.TestResult("throttling", status, detail, DateTime.Now);
            _session.Tests[r.TestKey] = r;
            UpsertTestRow("Throttling térmico", r);
            ThrottleResult = $"{r.Status} — {r.Details}";
            StatusMessage = $"Throttling: {r.Status}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no teste de throttling");
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RunBatteryDischargeTestAsync()
    {
        await Task.Yield();
        IsBusy = true;
        StatusMessage = "Abrindo teste de descarga de bateria...";
        try
        {
            var dlg = new Views.BatteryDischargeTestWindow
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            dlg.ShowDialog();

            AutoStatus status;
            string detail;
            if (dlg.Result is true) { status = AutoStatus.OK; detail = dlg.Message; }
            else if (dlg.Result is false) { status = AutoStatus.Falha; detail = dlg.Message; }
            else { status = AutoStatus.NaoTestado; detail = "Teste de descarga não concluído pelo técnico"; }

            var r = new Domain.Models.TestResult("descarga_bateria", status, detail, DateTime.Now);
            _session.Tests[r.TestKey] = r;
            UpsertTestRow("Descarga de bateria", r);
            BatteryDischargeResult = $"{r.Status} — {r.Details}";
            StatusMessage = $"Descarga de bateria: {r.Status}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no teste de descarga de bateria");
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RunMicAsync()
    {
        await Task.Yield();
        IsBusy = true;
        StatusMessage = "Abrindo teste de microfone...";
        try
        {
            // Janela com VU meter AO VIVO: o técnico vê a barra reagir à voz e
            // decide OK/Falha. O áudio captado volta no buffer para reprodução.
            var dlg = new Views.MicTestWindow
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            dlg.ShowDialog();

            AutoStatus status;
            string detail;
            if (dlg.Result is true) { status = AutoStatus.OK; detail = dlg.Message; }
            else if (dlg.Result is false) { status = AutoStatus.Falha; detail = dlg.Message; }
            else { status = AutoStatus.NaoTestado; detail = "Teste de microfone não concluído pelo técnico"; }

            var r = new Domain.Models.TestResult("microfone", status, detail, DateTime.Now);
            _session.Tests[r.TestKey] = r;
            UpsertTestRow("Microfone", r);

            _lastMicWav = dlg.Wav;
            HasMicRecording = dlg.Wav is { Length: > 0 };
            StatusMessage = HasMicRecording
                ? $"Microfone: {r.Status}. Use 'Reproduzir gravação' para ouvir."
                : $"Microfone: {r.Status}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no teste de microfone");
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task PlayMicAsync()
    {
        if (_lastMicWav is null || _lastMicWav.Length == 0)
        {
            StatusMessage = "Nada gravado ainda.";
            return;
        }
        IsBusy = true; StatusMessage = "Reproduzindo gravação...";
        if (_engine is Application.Testing.TestEngine te)
        {
            var ok = await te.PlayWavBufferAsync(_lastMicWav, CancellationToken.None);
            StatusMessage = ok ? "Reprodução concluída." : "Falha ao reproduzir.";
        }
        IsBusy = false;
    }

    private byte[]? _lastMicWav;
    [ObservableProperty] private bool hasMicRecording;

    [RelayCommand]
    private void GoManual()
    {
        Goto(WizardStep.Manual);
    }

    /// <summary>
    /// Inicia o servidor HTTP local de inspeção e gera o QR apontando para o
    /// IP da LAN. Funciona sem internet — basta o celular estar na mesma rede
    /// WiFi. As fotos são salvas no checklist atual (marcadas pelo serial).
    /// </summary>
    private void StartInspection()
    {
        try
        {
            // Popula a lista de itens (uma vez), conforme o modo (Desktop usa
            // 3 fotos de carcaça + 1 interna; notebook usa o catálogo padrão).
            if (InspectionItems.Count == 0)
            {
                foreach (var item in Domain.Models.InspectionCatalog.ForMode(_session.Mode))
                    InspectionItems.Add(new InspectionItemRow(item.Key, item.Label, item.Instruction, item.Optional));
            }
            // O progresso conta só as fotos principais; defeitos são opcionais.
            InspectionTotalCount = Domain.Models.InspectionCatalog.MainItemsForMode(_session.Mode).Count;

            // URL da página de inspeção no site (Vercel). O celular precisa de
            // internet para abrir. As fotos vão direto pro MongoDB, vinculadas
            // ao serial pelo slug desta sessão.
            var serial = _session.Machine?.Serial ?? "";
            var machineName = string.Join(" ",
                new[] { _session.Machine?.Manufacturer, _session.Machine?.Model }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(machineName)) machineName = _session.Machine?.Hostname ?? "Equipamento";

            var baseUrl = Bootstrap.AppDefaults.ApiBaseUrl.TrimEnd('/');
            // kind diz ao site qual catálogo de fotos usar no celular.
            var kind = _session.Mode == ChecklistMode.Desktop ? "desktop" : "notebook";
            var qs = $"?serial={Uri.EscapeDataString(serial)}&machine={Uri.EscapeDataString(machineName)}&kind={kind}";
            var url = $"{baseUrl}/inspecao/{_session.InspectionSlug}{qs}";

            InspectionUrl = url;
            InspectionQr = Infrastructure.Inspection.QrCodeFactory.Create(url);
            InspectionStatus = InspectionQr is null
                ? $"Falha ao gerar o QR. Abra no celular: {url}"
                : "Escaneie o QR com o celular (com internet) e tire as fotos. Elas salvam no relatório pelo serial.";

            // Começa a sincronizar o estado (fotos recebidas) periodicamente.
            StartInspectionPolling();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha iniciando inspeção física");
            InspectionStatus = "Erro ao iniciar a inspeção: " + ex.Message;
            InspectionQr = null;
        }
    }

    private System.Threading.Timer? _inspectionPollTimer;

    /// <summary>
    /// Consulta o estado da inspeção no site a cada poucos segundos para
    /// refletir, no app, quais fotos já foram enviadas pelo celular.
    /// </summary>
    private void StartInspectionPolling()
    {
        _inspectionPollTimer?.Dispose();
        _inspectionPollTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                var slug = _session.InspectionSlug;
                var baseUrl = Bootstrap.AppDefaults.ApiBaseUrl.TrimEnd('/');
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                var json = await http.GetStringAsync($"{baseUrl}/api/inspecao/{slug}").ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var doneKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("items", out var items))
                {
                    foreach (var el in items.EnumerateArray())
                    {
                        if (el.TryGetProperty("done", out var d) && d.GetBoolean()
                            && el.TryGetProperty("key", out var k))
                        {
                            doneKeys.Add(k.GetString() ?? "");
                        }
                    }
                }

                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp is null) return;
                _ = disp.BeginInvoke(() =>
                {
                    var done = 0;       // fotos principais
                    var defects = 0;    // slots opcionais de defeito
                    foreach (var row in InspectionItems)
                    {
                        // Remota (celular via QR) OU local (webcam da bancada,
                        // guardada em base64 na sessão) — o polling não pode
                        // apagar a marca de uma foto tirada pela webcam.
                        var local = _session.InspectionPhotos.ContainsKey(row.Key);
                        row.HasPhoto = doneKeys.Contains(row.Key) || local;
                        if (row.HasPhoto)
                        {
                            if (row.IsOptional) defects++; else done++;
                        }
                        if (doneKeys.Contains(row.Key))
                        {
                            // URL da foto no site (JPEG) + cache-buster para atualizar.
                            var bu = Bootstrap.AppDefaults.ApiBaseUrl.TrimEnd('/');
                            row.PhotoUrl = $"{bu}/api/inspecao/{_session.InspectionSlug}/photo/{row.Key}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                        }
                        else if (!local)
                        {
                            row.PhotoUrl = null;
                        }
                    }
                    InspectionDoneCount = done;
                    if (done > 0 || defects > 0)
                    {
                        var defTxt = defects > 0 ? $" • {defects} defeito(s)" : "";
                        InspectionStatus = done >= InspectionTotalCount
                            ? $"Fotos principais completas ✓{defTxt}"
                            : $"{done} de {InspectionTotalCount} fotos principais{defTxt}. Fotos são opcionais.";
                    }
                });
            }
            catch
            {
                // sem internet ou site fora — silencioso (continua tentando)
            }
        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6));
    }

    private void StopInspectionPolling()
    {
        _inspectionPollTimer?.Dispose();
        _inspectionPollTimer = null;
    }

    // ---- Inspeção física: escolha webcam × QR ------------------------------
    // O QR continua sendo o caminho padrão (celular fotografa e sobe pro site);
    // a webcam captura direto na bancada e salva a foto em base64 DENTRO do
    // relatório (ChecklistSession.InspectionPhotos → ApiPayload.inspection_photos),
    // caminho que já existia no payload e estava sem uso.

    private readonly Infrastructure.Inspection.WebcamCapture _inspWebcam = new();
    private bool _inspWebcamHooked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InspecaoEscolheuModo), nameof(InspecaoUsandoWebcam), nameof(InspecaoUsandoQr))]
    private FotoModo inspecaoModo = FotoModo.NaoEscolhido;

    public bool InspecaoEscolheuModo => InspecaoModo != FotoModo.NaoEscolhido;
    public bool InspecaoUsandoWebcam => InspecaoModo == FotoModo.Webcam;
    public bool InspecaoUsandoQr => InspecaoModo == FotoModo.QrCode;

    public ObservableCollection<Infrastructure.Inspection.CameraOption> InspecaoCameras { get; } = new();

    [ObservableProperty] private Infrastructure.Inspection.CameraOption? inspecaoCameraSelecionada;
    [ObservableProperty] private System.Windows.Media.Imaging.BitmapSource? inspecaoCameraFrame;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodeTirarFotoInspecao))]
    private bool inspecaoCameraLigada;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodeTirarFotoInspecao))]
    private bool inspecaoEnviandoFoto;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodeTirarFotoInspecao))]
    private InspectionItemRow? inspecaoItemSelecionado;
    [ObservableProperty] private string inspecaoFotoErro = "";

    public bool PodeTirarFotoInspecao =>
        InspecaoCameraLigada && !InspecaoEnviandoFoto && InspecaoItemSelecionado is not null;

    /// <summary>Volta para o QR (modo padrão) e solta a webcam.</summary>
    [RelayCommand]
    private async Task InspecaoUsarQrAsync()
    {
        InspecaoModo = FotoModo.QrCode;
        InspecaoFotoErro = "";
        await PararWebcamInspecaoAsync();
    }

    [RelayCommand]
    private async Task InspecaoUsarWebcamAsync()
    {
        InspecaoModo = FotoModo.Webcam;
        InspecaoFotoErro = "";
        try
        {
            if (InspecaoCameras.Count == 0)
            {
                foreach (var cam in await Infrastructure.Inspection.WebcamCapture.ListarCamerasAsync())
                    InspecaoCameras.Add(cam);
            }
            if (InspecaoCameras.Count == 0)
            {
                InspecaoFotoErro = "Nenhuma webcam encontrada — use o QR code.";
                return;
            }
            InspecaoCameraSelecionada ??= InspecaoCameras[0];
            if (!_inspWebcamHooked)
            {
                _inspWebcam.FrameReady += OnInspecaoFrameReady;
                _inspWebcamHooked = true;
            }
            await _inspWebcam.IniciarAsync(InspecaoCameraSelecionada!.Id);
            InspecaoCameraLigada = true;
            // pré-seleciona o primeiro item ainda sem foto
            InspecaoItemSelecionado ??= InspectionItems.FirstOrDefault(i => !i.HasPhoto && !i.IsOptional)
                ?? InspectionItems.FirstOrDefault();
        }
        catch (InvalidOperationException ex)
        {
            InspecaoFotoErro = ex.Message; // sem câmera / privacidade / em uso
            InspecaoCameraLigada = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ligando webcam da inspeção");
            InspecaoFotoErro = $"Erro ao ligar a webcam: {ex.Message}";
            InspecaoCameraLigada = false;
        }
    }

    [RelayCommand]
    private async Task InspecaoTrocarCameraAsync()
    {
        if (InspecaoCameraSelecionada is null) return;
        try
        {
            await _inspWebcam.PararAsync();
            await _inspWebcam.IniciarAsync(InspecaoCameraSelecionada.Id);
            InspecaoCameraLigada = true;
        }
        catch (Exception ex)
        {
            InspecaoFotoErro = $"Erro trocando de câmera: {ex.Message}";
            InspecaoCameraLigada = false;
        }
    }

    /// <summary>
    /// Captura o frame atual e grava no relatório como a foto do item
    /// selecionado. Sem rede envolvida: a foto viaja em base64 dentro do
    /// checklist quando ele for enviado.
    /// </summary>
    [RelayCommand]
    private async Task InspecaoTirarFotoAsync()
    {
        var item = InspecaoItemSelecionado;
        if (item is null)
        {
            InspecaoFotoErro = "Escolha qual parte está sendo fotografada.";
            return;
        }
        InspecaoEnviandoFoto = true;
        InspecaoFotoErro = "";
        try
        {
            var jpeg = await _inspWebcam.TirarFotoJpegAsync();
            if (jpeg is null || jpeg.Length == 0)
            {
                InspecaoFotoErro = "A câmera ainda não entregou um frame — tente de novo.";
                return;
            }
            _session.InspectionPhotos[item.Key] = new Domain.Models.InspectionPhoto(
                item.Key, Convert.ToBase64String(jpeg), null, DateTime.UtcNow);
            item.HasPhoto = true;
            RecountInspectionLocal();
            // avança para o próximo item principal sem foto
            InspecaoItemSelecionado = InspectionItems.FirstOrDefault(i => !i.HasPhoto && !i.IsOptional)
                ?? InspectionItems.FirstOrDefault(i => !i.HasPhoto);
            InspectionStatus = $"Foto de \"{item.Label}\" capturada pela webcam e anexada ao relatório.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha capturando foto da inspeção pela webcam");
            InspecaoFotoErro = $"Erro na captura: {ex.Message}";
        }
        finally
        {
            InspecaoEnviandoFoto = false;
        }
    }

    private void OnInspecaoFrameReady(object? sender, System.Windows.Media.Imaging.BitmapSource frame)
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        _ = disp?.BeginInvoke(() => InspecaoCameraFrame = frame);
    }

    private async Task PararWebcamInspecaoAsync()
    {
        try
        {
            await _inspWebcam.PararAsync();
        }
        catch
        {
            // soltar a câmera nunca pode derrubar o fluxo
        }
        InspecaoCameraLigada = false;
        InspecaoCameraFrame = null;
    }

    /// <summary>Recalcula o progresso contando fotos locais (webcam) + remotas.</summary>
    private void RecountInspectionLocal()
    {
        var done = 0;
        foreach (var row in InspectionItems)
        {
            if (row.HasPhoto && !row.IsOptional) done++;
        }
        InspectionDoneCount = done;
    }

    private void OnInspectionPhotoReceived(string itemKey)
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp is null) return;
        disp.BeginInvoke(() => RefreshInspectionStates());
    }

    partial void OnInspectionDoneCountChanged(int value)
    {
        // Limpa o aviso de inspeção incompleta quando todas as fotos chegam.
        if (InspectionTotalCount > 0 && value >= InspectionTotalCount)
        {
            InspectionIncomplete = false;
            if (!KeyboardBacklightInvalid && !NumericKeypadInvalid) ManualWarning = "";
        }
    }

    private void RefreshInspectionStates()
    {
        var done = 0;
        foreach (var row in InspectionItems)
        {
            if (row.HasPhoto && !row.IsOptional) done++;
        }
        InspectionDoneCount = done;
    }

    /// <summary>
    /// Indica se a etapa de Desempenho (stress + humanização) está disponível.
    /// Básico não tem nenhum dos dois; Padrão tem só humanização; Detalhado
    /// tem ambos. Logo, a etapa aparece em Padrão e Detalhado.
    /// </summary>
    public bool HasPerformanceStep =>
        ChecklistMode is ChecklistMode.Padrao or ChecklistMode.Detalhado;

    /// <summary>
    /// Benchmark/stress liberado em toda etapa de Desempenho (Padrão e
    /// Detalhado) — não fica mais travado só no Detalhado.
    /// </summary>
    public bool StressAvailable => HasPerformanceStep;

    /// <summary>Humanização liberada em toda etapa de Desempenho.</summary>
    public bool HumanizationAvailable => HasPerformanceStep;

    /// <summary>True em todos os modos menos o Desktop.</summary>
    public bool IsDesktop => ChecklistMode == ChecklistMode.Desktop;

    /// <summary>Mostra o card de bateria (oculto no Desktop).</summary>
    public bool ShowBatteryCard => ChecklistMode != ChecklistMode.Desktop;

    /// <summary>
    /// Mostra todos os testes do técnico (mic, câmera, tela, brilho, teclado,
    /// touchpad). No Desktop só fica o teste de som.
    /// </summary>
    public bool ShowFullTechTests => ChecklistMode != ChecklistMode.Desktop;

    /// <summary>Mostra as confirmações de teclado retroiluminado/numérico (N/A no Desktop).</summary>
    public bool ShowKeyboardChecks => ChecklistMode != ChecklistMode.Desktop;

    partial void OnChecklistModeChanged(ChecklistMode value)
    {
        OnPropertyChanged(nameof(HasPerformanceStep));
        OnPropertyChanged(nameof(StressAvailable));
        OnPropertyChanged(nameof(HumanizationAvailable));
        OnPropertyChanged(nameof(IsDesktop));
        OnPropertyChanged(nameof(ShowBatteryCard));
        OnPropertyChanged(nameof(ShowFullTechTests));
        OnPropertyChanged(nameof(ShowKeyboardChecks));
    }

    partial void OnNtbCodeChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) NtbInvalid = false;
    }

    /// <summary>
    /// Padroniza o código NTB para o formato de estoque "NTBXXX" (sem hífen).
    /// "123" → "NTB123" • "ntb123" / "NTB 123" / "ntb-123" → "NTB123".
    /// Vazio permanece vazio (a obrigatoriedade é validada à parte).
    /// </summary>
    internal static string NormalizeNtbCode(string? raw) => Domain.Rules.NtbCode.Normalize(raw);

    partial void OnTechnicianNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) TechnicianInvalid = false;
    }

    /// <summary>
    /// Avança dos testes automáticos para a próxima etapa: Desempenho (se o
    /// modo tiver stress/humanização) ou direto para Inputs (modo Básico).
    /// </summary>
    [RelayCommand]
    private async Task GoAfterAutoTestsAsync()
    {
        if (HasPerformanceStep)
        {
            Goto(WizardStep.Performance);
        }
        else
        {
            await GoInputsAsync();
        }
    }

    [RelayCommand]
    private async Task GoInputsAsync()
    {
        Goto(WizardStep.Inputs);
        await RefreshPortsAsync();
        StartPortsAutoRefresh();
    }

    [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        if (_portsRefreshing) return;
        _portsRefreshing = true;

        try
        {
            var ports = await _portCollector.CollectAsync(CancellationToken.None);

            // Cada PortInfo já é uma linha pronta (a coleção de portas explodida
            // já vem expandida do collector). Sem multiplicar por Total aqui.
            var newSnapshot = ports
                .Select(p => new PortRow(
                    Type: p.Type.ToString(),
                    Symbol: p.Symbol,
                    Label: p.Label,
                    IsActive: p.Active > 0,
                    Detail: p.Detail ?? (p.Active > 0 ? "Em uso" : "Livre")))
                .ToList();

            // Diff in-place
            for (var i = 0; i < newSnapshot.Count; i++)
            {
                if (i < Ports.Count)
                {
                    if (Ports[i] != newSnapshot[i]) Ports[i] = newSnapshot[i];
                }
                else
                {
                    Ports.Add(newSnapshot[i]);
                }
            }
            while (Ports.Count > newSnapshot.Count)
            {
                Ports.RemoveAt(Ports.Count - 1);
            }

            var active = newSnapshot.Count(r => r.IsActive);
            StatusMessage = $"Inputs: {active} ativos de {newSnapshot.Count} • atualização automática a cada 2s";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha coletando portas");
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally
        {
            _portsRefreshing = false;
        }
    }

    /// <summary>
    /// Dispara um timer que roda <see cref="RefreshPortsAsync"/> a cada 2s
    /// enquanto a tela Inputs estiver visível. Para automaticamente quando
    /// o técnico navega para outra etapa.
    /// </summary>
    private void StartPortsAutoRefresh()
    {
        if (_portsTimer is not null) return;
        _portsTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _portsTimer.Tick += async (_, _) =>
        {
            if (CurrentStep != WizardStep.Inputs)
            {
                StopPortsAutoRefresh();
                return;
            }
            try { await RefreshPortsAsync(); } catch { /* logged em RefreshPortsAsync */ }
        };
        _portsTimer.Start();
    }

    private void StopPortsAutoRefresh()
    {
        _portsTimer?.Stop();
        _portsTimer = null;
    }

    private System.Windows.Threading.DispatcherTimer? _portsTimer;
    private bool _portsRefreshing;

    [RelayCommand]
    private async Task GoSummaryAsync()
    {
        // Validação dos itens manuais (NTB e localização já foram validados antes)
        var manualItems = ManualItems
            .Select(m => new ManualCheckItem(m.Key, m.SelectedStatus, m.Notes ?? ""))
            .ToList();
        var errors = ManualChecklistValidator.ValidateManualItems(GeneralNotes, manualItems);
        if (errors.Count > 0)
        {
            StatusMessage = string.Join(" | ", errors);
            return;
        }

        // Confirmações de teclado não se aplicam ao Desktop (teclado externo).
        var requireKeyboardChecks = ChecklistMode != ChecklistMode.Desktop;
        KeyboardBacklightInvalid = requireKeyboardChecks && !KeyboardBacklightSim && !KeyboardBacklightNao;
        NumericKeypadInvalid = requireKeyboardChecks && !NumericKeypadSim && !NumericKeypadNao;
        // Inspeção física: fotos são OPCIONAIS — nunca bloqueiam a finalização.
        InspectionIncomplete = false;

        if (KeyboardBacklightInvalid || NumericKeypadInvalid)
        {
            var faltas = new System.Collections.Generic.List<string>();
            if (KeyboardBacklightInvalid) faltas.Add("confirme o teclado retroiluminado");
            if (NumericKeypadInvalid) faltas.Add("confirme o teclado numérico");

            ManualWarning = "Antes de finalizar: " + string.Join("; ", faltas) + ".";
            StatusMessage = ManualWarning;
            return;
        }

        ManualWarning = "";

        _session.GeneralNotes = GeneralNotes ?? "";
        _session.KeyboardBacklight = KeyboardBacklightSim ? KeyboardBacklight.Sim : KeyboardBacklight.Nao;
        _session.HasNumericKeypad = NumericKeypadSim;
        _session.TechnicianName = TechnicianName.Trim();

        _session.Manual.Clear();
        foreach (var item in manualItems) _session.Manual[item.ItemKey] = item;

        // Mostra a SUGESTÃO automática no resumo; a decisão final e o envio
        // acontecem só quando o técnico clica em "Enviar relatório" no resumo.
        var suggested = DomainRules.ClassifyFinal(
            _session.Tests.Values.Select(t => t.Status),
            _session.Manual.Values.Select(t => t.Status));
        FinalClassification = ClassificationLabel(suggested) + " (sugestão)";

        PopulateBenchSummary();
        StatusMessage = "Revise o resumo e clique em 'Enviar relatório' para finalizar.";
        Goto(WizardStep.Summary);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Botão "Enviar relatório" do RESUMO: abre a janela de decisão final
    /// (com a sugestão automática) e, confirmada, envia o relatório. É o ÚNICO
    /// ponto de envio do fluxo — a etapa Manual apenas navega para cá.
    /// </summary>
    [RelayCommand]
    private async Task FinalizeAndSendAsync()
    {
        var suggested = DomainRules.ClassifyFinal(
            _session.Tests.Values.Select(t => t.Status),
            _session.Manual.Values.Select(t => t.Status));

        var dlg = new Views.FinalDecisionWindow(suggested)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        var ok = dlg.ShowDialog() == true && dlg.ChosenClassification.HasValue;
        if (!ok)
        {
            StatusMessage = "Decisão final não confirmada — relatório não foi enviado.";
            return;
        }

        var chosen = dlg.ChosenClassification!.Value;
        _session.FinalOverride = chosen != suggested ? chosen : null;
        _session.FinalOverrideReason = dlg.IsOverride ? dlg.Reason : null;

        FinalClassification = ClassificationLabel(chosen);
        await SendReportAsync(chosen).ConfigureAwait(true);
    }

    private static string ClassificationLabel(Domain.Enums.FinalClassification c) => c switch
    {
        Domain.Enums.FinalClassification.Aprovado => "Aprovado",
        Domain.Enums.FinalClassification.AprovadoComRessalvas => "Aprovado com ressalvas",
        Domain.Enums.FinalClassification.Reprovado => "Reprovado",
        _ => c.ToString(),
    };

    /// <summary>Monta as linhas de benchmark exibidas no resumo a partir do snapshot.</summary>
    private void PopulateBenchSummary()
    {
        BenchSummary.Clear();
        var s = _session.StressResult;
        if (s is null) { HasBenchSummary = false; return; }

        void Add(string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) BenchSummary.Add(new BenchSummaryRow(label, value));
        }

        // Nota final consolidada em primeiro lugar.
        var finalScore = s.FinalScore > 0 ? s.FinalScore : Application.Bench.BenchmarkSuite.ComputeFinalFromSnapshot(s);
        if (finalScore > 0) Add("Nota final", $"{finalScore} (100 = referência)");

        if (s.CpuSingleThread > 0 || s.CpuMultiThread > 0)
            Add("CPU", $"ST {s.CpuSingleThread} • MT {s.CpuMultiThread} • {s.CpuThreads}T");
        if (s.GpuGraphics > 0 || s.GpuCompute > 0)
            Add("GPU", $"Graf {s.GpuGraphics} • Comp {s.GpuCompute} • BW {s.GpuBandwidth}");
        if (s.DiskScore > 0)
            Add("Disco", $"Nota {s.DiskScore} • ↓{s.DiskReadMbPerSec:F0} ↑{s.DiskWriteMbPerSec:F0} MB/s");
        if (s.VramAllocatedMb > 0)
            Add("VRAM", s.VramOk ? $"OK • {s.VramAllocatedMb} MB" : $"Erros ({s.VramMismatchCount})");
        if (s.RamAllocatedMb > 0)
            Add("RAM", s.RamOk ? $"OK • {s.RamAllocatedMb} MB • {s.RamBandwidthGbs:F1} GB/s" : $"Erros ({s.RamErrorCount})");

        HasBenchSummary = BenchSummary.Count > 0;
    }

    private async Task SendReportAsync(Domain.Enums.FinalClassification finalClass)
    {
        IsBusy = true; StatusMessage = "Salvando relatório...";
        try
        {
            var report = _session.BuildReport(finalClass);

            var path = await _repo.SaveAsync(report, CancellationToken.None);
            // Avisa quando o relatório foi parar no fallback local (pendrive
            // indisponível) em vez da pasta do app.
            var savedDir = Path.GetDirectoryName(path) ?? "";
            var exeDir = AppContext.BaseDirectory.TrimEnd('\\');
            StatusMessage = string.Equals(savedDir.TrimEnd('\\'), exeDir, StringComparison.OrdinalIgnoreCase)
                ? $"Relatório salvo: {Path.GetFileName(path)}"
                : $"Pendrive indisponível — relatório salvo em {savedDir}";

            var payload = PayloadBuilder.Build(report);
            // Sempre arquiva localmente — independente de envio para a API.
            await _archive.AppendAsync(payload, CancellationToken.None);

            await TrySendAsync(payload);
            _reportSent = true;   // libera a etapa "Concluído" na navegação
            Goto(WizardStep.Done);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao salvar/enviar relatório");
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void RestartFlow()
    {
        TestResults.Clear();
        StorageRows.Clear();
        SystemFields.Clear();
        SecurityFields.Clear();
        NetworkFields.Clear();
        DisplayFields.Clear();
        StorageFields.Clear();
        BatteryFields.Clear();
        BatteryHasCharge = false;
        BatteryChargeText = "";
        BatteryTitle = "Bateria";
        KeyboardResult = "Não testado";
        TouchpadResult = "Não testado";
        ThrottleResult = "Não testado";
        BatteryDischargeResult = "Não testado";
        foreach (var m in ManualItems) m.Reset();
        TechnicianName = "";
        NtbCode = "";
        LocationField = "";
        AssetTag = "";
        GeneralNotes = "";
        FinalClassification = "";
        DeviceHeadline = "";
        IdentificationSummary = "";
        StatusMessage = "";
        HighWaterMark = 0;
        ModeChosen = false;
        NtbInvalid = false;
        TechnicianInvalid = false;
        _reportSent = false;
        // Nova identidade: sem isso o próximo checklist reusaria o mesmo
        // test_id (sobrescrevendo o anterior no painel) e o mesmo slug.
        _session.RenewIdentity();
        Goto(WizardStep.Start);
    }

    [RelayCommand]
    private void GoBack()
    {
        switch (CurrentStep)
        {
            case WizardStep.ModeSelect: Goto(WizardStep.Start); break;
            case WizardStep.Identification: Goto(WizardStep.ModeSelect); break;
            case WizardStep.Hardware: Goto(WizardStep.Identification); break;
            case WizardStep.AutoTests: Goto(WizardStep.Hardware); break;
            case WizardStep.Performance: Goto(WizardStep.AutoTests); break;
            case WizardStep.Inputs: Goto(HasPerformanceStep ? WizardStep.Performance : WizardStep.AutoTests); break;
            case WizardStep.Manual: Goto(WizardStep.Inputs); break;
            case WizardStep.Summary: Goto(WizardStep.Manual); break;
        }
    }

    /// <summary>
    /// Permite ao técnico saltar livremente entre as etapas pelo trilho
    /// lateral. As etapas de coleta podem ser revisitadas a qualquer momento;
    /// a única restrição é que para acessar Resumo é preciso ter preenchido
    /// a identificação (NTB + técnico) — caso contrário o relatório sairia
    /// vazio. Mesmo essa restrição é só um aviso: o trilho continua clicável
    /// e a etapa abre, mas o usuário vê uma mensagem.
    /// </summary>
    [RelayCommand]
    private async Task GoToStepAsync(WizardStep target)
    {
        if (target == CurrentStep) return;
        StatusMessage = "";

        // Bloqueia avançar além da seleção de modo sem ter escolhido um modo.
        if ((int)target > (int)WizardStep.ModeSelect && !ModeChosen)
        {
            StatusMessage = "Escolha o modo do checklist antes de continuar.";
            Goto(WizardStep.ModeSelect);
            return;
        }

        // Bloqueia avançar além da Identificação sem NTB e técnico preenchidos.
        // (Localização continua opcional.) Voltar é sempre permitido. Marca os
        // campos faltantes em vermelho.
        var ntbOk = !string.IsNullOrWhiteSpace(NtbCode);
        var techOk = !string.IsNullOrWhiteSpace(TechnicianName);
        if ((int)target > (int)WizardStep.Identification && (!ntbOk || !techOk))
        {
            NtbInvalid = !ntbOk;
            TechnicianInvalid = !techOk;
            StatusMessage = "Preencha os campos obrigatórios (destacados) antes de avançar.";
            Goto(WizardStep.Identification);
            return;
        }

        // "Concluído" só depois do relatório ser ENVIADO no resumo — sem isso,
        // dava para pular para a tela final sem mandar nada.
        if (target == WizardStep.Done && !_reportSent)
        {
            StatusMessage = "Envie o relatório na página de resumo (botão 📤) antes de concluir.";
            return;
        }

        // Caso especial: pular para Hardware sem ter coletado ainda dispara a coleta.
        if (target == WizardStep.Hardware && _session.Machine is null)
        {
            Goto(WizardStep.Hardware);
            await CollectIdentificationAsync().ConfigureAwait(true);
            return;
        }

        // Caso especial: ao entrar em AutoTests pela primeira vez, semeia a grade.
        if (target == WizardStep.AutoTests)
        {
            EnsureTestRunnersRegistered();
            SeedTestRowsAsPending();
        }

        // Caso especial: ao entrar em Inputs, dispara o auto-refresh.
        if (target == WizardStep.Inputs)
        {
            Goto(target);
            await RefreshPortsAsync().ConfigureAwait(true);
            StartPortsAutoRefresh();
            return;
        }

        Goto(target);
    }

    private async Task TrySendAsync(ApiPayload payload)
    {
        try
        {
            var r = await _api.SendAsync(payload, CancellationToken.None);
            if (r.Outcome == ApiOutcome.Sent)
            {
                StatusMessage += " — enviado para o painel.";
                await _archive.MarkSyncedAsync(payload.TestId, CancellationToken.None);
            }
            else if (r.Outcome == ApiOutcome.ConfigInvalid)
            {
                StatusMessage += " — painel não configurado.";
            }
            else
            {
                await _queue.EnqueueAsync(payload, CancellationToken.None);
                StatusMessage += $" — enfileirado offline ({r.Outcome}).";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro tentativa direta de envio");
            try { await _queue.EnqueueAsync(payload, CancellationToken.None); }
            catch (Exception ex2) { _logger.LogWarning(ex2, "Erro enfileirando"); }
            StatusMessage += " — enfileirado offline.";
        }
        finally { PendingCount = _queue.Count; }
    }

    private void Goto(WizardStep step)
    {
        // Para o auto-refresh quando saímos da etapa Inputs.
        if (CurrentStep == WizardStep.Inputs && step != WizardStep.Inputs)
        {
            StopPortsAutoRefresh();
        }
        // Para o polling de inspeção e solta a webcam ao sair da etapa Manual.
        if (CurrentStep == WizardStep.Manual && step != WizardStep.Manual)
        {
            StopInspectionPolling();
            _ = PararWebcamInspecaoAsync();
        }
        CurrentStep = step;
        if ((int)step > HighWaterMark) HighWaterMark = (int)step;

        // Inicia a inspeção ao ENTRAR na etapa Manual, por qualquer caminho
        // (botão "Continuar" ou clique na trilha lateral).
        if (step == WizardStep.Manual)
        {
            StartInspection();
        }
    }

    private void BuildHardwareFields(MachineInfo m)
    {
        SystemFields.Clear();
        SystemFields.Add(new HardwareField("Fabricante", m.Manufacturer ?? "—"));
        SystemFields.Add(new HardwareField("Modelo", m.Model ?? "—"));
        SystemFields.Add(new HardwareField("Serial", m.Serial ?? "—"));
        SystemFields.Add(new HardwareField("Hostname", m.Hostname));
        SystemFields.Add(new HardwareField("CPU", m.Cpu ?? "—"));
        if (m.Processor is { } cpu && (cpu.Cores.HasValue || cpu.Threads.HasValue || cpu.MaxClockMhz.HasValue))
        {
            var parts = new List<string>();
            if (cpu.Cores.HasValue) parts.Add($"{cpu.Cores} núcleos");
            if (cpu.Threads.HasValue) parts.Add($"{cpu.Threads} threads");
            if (cpu.MaxClockMhz is int mhz && mhz > 0) parts.Add($"{mhz / 1000.0:0.0} GHz");
            SystemFields.Add(new HardwareField("CPU — detalhes", string.Join(" • ", parts)));
        }

        SystemFields.Add(new HardwareField("Memória RAM", FormatRam(m)));
        if (m.Memory is { Modules.Count: > 0 } mem)
        {
            for (var i = 0; i < mem.Modules.Count; i++)
            {
                var mod = mem.Modules[i];
                var bits = new List<string>();
                if (mod.CapacityGb.HasValue) bits.Add($"{mod.CapacityGb:0.#} GB");
                if (!string.IsNullOrWhiteSpace(mod.Type)) bits.Add(mod.Type!);
                if (mod.SpeedMhz is int sp && sp > 0) bits.Add($"{sp} MHz");
                if (!string.IsNullOrWhiteSpace(mod.Manufacturer)) bits.Add(mod.Manufacturer!);
                if (!string.IsNullOrWhiteSpace(mod.PartNumber)) bits.Add(mod.PartNumber!);
                var slot = string.IsNullOrWhiteSpace(mod.Locator) ? $"Pente {i + 1}" : mod.Locator!;
                SystemFields.Add(new HardwareField($"RAM — {slot}", string.Join(" • ", bits)));
            }
        }

        SystemFields.Add(new HardwareField("Sistema", m.Os));
        SystemFields.Add(new HardwareField("Versão SO", m.OsVersion));

        SecurityFields.Clear();
        SecurityFields.Add(new HardwareField("TPM", string.IsNullOrEmpty(m.TpmVersion)
            ? m.Tpm.ToString()
            : $"{m.Tpm} • v{m.TpmVersion}"));
        SecurityFields.Add(new HardwareField("Secure Boot", m.SecureBoot.ToString()));
        SecurityFields.Add(new HardwareField("Autopilot (beta)", string.IsNullOrWhiteSpace(m.AutopilotDetail)
            ? FormatAutopilotFlag(m.Autopilot)
            : $"{FormatAutopilotFlag(m.Autopilot)} • {m.AutopilotDetail}"));
        // Durante o beta: quando o veredito acusa algo (Possível/Provável),
        // mostra as evidências que pontuaram para o técnico auditar.
        if (m.Autopilot is Domain.Enums.AvailabilityFlag.Registrado or Domain.Enums.AvailabilityFlag.NaoDeterminado
            && m.AutopilotEvidence is { Count: > 0 } evidence)
        {
            var relevant = evidence
                .Where(d => d.Contains("evidência", StringComparison.OrdinalIgnoreCase)
                         || d.Contains("sinal", StringComparison.OrdinalIgnoreCase)
                         || d.Contains("Entra ID", StringComparison.OrdinalIgnoreCase)
                         || d.Contains("evento", StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .ToList();
            if (relevant.Count == 0) relevant = evidence.Take(4).ToList();
            SecurityFields.Add(new HardwareField("Autopilot — por quê", string.Join("\n", relevant)));
        }
        SecurityFields.Add(new HardwareField("Ativação Windows", m.WindowsActivation.ToString()));
        if (m.BiosSecurity is { } bios)
        {
            var allUnavailable = bios.HasSetupPassword == AvailabilityFlag.Indisponivel
                && bios.HasPowerOnPassword == AvailabilityFlag.Indisponivel
                && bios.HasHddPassword == AvailabilityFlag.Indisponivel;
            if (allUnavailable)
            {
                // Sem a interface WMI do fabricante (HP/Dell/Lenovo) não há como
                // ler o estado das senhas de BIOS — deixa isso explícito.
                SecurityFields.Add(new HardwareField("Senha de BIOS",
                    "Indisponível (leitura só em HP/Dell/Lenovo com a ferramenta de gestão do fabricante)"));
            }
            else
            {
                SecurityFields.Add(new HardwareField("Senha BIOS (Setup/Admin)", FormatBiosPassword(bios.HasSetupPassword)));
                SecurityFields.Add(new HardwareField("Senha BIOS (Power-On)", FormatBiosPassword(bios.HasPowerOnPassword)));
                if (bios.HasHddPassword != AvailabilityFlag.Indisponivel)
                {
                    SecurityFields.Add(new HardwareField("Senha do disco", FormatBiosPassword(bios.HasHddPassword)));
                }
            }
        }
        if (m.Computrace is { } ct)
        {
            var cmpText = ct.ModuleActive == AvailabilityFlag.NaoAtivado
                ? "Não detectado"
                : $"{ct.ModuleActive}{(string.IsNullOrEmpty(ct.Version) ? "" : $" • v{ct.Version}")}";
            SecurityFields.Add(new HardwareField("Absolute / Computrace", cmpText));
        }

        NetworkFields.Clear();
        NetworkFields.Add(new HardwareField("Adaptadores Wi-Fi", m.WifiAdapterCount.ToString()));
        NetworkFields.Add(new HardwareField("Adaptadores Ethernet", m.EthernetAdapterCount.ToString()));
        if (m.Bluetooth is { } bt)
        {
            var btText = bt.Present
                ? string.IsNullOrEmpty(bt.Version)
                    ? (bt.Name ?? "Presente")
                    : $"Bluetooth {bt.Version}"
                : "Não detectado";
            NetworkFields.Add(new HardwareField("Bluetooth", btText));
        }
        else
        {
            NetworkFields.Add(new HardwareField("Bluetooth", "Indisponível"));
        }
        NetworkFields.Add(new HardwareField("MAC principal", m.MacAddress ?? "—"));

        DisplayFields.Clear();
        DisplayFields.Add(new HardwareField("Resolução", m.ScreenResolution));
        if (m.GraphicsDetails is { Count: > 0 } gpus)
        {
            for (var i = 0; i < gpus.Count; i++)
            {
                var g = gpus[i];
                var label = gpus.Count == 1 ? "Placa de vídeo" : $"Placa de vídeo #{i + 1}";
                var extra = new List<string>();
                if (g.VramMb is int vram && vram > 0)
                    extra.Add(vram >= 1024 ? $"{vram / 1024.0:0.#} GB VRAM" : $"{vram} MB VRAM");
                if (!string.IsNullOrWhiteSpace(g.DriverVersion)) extra.Add($"driver {g.DriverVersion}");
                if (!string.IsNullOrWhiteSpace(g.DriverDate)) extra.Add(g.DriverDate!);
                var value = extra.Count > 0 ? $"{g.Name}\n{string.Join(" • ", extra)}" : g.Name;
                DisplayFields.Add(new HardwareField(label, value));
            }
        }
        else if (m.GraphicsAdapters is { Count: > 0 })
        {
            for (var i = 0; i < m.GraphicsAdapters.Count; i++)
            {
                var label = m.GraphicsAdapters.Count == 1 ? "Placa de vídeo" : $"Placa de vídeo #{i + 1}";
                DisplayFields.Add(new HardwareField(label, m.GraphicsAdapters[i]));
            }
        }
        else
        {
            DisplayFields.Add(new HardwareField("Placa de vídeo", m.GraphicsAdapter ?? "—"));
        }

        StorageFields.Clear();
        if (_session.Storage.Count == 0)
        {
            StorageFields.Add(new HardwareField("Discos", "Nenhum disco detectado"));
        }
        else
        {
            for (var i = 0; i < _session.Storage.Count; i++)
            {
                var s = _session.Storage[i];

                // Linha 1: tipo, capacidade e SMART (sempre presente).
                var basics = $"{FormatStorageType(s.Type)} • {s.CapacityGb:F1} GB • SMART {s.SmartStatus}";

                // Linha 2: métricas de saúde do CrystalDiskInfo (quando houver).
                var health = new List<string>();
                if (s.LifePercentRemaining is int life)
                    health.Add($"vida útil {life}%");
                if (s.PowerOnHours is long h && h > 0)
                    health.Add($"{h:N0} h");
                if (s.PowerOnCount is long pc && pc > 0)
                    health.Add($"{pc:N0} ciclos");
                if (s.DataWrittenTb is double tbw && tbw > 0)
                    health.Add($"{tbw:F1} TB escritos");
                if (s.TemperatureC is decimal temp && temp > 0)
                    health.Add($"{temp:F0}°C");

                var value = health.Count > 0
                    ? basics + "\n" + string.Join(" • ", health)
                    : basics;

                // Nome do disco vai no Label (em cima, negrito).
                var label = string.IsNullOrWhiteSpace(s.Model)
                    ? $"Disco #{s.Index}"
                    : s.Model.Trim();
                StorageFields.Add(new HardwareField(label, value));
            }
        }

        BatteryFields.Clear();
        if (_session.Battery is { } b)
        {
            // Barra de carga atual.
            if (b.ChargePercent is int pct)
            {
                BatteryHasCharge = true;
                BatteryChargePercent = pct;
                BatteryChargeText = $"{pct}%  •  {b.ChargingStatus}";
            }
            else
            {
                BatteryHasCharge = false;
                BatteryChargeText = b.ChargingStatus;
            }

            BatteryTitle = !string.IsNullOrWhiteSpace(b.Name) ? b.Name! : "Bateria";

            // Identidade.
            if (!string.IsNullOrWhiteSpace(b.Manufacturer))
                BatteryFields.Add(new HardwareField("Marca", b.Manufacturer!));
            if (!string.IsNullOrWhiteSpace(b.Chemistry))
                BatteryFields.Add(new HardwareField("Química", b.Chemistry!));
            if (!string.IsNullOrWhiteSpace(b.SerialNumber))
                BatteryFields.Add(new HardwareField("Serial", b.SerialNumber!));

            BatteryFields.Add(new HardwareField("Status", b.ChargingStatus));

            // Capacidade máxima (full-charge) em % da de design + valores em mWh.
            if (b.DesignCapacityMwh is int d && b.FullChargeCapacityMwh is int f && d > 0)
            {
                var maxPct = Math.Round(100m * f / d, 1, MidpointRounding.AwayFromZero);
                BatteryFields.Add(new HardwareField("Capacidade máxima", $"{maxPct:0.#}% da original ({f:N0} / {d:N0} mWh)"));
            }
            else if (b.FullChargeCapacityMwh is int f2 && b.DesignCapacityMwh is int d2)
            {
                BatteryFields.Add(new HardwareField("Capacidade", $"{f2:N0} / {d2:N0} mWh"));
            }

            // Carga atual em mWh, quando conhecida.
            if (b.RemainingCapacityMwh is int rem && rem > 0)
                BatteryFields.Add(new HardwareField("Carga atual", $"{rem:N0} mWh"));

            if (b.HealthPercent is decimal h)
                BatteryFields.Add(new HardwareField("Saúde", $"{h:0.##}%"));
            if (b.WearPercent.HasValue)
                BatteryFields.Add(new HardwareField("Desgaste", $"{b.WearPercent:0.##}%"));

            if (b.VoltageMv is int mv && mv > 0)
                BatteryFields.Add(new HardwareField("Voltagem", $"{mv / 1000m:0.###} V"));

            // Charge/discharge rate (sinal: + carregando, − descarregando).
            if (b.RateMw is int rate && rate != 0)
            {
                var w = rate / 1000m;
                var label = rate > 0 ? "Taxa de carga" : "Taxa de descarga";
                BatteryFields.Add(new HardwareField(label, $"{Math.Abs(w):0.##} W"));
            }

            // Ciclos: só exibe quando o firmware reporta de verdade (não 0/n/d).
            if (b.CycleCount is int cycles && cycles > 0)
                BatteryFields.Add(new HardwareField("Ciclos", cycles.ToString()));
        }
        else
        {
            BatteryHasCharge = false;
            BatteryChargeText = "";
            BatteryTitle = "Bateria";
            BatteryFields.Add(new HardwareField("Status", "Sem bateria detectada"));
        }
    }

    /// <summary>Rótulo amigável para o estado de uma senha de BIOS.</summary>
    private static string FormatBiosPassword(Domain.Enums.AvailabilityFlag f) => f switch
    {
        Domain.Enums.AvailabilityFlag.Habilitado => "Definida ⚠",
        Domain.Enums.AvailabilityFlag.Desabilitado => "Não definida",
        Domain.Enums.AvailabilityFlag.Ausente => "Não definida",
        _ => "Indisponível",
    };

    /// <summary>Linha-resumo da RAM: total + tipo + velocidade + slots.</summary>
    private static string FormatRam(MachineInfo m)
    {
        // Total: prefere a soma dos pentes (mais precisa que TotalPhysicalMemory).
        var total = m.Memory?.TotalGb ?? m.RamGb;
        var text = $"{total:0.#} GB";
        if (m.Memory is { } mem)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(mem.Type)) parts.Add(mem.Type!);
            if (mem.SpeedMhz is int sp && sp > 0) parts.Add($"{sp} MHz");
            if (mem.SlotsUsed.HasValue)
            {
                parts.Add(mem.SlotsTotal is int st && st > 0
                    ? $"{mem.SlotsUsed}/{st} slots"
                    : $"{mem.SlotsUsed} pente(s)");
            }
            if (parts.Count > 0) text += " • " + string.Join(" • ", parts);
        }
        return text;
    }

    /// <summary>Rótulo amigável para o veredito do checker de Autopilot.</summary>
    private static string FormatAutopilotFlag(Domain.Enums.AvailabilityFlag f) => f switch
    {
        Domain.Enums.AvailabilityFlag.Registrado => "Provável",
        Domain.Enums.AvailabilityFlag.NaoDeterminado => "Possível",
        Domain.Enums.AvailabilityFlag.NaoRegistrado => "Improvável",
        _ => "Indisponível",
    };

    /// <summary>Rótulo amigável para o tipo de mídia de armazenamento.</summary>
    private static string FormatStorageType(Domain.Enums.StorageType type) => type switch
    {
        Domain.Enums.StorageType.HDD => "HDD",
        Domain.Enums.StorageType.SSD_SATA => "SSD SATA",
        Domain.Enums.StorageType.SSD_NVMe => "SSD NVMe",
        _ => "Indisponível",
    };
}

public enum WizardStep
{
    Start,
    ModeSelect,
    Identification,
    Hardware,
    AutoTests,
    Performance,
    Inputs,
    Manual,
    Summary,
    Done,
}

public record StorageRow(string Description, string Smart);

/// <summary>
/// Linha apresentada na grade de testes automáticos. <see cref="Status"/> é
/// editável via ComboBox para que o técnico possa sobrescrever o resultado
/// (ex.: marcar como "Atenção" um teste que veio "OK"). A mudança é
/// propagada para <see cref="ChecklistSession.Tests"/> pelo ViewModel.
/// </summary>
public sealed partial class TestResultRow : ObservableObject
{
    public string TestKey { get; }
    public string Label { get; }
    [ObservableProperty] private string status;
    [ObservableProperty] private string details;
    /// <summary>Comentário opcional do técnico (botão "+").</summary>
    [ObservableProperty] private string comment = "";
    /// <summary>True quando há comentário — controla a visibilidade do balão.</summary>
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);
    /// <summary>
    /// True quando o status foi alterado manualmente pelo técnico — usado
    /// para mostrar um indicador visual e preservar a override mesmo se o
    /// teste for refeito.
    /// </summary>
    [ObservableProperty] private bool isOverridden;

    partial void OnCommentChanged(string value) => OnPropertyChanged(nameof(HasComment));

    public TestResultRow(string testKey, string label, string status, string details, string comment = "")
    {
        TestKey = testKey;
        Label = label;
        this.status = status;
        this.details = details;
        this.comment = comment;
    }
}

/// <summary>Linha "rótulo : valor" exibida nos cards de hardware.</summary>
public record HardwareField(string Label, string Value);

/// <summary>Linha de porta exibida na etapa "Inputs e portas".</summary>
public record PortRow(string Type, string Symbol, string Label, bool IsActive, string Detail);

public sealed partial class ManualItemRow : ObservableObject
{
    public string Key { get; }
    public string Display { get; }
    [ObservableProperty] private string status = "OK";
    [ObservableProperty] private string notes = "";

    public ManualStatus SelectedStatus => Status switch
    {
        "Com defeito" => ManualStatus.ComDefeito,
        "Não testado" => ManualStatus.NaoTestado,
        "Observação" => ManualStatus.Observacao,
        _ => ManualStatus.OK,
    };

    public ManualItemRow(string key)
    {
        Key = key;
        Display = key switch
        {
            "carcaca" => "Carcaça",
            "tela" => "Tela",
            "teclado" => "Teclado",
            "touchpad" => "Touchpad",
            "dobradicas" => "Dobradiças",
            "usb" => "Portas USB",
            "hdmi" => "HDMI",
            "carregador" => "Carregador",
            _ => key,
        };
    }

    public void Reset() { Status = "OK"; Notes = ""; }
}

/// <summary>
/// Linha de um item de inspeção física fotográfica. <see cref="HasPhoto"/> é
/// atualizado quando a foto correspondente chega do celular.
/// </summary>
public sealed partial class InspectionItemRow : ObservableObject
{
    public string Key { get; }
    public string Label { get; }
    public string Instruction { get; }
    /// <summary>true = slot opcional de defeito (não conta no progresso).</summary>
    public bool IsOptional { get; }
    [ObservableProperty] private bool hasPhoto;

    /// <summary>URL da foto no site (JPEG), preenchida quando a foto chega.</summary>
    [ObservableProperty] private string? photoUrl;

    public InspectionItemRow(string key, string label, string instruction, bool isOptional = false)
    {
        Key = key;
        Label = label;
        Instruction = instruction;
        IsOptional = isOptional;
    }
}

/// <summary>Linha de resultado de benchmark exibida no resumo (rótulo + valor).</summary>
public sealed class BenchSummaryRow
{
    public string Label { get; }
    public string Value { get; }
    public BenchSummaryRow(string label, string value) { Label = label; Value = value; }
}
