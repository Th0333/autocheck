using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotebookCheck.Bootstrap;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;
using NotebookCheck.Infrastructure.Erp;
using NotebookCheck.Infrastructure.Inspection;
using NotebookCheck.Infrastructure.Persistence;

namespace NotebookCheck.Presentation;

/// <summary>
/// Etapas do teste completo. São três, e só a primeira pede algo do técnico:
/// escolher a máquina. O check roda e reporta sozinho; no fim, as fotos.
/// </summary>
public enum TesteStep { Selecao = 0, Executando = 1, Fotos = 2 }

/// <summary>Como as fotos da máquina vão ser tiradas.</summary>
public enum FotoModo { NaoEscolhido = 0, Webcam = 1, QrCode = 2 }

/// <summary>Um pedido de compra com as máquinas dele que estão esperando o teste.</summary>
public sealed class PedidoDaFila
{
    public string Numero { get; }
    public List<ErpFilaTesteMaquina> Maquinas { get; }

    public PedidoDaFila(string numero, List<ErpFilaTesteMaquina> maquinas)
    {
        Numero = numero;
        Maquinas = maquinas;
    }

    public string Display => $"{Numero} · {Maquinas.Count} máquina(s) aguardando teste";
    public override string ToString() => Display;
}

/// <summary>Linha da grade de testes executados nesta sessão.</summary>
public sealed partial class TesteLinha : ObservableObject
{
    public string Codigo { get; }
    public string Label { get; }

    [ObservableProperty] private string status = "Aguardando";
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

/// <summary>Acessório obrigatório do pedido, marcável depois do check.</summary>
public sealed partial class AcessorioFaltanteItem : ObservableObject
{
    public string Nome { get; }
    [ObservableProperty] private bool veio = true;
    public AcessorioFaltanteItem(string nome) => Nome = nome;
}

/// <summary>
/// ViewModel do "Teste completo" (check automático):
///
///   1. <b>Seleção</b> — escolhe o pedido de compra e, dentro dele, a máquina.
///   2. <b>Executando</b> — o app lê as especificações, roda a bateria de testes,
///      compara com a configuração acordada no pedido e <b>reporta ao ERP sozinho</b>.
///      A ordem fica em <c>em_andamento</c>; o "Dar OK" é feito no ERP por uma pessoa.
///   3. <b>Fotos</b> — pergunta se quer mandar fotos, pela webcam da bancada ou por
///      QR code no celular. Opcional.
/// </summary>
public sealed partial class TesteCompletoViewModel : ObservableObject
{
    private readonly ErpClient _erp;
    private readonly IHardwareCollector _collector;
    private readonly ITestEngine _engine;
    private readonly MachineIdentityStore _identityStore;
    private readonly AppConfig _config;
    private readonly ILogger<TesteCompletoViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    /// <summary>Disparado quando o usuário pede para fechar a janela.</summary>
    public event EventHandler? CloseRequested;

    public TesteCompletoViewModel(
        ErpClient erp,
        IHardwareCollector collector,
        ITestEngine engine,
        MachineIdentityStore identityStore,
        AppConfig config,
        ILogger<TesteCompletoViewModel> logger)
    {
        _erp = erp;
        _collector = collector;
        _engine = engine;
        _identityStore = identityStore;
        _config = config;
        _logger = logger;
        // os frames da webcam chegam numa thread do pool; a UI só aceita da dela
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    // ---------------------------------------------------------------- step ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelecao), nameof(IsExecutando), nameof(IsFotos),
        nameof(StepTitle), nameof(StepNumberLabel), nameof(CanGoBack), nameof(PodeIrParaFotos))]
    private TesteStep step = TesteStep.Selecao;

    public bool IsSelecao => Step == TesteStep.Selecao;
    public bool IsExecutando => Step == TesteStep.Executando;
    public bool IsFotos => Step == TesteStep.Fotos;

    /// <summary>Só dá para voltar antes do check começar — depois de reportado, não.</summary>
    public bool CanGoBack => Step == TesteStep.Executando && !Rodando && !Reportado;

    /// <summary>O "Continuar → Fotos" só existe na etapa 2, depois do envio.</summary>
    public bool PodeIrParaFotos => Step == TesteStep.Executando && Reportado;

    public string StepTitle => Step switch
    {
        TesteStep.Selecao => "Qual máquina?",
        TesteStep.Executando => Rodando ? "Rodando o check automático" : "Check automático",
        _ => "Fotos da máquina",
    };

    public string StepNumberLabel => $"Etapa {(int)Step + 1} de 3";

    // -------------------------------------------------------------- estado ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodeIniciar))]
    private bool isBusy;

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool isDone;

    // ------------------------------------------- Etapa 1: pedido + máquina ---

    /// <summary>Pedidos de compra que têm máquina esperando teste (montado da fila).</summary>
    public ObservableCollection<PedidoDaFila> Pedidos { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PedidoSelecionado))]
    private PedidoDaFila? selectedPedido;

    /// <summary>Máquinas do pedido escolhido.</summary>
    public ObservableCollection<ErpFilaTesteMaquina> MaquinasDoPedido { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaquinaSelecionada), nameof(ConfigAcordadaText),
        nameof(TemConfigAcordada), nameof(MaquinaEtapa), nameof(JaReportado),
        nameof(JaReportadoAviso), nameof(PodeIniciar))]
    private ErpFilaTesteMaquina? selectedMaquina;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemFila), nameof(FilaVaziaInfo))]
    private bool filaCarregada;

    public bool TemFila => Pedidos.Count > 0;
    public bool FilaVaziaInfo => FilaCarregada && !TemFila;
    public bool PedidoSelecionado => SelectedPedido is not null;
    public bool MaquinaSelecionada => SelectedMaquina is not null;

    public bool PodeIniciar => SelectedMaquina is not null && !Rodando && !IsBusy;

    public string MaquinaEtapa => SelectedMaquina?.EtapaKanban switch
    {
        "aguardando_tecnico" => "Na fila de teste",
        "em_andamento" => "Em execução (assumida)",
        _ => "—",
    };

    public bool JaReportado => SelectedMaquina?.AutocheckReportado == true;
    public string JaReportadoAviso => JaReportado
        ? "Esta máquina já foi testada e espera o OK no ERP. Rodar de novo substitui os dados anteriores."
        : "";

    public bool TemConfigAcordada => SelectedMaquina?.ConfigAcordada is not null;
    public string ConfigAcordadaText =>
        ErpConfigComparer.Describe(SelectedMaquina?.ConfigAcordada) is { Length: > 0 } d ? d : "—";

    partial void OnSelectedPedidoChanged(PedidoDaFila? value)
    {
        SelectedMaquina = null;
        MaquinasDoPedido.Clear();
        foreach (var m in value?.Maquinas ?? new List<ErpFilaTesteMaquina>())
            MaquinasDoPedido.Add(m);

        // pedido com uma máquina só: escolha óbvia
        if (MaquinasDoPedido.Count == 1) SelectedMaquina = MaquinasDoPedido[0];
    }

    partial void OnSelectedMaquinaChanged(ErpFilaTesteMaquina? value)
    {
        RebuildAcessorios(value);
        DescartarSessaoDeFotos();
    }

    // --------------------------------------- especificações da máquina ------

    private ErpEspecificacoes? _specs;
    private MachineInfo? _machine;
    private IReadOnlyList<StorageInfo> _storage = Array.Empty<StorageInfo>();
    private BatteryInfo? _battery;
    private Task? _coleta;

    public string SpecsResumo => _specs is null ? "—" : DescribeSpecs(_specs);
    public string SpecSerial => Trimmed(_machine?.Serial, "—");

    /// <summary>Divergências entre a config acordada no pedido e o que a máquina tem.</summary>
    public IReadOnlyList<string> ConfigDivergencias =>
        ErpConfigComparer.Divergencias(SelectedMaquina?.ConfigAcordada, _specs);

    public bool TemConfigDivergencias => ConfigDivergencias.Count > 0;
    public string ConfigDivergenciasText =>
        string.Join("\n", ConfigDivergencias.Select(d => $"• {d}"));

    // -------------------------------------------- Etapa 2: rodando o check ---

    public ObservableCollection<TesteLinha> Testes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(PodeIniciar), nameof(StepTitle))]
    private bool rodando;

    [ObservableProperty] private string progressoTexto = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(TemResultado), nameof(PodeIrParaFotos))]
    private bool reportado;

    [ObservableProperty] private string erroExecucao = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultadoLabel))]
    private string resultadoErp = "";

    public bool TemResultado => Reportado;

    public string ResultadoLabel => ResultadoErp switch
    {
        "aprovado" => "Aprovado",
        "aprovado_com_ressalvas" => "Aprovado com ressalvas",
        "reprovado" => "Reprovado",
        _ => "—",
    };

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

    public string ReportadoTexto =>
        $"Enviado ao ERP. A ordem continua aberta esperando alguém conferir e dar o OK.";

    // --- ajustes opcionais depois do envio (o técnico é quem sabe da caixa)
    public ObservableCollection<AcessorioFaltanteItem> AcessoriosChecklist { get; } = new();
    public bool TemAcessoriosObrigatorios => AcessoriosChecklist.Count > 0;

    public IReadOnlyList<string> AcessoriosFaltando =>
        AcessoriosChecklist.Where(a => !a.Veio).Select(a => a.Nome).ToList();

    [ObservableProperty] private string observacoes = "";
    [ObservableProperty] private bool ajustesAbertos;
    [ObservableProperty] private bool atualizando;

    private void RebuildAcessorios(ErpFilaTesteMaquina? maquina)
    {
        foreach (var item in AcessoriosChecklist)
            item.PropertyChanged -= OnAcessorioChanged;
        AcessoriosChecklist.Clear();
        foreach (var nome in maquina?.AcessoriosObrigatorios ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(nome)) continue;
            // vieram conferidos no check de entrada; o técnico desmarca o que faltar
            var item = new AcessorioFaltanteItem(nome.Trim());
            item.PropertyChanged += OnAcessorioChanged;
            AcessoriosChecklist.Add(item);
        }
        OnPropertyChanged(nameof(TemAcessoriosObrigatorios));
        OnPropertyChanged(nameof(AcessoriosFaltando));
    }

    private void OnAcessorioChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(AcessoriosFaltando));

    // ------------------------------------------------------------- init -----

    /// <summary>Conecta no ERP, monta a fila por pedido e começa a ler o hardware.</summary>
    public async Task InitializeAsync()
    {
        if (!_erp.IsConfigured)
        {
            StatusMessage = "Integração com o ERP não configurada. Verifique URL e credenciais.";
            IsConnected = false;
            return;
        }

        // a leitura do hardware roda em paralelo com a escolha da máquina
        _coleta = CollectHardwareAsync();

        IsBusy = true;
        StatusMessage = "Conectando ao ERP…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _erp.ConnectAsync(cts.Token).ConfigureAwait(true);
            IsConnected = true;
            StatusMessage = "";
            await CarregarFilaAsync(cts.Token).ConfigureAwait(true);
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
        finally { IsBusy = false; }
    }

    /// <summary>
    /// A fila do ERP vem plana; aqui ela é agrupada por pedido de compra, que é como
    /// o técnico pensa ("chegou o pedido X, vou testar as máquinas dele").
    /// </summary>
    private async Task CarregarFilaAsync(CancellationToken ct)
    {
        var maquinas = await _erp.GetFilaTesteAsync(ct).ConfigureAwait(true);

        Pedidos.Clear();
        MaquinasDoPedido.Clear();
        SelectedPedido = null;
        SelectedMaquina = null;

        foreach (var grupo in maquinas
                     .GroupBy(m => string.IsNullOrWhiteSpace(m.PedidoNumero) ? "Sem pedido" : m.PedidoNumero!)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            Pedidos.Add(new PedidoDaFila(grupo.Key, grupo.ToList()));
        }

        FilaCarregada = true;
        OnPropertyChanged(nameof(TemFila));
        OnPropertyChanged(nameof(FilaVaziaInfo));

        if (Pedidos.Count == 0)
        {
            StatusMessage = "Nenhuma máquina aguardando teste completo. Faça o check de entrada primeiro.";
            return;
        }

        PreselecionarMaquinaDestaBancada();
    }

    /// <summary>
    /// O app roda NA máquina que vai ser testada. Se o cadastro gravou a identidade
    /// aqui (ou se o serial bate com alguém da fila), já abre no pedido e na máquina
    /// certos — o técnico só confere.
    /// </summary>
    private void PreselecionarMaquinaDestaBancada()
    {
        var identidade = _identityStore.TryRead();
        var serial = _machine?.Serial;

        foreach (var pedido in Pedidos)
        {
            var alvo = pedido.Maquinas.FirstOrDefault(m =>
                    identidade?.AssetId is { Length: > 0 } id &&
                    string.Equals(m.AssetId, id, StringComparison.OrdinalIgnoreCase))
                ?? pedido.Maquinas.FirstOrDefault(m =>
                    !string.IsNullOrWhiteSpace(serial) &&
                    !string.IsNullOrWhiteSpace(m.SerialNumber) &&
                    string.Equals(m.SerialNumber!.Trim(), serial!.Trim(), StringComparison.OrdinalIgnoreCase));

            if (alvo is null) continue;

            SelectedPedido = pedido;
            SelectedMaquina = alvo;
            StatusMessage = $"Máquina reconhecida: {alvo.Display}.";
            return;
        }

        // um pedido só na fila: já abre nele
        if (Pedidos.Count == 1) SelectedPedido = Pedidos[0];
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
            OnPropertyChanged(nameof(SpecsResumo));
            OnPropertyChanged(nameof(SpecSerial));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha coletando hardware para o teste completo");
        }
    }

    [RelayCommand]
    private async Task RecarregarFilaAsync()
    {
        if (IsBusy || Rodando) return;
        IsBusy = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await CarregarFilaAsync(cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro recarregando a fila: {ex.Message}";
            _logger.LogWarning(ex, "Falha recarregando a fila de teste");
        }
        finally { IsBusy = false; }
    }

    // -------------------------------------------------- rodar + reportar ----

    /// <summary>
    /// O coração da tela: coleta, roda os testes e reporta ao ERP, sem parar para
    /// perguntar nada. O técnico só assiste.
    /// </summary>
    [RelayCommand]
    private async Task IniciarCheckAsync()
    {
        if (SelectedMaquina is null)
        {
            StatusMessage = "Escolha o pedido e a máquina.";
            return;
        }
        if (Rodando) return;

        Step = TesteStep.Executando;
        Rodando = true;
        Reportado = false;
        ErroExecucao = "";
        Testes.Clear();
        OnPropertyChanged(nameof(StepTitle));

        try
        {
            // a máquina sai da fila JÁ — quem olhar o kanban tem que ver "em andamento"
            ProgressoTexto = "Assumindo a máquina no ERP…";
            await AssumirNoErpAsync().ConfigureAwait(true);
            if (!string.IsNullOrEmpty(ErroExecucao)) return;

            ProgressoTexto = "Lendo as especificações da máquina…";
            if (_coleta is not null) await _coleta.ConfigureAwait(true);
            if (_specs is null) await CollectHardwareAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(SpecsResumo));
            OnPropertyChanged(nameof(ConfigDivergencias));
            OnPropertyChanged(nameof(TemConfigDivergencias));
            OnPropertyChanged(nameof(ConfigDivergenciasText));

            await RodarTestesAsync().ConfigureAwait(true);

            ProgressoTexto = "Enviando ao ERP…";
            await EnviarAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ErroExecucao = "Os testes demoraram demais e foram interrompidos.";
        }
        catch (Exception ex)
        {
            ErroExecucao = $"Erro inesperado: {ex.Message}";
            _logger.LogError(ex, "Erro no check automático");
        }
        finally
        {
            Rodando = false;
            ProgressoTexto = "";
            OnPropertyChanged(nameof(StepTitle));
            OnPropertyChanged(nameof(CanGoBack));
        }
    }

    /// <summary>
    /// Tira a ordem da fila e põe em execução no kanban do ERP, antes de gastar os
    /// minutos da bateria de testes. Se já estava em execução, o ERP não faz nada.
    /// </summary>
    private async Task AssumirNoErpAsync()
    {
        if (SelectedMaquina is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var resp = await _erp.StartAutocheckAsync(SelectedMaquina.AssetId, Environment.UserName, cts.Token)
                .ConfigureAwait(true);

            SelectedMaquina.EtapaKanban = resp.Etapa ?? "em_andamento";
            OnPropertyChanged(nameof(MaquinaEtapa));
        }
        catch (ErpException ex)
        {
            // 409 = a máquina saiu da fase de teste (alguém mexeu no kanban)
            ErroExecucao = ex.StatusCode == 409
                ? ex.Message
                : $"Não consegui assumir a máquina no ERP: {ex.Message}";
        }
        catch (Exception ex)
        {
            ErroExecucao = $"Erro inesperado ao assumir a máquina: {ex.Message}";
            _logger.LogWarning(ex, "Falha assumindo a máquina no ERP");
        }
    }

    /// <summary>
    /// Bateria de testes automáticos. Os que exigem o técnico (microfone, estéreo,
    /// câmera, teclado, touchpad, pixels) ficam de fora: o check automático é o que
    /// a máquina consegue responder sozinha.
    /// </summary>
    private async Task RodarTestesAsync()
    {
        if (_machine is null)
        {
            ErroExecucao = "Não consegui ler o hardware desta máquina.";
            return;
        }

        var probeUrl = string.IsNullOrWhiteSpace(_config.Options.InternetTestUrl)
            ? "https://www.gstatic.com/generate_204"
            : _config.Options.InternetTestUrl;

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

        // a grade nasce inteira, para o técnico ver o que falta
        foreach (var (codigo, label, _) in runners)
            Testes.Add(new TesteLinha(codigo, label));

        foreach (var (codigo, label, run) in runners)
        {
            ct.ThrowIfCancellationRequested();
            ProgressoTexto = $"Testando: {label}…";

            var linha = Testes.First(t => t.Codigo == codigo);
            linha.Status = "Rodando…";

            // o motor de testes nunca propaga exceção: falha vira AutoStatus.Falha
            var r = await run().ConfigureAwait(true);

            linha.Status = StatusDisplay(r.Status);
            linha.Detalhe = r.Details ?? "";
            linha.ResultadoErp = MapStatus(r.Status);

            // a bateria merece mais que "desgaste X%": quem lê no ERP quer saber
            // quanto sobrou também
            if (codigo == "bateria")
            {
                if (DescreverBateria(_battery) is { Length: > 0 } detalhe) linha.Detalhe = detalhe;
                if (_battery?.HealthPercent is decimal saude) linha.Valor = $"{saude:F0}% de saúde";
            }

            OnPropertyChanged(nameof(TestesResumo));
        }
    }

    /// <summary>
    /// Resultado derivado do que os testes acharam: qualquer falha reprova; ressalva
    /// (ou config/acessório fora do acordado) aprova com ressalvas.
    /// </summary>
    private string CalcularResultado()
    {
        if (Testes.Any(t => t.ResultadoErp == "reprovado")) return "reprovado";
        if (Testes.Any(t => t.ResultadoErp == "atencao") || TemConfigDivergencias || AcessoriosFaltando.Count > 0)
            return "aprovado_com_ressalvas";
        return "aprovado";
    }

    private async Task EnviarAsync()
    {
        if (SelectedMaquina is null) return;

        var divergencias = ConfigDivergencias.ToList();
        var faltantes = AcessoriosFaltando.ToList();
        var resultado = CalcularResultado();

        var req = new ErpAutocheckRequest
        {
            AssetId = SelectedMaquina.AssetId,
            Resultado = resultado,
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
            ConfigConfere = TemConfigAcordada ? !TemConfigDivergencias : null,
            ConfigDivergencias = divergencias.Count == 0 ? null : divergencias,
            AcessoriosFaltantes = faltantes.Count == 0 ? null : faltantes,
        };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            // chave nova a cada envio: reenviar SUBSTITUI os dados no ERP
            await _erp.CreateAutocheckAsync(req, Guid.NewGuid().ToString(), cts.Token).ConfigureAwait(true);

            ResultadoErp = resultado;
            Reportado = true;
            ErroExecucao = "";
        }
        catch (ErpException ex)
        {
            ErroExecucao = ex.StatusCode == 409
                ? ex.Message // máquina saiu da fase de teste completo
                : $"Falha ao enviar ao ERP: {ex.Message}";
        }
    }

    /// <summary>Reenvia com as observações/acessórios que o técnico ajustou depois.</summary>
    [RelayCommand]
    private async Task AtualizarNoErpAsync()
    {
        if (Atualizando || !Reportado) return;
        Atualizando = true;
        try
        {
            await EnviarAsync().ConfigureAwait(true);
            if (string.IsNullOrEmpty(ErroExecucao)) StatusMessage = "Dados atualizados no ERP.";
        }
        finally { Atualizando = false; }
    }

    [RelayCommand]
    private async Task TentarDeNovoAsync()
    {
        ErroExecucao = "";
        await IniciarCheckAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void VoltarParaSelecao()
    {
        if (!CanGoBack) return;
        Step = TesteStep.Selecao;
        Testes.Clear();
        ErroExecucao = "";
    }

    /// <summary>Depois do check reportado, segue para a pergunta das fotos.</summary>
    [RelayCommand]
    private void IrParaFotos()
    {
        if (!Reportado) return;
        Step = TesteStep.Fotos;
    }

    // -------------------------------------------------- Etapa 3: fotos ------

    private readonly WebcamCapture _webcam = new();
    private ErpFotoSessao? _sessaoFotos;
    private CancellationTokenSource? _pollCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EscolheuModo), nameof(UsandoWebcam), nameof(UsandoQr))]
    private FotoModo fotoModo = FotoModo.NaoEscolhido;

    public bool EscolheuModo => FotoModo != FotoModo.NaoEscolhido;
    public bool UsandoWebcam => FotoModo == FotoModo.Webcam;
    public bool UsandoQr => FotoModo == FotoModo.QrCode;

    [ObservableProperty] private string fotoErro = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FotosResumo))]
    private int fotosEnviadas;

    public string FotosResumo => FotosEnviadas == 0
        ? "Nenhuma foto enviada — a etapa é opcional"
        : $"{FotosEnviadas} foto(s) no ERP";

    public ObservableCollection<CameraOption> Cameras { get; } = new();
    [ObservableProperty] private CameraOption? selectedCamera;
    [ObservableProperty] private BitmapSource? cameraFrame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodeTirarFoto))]
    private bool cameraLigada;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodeTirarFoto))]
    private bool enviandoFoto;

    public bool PodeTirarFoto => CameraLigada && !EnviandoFoto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemQr))]
    private BitmapSource? qrImagem;

    [ObservableProperty] private string qrUrl = "";
    public bool TemQr => QrImagem is not null;

    private void DescartarSessaoDeFotos()
    {
        if (_sessaoFotos is null) return;
        PararPolling();
        _sessaoFotos = null;
        QrImagem = null;
        QrUrl = "";
        FotoModo = FotoModo.NaoEscolhido;
        FotosEnviadas = 0;
    }

    /// <summary>
    /// Abre (uma vez) a sessão de fotos da máquina. O token é a credencial: vale só
    /// para esta máquina, expira em 2h e só serve para enviar foto — é o que vai
    /// dentro do QR e o que a webcam usa.
    /// </summary>
    private async Task<ErpFotoSessao?> GarantirSessaoAsync()
    {
        if (_sessaoFotos is not null) return _sessaoFotos;
        if (SelectedMaquina is null) return null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _sessaoFotos = await _erp.CreatePhotoSessionAsync(SelectedMaquina.AssetId, "teste", cts.Token)
            .ConfigureAwait(true);
        return _sessaoFotos;
    }

    [RelayCommand]
    private async Task UsarQrAsync()
    {
        FotoErro = "";
        await PararWebcamAsync().ConfigureAwait(true);
        FotoModo = FotoModo.QrCode;

        try
        {
            var sessao = await GarantirSessaoAsync().ConfigureAwait(true);
            if (sessao is null) { FotoErro = "Escolha a máquina primeiro."; return; }

            QrUrl = sessao.Url;
            QrImagem = QrCodeFactory.Create(sessao.Url, pixelsPerModule: 6);
            OnPropertyChanged(nameof(TemQr));
            IniciarPolling(sessao.Token);
        }
        catch (ErpException ex)
        {
            FotoErro = ex.StatusCode == 404
                ? "O ERP ainda não expõe as sessões de foto (endpoint pendente). Atualize o ERP."
                : $"Não consegui gerar o QR: {ex.Message}";
        }
        catch (Exception ex)
        {
            FotoErro = $"Erro inesperado: {ex.Message}";
            _logger.LogWarning(ex, "Falha gerando o QR de fotos");
        }
    }

    /// <summary>Pergunta ao ERP, de tempos em tempos, quantas fotos o celular já mandou.</summary>
    private void IniciarPolling(string token)
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    var status = await _erp.GetPhotoSessionStatusAsync(token, ct).ConfigureAwait(false);
                    _dispatcher.Invoke(() => FotosEnviadas = status.FotosEnviadas);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Falha consultando a sessão de fotos (segue tentando)");
                }
            }
        }, ct);
    }

    private void PararPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    [RelayCommand]
    private async Task UsarWebcamAsync()
    {
        FotoErro = "";
        PararPolling();
        FotoModo = FotoModo.Webcam;
        QrImagem = null;

        try
        {
            var sessao = await GarantirSessaoAsync().ConfigureAwait(true);
            if (sessao is null) { FotoErro = "Escolha a máquina primeiro."; return; }

            if (Cameras.Count == 0)
            {
                foreach (var c in await WebcamCapture.ListarCamerasAsync().ConfigureAwait(true))
                    Cameras.Add(c);
            }
            if (Cameras.Count == 0)
            {
                FotoErro = "Nenhuma câmera encontrada. Ligue a webcam da bancada ou use o QR code.";
                return;
            }
            SelectedCamera ??= Cameras[0];

            _webcam.FrameReady -= OnFrameReady;
            _webcam.FrameReady += OnFrameReady;
            await _webcam.IniciarAsync(SelectedCamera!.Id).ConfigureAwait(true);
            CameraLigada = true;
        }
        catch (InvalidOperationException ex)
        {
            CameraLigada = false;
            FotoErro = ex.Message;
        }
        catch (ErpException ex)
        {
            FotoErro = $"Não consegui abrir a sessão de fotos: {ex.Message}";
        }
        catch (Exception ex)
        {
            CameraLigada = false;
            FotoErro = $"Erro inesperado: {ex.Message}";
            _logger.LogWarning(ex, "Falha iniciando a webcam");
        }
    }

    private void OnFrameReady(object? sender, BitmapSource frame) =>
        _dispatcher.BeginInvoke(() => CameraFrame = frame);

    [RelayCommand]
    private async Task TrocarCameraAsync()
    {
        if (SelectedCamera is null) return;
        await PararWebcamAsync().ConfigureAwait(true);
        await UsarWebcamAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task TirarFotoAsync()
    {
        if (!PodeTirarFoto || _sessaoFotos is null) return;

        EnviandoFoto = true;
        FotoErro = "";
        try
        {
            var jpeg = await _webcam.TirarFotoJpegAsync().ConfigureAwait(true);
            if (jpeg is null || jpeg.Length == 0)
            {
                FotoErro = "A câmera ainda não entregou imagem. Espere um instante e tente de novo.";
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var resp = await _erp.UploadPhotoAsync(_sessaoFotos.Token, jpeg, cts.Token).ConfigureAwait(true);
            FotosEnviadas = resp.FotosEnviadas;
        }
        catch (ErpException ex)
        {
            FotoErro = ex.StatusCode == 409
                ? "Limite de fotos atingido nesta sessão."
                : $"Falha ao enviar a foto: {ex.Message}";
        }
        catch (Exception ex)
        {
            FotoErro = $"Erro inesperado: {ex.Message}";
            _logger.LogWarning(ex, "Falha enviando foto da webcam");
        }
        finally { EnviandoFoto = false; }
    }

    private async Task PararWebcamAsync()
    {
        _webcam.FrameReady -= OnFrameReady;
        await _webcam.PararAsync().ConfigureAwait(true);
        CameraLigada = false;
        CameraFrame = null;
    }

    /// <summary>Solta a câmera e o polling. A janela chama ao fechar.</summary>
    public async Task LiberarRecursosAsync()
    {
        PararPolling();
        await PararWebcamAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------- fim do fluxo ---

    [RelayCommand]
    private async Task FinalizarAsync()
    {
        await LiberarRecursosAsync().ConfigureAwait(true);
        IsDone = true;
    }

    [RelayCommand]
    private async Task TestarOutraAsync()
    {
        await LiberarRecursosAsync().ConfigureAwait(true);
        _sessaoFotos = null;
        FotoModo = FotoModo.NaoEscolhido;
        FotosEnviadas = 0;
        QrImagem = null;
        QrUrl = "";
        FotoErro = "";

        IsDone = false;
        Reportado = false;
        ResultadoErp = "";
        ErroExecucao = "";
        Observacoes = "";
        AjustesAbertos = false;
        Testes.Clear();
        SelectedMaquina = null;
        Step = TesteStep.Selecao;

        await RecarregarFilaAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void Fechar() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ----------------------------------------------------------- helpers ----

    /// <summary>
    /// "Resta 68% da capacidade original · 32% de desgaste · 45.100 de 66.000 mWh ·
    /// 123 ciclos". O motor de testes só reporta o desgaste; quem confere no ERP
    /// quer os dois lados do número.
    /// </summary>
    private static string DescreverBateria(BatteryInfo? b)
    {
        if (b?.HealthPercent is not decimal saude) return "";

        var partes = new List<string>
        {
            $"Resta {saude:F1}% da capacidade original",
            $"{(b.WearPercent ?? (100m - saude)):F1}% de desgaste",
        };

        if (b.FullChargeCapacityMwh is int cheia && b.DesignCapacityMwh is int projeto && projeto > 0)
            partes.Add($"{cheia:N0} de {projeto:N0} mWh");
        if (b.CycleCount is int ciclos && ciclos > 0)
            partes.Add($"{ciclos} ciclos");

        return string.Join(" · ", partes);
    }

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
