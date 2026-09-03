using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Infrastructure.Erp;
using NotebookCheck.Infrastructure.Persistence;

namespace NotebookCheck.Presentation;

/// <summary>Etapas do wizard de cadastro no estoque.</summary>
public enum CadastroStep { Pedido = 0, Identificacao = 1, Condicao = 2, Destino = 3, Revisao = 4 }

/// <summary>Opção (valor técnico + rótulo amigável) para dropdowns de enum.</summary>
public sealed record CadastroOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Campo ainda vazio no momento da revisão (obrigatório ou opcional).</summary>
public sealed record Pendencia(string Label, bool Required);

/// <summary>Acessório obrigatório do pedido de compra, marcável pelo técnico.</summary>
public sealed partial class AcessorioCheckItem : ObservableObject
{
    public string Nome { get; }
    [ObservableProperty] private bool isChecked;
    public AcessorioCheckItem(string nome) => Nome = nome;
}

/// <summary>
/// Peça que o técnico marca como diferente da config acordada no pedido. A
/// conferência automática pré-marca RAM/armazenamento/processador quando
/// divergem; o técnico confirma, desmarca (falso positivo) ou acrescenta o que
/// a leitura automática não enxerga (placa de vídeo, tela, outro).
/// </summary>
public sealed partial class PecaDivergenteItem : ObservableObject
{
    public string Codigo { get; }
    public string Nome { get; }
    [ObservableProperty] private bool isChecked;
    /// <summary>Divergência detectada automaticamente (o técnico ainda decide).</summary>
    [ObservableProperty] private bool automatica;
    /// <summary>Texto da divergência automática ("RAM: acordado 16 GB, encontrado 8 GB").</summary>
    public string? TextoAutomatico { get; set; }
    public PecaDivergenteItem(string codigo, string nome) { Codigo = codigo; Nome = nome; }
}

/// <summary>
/// ViewModel do modo "Cadastro no estoque": wizard de 4 etapas (Identificação,
/// Condição, Destino, Revisão). Todo cadastro parte de um pedido de compra do
/// ERP — fornecedor, documento e valores vêm do pedido; o NTB é gerado pelo
/// servidor. O pedido também define os requisitos (condição estética mínima e
/// acessórios obrigatórios) que o app sinaliza quando não atendidos.
/// </summary>
public sealed partial class CadastroViewModel : ObservableObject
{
    private readonly ErpClient _erp;
    private readonly IHardwareCollector _collector;
    private readonly SerialNtbStore _serialNtb;
    private readonly LinhaStore _linhaStore;
    private readonly MachineIdentityStore _identityStore;
    private readonly ILogger<CadastroViewModel> _logger;

    /// <summary>Disparado quando o usuário pede para fechar a janela.</summary>
    public event EventHandler? CloseRequested;

    public CadastroViewModel(
        ErpClient erp,
        IHardwareCollector collector,
        SerialNtbStore serialNtb,
        LinhaStore linhaStore,
        MachineIdentityStore identityStore,
        ILogger<CadastroViewModel> logger)
    {
        _erp = erp;
        _collector = collector;
        _serialNtb = serialNtb;
        _linhaStore = linhaStore;
        _identityStore = identityStore;
        _logger = logger;
        // Fluxo padrão (§3.5): "producao_tecnica" leva a máquina à bancada (Em
        // andamento) para o teste completo/autocheck; o técnico só dá o OK depois.
        // O ERP já corrigiu a função da OS (v_os) e a transição de aguardando_teste,
        // então o cadastro pelo app não escolhe mais destino — segue sempre para o
        // teste. A colocação manual em outra etapa é feita pelo kanban do ERP.
        SelectedProximoDestino = ProximoDestinos.First(o => o.Value == "producao_tecnica");
        InitPecasDivergentes();
    }

    // ---------------------------------------------------------------- step ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPedido), nameof(IsIdentificacao), nameof(IsCondicao),
        nameof(IsDestino), nameof(IsRevisao), nameof(StepTitle), nameof(StepNumberLabel),
        nameof(CanGoBack), nameof(NextLabel), nameof(IsLastStep))]
    private CadastroStep step = CadastroStep.Pedido;

    public bool IsPedido => Step == CadastroStep.Pedido;
    public bool IsIdentificacao => Step == CadastroStep.Identificacao;
    public bool IsCondicao => Step == CadastroStep.Condicao;
    public bool IsDestino => Step == CadastroStep.Destino;
    public bool IsRevisao => Step == CadastroStep.Revisao;
    public bool IsLastStep => Step == CadastroStep.Revisao;
    public bool CanGoBack => Step != CadastroStep.Pedido && !IsBusy && !IsDone;
    public string NextLabel => Step == CadastroStep.Destino ? "Revisar →" : "Avançar →";

    public string StepTitle => Step switch
    {
        CadastroStep.Pedido => "Pedido de compra",
        CadastroStep.Identificacao => "Identificação",
        CadastroStep.Condicao => "Condição",
        CadastroStep.Destino => "Localização",
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

    // ------------------------------------ Etapa 1: Pedido + Identificação ---

    public ObservableCollection<ErpPedidoCompra> Pedidos { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PedidoSelecionado), nameof(PedidoFornecedor),
        nameof(PedidoDocumento), nameof(PedidoProgresso), nameof(PedidoCondicaoMinima),
        nameof(PedidoTemRequisitos), nameof(TemCondicaoMinima), nameof(CondicaoAbaixoMinimo),
        nameof(CondicaoAvisoText), nameof(TemAcessoriosObrigatorios))]
    private ErpPedidoCompra? selectedPedido;

    public bool PedidoSelecionado => SelectedPedido is not null;
    public string PedidoFornecedor => Trimmed(SelectedPedido?.FornecedorNome, "—");
    public string PedidoDocumento => Trimmed(SelectedPedido?.DocumentoEntrada, "—");
    public string PedidoProgresso => SelectedPedido?.QuantidadeTotal is int total
        ? $"{SelectedPedido?.QuantidadeRecebida ?? 0} de {total} máquinas recebidas"
        : "—";
    public string PedidoCondicaoMinima =>
        Domain.Rules.CondicaoEstetica.Label(SelectedPedido?.CondicaoMinima) is { Length: > 0 } l ? l : "—";
    public bool TemCondicaoMinima => !string.IsNullOrWhiteSpace(SelectedPedido?.CondicaoMinima);
    public bool PedidoTemRequisitos => TemCondicaoMinima || TemAcessoriosObrigatorios;
    public bool TemAcessoriosObrigatorios => (SelectedPedido?.AcessoriosObrigatorios?.Count ?? 0) > 0;

    partial void OnSelectedPedidoChanged(ErpPedidoCompra? value)
    {
        RebuildAcessorios(value);
        // Modelo previsto do pedido ajuda quando o WMI não trouxe nada útil.
        if (string.IsNullOrWhiteSpace(Modelo) && !string.IsNullOrWhiteSpace(value?.ModeloPrevisto))
            Modelo = value!.ModeloPrevisto!.Trim();
        TrySelectMarca();
        RaiseCondicaoWarnings();
        RaiseAcessorioWarnings();
        _ = LoadMaquinasAsync(value);
    }

    // ------------------------------------------ máquinas em branco do pedido ---

    /// <summary>Máquinas em branco do pedido (uma delas é reivindicada no cadastro).</summary>
    public ObservableCollection<ErpPedidoMaquina> MaquinasDisponiveis { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaquinaConfigAcordadaText), nameof(TemConfigAcordada))]
    private ErpPedidoMaquina? selectedMaquina;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemMaquinas), nameof(SemMaquinasInfo),
        nameof(PedidoSemVaga), nameof(PedidoSemVagaTexto))]
    private bool maquinasCarregadas;

    [ObservableProperty] private bool maquinasCarregando;

    /// <summary>A lista do pedido respondeu (o endpoint de máquinas existe e trouxe o quadro).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SemMaquinasInfo), nameof(PedidoSemVaga), nameof(PedidoSemVagaTexto))]
    private bool maquinasListaOk;

    /// <summary>Quantas máquinas o pedido tem no ERP (cadastradas ou não).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PedidoSemVagaTexto))]
    private int maquinasNoPedido;

    public bool TemMaquinas => MaquinasDisponiveis.Count > 0;

    /// <summary>Lista indisponível (endpoint fora do ar): o servidor escolhe a próxima em branco.</summary>
    public bool SemMaquinasInfo => MaquinasCarregadas && !TemMaquinas && !MaquinasListaOk;

    /// <summary>
    /// A lista veio, mas nenhuma máquina do pedido aceita cadastro. Antes o
    /// wizard deixava seguir e só o POST reclamava lá no fim ("o pedido já
    /// recebeu todas as máquinas previstas") — agora o bloqueio é aqui.
    /// </summary>
    public bool PedidoSemVaga => MaquinasCarregadas && !TemMaquinas && MaquinasListaOk;

    public string PedidoSemVagaTexto => MaquinasNoPedido == 0
        ? "Este pedido não tem máquinas para cadastrar no ERP."
        : "Todas as máquinas deste pedido já foram cadastradas — escolha outro pedido.";

    public bool TemConfigAcordada => SelectedMaquina?.ConfigAcordada is not null;
    public string MaquinaConfigAcordadaText =>
        ErpConfigComparer.Describe(SelectedMaquina?.ConfigAcordada) is { Length: > 0 } d
            ? $"Config acordada: {d}"
            : "";

    // ------------------------------- conferência: config × pedido de compra ---

    /// <summary>Linhas da conferência automática (RAM, armazenamento, processador).</summary>
    public ObservableCollection<ConfigCheckItem> ConfigChecagem { get; } = new();

    /// <summary>Peças que o técnico marca como diferentes do acordado.</summary>
    public ObservableCollection<PecaDivergenteItem> PecasDivergentes { get; } = new();

    /// <summary>Descrição livre quando "Outro" está marcado.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigDivergencias), nameof(ConfigDivergenciasText))]
    private string outroDescricao = "";

    public bool SpecsProntas => _specs is not null;

    /// <summary>Pedido fixou config, mas a leitura da máquina ainda não terminou.</summary>
    public bool ChecagemAguardando => TemConfigAcordada && !SpecsProntas;

    /// <summary>Há linhas para mostrar (config acordada + specs lidas).</summary>
    public bool MostraChecagem => ConfigChecagem.Count > 0;

    /// <summary>Tudo bateu na conferência e o técnico não marcou nada.</summary>
    public bool ConfigBate => MostraChecagem && !ConfigDiverge;

    /// <summary>Alguma peça diverge — pela leitura automática ou pela marcação do técnico.</summary>
    public bool ConfigDiverge => PecasDivergentes.Any(p => p.IsChecked);

    public bool OutroMarcado =>
        PecasDivergentes.FirstOrDefault(p => p.Codigo == ErpConfigComparer.Outro)?.IsChecked == true;

    public string ConfigAlertaText
    {
        get
        {
            var autos = PecasDivergentes
                .Where(p => p.IsChecked && p.Automatica && !string.IsNullOrWhiteSpace(p.TextoAutomatico))
                .Select(p => p.TextoAutomatico!)
                .ToList();
            var cabeca = autos.Count > 0
                ? $"Configuração fora do acordado no pedido: {string.Join("; ", autos)}."
                : "Peça marcada como diferente do acordado no pedido.";
            return cabeca + " O pedido de compra recebe o alerta e quem o criou é avisado — a máquina segue o fluxo normal.";
        }
    }

    private void InitPecasDivergentes()
    {
        foreach (var (codigo, nome) in new[]
        {
            (ErpConfigComparer.Processador, "Processador"),
            (ErpConfigComparer.Ram, "Memória RAM"),
            (ErpConfigComparer.Armazenamento, "Armazenamento (SSD/HD)"),
            (ErpConfigComparer.PlacaVideo, "Placa de vídeo"),
            (ErpConfigComparer.Tela, "Tela"),
            (ErpConfigComparer.Outro, "Outro (descrever)"),
        })
        {
            var item = new PecaDivergenteItem(codigo, nome);
            item.PropertyChanged += OnPecaDivergenteChanged;
            PecasDivergentes.Add(item);
        }
    }

    private void OnPecaDivergenteChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PecaDivergenteItem.IsChecked)) RaiseConfigWarnings();
    }

    /// <summary>
    /// Refaz a conferência automática (config acordada da máquina escolhida ×
    /// specs lidas) e pré-marca as peças que divergem. Só mexe na marcação
    /// quando o resultado automático MUDA — o que o técnico desmarcou à mão
    /// não volta sozinho a cada releitura.
    /// </summary>
    private void RecomputeConfigChecagem()
    {
        ConfigChecagem.Clear();
        foreach (var linha in ErpConfigComparer.Comparar(SelectedMaquina?.ConfigAcordada, _specs))
            ConfigChecagem.Add(linha);

        foreach (var peca in PecasDivergentes)
        {
            var linha = ConfigChecagem.FirstOrDefault(l => l.Codigo == peca.Codigo);
            var divergeAgora = linha?.Diverge == true;
            peca.TextoAutomatico = divergeAgora ? linha!.Texto : null;
            if (divergeAgora != peca.Automatica)
            {
                peca.Automatica = divergeAgora;
                peca.IsChecked = divergeAgora;
            }
        }
        RaiseConfigWarnings();
    }

    private void RaiseConfigWarnings()
    {
        foreach (var p in new[]
        {
            nameof(SpecsProntas), nameof(ChecagemAguardando), nameof(MostraChecagem),
            nameof(ConfigBate), nameof(ConfigDiverge), nameof(OutroMarcado), nameof(ConfigAlertaText),
            nameof(ConfigDivergencias), nameof(TemConfigDivergencias), nameof(ConfigDivergenciasText),
        })
            OnPropertyChanged(p);
    }

    private void ResetPecasDivergentes()
    {
        foreach (var peca in PecasDivergentes)
        {
            peca.Automatica = false;
            peca.TextoAutomatico = null;
            peca.IsChecked = false;
        }
        OutroDescricao = "";
    }

    partial void OnSelectedMaquinaChanged(ErpPedidoMaquina? value)
    {
        // Mudança que não veio de SelecionarMaquina() foi o técnico no combo:
        // a partir daqui o app não troca mais a máquina sozinho.
        if (!_ajustandoSelecao) _selecaoAutomatica = false;
        // A config acordada é da máquina escolhida: refaz a conferência.
        RecomputeConfigChecagem();
        if (value is null) return;
        // Prefill a partir da máquina do pedido, sem sobrescrever o que o
        // técnico ou o WMI já preencheram.
        if (string.IsNullOrWhiteSpace(Modelo) && !string.IsNullOrWhiteSpace(value.Modelo))
            Modelo = value.Modelo!.Trim();
        if (string.IsNullOrWhiteSpace(Linha) && !string.IsNullOrWhiteSpace(value.Linha))
            Linha = value.Linha!.Trim();
    }

    private async Task LoadMaquinasAsync(ErpPedidoCompra? pedido)
    {
        SelecionarMaquina(null, automatica: false);
        MaquinasDisponiveis.Clear();
        MaquinasCarregadas = false;
        MaquinasListaOk = false;
        MaquinasNoPedido = 0;
        if (pedido is null) return;

        MaquinasCarregando = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var resp = await _erp.GetPedidoMaquinasAsync(pedido.Id, cts.Token).ConfigureAwait(true);
            if (SelectedPedido?.Id != pedido.Id) return; // usuário trocou de pedido no meio

            MaquinasNoPedido = resp.Maquinas.Count;
            MaquinasListaOk = true;

            // Inclui também as que estão em "aguardando recebimento": é o estado
            // em que a máquina nasce no pedido de compra, e é justamente a que
            // está na bancada agora. O cadastro confirma a chegada antes de
            // enviar (ver ConfirmarChegadaAsync).
            foreach (var m in resp.Maquinas.Where(m => m.PodeCadastrar))
                MaquinasDisponiveis.Add(m);

            // Kanban pediu uma máquina específica (vale como escolha do técnico);
            // senão o app escolhe pela config acordada — e reescolhe quando as
            // specs chegarem, se o técnico não tiver mexido no combo.
            if (_preselectAssetId is not null)
            {
                SelecionarMaquina(MaquinasDisponiveis.FirstOrDefault(m =>
                    string.Equals(m.AssetId, _preselectAssetId, StringComparison.OrdinalIgnoreCase)),
                    automatica: false);
                _preselectAssetId = null;
            }
            if (SelectedMaquina is null)
                SelecionarMaquina(EscolherMaquinaPelaConfig(), automatica: true);
        }
        catch (ErpException ex)
        {
            // Endpoint indisponível: segue sem seleção — o servidor pega a próxima em branco.
            _logger.LogWarning(ex, "Falha carregando máquinas do pedido {Pedido}", pedido.Numero);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro inesperado carregando máquinas do pedido");
        }
        finally
        {
            MaquinasCarregando = false;
            MaquinasCarregadas = true;
            OnPropertyChanged(nameof(TemMaquinas));
            OnPropertyChanged(nameof(SemMaquinasInfo));
            OnPropertyChanged(nameof(PedidoSemVaga));
            OnPropertyChanged(nameof(PedidoSemVagaTexto));
        }
    }

    private string? _preselectPedidoId;
    private string? _preselectAssetId;

    /// <summary>A máquina selecionada foi escolha do app (pode ser trocada quando as specs chegarem).</summary>
    private bool _selecaoAutomatica;
    /// <summary>True enquanto SelecionarMaquina() atribui — distingue do técnico mexendo no combo.</summary>
    private bool _ajustandoSelecao;

    private void SelecionarMaquina(ErpPedidoMaquina? maquina, bool automatica)
    {
        _ajustandoSelecao = true;
        try { SelectedMaquina = maquina; }
        finally { _ajustandoSelecao = false; }
        _selecaoAutomatica = automatica && maquina is not null;
    }

    /// <summary>
    /// Qual máquina em branco do pedido é a que está na bancada. Um pedido pode
    /// misturar configs (PC-24: uma Ultra 5 e duas Ryzen 7); pegar "a primeira"
    /// faria o alerta de config acusar processador errado numa máquina certa.
    /// Prefere a em branco cuja config acordada bate com o hardware lido; sem
    /// specs ainda, ou sem nenhuma batendo, fica a primeira — e o alerta fala.
    /// </summary>
    private ErpPedidoMaquina? EscolherMaquinaPelaConfig()
    {
        if (MaquinasDisponiveis.Count == 0) return null;
        if (_specs is null) return MaquinasDisponiveis[0];
        return MaquinasDisponiveis.FirstOrDefault(m =>
                   m.ConfigAcordada is not null
                   && ErpConfigComparer.Divergencias(m.ConfigAcordada, _specs).Count == 0)
               ?? MaquinasDisponiveis[0];
    }

    /// <summary>
    /// Chamado quando as specs chegam: se o app é quem escolheu a máquina,
    /// troca para a de config compatível. Modelo/linha que vieram do prefill da
    /// máquina errada são limpos para o prefill refazer a partir da certa.
    /// </summary>
    private void ReescolherMaquinaPelaConfig()
    {
        if (!_selecaoAutomatica) return;
        var melhor = EscolherMaquinaPelaConfig();
        var anterior = SelectedMaquina;
        if (melhor is null || ReferenceEquals(melhor, anterior)) return;

        if (anterior is not null)
        {
            if (string.Equals(Modelo.Trim(), (anterior.Modelo ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) Modelo = "";
            if (string.Equals(Linha.Trim(), (anterior.Linha ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) Linha = "";
        }
        SelecionarMaquina(melhor, automatica: true);
    }

    /// <summary>
    /// Usado pelo kanban: pré-seleciona o pedido e a máquina em branco assim
    /// que as listas carregarem. Chamar antes de <see cref="InitializeAsync"/>.
    /// </summary>
    public void Preselect(string? pedidoId, string? assetId)
    {
        _preselectPedidoId = NullIfEmpty(pedidoId);
        _preselectAssetId = NullIfEmpty(assetId);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeloInvalid))]
    private string modelo = "";

    [ObservableProperty] private string linha = "";

    public ObservableCollection<ErpRef> Marcas { get; } = new();
    [ObservableProperty] private ErpRef? selectedMarca;

    [ObservableProperty] private string serial = "";
    [ObservableProperty] private bool serialReady;

    public bool ModeloInvalid => string.IsNullOrWhiteSpace(Modelo) || Modelo.Trim().Length < 2;

    // ------------------------------------------------ Etapa 2: Condição -----

    public IReadOnlyList<CadastroOption> Condicoes { get; } = new[]
    {
        new CadastroOption("excelente", "Excelente"),
        new CadastroOption("boa", "Boa"),
        new CadastroOption("regular", "Regular"),
        new CadastroOption("ruim", "Ruim"),
        new CadastroOption("sucata", "Sucata"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CondicaoAbaixoMinimo), nameof(CondicaoAvisoText))]
    private CadastroOption? selectedCondicao;

    /// <summary>True quando a condição informada é pior que a exigida pelo pedido.</summary>
    public bool CondicaoAbaixoMinimo => Domain.Rules.CondicaoEstetica.IsAbaixoDoMinimo(
        SelectedCondicao?.Value, SelectedPedido?.CondicaoMinima);

    public string CondicaoAvisoText => CondicaoAbaixoMinimo
        ? $"Condição abaixo do mínimo do pedido ({PedidoCondicaoMinima}). Confirme com o responsável antes de cadastrar."
        : "";

    /// <summary>Checklist dos acessórios obrigatórios do pedido selecionado.</summary>
    public ObservableCollection<AcessorioCheckItem> AcessoriosChecklist { get; } = new();

    [ObservableProperty] private string acessoriosExtras = "";
    [ObservableProperty] private string defeitos = "";
    [ObservableProperty] private string observacoes = "";

    public IReadOnlyList<string> AcessoriosFaltando =>
        AcessoriosChecklist.Where(a => !a.IsChecked).Select(a => a.Nome).ToList();

    public bool TemAcessoriosFaltando => AcessoriosChecklist.Any(a => !a.IsChecked);

    public string AcessoriosAvisoText => TemAcessoriosFaltando
        ? $"Acessórios obrigatórios não recebidos: {string.Join(", ", AcessoriosFaltando)}."
        : "";

    private void RebuildAcessorios(ErpPedidoCompra? pedido)
    {
        foreach (var item in AcessoriosChecklist)
            item.PropertyChanged -= OnAcessorioChanged;
        AcessoriosChecklist.Clear();
        foreach (var nome in pedido?.AcessoriosObrigatorios ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(nome)) continue;
            var item = new AcessorioCheckItem(nome.Trim());
            item.PropertyChanged += OnAcessorioChanged;
            AcessoriosChecklist.Add(item);
        }
        RaiseAcessorioWarnings();
    }

    private void OnAcessorioChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        RaiseAcessorioWarnings();

    private void RaiseAcessorioWarnings()
    {
        OnPropertyChanged(nameof(AcessoriosFaltando));
        OnPropertyChanged(nameof(TemAcessoriosFaltando));
        OnPropertyChanged(nameof(AcessoriosAvisoText));
    }

    private void RaiseCondicaoWarnings()
    {
        OnPropertyChanged(nameof(CondicaoAbaixoMinimo));
        OnPropertyChanged(nameof(CondicaoAvisoText));
    }

    // -------------------------------------------------- Etapa 3: Destino ----

    public ObservableCollection<ErpRef> Localizacoes { get; } = new();
    [ObservableProperty] private ErpRef? selectedLocalizacao;

    public IReadOnlyList<CadastroOption> ProximoDestinos { get; } = new[]
    {
        new CadastroOption("producao_tecnica", "Produção técnica (cria ordem de diagnóstico)"),
        new CadastroOption("quarentena", "Quarentena (decisão pendente)"),
        new CadastroOption("uso_interno", "Uso interno (aprovação)"),
        new CadastroOption("aprovacao_direta", "Aprovação direta (vai para a fila de aprovação)"),
        new CadastroOption("devolucao_fornecedor", "Devolução ao fornecedor"),
    };
    [ObservableProperty] private CadastroOption? selectedProximoDestino;

    // ------------------------------------------------ Etapa 4: Revisão ------

    public string RevPedido => SelectedPedido?.Display ?? "—";
    public string RevMaquina => SelectedMaquina is { } m
        ? (m.AguardandoRecebimento ? $"{m.Display} (o cadastro confirma o recebimento)" : m.Display)
        : (TemMaquinas ? "—" : "Próxima máquina em branco do pedido");
    public string RevModelo => Trimmed(Modelo, "—");
    public string RevLinha => Trimmed(Linha, "—");
    public string RevMarca => SelectedMarca?.Nome ?? "—";
    public string RevSerial => Trimmed(Serial, "—");
    public string RevCondicao => SelectedCondicao?.Label ?? "—";
    public string RevAcessorios
    {
        get
        {
            var marcados = AcessoriosChecklist.Where(a => a.IsChecked).Select(a => a.Nome).ToList();
            marcados.AddRange(SplitItems(AcessoriosExtras) ?? new List<string>());
            return marcados.Count == 0 ? "—" : string.Join(", ", marcados);
        }
    }
    public string RevDefeitos => Trimmed(Defeitos, "—");
    public string RevObservacoes => Trimmed(Observacoes, "—");
    public string RevLocalizacao => SelectedLocalizacao?.Display ?? "—";
    public string RevProximoDestino => SelectedProximoDestino?.Label ?? "—";

    private ErpEspecificacoes? _specs;
    private Domain.Models.MachineInfo? _machine;

    /// <summary>Resumo das especificações coletadas, exibido na Identificação.</summary>
    public string SpecsResumo => _specs is null ? "Coletando…" : DescribeSpecs(_specs);

    /// <summary>NTB devolvido pelo servidor após o cadastro (gerado automaticamente).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NtbGeradoDisplay), nameof(TemNtbGerado))]
    private string ntbGerado = "";

    public string NtbGeradoDisplay => string.IsNullOrWhiteSpace(NtbGerado)
        ? "" : Domain.Rules.NtbCode.Normalize(NtbGerado);
    public bool TemNtbGerado => !string.IsNullOrWhiteSpace(NtbGerado);

    /// <summary>
    /// Diferenças entre a config acordada no pedido e o que foi coletado da
    /// máquina (RAM, armazenamento, processador). Vão como alerta na aprovação.
    /// </summary>
    private List<string> BuildConfigDivergencias()
    {
        // O que vai para o ERP é o que o TÉCNICO deixou marcado: a leitura
        // automática só sugere. Peça automática desmarcada = falso positivo.
        var list = new List<string>();
        foreach (var p in PecasDivergentes.Where(p => p.IsChecked))
        {
            if (p.Automatica && !string.IsNullOrWhiteSpace(p.TextoAutomatico))
                list.Add(p.TextoAutomatico!);
            else if (p.Codigo == ErpConfigComparer.Outro)
                list.Add(string.IsNullOrWhiteSpace(OutroDescricao)
                    ? "Outro: peça diferente do acordado (marcado pelo técnico)"
                    : $"Outro: {OutroDescricao.Trim()}");
            else
                list.Add($"{p.Nome}: diferente do acordado no pedido (marcado pelo técnico)");
        }
        return list;
    }

    private List<string> BuildPecasDivergentesCodigos() =>
        PecasDivergentes.Where(p => p.IsChecked).Select(p => p.Codigo).ToList();

    public IReadOnlyList<string> ConfigDivergencias => BuildConfigDivergencias();
    public bool TemConfigDivergencias => ConfigDivergencias.Count > 0;
    public string ConfigDivergenciasText => TemConfigDivergencias
        ? $"Configuração fora do acordado no pedido: {string.Join("; ", ConfigDivergencias)}."
        : "";

    /// <summary>Campos vazios destacados na revisão (obrigatórios em vermelho).</summary>
    public IReadOnlyList<Pendencia> Pendencias
    {
        get
        {
            var list = new List<Pendencia>();
            void Add(bool empty, string label, bool required = false)
            {
                if (empty) list.Add(new Pendencia(label, required));
            }
            Add(SelectedPedido is null, "Pedido de compra", required: true);
            Add(TemMaquinas && SelectedMaquina is null, "Máquina do pedido", required: true);
            Add(ModeloInvalid, "Modelo", required: true);
            Add(SelectedMarca is null, "Marca");
            Add(string.IsNullOrWhiteSpace(Linha), "Linha");
            Add(string.IsNullOrWhiteSpace(Serial), "Número de série");
            Add(SelectedCondicao is null, "Condição estética");
            Add(SelectedLocalizacao is null, "Localização inicial");
            return list;
        }
    }

    public bool TemPendencias => Pendencias.Count > 0;
    public bool TudoPreenchido => !TemPendencias;

    // ------------------------------------------------------------- init -----

    /// <summary>Conecta no ERP, carrega pedidos/listas e lê serial + specs da máquina.</summary>
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
            StatusMessage = "Conectado. Carregando pedidos de compra…";

            await LoadListsAsync(cts.Token).ConfigureAwait(true);
        }
        catch (ErpException ex)
        {
            IsConnected = false;
            StatusMessage = $"Erro ao conectar: {ex.Message}";
            _logger.LogWarning(ex, "Falha conectando ao ERP");
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

        // Leitura de hardware em paralelo (não bloqueia o wizard).
        _hardwareTask = CollectHardwareAsync();
    }

    /// <summary>Coleta de hardware em andamento — o cadastro espera por ela.</summary>
    private Task? _hardwareTask;

    /// <summary>
    /// Segura o envio até as specs ficarem prontas. Sem isso um cadastro rápido
    /// manda <c>especificacoes: null</c> e a máquina entra no estoque sem
    /// configuração nenhuma — o técnico vê o cadastro dar certo e o ERP sem a
    /// config que a máquina acabou de reportar.
    /// </summary>
    private async Task AguardarHardwareAsync()
    {
        if (_specs is not null || _hardwareTask is null or { IsCompleted: true }) return;
        var anterior = StatusMessage;
        StatusMessage = "Lendo a configuração da máquina…";
        try { await _hardwareTask.ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Coleta de hardware falhou antes do cadastro"); }
        StatusMessage = anterior;
    }

    private async Task LoadListsAsync(CancellationToken ct)
    {
        var marcas = await _erp.GetMarcasAsync(null, ct).ConfigureAwait(true);
        var locais = await _erp.GetLocalizacoesAsync(null, ct).ConfigureAwait(true);

        Marcas.Clear();
        foreach (var m in marcas) Marcas.Add(m);
        Localizacoes.Clear();
        foreach (var l in locais) Localizacoes.Add(l);

        // Pré-seleciona o "Estoque padrão" se vier marcado.
        SelectedLocalizacao = locais.FirstOrDefault(l => l.IsDefault);

        // Pedidos por último: se o endpoint ainda não existir no ERP, as demais
        // listas já carregaram e a mensagem explica o bloqueio.
        try
        {
            var pedidos = await _erp.GetPedidosCompraAsync(null, ct).ConfigureAwait(true);
            Pedidos.Clear();
            foreach (var p in pedidos) Pedidos.Add(p);
            StatusMessage = pedidos.Count == 0
                ? "Nenhum pedido de compra aberto no ERP. Crie o pedido antes de cadastrar máquinas."
                : "";

            // Vindo do kanban: seleciona o pedido pedido (a máquina é aplicada
            // quando LoadMaquinasAsync terminar).
            if (_preselectPedidoId is not null)
            {
                var alvo = pedidos.FirstOrDefault(p =>
                    string.Equals(p.Id, _preselectPedidoId, StringComparison.OrdinalIgnoreCase));
                _preselectPedidoId = null;
                if (alvo is not null) SelectedPedido = alvo;
            }
        }
        catch (ErpException ex)
        {
            StatusMessage = ex.StatusCode == 404
                ? "O ERP ainda não expõe pedidos de compra (endpoint pendente). Cadastro bloqueado até a API ser atualizada."
                : $"Erro carregando pedidos de compra: {ex.Message}";
            _logger.LogWarning(ex, "Falha carregando pedidos de compra");
        }

        TrySelectMarca();
    }

    private async Task CollectHardwareAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var machine = await _collector.CollectMachineAsync(cts.Token).ConfigureAwait(true);
            var storage = await _collector.CollectStorageAsync(cts.Token).ConfigureAwait(true);
            Domain.Models.BatteryInfo? battery = null;
            Domain.Models.DisplayInfo? display = null;
            try { battery = await _collector.CollectBatteryAsync(cts.Token).ConfigureAwait(true); } catch { }
            try { display = await _collector.CollectDisplayAsync(cts.Token).ConfigureAwait(true); } catch { }

            _machine = machine;
            // Editável: máquinas com serial repetido são diferenciadas à mão.
            if (string.IsNullOrWhiteSpace(Serial))
                Serial = machine.Serial ?? "";
            SerialReady = true;

            _specs = BuildSpecs(machine, storage, battery, display);
            OnPropertyChanged(nameof(SpecsResumo));

            // Com as specs na mão dá para achar a máquina CERTA do pedido (a de
            // config acordada compatível) — antes do prefill de modelo/linha,
            // para ele partir da máquina certa.
            ReescolherMaquinaPelaConfig();

            // Se não veio modelo/marca preenchidos, sugere a partir do hardware.
            if (string.IsNullOrWhiteSpace(Modelo) && !string.IsNullOrWhiteSpace(machine.Model))
                Modelo = machine.Model!.Trim();

            SuggestLinha(machine);
            TrySelectMarca();
            RecomputeConfigChecagem();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha coletando hardware para o cadastro");
            SerialReady = true; // libera o campo mesmo sem serial
        }
    }

    /// <summary>
    /// Preenche a linha: correção já aprendida em cadastros anteriores vence;
    /// senão o "brand name" reportado pela própria máquina (SMBIOS SystemFamily);
    /// por fim a heurística sobre o modelo. O campo continua editável — ao
    /// cadastrar, o valor confirmado é aprendido para as próximas máquinas.
    /// </summary>
    private void SuggestLinha(Domain.Models.MachineInfo machine)
    {
        if (!string.IsNullOrWhiteSpace(Linha)) return;
        var learned = _linhaStore.Lookup(machine.Manufacturer, machine.Model);
        var brandName = string.IsNullOrWhiteSpace(machine.Family) ? null : machine.Family!.Trim();
        var sugestao = learned ?? brandName ?? LinhaStore.Suggest(machine.Manufacturer, machine.Model);
        if (!string.IsNullOrWhiteSpace(sugestao)) Linha = sugestao!;
    }

    /// <summary>Pré-seleciona a marca pelo pedido (brand_id/nome) ou pelo fabricante lido.</summary>
    private void TrySelectMarca()
    {
        if (SelectedMarca is not null || Marcas.Count == 0) return;

        if (!string.IsNullOrWhiteSpace(SelectedPedido?.BrandId))
        {
            var byId = Marcas.FirstOrDefault(m =>
                string.Equals(m.Id, SelectedPedido!.BrandId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) { SelectedMarca = byId; return; }
        }

        var nome = SelectedPedido?.MarcaNome ?? _machine?.Manufacturer;
        if (string.IsNullOrWhiteSpace(nome)) return;
        SelectedMarca = Marcas.FirstOrDefault(m =>
            nome!.Contains(m.Nome, StringComparison.OrdinalIgnoreCase) ||
            m.Nome.Contains(nome!, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------- comandos ----

    [RelayCommand]
    private void Next()
    {
        if (IsBusy || IsDone) return;
        if (Step == CadastroStep.Pedido && !ValidatePedido()) return;
        if (Step == CadastroStep.Identificacao && !ValidateIdentificacao()) return;
        StatusMessage = "";
        if (Step != CadastroStep.Revisao)
        {
            Step = (CadastroStep)((int)Step + 1);
            RaiseReviewFields();
        }
    }

    private bool ValidatePedido()
    {
        if (SelectedPedido is null)
        {
            StatusMessage = "Selecione o pedido de compra para continuar.";
            return false;
        }
        if (PedidoSemVaga)
        {
            StatusMessage = PedidoSemVagaTexto;
            return false;
        }
        if (TemMaquinas && SelectedMaquina is null)
        {
            StatusMessage = "Selecione qual máquina do pedido está na sua mão.";
            return false;
        }
        return true;
    }

    private bool ValidateIdentificacao()
    {
        if (ModeloInvalid)
        {
            StatusMessage = "Informe o modelo (mínimo 2 caracteres) para continuar.";
            return false;
        }
        return true;
    }

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack) return;
        StatusMessage = "";
        Step = (CadastroStep)((int)Step - 1);
    }

    [RelayCommand]
    private void GoToStep(string? index)
    {
        if (IsBusy || IsDone) return;
        if (int.TryParse(index, out var i) && i >= 0 && i <= 4)
        {
            if (i > (int)CadastroStep.Pedido && !ValidatePedido())
            {
                Step = CadastroStep.Pedido;
                return;
            }
            if (i > (int)CadastroStep.Identificacao && !ValidateIdentificacao())
            {
                Step = CadastroStep.Identificacao;
                return;
            }
            StatusMessage = "";
            Step = (CadastroStep)i;
            RaiseReviewFields();
        }
    }

    private string? _idempotencyKey;

    [RelayCommand]
    private async Task CadastrarAsync()
    {
        if (IsBusy || IsDone) return;
        if (!ValidatePedido())
        {
            Step = CadastroStep.Pedido;
            return;
        }
        if (!ValidateIdentificacao())
        {
            Step = CadastroStep.Identificacao;
            return;
        }
        if (!_erp.IsConfigured) { StatusMessage = "ERP não configurado."; return; }

        // Mesma chave em retries (idempotência); nova só após sucesso.
        _idempotencyKey ??= Guid.NewGuid().ToString();

        IsBusy = true;
        StatusMessage = "Enviando para o estoque…";
        try
        {
            await AguardarHardwareAsync().ConfigureAwait(true);

            var incluidos = AcessoriosChecklist.Where(a => a.IsChecked).Select(a => a.Nome).ToList();
            incluidos.AddRange(SplitItems(AcessoriosExtras) ?? new List<string>());
            var faltantes = AcessoriosFaltando.ToList();

            var divergencias = BuildConfigDivergencias();
            var pecas = BuildPecasDivergentesCodigos();

            var req = new ErpRecebimentoRequest
            {
                PedidoCompraId = SelectedPedido!.Id,
                AssetId = NullIfEmpty(SelectedMaquina?.AssetId),
                Modelo = Modelo.Trim(),
                Linha = NullIfEmpty(Linha),
                BrandId = NullIfEmpty(SelectedMarca?.Id),
                SerialNumber = NullIfEmpty(Serial),
                CondicaoEstetica = SelectedCondicao?.Value,
                CondicaoAbaixoMinimo = TemCondicaoMinima ? CondicaoAbaixoMinimo : null,
                DefeitosAparentes = SplitItems(Defeitos),
                AcessoriosIncluidos = incluidos.Count == 0 ? null : incluidos,
                AcessoriosFaltantes = faltantes.Count == 0 ? null : faltantes,
                ConfigDivergencias = divergencias.Count == 0 ? null : divergencias,
                // false = alerta no pedido + aviso a quem o criou; nulo = sem o que comparar
                ConfigConfere = pecas.Count > 0 ? false : (TemConfigAcordada && SpecsProntas ? true : null),
                ConfigPecasDivergentes = pecas.Count == 0 ? null : pecas,
                Observacoes = NullIfEmpty(Observacoes),
                LocalizacaoInicialId = NullIfEmpty(SelectedLocalizacao?.Id),
                ProximoDestino = SelectedProximoDestino?.Value,
                Especificacoes = _specs,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

            // A máquina do pedido nasce em "aguardando recebimento" e o check de
            // entrada só reivindica quem já teve a chegada confirmada. Quem está
            // com ela na mão é este técnico, então o cadastro confirma aqui.
            if (SelectedMaquina is { AguardandoRecebimento: true } naBancada)
            {
                StatusMessage = "Confirmando o recebimento da máquina no ERP…";
                await ConfirmarChegadaAsync(naBancada, cts.Token).ConfigureAwait(true);
                StatusMessage = "Enviando para o estoque…";
            }

            var resp = await _erp.CreateRecebimentoAsync(req, _idempotencyKey, cts.Token).ConfigureAwait(true);

            NtbGerado = resp.Ntb ?? "";
            var ntbNormalizado = NtbGeradoDisplay;

            // Serial → NTB para auto-preencher no checklist futuro desta máquina.
            if (!string.IsNullOrWhiteSpace(Serial) && ntbNormalizado.Length > 0)
                _serialNtb.Save(Serial, ntbNormalizado);

            // Aprende a linha confirmada para as próximas máquinas da mesma família.
            if (_machine is not null)
                _linhaStore.Learn(_machine.Manufacturer, _machine.Model, Linha);

            // Arquivo de identidade gravado NA máquina, para o checklist principal.
            try
            {
                _identityStore.Save(new MachineIdentity
                {
                    Serial = NullIfEmpty(Serial),
                    Ntb = NullIfEmpty(ntbNormalizado),
                    AssetId = NullIfEmpty(resp.AssetId),
                    CodigoInterno = NullIfEmpty(resp.CodigoInterno),
                    Modelo = Modelo.Trim(),
                    Linha = NullIfEmpty(Linha),
                    Marca = NullIfEmpty(SelectedMarca?.Nome),
                    PedidoCompraId = NullIfEmpty(resp.PedidoCompraId) ?? SelectedPedido!.Id,
                    PedidoCompraNumero = NullIfEmpty(resp.PedidoNumero) ?? NullIfEmpty(SelectedPedido!.Numero),
                    CadastradoEmUtc = DateTime.UtcNow,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha gravando o arquivo de identidade da máquina");
            }

            ResultOk = true;
            IsDone = true;
            var codigo = string.IsNullOrWhiteSpace(resp.CodigoInterno) ? resp.AssetId : resp.CodigoInterno;
            var ntbParte = ntbNormalizado.Length > 0 ? $"NTB: {ntbNormalizado}. " : "";
            // Com alertas o ERP abre aprovação; sem alertas + produção técnica a
            // máquina segue para a bancada (Em andamento no kanban). O status
            // exato varia, então derivamos do que a resposta indicar.
            var vaiParaAprovacao = (resp.Status ?? "").Contains("aprovacao", StringComparison.OrdinalIgnoreCase);
            var destinoParte = vaiParaAprovacao
                ? "Aguardando aprovação em /aprovacoes."
                : "Seguiu para o teste — acompanhe no kanban.";
            var configParte = divergencias.Count > 0
                ? " Alerta de configuração registrado no pedido de compra — quem criou o pedido será avisado."
                : "";
            ResultText = (resp.Idempotent
                ? $"Já estava cadastrado (reenvio). {ntbParte}Código: {codigo}. {destinoParte}"
                : $"Máquina cadastrada! {ntbParte}Código: {codigo}. {destinoParte}") + configParte;
            StatusMessage = "";
            _idempotencyKey = null; // próximo cadastro gera nova chave
        }
        catch (ErpException ex)
        {
            ResultOk = false;
            // 409 já vem com mensagem de negócio pronta do ERP (ex.: pedido
            // fechado ou todas as máquinas já recebidas).
            StatusMessage = ex.StatusCode == 409
                ? ex.Message
                : $"Erro ao cadastrar: {ex.Message}";
        }
        catch (Exception ex)
        {
            ResultOk = false;
            StatusMessage = $"Erro inesperado: {ex.Message}";
            _logger.LogError(ex, "Erro no cadastro de recebimento");
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Confirma no ERP que a mercadoria chegou: a máquina sai de "aguardando
    /// recebimento" e entra no check de entrada — o único estado em que o
    /// recebimento via app consegue reivindicá-la. Mesma ação do botão
    /// "Confirmar" do kanban, feita aqui porque quem ligou a máquina na bancada
    /// já é a prova de que ela chegou. Se alguém confirmou antes (ou é reenvio),
    /// segue em frente.
    /// </summary>
    private async Task ConfirmarChegadaAsync(ErpPedidoMaquina maquina, CancellationToken ct)
    {
        try
        {
            await _erp.ConfirmarRecebimentoAsync(maquina.AssetId, ct).ConfigureAwait(true);
        }
        catch (ErpException ex) when (ex.StatusCode == 409)
        {
            _logger.LogInformation("Recebimento de {Asset} já estava confirmado: {Erro}",
                maquina.AssetId, ex.Message);
        }
        catch (ErpException ex)
        {
            _logger.LogWarning(ex, "Falha confirmando o recebimento de {Asset}", maquina.AssetId);
            throw new ErpException(
                $"Não foi possível confirmar o recebimento da máquina no ERP: {ex.Message}", ex.StatusCode);
        }
        // A máquina saiu de "aguardando recebimento": um reenvio não tenta de novo.
        maquina.EtapaKanban = "check_entrada";
    }

    [RelayCommand]
    private void NovoCadastro()
    {
        // Mantém o pedido e o destino (mesma remessa costuma repetir), zera a máquina.
        IsDone = false;
        ResultText = "";
        NtbGerado = "";
        Modelo = Linha = "";
        Serial = "";
        SerialReady = false;
        SelectedMarca = null;
        SelectedCondicao = null;
        AcessoriosExtras = Defeitos = Observacoes = "";
        foreach (var item in AcessoriosChecklist) item.IsChecked = false;
        _specs = null;
        _machine = null;
        ResetPecasDivergentes();
        ConfigChecagem.Clear();
        RaiseConfigWarnings();
        Step = CadastroStep.Pedido;
        OnPropertyChanged(nameof(SpecsResumo));
        // Recarrega as máquinas em branco: a que acabou de ser cadastrada saiu da lista.
        _ = LoadMaquinasAsync(SelectedPedido);
        _hardwareTask = CollectHardwareAsync();
    }

    [RelayCommand]
    private void Fechar() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void RaiseReviewFields()
    {
        foreach (var p in new[]
        {
            nameof(RevPedido), nameof(RevMaquina), nameof(RevModelo), nameof(RevLinha), nameof(RevMarca),
            nameof(RevSerial), nameof(RevCondicao), nameof(RevAcessorios), nameof(RevDefeitos),
            nameof(RevObservacoes), nameof(RevLocalizacao), nameof(RevProximoDestino), nameof(SpecsResumo),
            nameof(Pendencias), nameof(TemPendencias), nameof(TudoPreenchido),
            nameof(CondicaoAbaixoMinimo), nameof(CondicaoAvisoText),
            nameof(TemAcessoriosFaltando), nameof(AcessoriosAvisoText),
            nameof(ConfigDivergencias), nameof(TemConfigDivergencias), nameof(ConfigDivergenciasText),
        })
            OnPropertyChanged(p);
    }

    // ----------------------------------------------------------- helpers ----

    private static string Trimmed(string? s, string fallback) =>
        string.IsNullOrWhiteSpace(s) ? fallback : s!.Trim();

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s!.Trim();

    /// <summary>Quebra texto livre em itens (linha/; /,) limitados a 50 × 200 chars.</summary>
    private static List<string>? SplitItems(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var items = text
            .Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => s.Length > 200 ? s[..200] : s)
            .Take(50)
            .ToList();
        return items.Count == 0 ? null : items;
    }

    private static string Clip(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? "" : (s!.Length > max ? s[..max] : s);

    private static ErpEspecificacoes BuildSpecs(
        Domain.Models.MachineInfo m,
        IReadOnlyList<Domain.Models.StorageInfo> disks,
        Domain.Models.BatteryInfo? bat,
        Domain.Models.DisplayInfo? disp)
    {
        var primary = disks?.OrderByDescending(d => d.CapacityGb).FirstOrDefault();
        var totalGb = disks is null ? 0 : disks.Sum(d => (double)d.CapacityGb);
        var gpus = ErpGpuMapper.Listar(m);

        return new ErpEspecificacoes
        {
            Processador = NullIfEmptyStr(Clip(m.Processor?.Name ?? m.Cpu, 120)),
            RamGb = m.RamGb > 0 ? (int)Math.Round(m.RamGb) : (m.Memory?.TotalGb is decimal tg ? (int)Math.Round(tg) : null),
            RamTipo = NullIfEmptyStr(Clip(m.Memory?.Type, 40)),
            RamSlots = m.Memory?.SlotsTotal,
            StorageGb = totalGb > 0 ? (int)Math.Round(totalGb) : null,
            StorageTipo = MapStorageType(primary?.Type),
            StorageHealthPct = primary?.LifePercentRemaining,
            // todas as placas, dedicada primeiro — antes ia só a principal
            Gpu = NullIfEmptyStr(ErpGpuMapper.Texto(gpus)),
            Gpus = gpus.Count > 0 ? gpus : null,
            Resolucao = NullIfEmptyStr(Clip(disp?.Resolution ?? m.ScreenResolution, 40)),
            So = NullIfEmptyStr(Clip(m.Os, 80)),
            Licenca = m.WindowsActivation == AvailabilityFlag.Ativado ? "Ativado"
                      : m.WindowsActivation == AvailabilityFlag.NaoAtivado ? "Não ativado" : null,
            BateriaSaudePct = bat?.HealthPercent is decimal h ? (int)Math.Round(Math.Clamp(h, 0, 100)) : null,
            WifiOk = m.WifiAdapterCount > 0,
            BluetoothOk = m.Bluetooth is not null,
        };
    }

    private static string? NullIfEmptyStr(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

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
