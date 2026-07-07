using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotebookCheck.Infrastructure.Erp;
using NotebookCheck.Infrastructure.Persistence;

namespace NotebookCheck.Presentation;

/// <summary>Card do kanban: uma máquina de um pedido de compra.</summary>
public sealed partial class KanbanCard : ObservableObject
{
    public ErpPedidoMaquina Maquina { get; }
    public string PedidoId { get; }
    public string PedidoNumero { get; }

    /// <summary>Nome do técnico que assumiu a máquina ("" = ninguém).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemAssumido), nameof(AssumidoLabel), nameof(MostraOk))]
    private string assumidoPor = "";

    public KanbanCard(ErpPedidoMaquina maquina, string pedidoId, string pedidoNumero, string? assumidoPor)
    {
        Maquina = maquina;
        PedidoId = pedidoId;
        PedidoNumero = pedidoNumero;
        this.assumidoPor = assumidoPor ?? "";
    }

    /// <summary>Etapa normalizada (sem acento/caixa/separador) — casa com as colunas fixas.</summary>
    public string Etapa => KanbanViewModel.NormalizeEtapa(Maquina.EtapaKanban);

    /// <summary>Etapa crua vinda do ERP — enviada no avancar (o servidor compara exato).</summary>
    public string RawEtapa => Maquina.EtapaKanban ?? "";

    public string NtbDisplay => !string.IsNullOrWhiteSpace(Maquina.Ntb)
        ? Domain.Rules.NtbCode.Normalize(Maquina.Ntb)
        : (Maquina.CodigoInterno ?? "—");

    public string ModeloDisplay
    {
        get
        {
            var nome = string.Join(" ", new[] { Maquina.Linha, Maquina.Modelo }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));
            return nome.Length == 0 ? "Modelo a definir" : nome;
        }
    }

    public string PedidoLabel => $"Pedido {PedidoNumero}";
    public bool TemAssumido => !string.IsNullOrWhiteSpace(AssumidoPor);
    public string AssumidoLabel => TemAssumido ? $"👤 {AssumidoPor}" : "";

    /// <summary>Check de entrada tem só "Assumir" — assumir avança a etapa.</summary>
    public bool MostraAssumir => Etapa == "check_entrada";

    /// <summary>
    /// Botão "OK" (rótulo) — só onde o avanço é uma AÇÃO especial que a seta não
    /// cobre: abrir o cadastro em "aguardando técnico" (e o plano B no check de
    /// entrada já assumido). Avanços de etapa "puros" (em andamento →
    /// componente → aprovação) ficam na seta ▶.
    /// </summary>
    public bool MostraOk => Etapa == "aguardando_tecnico"
        || (Etapa == "check_entrada" && TemAssumido);

    public string OkToolTip => "Iniciar o cadastro desta máquina";

    // --------------------------------------------- setas / remover (NTB row) ---

    /// <summary>▶ avança a etapa (só os avanços "puros" via /kanban/avancar).</summary>
    public bool PodeAvancarSeta => Etapa is "em_andamento" or "aguardando_componente";

    /// <summary>◀ retrocede a etapa (endpoint proposto); não há etapa antes do check de entrada.</summary>
    public bool PodeRetroceder => Etapa is "aguardando_tecnico" or "em_andamento"
        or "aguardando_componente" or "aguardando_aprovacao" or "concluido";

    public string AvancarSetaToolTip => Etapa switch
    {
        "em_andamento" => "Avançar para 'aguardando componente'",
        "aguardando_componente" => "Avançar para 'aguardando aprovação'",
        "check_entrada" => "Use 'Assumir' para avançar",
        "aguardando_tecnico" => "Use 'OK' (cadastro) para avançar",
        "aguardando_aprovacao" => "A aprovação é decidida no painel /aprovacoes",
        _ => "Sem avanço disponível",
    };

    public string RetrocederToolTip => PodeRetroceder
        ? "Voltar uma etapa"
        : "Já é a primeira etapa";
}

/// <summary>Coluna do kanban (uma etapa).</summary>
public sealed class KanbanColuna
{
    public string Titulo { get; }
    public string Aviso { get; }
    public ObservableCollection<KanbanCard> Maquinas { get; } = new();

    public KanbanColuna(string titulo, string aviso = "")
    {
        Titulo = titulo;
        Aviso = aviso;
    }

    public string ContagemLabel => Maquinas.Count == 1 ? "1 máquina" : $"{Maquinas.Count} máquinas";
    public bool TemAviso => Aviso.Length > 0 && Maquinas.Count > 0;
}

/// <summary>
/// ViewModel da janela de kanban: máquinas de todos os pedidos de compra
/// abertos nas 6 etapas fixas do fluxo. Ações por etapa: check de entrada tem
/// "Assumir" (registra o técnico e avança); aguardando técnico tem "OK" que
/// abre o cadastro; em andamento/componente/aprovação têm "OK" que avança a
/// etapa no ERP (endpoint proposto — ver docs/relatorio-api-kanban.md).
/// </summary>
public sealed partial class KanbanViewModel : ObservableObject
{
    private readonly ErpClient _erp;
    private readonly AssumidosStore _assumidos;
    private readonly KanbanHiddenStore _hidden;
    private readonly ILogger<KanbanViewModel> _logger;

    /// <summary>Pergunta o nome do técnico (janela define). Recebe o default, devolve o nome ou null.</summary>
    public Func<string?, string?>? TecnicoPrompt { get; set; }

    /// <summary>Pede à janela para abrir o cadastro pré-selecionando (pedidoId, assetId).</summary>
    public event Action<string, string>? CadastroRequested;

    public ObservableCollection<KanbanColuna> Colunas { get; } = new();

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusMessage = "";

    public KanbanViewModel(ErpClient erp, AssumidosStore assumidos,
        KanbanHiddenStore hidden, ILogger<KanbanViewModel> logger)
    {
        _erp = erp;
        _assumidos = assumidos;
        _hidden = hidden;
        _logger = logger;
    }

    // Cartões ocultados localmente ("remover do kanban").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemOcultos), nameof(OcultosLabel))]
    private int ocultosCount;

    public bool TemOcultos => OcultosCount > 0;
    public string OcultosLabel => OcultosCount == 1 ? "1 cartão oculto" : $"{OcultosCount} cartões ocultos";

    /// <summary>As 6 etapas do fluxo, sempre exibidas (mesmo vazias), nesta ordem.</summary>
    private static readonly (string Etapa, string Titulo, string Aviso)[] EtapasFixas =
    {
        ("check_entrada", "Check de entrada", ""),
        ("aguardando_tecnico", "Aguardando técnico", ""),
        ("em_andamento", "Em andamento", ""),
        ("aguardando_componente", "Aguardando componente", "Se não for necessário, avance na seta ▶."),
        ("aguardando_aprovacao", "Aguardando aprovação", "A aprovação é decidida no painel /aprovacoes — o cartão avança sozinho."),
        ("concluido", "Concluído", ""),
    };

    private static string TituloDaEtapa(string etapa)
    {
        if (etapa.Length == 0) return "Sem etapa";
        var t = etapa.Replace('_', ' ');
        return char.ToUpperInvariant(t[0]) + t[1..];
    }

    /// <summary>
    /// Normaliza a etapa vinda do ERP para casar com as colunas fixas mesmo que
    /// o texto varie: minúsculas, sem acentos, e qualquer separador (espaço,
    /// hífen) vira "_". Assim "Aguardando Técnico", "aguardando-tecnico" e
    /// "aguardando_tecnico" caem todos na mesma coluna, em vez de a máquina
    /// ir para uma coluna "desconhecida" fora da vista.
    /// </summary>
    public static string NormalizeEtapa(string? etapa)
    {
        if (string.IsNullOrWhiteSpace(etapa)) return "";
        var decomposed = etapa.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        var s = sb.ToString();
        while (s.Contains("__")) s = s.Replace("__", "_");
        return s.Trim('_');
    }

    public async Task InitializeAsync() => await RefreshAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        if (!_erp.IsConfigured)
        {
            StatusMessage = "Integração com o ERP não configurada.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Carregando pedidos e máquinas…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var pedidos = await _erp.GetPedidosCompraAsync(null, cts.Token).ConfigureAwait(true);

            // O ERP mantém o pedido na lista enquanto ele tiver máquina em
            // QUALQUER etapa do kanban (sai só quando conclui/cancela), então
            // basta percorrer a lista — máquinas em etapas seguintes vêm junto.
            var cards = new List<KanbanCard>();
            var pedidosComFalha = new List<string>();
            foreach (var pedido in pedidos)
            {
                try
                {
                    var resp = await _erp.GetPedidoMaquinasAsync(pedido.Id, cts.Token).ConfigureAwait(true);
                    foreach (var m in resp.Maquinas)
                    {
                        // "Removida do kanban" (ocultada localmente): não mostra.
                        if (_hidden.IsHidden(m.AssetId)) continue;
                        // Servidor é a fonte da verdade sobre quem assumiu; o
                        // registro local só entra quando o ERP ainda não trouxe.
                        var assumido = string.IsNullOrWhiteSpace(m.AssumidoPor)
                            ? _assumidos.Get(m.AssetId)?.Tecnico
                            : m.AssumidoPor;
                        cards.Add(new KanbanCard(m, pedido.Id, resp.Numero ?? pedido.Numero, assumido));
                    }
                }
                catch (ErpException ex)
                {
                    // Não engole o erro: sem isso, as máquinas do pedido
                    // sumiriam do quadro sem explicação.
                    pedidosComFalha.Add(pedido.Numero);
                    _logger.LogWarning(ex, "Falha listando máquinas do pedido {Pedido}", pedido.Numero);
                }
            }

            Colunas.Clear();
            var grupos = cards.GroupBy(c => c.Etapa).ToDictionary(g => g.Key, g => g.ToList());

            // As 6 etapas fixas sempre aparecem, mesmo sem máquinas.
            foreach (var (etapa, titulo, aviso) in EtapasFixas)
            {
                var col = new KanbanColuna(titulo, aviso);
                if (grupos.TryGetValue(etapa, out var doGrupo))
                    foreach (var card in doGrupo.OrderBy(c => c.NtbDisplay)) col.Maquinas.Add(card);
                Colunas.Add(col);
            }
            // Etapas desconhecidas (novas/diferentes no ERP) entram no fim, mas
            // NÃO some nada silenciosamente: elas viram coluna visível e o
            // rodapé avisa quantas máquinas caíram fora do fluxo esperado.
            var desconhecidas = grupos
                .Where(x => EtapasFixas.All(e => e.Etapa != x.Key))
                .OrderBy(x => x.Key)
                .ToList();
            foreach (var g in desconhecidas)
            {
                var col = new KanbanColuna(TituloDaEtapa(g.Key));
                foreach (var card in g.Value.OrderBy(c => c.NtbDisplay)) col.Maquinas.Add(card);
                Colunas.Add(col);
            }

            var avisos = new List<string>();
            if (pedidosComFalha.Count > 0)
                avisos.Add($"⚠ Falha ao carregar {pedidosComFalha.Count} pedido(s) ({string.Join(", ", pedidosComFalha)}) — máquinas podem estar faltando; tente Atualizar.");
            var foraDoFluxo = desconhecidas.Sum(g => g.Value.Count);
            if (foraDoFluxo > 0)
                avisos.Add($"⚠ {foraDoFluxo} máquina(s) em etapa não prevista (coluna à direita): {string.Join(", ", desconhecidas.Select(d => d.Key))}.");

            OcultosCount = _hidden.Count;

            StatusMessage = avisos.Count > 0
                ? string.Join(" ", avisos)
                : cards.Count == 0
                    ? (TemOcultos
                        ? "Nenhuma máquina visível (há cartões ocultos)."
                        : "Nenhuma máquina em pedidos de compra abertos.")
                    : "";
        }
        catch (ErpException ex)
        {
            StatusMessage = ex.StatusCode == 404
                ? "O ERP ainda não expõe o kanban de pedidos (endpoint pendente)."
                : $"Erro carregando o kanban: {ex.Message}";
            _logger.LogWarning(ex, "Falha carregando kanban");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro inesperado: {ex.Message}";
            _logger.LogError(ex, "Erro inesperado no kanban");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AssumirAsync(KanbanCard? card)
    {
        if (card is null || IsBusy || TecnicoPrompt is null) return;
        var nome = TecnicoPrompt(string.IsNullOrWhiteSpace(card.AssumidoPor)
            ? _assumidos.LastTecnico() : card.AssumidoPor);
        if (string.IsNullOrWhiteSpace(nome)) return;

        // Registro local: segue a máquina pelo asset_id e preenche o checklist.
        _assumidos.Save(card.Maquina.AssetId, nome, card.Maquina.Ntb);
        card.AssumidoPor = nome!.Trim();

        // Sincroniza com o ERP: assumir avança check_entrada → aguardando_tecnico.
        var avancou = await TryAvancarAsync(card, card.AssumidoPor).ConfigureAwait(true);
        if (avancou)
            StatusMessage = $"{card.NtbDisplay} assumida por {card.AssumidoPor} — movida para Aguardando técnico.";
    }

    [RelayCommand]
    private void Ok(KanbanCard? card)
    {
        if (card is null || IsBusy) return;

        // OK só abre o cadastro (aguardando técnico, ou plano B no check de
        // entrada já assumido); avanços "puros" de etapa ficam na seta ▶.
        if (card.Etapa is "aguardando_tecnico" or "check_entrada")
            CadastroRequested?.Invoke(card.PedidoId, card.Maquina.AssetId);
        else
            StatusMessage = "Esta máquina não tem ação disponível nesta etapa.";
    }

    /// <summary>▶ — avança a etapa (avanços puros via /kanban/avancar).</summary>
    [RelayCommand]
    private async Task AvancarSetaAsync(KanbanCard? card)
    {
        if (card is null || IsBusy) return;
        if (!card.PodeAvancarSeta)
        {
            StatusMessage = card.AvancarSetaToolTip;
            return;
        }
        var avancou = await TryAvancarAsync(card, null).ConfigureAwait(true);
        if (avancou) StatusMessage = $"{card.NtbDisplay} avançou de etapa.";
    }

    /// <summary>◀ — retrocede a etapa (endpoint proposto; 404 até o ERP implementar).</summary>
    [RelayCommand]
    private async Task RetrocederAsync(KanbanCard? card)
    {
        if (card is null || IsBusy) return;
        if (!card.PodeRetroceder)
        {
            StatusMessage = card.RetrocederToolTip;
            return;
        }

        IsBusy = true;
        StatusMessage = $"Retrocedendo {card.NtbDisplay}…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await _erp.RetrocederKanbanAsync(new ErpKanbanRetrocederRequest
            {
                AssetId = card.Maquina.AssetId,
                EtapaAtual = string.IsNullOrWhiteSpace(card.RawEtapa) ? card.Etapa : card.RawEtapa,
            }, Guid.NewGuid().ToString(), cts.Token).ConfigureAwait(true);
        }
        catch (ErpException ex)
        {
            StatusMessage = ex.StatusCode == 404
                ? "O ERP ainda não aceita retroceder etapa (endpoint pendente)."
                : $"Erro ao retroceder etapa: {ex.Message}";
            _logger.LogWarning(ex, "Falha retrocedendo etapa de {Asset}", card.Maquina.AssetId);
            IsBusy = false;
            return;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro inesperado: {ex.Message}";
            _logger.LogError(ex, "Erro inesperado retrocedendo etapa");
            IsBusy = false;
            return;
        }
        IsBusy = false;
        await RefreshAsync().ConfigureAwait(true);
        StatusMessage = $"{card.NtbDisplay} voltou uma etapa.";
    }

    /// <summary>✕ — remove o cartão do kanban (ocultamento LOCAL neste dispositivo).</summary>
    [RelayCommand]
    private void Remover(KanbanCard? card)
    {
        if (card is null) return;
        if (RemoverConfirm is not null && !RemoverConfirm(card.NtbDisplay)) return;

        _hidden.Hide(card.Maquina.AssetId);
        foreach (var col in Colunas) col.Maquinas.Remove(card);
        OcultosCount = _hidden.Count;
        StatusMessage = $"{card.NtbDisplay} removida do kanban (só neste computador). Use 'Mostrar ocultos' para trazer de volta.";
    }

    /// <summary>Traz de volta todos os cartões ocultados localmente.</summary>
    [RelayCommand]
    private async Task MostrarOcultosAsync()
    {
        // Se houver refresh/avancar/retroceder em andamento, o RefreshAsync
        // abaixo cairia fora (guard de IsBusy) e os cartões ficariam limpos do
        // store mas ainda invisíveis. Espera a operação atual terminar.
        if (IsBusy) return;
        _hidden.ClearAll();
        OcultosCount = 0;
        await RefreshAsync().ConfigureAwait(true);
        StatusMessage = "Cartões ocultos restaurados.";
    }

    /// <summary>Confirmação de remoção (a janela define). Recebe o NTB, devolve true p/ prosseguir.</summary>
    public Func<string, bool>? RemoverConfirm { get; set; }

    /// <summary>
    /// Chama o POST /kanban/avancar e atualiza o quadro. 404 = endpoint ainda
    /// não implementado no ERP — avisa sem travar o fluxo. Devolve true se avançou.
    /// </summary>
    private async Task<bool> TryAvancarAsync(KanbanCard card, string? tecnico)
    {
        IsBusy = true;
        StatusMessage = $"Avançando {card.NtbDisplay}…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await _erp.AvancarKanbanAsync(new ErpKanbanAvancarRequest
            {
                AssetId = card.Maquina.AssetId,
                // Etapa crua (não normalizada): o ERP valida etapa_atual por
                // igualdade exata contra o valor que ele mesmo gravou.
                EtapaAtual = string.IsNullOrWhiteSpace(card.RawEtapa) ? card.Etapa : card.RawEtapa,
                Tecnico = tecnico,
            }, Guid.NewGuid().ToString(), cts.Token).ConfigureAwait(true);
        }
        catch (ErpException ex)
        {
            StatusMessage = ex.StatusCode == 404
                ? "O ERP ainda não aceita avanço de etapa (endpoint pendente) — registro mantido só no app."
                : $"Erro ao avançar etapa: {ex.Message}";
            _logger.LogWarning(ex, "Falha avançando etapa de {Asset}", card.Maquina.AssetId);
            IsBusy = false;
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro inesperado: {ex.Message}";
            _logger.LogError(ex, "Erro inesperado avançando etapa");
            IsBusy = false;
            return false;
        }
        IsBusy = false;
        await RefreshAsync().ConfigureAwait(true);
        return true;
    }
}
