using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotebookCheck.Bootstrap;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;
using NotebookCheck.Infrastructure.Erp;

namespace NotebookCheck.Presentation;

/// <summary>Etapas do wizard de teste completo (check automático).</summary>
public enum TesteStep { Maquina = 0, Especificacoes = 1, Condicao = 2, Testes = 3, Revisao = 4 }

/// <summary>Linha da grade de testes executados nesta sessão.</summary>
public sealed partial class TesteLinha : ObservableObject
{
    public string Codigo { get; }
    public string Label { get; }

    [ObservableProperty] private string status = "Não executado";
    [ObservableProperty] private string detalhe = "";
    [ObservableProperty] private string? valor;

    /// <summary>Resultado no vocabulário do ERP (aprovado/atencao/reprovado/…).</summary>
    [ObservableProperty] private string resultadoErp = "nao_testado";

    public TesteLinha(string codigo, string label)
    {
        Codigo = codigo;
        Label = label;
    }
}

/// <summary>
/// ViewModel do modo "Teste completo": o técnico escolhe uma máquina da fila do
/// ERP (ordens de diagnóstico em <c>aguardando_tecnico</c>/<c>em_andamento</c>),
/// o app coleta as especificações, compara com a configuração acordada no
/// pedido, roda a bateria de testes automáticos e reporta tudo via
/// <c>POST /api/integracao/autocheck</c>.
///
/// A ordem NÃO é concluída aqui: ela fica em <c>em_andamento</c> com os dados
/// gravados, e alguém dá o OK no ERP depois de conferir.
/// </summary>
public sealed partial class TesteCompletoViewModel : ObservableObject
{
    private readonly ErpClient _erp;
    private readonly IHardwareCollector _collector;
    private readonly ITestEngine _engine;
    private readonly AppConfig _config;
    private readonly ILogger<TesteCompletoViewModel> _logger;

    /// <summary>Disparado quando o usuário pede para fechar a janela.</summary>
    public event EventHandler? CloseRequested;

    public TesteCompletoViewModel(
        ErpClient erp,
        IHardwareCollector collector,
        ITestEngine engine,
        AppConfig config,
        ILogger<TesteCompletoViewModel> logger)
    {
        _erp = erp;
        _collector = collector;
        _engine = engine;
        _config = config;
        _logger = logger;
    }

    // ---------------------------------------------------------------- step ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMaquina), nameof(IsEspecificacoes), nameof(IsCondicao),
        nameof(IsTestes), nameof(IsRevisao), nameof(StepTitle), nameof(StepNumberLabel),
        nameof(CanGoBack), nameof(NextLabel), nameof(IsLastStep))]
    private TesteStep step = TesteStep.Maquina;

    public bool IsMaquina => Step == TesteStep.Maquina;
    public bool IsEspecificacoes => Step == TesteStep.Especificacoes;
    public bool IsCondicao => Step == TesteStep.Condicao;
    public bool IsTestes => Step == TesteStep.Testes;
    public bool IsRevisao => Step == TesteStep.Revisao;
    public bool IsLastStep => Step == TesteStep.Revisao;
    public bool CanGoBack => Step != TesteStep.Maquina && !IsBusy && !IsDone && !TestesRodando;
    public string NextLabel => Step == TesteStep.Testes ? "Revisar →" : "Avançar →";

    public string StepTitle => Step switch
    {
        TesteStep.Maquina => "Máquina",
        TesteStep.Especificacoes => "Especificações",
        TesteStep.Condicao => "Condição",
        TesteStep.Testes => "Testes",
        _ => "Revisão",
    };

    public string StepNumberLabel => $"Etapa {(int)Step + 1} de 5";

    // -------------------------------------------------------------- estado ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    private bool isBusy;

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    private bool isDone;

    [ObservableProperty] private string resultText = "";
    [ObservableProperty] private bool resultOk;

    // ------------------------------------------------ Etapa 1: Máquina -------

    /// <summary>Máquinas na fila de teste completo (todas as ordens abertas do ERP).</summary>
    public ObservableCollection<ErpFilaTesteMaquina> Fila { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaquinaSelecionada), nameof(ConfigAcordadaText),
        nameof(TemConfigAcordada), nameof(MaquinaEtapa), nameof(MaquinaPedido),
        nameof(JaReportado), nameof(JaReportadoAviso))]
    private ErpFilaTesteMaquina? selectedMaquina;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemFila), nameof(FilaVaziaInfo))]
    private bool filaCarregada;

    [ObservableProperty] private bool filaCarregando;

    public bool TemFila => Fila.Count > 0;
    public bool FilaVaziaInfo => FilaCarregada && !TemFila;
    public bool MaquinaSelecionada => SelectedMaquina is not null;

    public string MaquinaEtapa => SelectedMaquina?.EtapaKanban switch
    {
        "aguardando_tecnico" => "Na fila de teste",
        "em_andamento" => "Em execução (assumida)",
        _ => "—",
    };

    public string MaquinaPedido => SelectedMaquina?.PedidoNumero ?? "—";

    public bool JaReportado => SelectedMaquina?.AutocheckReportado == true;
    public string JaReportadoAviso => JaReportado
        ? "Esta máquina já foi testada e está esperando o OK no ERP. Reportar de novo substitui os dados anteriores."
        : "";

    public bool TemConfigAcordada => SelectedMaquina?.ConfigAcordada is not null;
    public string ConfigAcordadaText =>
        ErpConfigComparer.Describe(SelectedMaquina?.ConfigAcordada) is { Length: > 0 } d ? d : "—";

    partial void OnSelectedMaquinaChanged(ErpFilaTesteMaquina? value)
    {
        RebuildAcessorios(value);
        RaiseCondicaoFields();
    }

    // --------------------------------------- Etapa 2: Especificações ---------

    private ErpEspecificacoes? _specs;
    private MachineInfo? _machine;
    private IReadOnlyList<StorageInfo> _storage = Array.Empty<StorageInfo>();
    private BatteryInfo? _battery;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpecsProntas))]
    private bool specsColetadas;

    public bool SpecsProntas => SpecsColetadas && _specs is not null;

    /// <summary>Resumo legível das specs coletadas da máquina onde o app roda.</summary>
    public string SpecsResumo => _specs is null ? "Coletando…" : DescribeSpecs(_specs);

    public string SpecProcessador => Trimmed(_specs?.Processador, "—");
    public string SpecRam => _specs?.RamGb is int r
        ? $"{r} GB{(string.IsNullOrWhiteSpace(_specs?.RamTipo) ? "" : $" {_specs!.RamTipo}")}"
        : "—";
    public string SpecStorage => _specs?.StorageGb is int g
        ? $"{g} GB {(_specs?.StorageTipo ?? "").ToUpperInvariant()}".Trim()
        : "—";
    public string SpecGpu => Trimmed(_specs?.Gpu, "—");
    public string SpecResolucao => Trimmed(_specs?.Resolucao, "—");
    public string SpecSo => Trimmed(_specs?.So, "—");
    public string SpecBateria => _specs?.BateriaSaudePct is int b ? $"{b}%" : "—";
    public string SpecSerial => Trimmed(_machine?.Serial, "—");

    // ---------------------------------------------- Etapa 3: Condição --------

    /// <summary>
    /// Divergências entre a config acordada no pedido e a coletada na máquina.
    /// Mesma regra do cadastro (<see cref="ErpConfigComparer"/>).
    /// </summary>
    public IReadOnlyList<string> ConfigDivergencias =>
        ErpConfigComparer.Divergencias(SelectedMaquina?.ConfigAcordada, _specs);

    public bool TemConfigDivergencias => ConfigDivergencias.Count > 0;
    public string ConfigDivergenciasText => TemConfigDivergencias
        ? string.Join("\n", ConfigDivergencias.Select(d => $"• {d}"))
        : "";

    /// <summary>
    /// Resposta do técnico à pergunta "a configuração bate com o pedido?".
    /// Começa espelhando o que o app detectou, mas ele decide — a máquina pode
    /// ter um pente de RAM a mais que o WMI não enxergou, por exemplo.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigNaoConfere))]
    private bool configConfere = true;

    public bool ConfigNaoConfere => !ConfigConfere;

    /// <summary>Checklist dos acessórios obrigatórios do pedido de origem.</summary>
    public ObservableCollection<AcessorioCheckItem> AcessoriosChecklist { get; } = new();

    public bool TemAcessoriosObrigatorios => AcessoriosChecklist.Count > 0;

    public IReadOnlyList<string> AcessoriosFaltando =>
        AcessoriosChecklist.Where(a => !a.IsChecked).Select(a => a.Nome).ToList();

    public bool TemAcessoriosFaltando => AcessoriosChecklist.Any(a => !a.IsChecked);

    public string AcessoriosAvisoText => TemAcessoriosFaltando
        ? $"Acessórios obrigatórios não recebidos: {string.Join(", ", AcessoriosFaltando)}."
        : "";

    [ObservableProperty] private string observacoes = "";

    private void RebuildAcessorios(ErpFilaTesteMaquina? maquina)
    {
        foreach (var item in AcessoriosChecklist)
            item.PropertyChanged -= OnAcessorioChanged;
        AcessoriosChecklist.Clear();
        foreach (var nome in maquina?.AcessoriosObrigatorios ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(nome)) continue;
            // acessórios já vieram conferidos no check de entrada: marca por padrão
            var item = new AcessorioCheckItem(nome.Trim()) { IsChecked = true };
            item.PropertyChanged += OnAcessorioChanged;
            AcessoriosChecklist.Add(item);
        }
        RaiseAcessorioFields();
    }

    private void OnAcessorioChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        RaiseAcessorioFields();

    private void RaiseAcessorioFields()
    {
        OnPropertyChanged(nameof(TemAcessoriosObrigatorios));
        OnPropertyChanged(nameof(AcessoriosFaltando));
        OnPropertyChanged(nameof(TemAcessoriosFaltando));
        OnPropertyChanged(nameof(AcessoriosAvisoText));
    }

    private void RaiseCondicaoFields()
    {
        OnPropertyChanged(nameof(ConfigDivergencias));
        OnPropertyChanged(nameof(TemConfigDivergencias));
        OnPropertyChanged(nameof(ConfigDivergenciasText));
        // o app sugere; o técnico confirma
        ConfigConfere = !TemConfigDivergencias;
    }

    // ------------------------------------------------- Etapa 4: Testes -------

    public ObservableCollection<TesteLinha> Testes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(PodeRodarTestes))]
    private bool testesRodando;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestesConcluidos))]
    private int testesExecutados;

    [ObservableProperty] private string testeAtual = "";

    public bool TestesConcluidos => TestesExecutados > 0 && !TestesRodando;
    public bool PodeRodarTestes => !TestesRodando && !IsDone;

    public int TotalTestes => Testes.Count;

    public string TestesResumo
    {
        get
        {
            if (Testes.Count == 0) return "";
            var falhas = Testes.Count(t => t.ResultadoErp == "reprovado");
            var atencoes = Testes.Count(t => t.ResultadoErp == "atencao");
            var ok = Testes.Count(t => t.ResultadoErp == "aprovado");
            return $"{ok} OK · {atencoes} com ressalva · {falhas} com falha";
        }
    }

    // ------------------------------------------------ Etapa 5: Revisão -------

    public IReadOnlyList<CadastroOption> Resultados { get; } = new[]
    {
        new CadastroOption("aprovado", "Aprovado"),
        new CadastroOption("aprovado_com_ressalvas", "Aprovado com ressalvas"),
        new CadastroOption("reprovado", "Reprovado"),
    };

    [ObservableProperty] private CadastroOption? selectedResultado;

    /// <summary>
    /// Resultado sugerido pelo que os testes acharam: qualquer falha reprova;
    /// ressalva (ou config/acessório fora do acordado) aprova com ressalvas.
    /// O técnico pode mudar na revisão.
    /// </summary>
    private string SugerirResultado()
    {
        if (Testes.Any(t => t.ResultadoErp == "reprovado")) return "reprovado";
        if (Testes.Any(t => t.ResultadoErp == "atencao") || TemAcessoriosFaltando || ConfigNaoConfere)
            return "aprovado_com_ressalvas";
        return "aprovado";
    }

    public string RevMaquina => SelectedMaquina?.Display ?? "—";
    public string RevOrdem => SelectedMaquina?.OrdemNumero is int n ? $"#{n}" : "—";
    public string RevPedido => MaquinaPedido;
    public string RevConfigAcordada => ConfigAcordadaText;
    public string RevConfigConfere => ConfigConfere ? "Sim — bate com o pedido" : "Não — divergente";
    public string RevAcessoriosFaltantes =>
        AcessoriosFaltando.Count == 0 ? "Nenhum" : string.Join(", ", AcessoriosFaltando);
    public string RevTestes => Testes.Count == 0 ? "Nenhum teste executado" : TestesResumo;
    public string RevObservacoes => Trimmed(Observacoes, "—");

    public bool PodeEnviar => SelectedMaquina is not null && SpecsProntas && !TestesRodando && !IsDone;

    // ------------------------------------------------------------- init -----

    /// <summary>Conecta no ERP, carrega a fila de teste e lê o hardware da máquina.</summary>
    public async Task InitializeAsync()
    {
        if (!_erp.IsConfigured)
        {
            StatusMessage = "Integração com o ERP não configurada. Verifique URL e credenciais.";
            IsConnected = false;
            return;
        }

        IsBusy = true;
        StatusMessage = "Conectando ao ERP…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _erp.ConnectAsync(cts.Token).ConfigureAwait(true);
            IsConnected = true;
            StatusMessage = "Conectado. Carregando a fila de teste…";
            await LoadFilaAsync(cts.Token).ConfigureAwait(true);
        }
        catch (ErpException ex)
        {
            IsConnected = false;
            StatusMessage = ex.StatusCode == 404
                ? "O ERP ainda não expõe a fila de teste (endpoint pendente). Atualize o ERP."
                : $"Erro ao conectar: {ex.Message}";
            _logger.LogWarning(ex, "Falha conectando ao ERP para o teste completo");
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusMessage = $"Erro inesperado: {ex.Message}";
            _logger.LogError(ex, "Erro inesperado na conexão com o ERP");
        }
        finally
        {
            IsBusy = false;
        }

        _ = CollectHardwareAsync();
    }

    private async Task LoadFilaAsync(CancellationToken ct)
    {
        FilaCarregando = true;
        try
        {
            var maquinas = await _erp.GetFilaTesteAsync(ct).ConfigureAwait(true);
            Fila.Clear();
            foreach (var m in maquinas) Fila.Add(m);
            StatusMessage = maquinas.Count == 0
                ? "Nenhuma máquina na fila de teste completo. Faça o check de entrada primeiro."
                : "";
            TrySelectMaquinaDoSerial();
        }
        finally
        {
            FilaCarregando = false;
            FilaCarregada = true;
            OnPropertyChanged(nameof(TemFila));
            OnPropertyChanged(nameof(FilaVaziaInfo));
        }
    }

    /// <summary>
    /// O app roda NA máquina que está sendo testada: se o serial lido bater com
    /// o de alguém na fila, essa é a máquina — seleciona sozinho.
    /// </summary>
    private void TrySelectMaquinaDoSerial()
    {
        if (SelectedMaquina is not null) return;
        var serial = _machine?.Serial;
        if (string.IsNullOrWhiteSpace(serial)) return;

        SelectedMaquina = Fila.FirstOrDefault(m =>
            !string.IsNullOrWhiteSpace(m.SerialNumber) &&
            string.Equals(m.SerialNumber!.Trim(), serial!.Trim(), StringComparison.OrdinalIgnoreCase));

        if (SelectedMaquina is not null)
            StatusMessage = $"Máquina reconhecida pelo número de série: {SelectedMaquina.Display}.";
    }

    private async Task CollectHardwareAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            _machine = await _collector.CollectMachineAsync(cts.Token).ConfigureAwait(true);
            _storage = await _collector.CollectStorageAsync(cts.Token).ConfigureAwait(true);
            try { _battery = await _collector.CollectBatteryAsync(cts.Token).ConfigureAwait(true); } catch { }
            DisplayInfo? display = null;
            try { display = await _collector.CollectDisplayAsync(cts.Token).ConfigureAwait(true); } catch { }

            _specs = BuildSpecs(_machine, _storage, _battery, display);
            SpecsColetadas = true;
            RaiseSpecFields();
            RaiseCondicaoFields();
            TrySelectMaquinaDoSerial();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha coletando hardware para o teste completo");
            SpecsColetadas = true; // libera o wizard mesmo sem specs
            RaiseSpecFields();
        }
    }

    private void RaiseSpecFields()
    {
        foreach (var p in new[]
        {
            nameof(SpecsResumo), nameof(SpecsProntas), nameof(SpecProcessador), nameof(SpecRam),
            nameof(SpecStorage), nameof(SpecGpu), nameof(SpecResolucao), nameof(SpecSo),
            nameof(SpecBateria), nameof(SpecSerial), nameof(PodeEnviar),
        })
            OnPropertyChanged(p);
    }

    // ---------------------------------------------------------- comandos ----

    [RelayCommand]
    private void Next()
    {
        if (IsBusy || IsDone || TestesRodando) return;
        if (Step == TesteStep.Maquina && !ValidateMaquina()) return;
        StatusMessage = "";
        if (Step != TesteStep.Revisao)
        {
            Step = (TesteStep)((int)Step + 1);
            if (Step == TesteStep.Revisao) PrepararRevisao();
            RaiseReviewFields();
        }
    }

    private bool ValidateMaquina()
    {
        if (SelectedMaquina is null)
        {
            StatusMessage = "Selecione a máquina que está na sua bancada.";
            return false;
        }
        return true;
    }

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack) return;
        StatusMessage = "";
        Step = (TesteStep)((int)Step - 1);
    }

    [RelayCommand]
    private void GoToStep(string? index)
    {
        if (IsBusy || IsDone || TestesRodando) return;
        if (!int.TryParse(index, out var i) || i < 0 || i > 4) return;
        if (i > (int)TesteStep.Maquina && !ValidateMaquina())
        {
            Step = TesteStep.Maquina;
            return;
        }
        StatusMessage = "";
        Step = (TesteStep)i;
        if (Step == TesteStep.Revisao) PrepararRevisao();
        RaiseReviewFields();
    }

    [RelayCommand]
    private async Task RecarregarFilaAsync()
    {
        if (IsBusy || TestesRodando) return;
        IsBusy = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await LoadFilaAsync(cts.Token).ConfigureAwait(true);
        }
        catch (ErpException ex)
        {
            StatusMessage = $"Erro recarregando a fila: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro inesperado: {ex.Message}";
            _logger.LogWarning(ex, "Falha recarregando a fila de teste");
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Roda a bateria de testes automáticos. Os testes que exigem o técnico
    /// (microfone, estéreo, câmera, teclado, touchpad, pixels) NÃO entram: o
    /// check automático é o que a máquina consegue responder sozinha.
    /// </summary>
    [RelayCommand]
    private async Task RodarTestesAsync()
    {
        if (TestesRodando || IsDone) return;
        if (_machine is null)
        {
            StatusMessage = "Ainda coletando o hardware da máquina — aguarde um instante.";
            return;
        }

        TestesRodando = true;
        TestesExecutados = 0;
        Testes.Clear();
        StatusMessage = "";

        var probeUrl = string.IsNullOrWhiteSpace(_config.Options.InternetTestUrl)
            ? "https://www.gstatic.com/generate_204"
            : _config.Options.InternetTestUrl;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var ct = cts.Token;

            var runners = new (string Codigo, string Label, Func<Task<TestResult>> Run)[]
            {
                ("ram", "Memória RAM", () => _engine.RunRamAsync(_machine!, ct)),
                ("armazenamento", "Armazenamento", () => _engine.RunStorageAsync(_storage, ct)),
                ("saude_disco", "Saúde do disco (SMART)", () => _engine.RunStorageHealthAsync(_storage, ct)),
                ("bateria", "Bateria", () => _engine.RunBatteryAsync(_battery, ct)),
                ("carregador", "Carregador", () => _engine.RunChargerAsync(ct)),
                ("hdmi", "HDMI / monitor externo", () => _engine.RunHdmiAsync(ct)),
                ("wifi", "Wi-Fi", () => _engine.RunWifiAsync(ct)),
                ("bluetooth", "Bluetooth", () => _engine.RunBluetoothAsync(ct)),
                ("internet", "Internet", () => _engine.RunInternetAsync(probeUrl, ct)),
                ("audio", "Áudio", () => _engine.RunAudioAsync(ct)),
                ("usb", "Portas USB", () => _engine.RunUsbPortsAsync(ct)),
                ("taxa_atualizacao", "Taxa de atualização", () => _engine.RunRefreshRateAsync(ct)),
                ("biometria", "Digital / Câmera IR", () => _engine.RunBiometricsAsync(ct)),
                ("leitor_cartao", "Leitor de cartão", () => _engine.RunCardReaderAsync(ct)),
            };

            foreach (var (codigo, label, run) in runners)
            {
                ct.ThrowIfCancellationRequested();
                TesteAtual = $"Executando: {label}…";

                var linha = new TesteLinha(codigo, label);
                Testes.Add(linha);

                // O motor de testes nunca propaga exceção: falha vira AutoStatus.Falha.
                var r = await run().ConfigureAwait(true);

                linha.Status = StatusDisplay(r.Status);
                linha.Detalhe = r.Details ?? "";
                linha.ResultadoErp = MapStatus(r.Status);

                TestesExecutados++;
                OnPropertyChanged(nameof(TestesResumo));
                OnPropertyChanged(nameof(TotalTestes));
            }

            TesteAtual = "";
            StatusMessage = $"Testes concluídos: {TestesResumo}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Os testes demoraram demais e foram interrompidos.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro rodando os testes: {ex.Message}";
            _logger.LogError(ex, "Erro na bateria de testes do teste completo");
        }
        finally
        {
            TestesRodando = false;
            TesteAtual = "";
            OnPropertyChanged(nameof(TestesConcluidos));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(PodeEnviar));
        }
    }

    private void PrepararRevisao()
    {
        var sugerido = SugerirResultado();
        SelectedResultado ??= Resultados.First(r => r.Value == sugerido);
    }

    private string? _idempotencyKey;

    [RelayCommand]
    private async Task EnviarAsync()
    {
        if (IsBusy || IsDone) return;
        if (!ValidateMaquina())
        {
            Step = TesteStep.Maquina;
            return;
        }
        if (Testes.Count == 0)
        {
            StatusMessage = "Rode os testes antes de enviar (etapa Testes).";
            Step = TesteStep.Testes;
            return;
        }
        if (SelectedResultado is null)
        {
            StatusMessage = "Escolha o resultado do teste.";
            return;
        }
        if (!_erp.IsConfigured) { StatusMessage = "ERP não configurado."; return; }

        // Mesma chave em retries (idempotência); nova só após sucesso.
        _idempotencyKey ??= Guid.NewGuid().ToString();

        IsBusy = true;
        StatusMessage = "Enviando o check automático…";
        try
        {
            var divergencias = ConfigDivergencias.ToList();
            var faltantes = AcessoriosFaltando.ToList();

            var req = new ErpAutocheckRequest
            {
                AssetId = SelectedMaquina!.AssetId,
                Resultado = SelectedResultado!.Value,
                Observacoes = NullIfEmpty(Observacoes),
                Especificacoes = _specs,
                Testes = Testes.Select(t => new ErpAutocheckTeste
                {
                    Codigo = t.Codigo,
                    Label = t.Label,
                    Resultado = t.ResultadoErp,
                    Detalhe = NullIfEmpty(t.Detalhe),
                    Valor = NullIfEmpty(t.Valor),
                }).ToList(),
                ConfigConfere = TemConfigAcordada ? ConfigConfere : null,
                ConfigDivergencias = divergencias.Count == 0 ? null : divergencias,
                AcessoriosFaltantes = faltantes.Count == 0 ? null : faltantes,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var resp = await _erp.CreateAutocheckAsync(req, _idempotencyKey, cts.Token).ConfigureAwait(true);

            ResultOk = true;
            IsDone = true;
            var ordem = resp.OrdemNumero is int n ? $" (ordem #{n})" : "";
            ResultText = resp.Idempotent
                ? $"Já tinha sido enviado (reenvio){ordem}. Aguardando o OK no ERP."
                : $"Check automático enviado{ordem}! Agora alguém precisa conferir e dar o OK no ERP para a máquina seguir para a aprovação.";
            StatusMessage = "";
            _idempotencyKey = null;
        }
        catch (ErpException ex)
        {
            ResultOk = false;
            // 409 = máquina fora da fase de teste completo (mensagem pronta do ERP)
            StatusMessage = ex.StatusCode == 409 ? ex.Message : $"Erro ao enviar: {ex.Message}";
        }
        catch (Exception ex)
        {
            ResultOk = false;
            StatusMessage = $"Erro inesperado: {ex.Message}";
            _logger.LogError(ex, "Erro enviando o check automático");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void NovoTeste()
    {
        IsDone = false;
        ResultText = "";
        Observacoes = "";
        SelectedResultado = null;
        SelectedMaquina = null;
        Testes.Clear();
        TestesExecutados = 0;
        Step = TesteStep.Maquina;
        OnPropertyChanged(nameof(TestesResumo));
        _ = RecarregarFilaAsync();
    }

    [RelayCommand]
    private void Fechar() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void RaiseReviewFields()
    {
        foreach (var p in new[]
        {
            nameof(RevMaquina), nameof(RevOrdem), nameof(RevPedido), nameof(RevConfigAcordada),
            nameof(RevConfigConfere), nameof(RevAcessoriosFaltantes), nameof(RevTestes),
            nameof(RevObservacoes), nameof(TestesResumo), nameof(PodeEnviar),
            nameof(ConfigDivergencias), nameof(TemConfigDivergencias), nameof(ConfigDivergenciasText),
            nameof(TemAcessoriosFaltando), nameof(AcessoriosAvisoText),
        })
            OnPropertyChanged(p);
    }

    // ----------------------------------------------------------- helpers ----

    /// <summary>Vocabulário do motor de testes → vocabulário do ERP.</summary>
    private static string MapStatus(AutoStatus s) => s switch
    {
        AutoStatus.OK => "aprovado",
        AutoStatus.Atencao => "atencao",
        AutoStatus.Falha => "reprovado",
        AutoStatus.NaoAplicavel => "nao_se_aplica",
        _ => "nao_testado",
    };

    private static string StatusDisplay(AutoStatus s) => s switch
    {
        AutoStatus.OK => "OK",
        AutoStatus.Atencao => "Atenção",
        AutoStatus.Falha => "Falha",
        AutoStatus.NaoAplicavel => "Não aplicável",
        _ => "Não testado",
    };

    private static string Trimmed(string? s, string fallback) =>
        string.IsNullOrWhiteSpace(s) ? fallback : s!.Trim();

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s!.Trim();

    private static string Clip(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? "" : (s!.Length > max ? s[..max] : s);

    private static string? NullIfEmptyStr(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static ErpEspecificacoes BuildSpecs(
        MachineInfo m,
        IReadOnlyList<StorageInfo> disks,
        BatteryInfo? bat,
        DisplayInfo? disp)
    {
        var primary = disks?.OrderByDescending(d => d.CapacityGb).FirstOrDefault();
        var totalGb = disks is null ? 0 : disks.Sum(d => (double)d.CapacityGb);

        return new ErpEspecificacoes
        {
            Processador = NullIfEmptyStr(Clip(m.Processor?.Name ?? m.Cpu, 120)),
            RamGb = m.RamGb > 0 ? (int)Math.Round(m.RamGb) : (m.Memory?.TotalGb is decimal tg ? (int)Math.Round(tg) : null),
            RamTipo = NullIfEmptyStr(Clip(m.Memory?.Type, 40)),
            RamSlots = m.Memory?.SlotsTotal,
            StorageGb = totalGb > 0 ? (int)Math.Round(totalGb) : null,
            StorageTipo = MapStorageType(primary?.Type),
            StorageHealthPct = primary?.LifePercentRemaining,
            Gpu = NullIfEmptyStr(Clip(m.GraphicsAdapter ?? m.GraphicsDetails?.FirstOrDefault()?.Name, 120)),
            Resolucao = NullIfEmptyStr(Clip(disp?.Resolution ?? m.ScreenResolution, 40)),
            So = NullIfEmptyStr(Clip(m.Os, 80)),
            Licenca = m.WindowsActivation == AvailabilityFlag.Ativado ? "Ativado"
                      : m.WindowsActivation == AvailabilityFlag.NaoAtivado ? "Não ativado" : null,
            BateriaSaudePct = bat?.HealthPercent is decimal h ? (int)Math.Round(Math.Clamp(h, 0, 100)) : null,
            WifiOk = m.WifiAdapterCount > 0,
            BluetoothOk = m.Bluetooth is not null,
        };
    }

    private static string? MapStorageType(StorageType? t)
    {
        if (t is null) return null;
        var s = t.ToString() ?? "";
        if (s.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) return "nvme";
        if (s.Contains("SSD", StringComparison.OrdinalIgnoreCase)) return "ssd";
        if (s.Contains("HDD", StringComparison.OrdinalIgnoreCase)) return "hdd";
        if (s.Contains("eMMC", StringComparison.OrdinalIgnoreCase)) return "emmc";
        return null;
    }

    private static string DescribeSpecs(ErpEspecificacoes s)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Processador)) parts.Add(s.Processador!);
        if (s.RamGb is int r) parts.Add($"{r} GB RAM{(string.IsNullOrWhiteSpace(s.RamTipo) ? "" : $" {s.RamTipo}")}");
        if (s.StorageGb is int g) parts.Add($"{g} GB {(s.StorageTipo ?? "armazenamento").ToUpperInvariant()}");
        if (!string.IsNullOrWhiteSpace(s.Gpu)) parts.Add(s.Gpu!);
        if (!string.IsNullOrWhiteSpace(s.Resolucao)) parts.Add(s.Resolucao!);
        if (s.BateriaSaudePct is int b) parts.Add($"bateria {b}%");
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }
}
