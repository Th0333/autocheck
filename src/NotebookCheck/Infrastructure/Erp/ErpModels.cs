using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NotebookCheck.Bootstrap;

namespace NotebookCheck.Infrastructure.Erp;

/// <summary>Configuração resolvida da integração com o ERP.</summary>
public sealed record ErpConfig(
    string BaseUrl,
    string SupabaseUrl,
    string SupabaseAnonKey,
    string Email,
    string Password)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(SupabaseUrl) &&
        !string.IsNullOrWhiteSpace(SupabaseAnonKey) &&
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(Password);

    /// <summary>
    /// Carrega de <c>erp-config.json</c> no diretório informado, caindo de volta
    /// para as constantes embutidas em <see cref="ErpDefaults"/> campo a campo.
    /// </summary>
    public static ErpConfig Load(string storageDir)
    {
        var cfg = new ErpConfig(
            ErpDefaults.BaseUrl,
            ErpDefaults.SupabaseUrl,
            ErpDefaults.SupabaseAnonKey,
            ErpDefaults.ServiceEmail,
            ErpDefaults.ServicePassword);

        try
        {
            var path = Path.Combine(storageDir, "erp-config.json");
            if (!File.Exists(path)) return cfg;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string Pick(string key, string fallback) =>
                root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? (v.GetString() ?? fallback) : fallback;
            cfg = new ErpConfig(
                Pick("baseUrl", cfg.BaseUrl).TrimEnd('/'),
                Pick("supabaseUrl", cfg.SupabaseUrl).TrimEnd('/'),
                Pick("supabaseAnonKey", cfg.SupabaseAnonKey),
                Pick("email", cfg.Email),
                Pick("password", cfg.Password));
        }
        catch { /* arquivo inválido: usa os defaults */ }

        return cfg with { BaseUrl = cfg.BaseUrl.TrimEnd('/'), SupabaseUrl = cfg.SupabaseUrl.TrimEnd('/') };
    }
}

/// <summary>Erro de negócio/HTTP da API do ERP, com mensagem amigável.</summary>
public sealed class ErpException : Exception
{
    public int? StatusCode { get; }
    public ErpException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;
}

/// <summary>Item de referência (marca, fornecedor ou localização) dos endpoints GET.</summary>
public sealed class ErpRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("nome")] public string Nome { get; set; } = "";
    [JsonPropertyName("documento")] public string? Documento { get; set; }
    [JsonPropertyName("caminho")] public string? Caminho { get; set; }
    [JsonPropertyName("is_default")] public bool IsDefault { get; set; }

    /// <summary>Texto exibido no dropdown.</summary>
    [JsonIgnore]
    public string Display
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Caminho)) return Caminho!;
            if (!string.IsNullOrWhiteSpace(Documento)) return $"{Nome} · {Documento}";
            return Nome;
        }
    }

    public override string ToString() => Display;
}

internal sealed class ErpListResponse
{
    [JsonPropertyName("items")] public List<ErpRef> Items { get; set; } = new();
}

/// <summary>
/// Pedido de compra aberto no ERP. O cadastro de máquinas agora parte sempre de
/// um pedido: ele carrega fornecedor, documento, requisitos de condição estética
/// mínima e a lista de acessórios obrigatórios que devem acompanhar cada máquina.
/// </summary>
public sealed class ErpPedidoCompra
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("numero")] public string Numero { get; set; } = "";
    [JsonPropertyName("fornecedor_id")] public string? FornecedorId { get; set; }
    [JsonPropertyName("fornecedor_nome")] public string? FornecedorNome { get; set; }
    [JsonPropertyName("documento_entrada")] public string? DocumentoEntrada { get; set; }
    [JsonPropertyName("data_pedido")] public string? DataPedido { get; set; }
    [JsonPropertyName("modelo_previsto")] public string? ModeloPrevisto { get; set; }
    [JsonPropertyName("brand_id")] public string? BrandId { get; set; }
    [JsonPropertyName("marca_nome")] public string? MarcaNome { get; set; }
    [JsonPropertyName("condicao_minima")] public string? CondicaoMinima { get; set; }
    [JsonPropertyName("acessorios_obrigatorios")] public List<string> AcessoriosObrigatorios { get; set; } = new();
    [JsonPropertyName("quantidade_total")] public int? QuantidadeTotal { get; set; }
    [JsonPropertyName("quantidade_recebida")] public int? QuantidadeRecebida { get; set; }
    [JsonPropertyName("observacoes")] public string? Observacoes { get; set; }

    /// <summary>Texto exibido no dropdown de pedidos.</summary>
    [JsonIgnore]
    public string Display
    {
        get
        {
            var parts = new List<string> { Numero };
            if (!string.IsNullOrWhiteSpace(FornecedorNome)) parts.Add(FornecedorNome!);
            if (QuantidadeTotal is int total)
                parts.Add($"{QuantidadeRecebida ?? 0}/{total} recebidas");
            return string.Join(" · ", parts);
        }
    }

    public override string ToString() => Display;
}

internal sealed class ErpPedidoListResponse
{
    [JsonPropertyName("items")] public List<ErpPedidoCompra> Items { get; set; } = new();
}

/// <summary>Configuração acordada no pedido para uma máquina (comparada com o autocheck).</summary>
public sealed class ErpConfigAcordada
{
    [JsonPropertyName("processador")] public string? Processador { get; set; }
    [JsonPropertyName("ram_gb")] public int? RamGb { get; set; }
    [JsonPropertyName("storage_gb")] public int? StorageGb { get; set; }
}

/// <summary>
/// Máquina de um pedido de compra com a etapa do kanban. As "em branco"
/// (<c>pode_check_entrada</c>) são reivindicadas pelo cadastro via
/// <c>asset_id</c>; as na fila/execução (<c>pode_check_automatico</c>) são
/// elegíveis ao POST /autocheck.
/// </summary>
public sealed class ErpPedidoMaquina
{
    [JsonPropertyName("asset_id")] public string AssetId { get; set; } = "";
    [JsonPropertyName("ntb")] public string? Ntb { get; set; }
    [JsonPropertyName("codigo_interno")] public string? CodigoInterno { get; set; }
    [JsonPropertyName("modelo")] public string? Modelo { get; set; }
    [JsonPropertyName("linha")] public string? Linha { get; set; }
    [JsonPropertyName("serial_number")] public string? SerialNumber { get; set; }
    [JsonPropertyName("status_operacional")] public string? StatusOperacional { get; set; }
    [JsonPropertyName("etapa_kanban")] public string? EtapaKanban { get; set; }
    [JsonPropertyName("config_acordada")] public ErpConfigAcordada? ConfigAcordada { get; set; }
    [JsonPropertyName("pode_check_entrada")] public bool PodeCheckEntrada { get; set; }
    [JsonPropertyName("pode_check_automatico")] public bool PodeCheckAutomatico { get; set; }
    /// <summary>Técnico que assumiu a máquina (do Assumir no app ou da web). Fonte da verdade sobre o local.</summary>
    [JsonPropertyName("assumido_por")] public string? AssumidoPor { get; set; }

    /// <summary>Texto exibido em dropdowns/cards ("NTB 11801 · Latitude 5420").</summary>
    [JsonIgnore]
    public string Display
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Ntb)) parts.Add($"NTB {Ntb}");
            else if (!string.IsNullOrWhiteSpace(CodigoInterno)) parts.Add(CodigoInterno!);
            var nome = string.Join(" ", new[] { Linha, Modelo }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (nome.Length > 0) parts.Add(nome);
            return parts.Count == 0 ? AssetId : string.Join(" · ", parts);
        }
    }

    public override string ToString() => Display;
}

/// <summary>Resposta do GET /api/integracao/pedidos-compra/{id}/maquinas.</summary>
public sealed class ErpPedidoMaquinasResponse
{
    [JsonPropertyName("pedido_compra_id")] public string? PedidoCompraId { get; set; }
    [JsonPropertyName("numero")] public string? Numero { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("maquinas")] public List<ErpPedidoMaquina> Maquinas { get; set; } = new();
}

/// <summary>
/// Corpo do POST /api/integracao/kanban/avancar (PROPOSTO — ver
/// docs/relatorio-api-kanban.md): move a máquina para a próxima etapa do
/// kanban. Usado pelo "Assumir" (check_entrada → aguardando_tecnico, com o
/// nome do técnico) e pelo "OK" nas etapas em_andamento/componente/aprovação.
/// </summary>
public sealed class ErpKanbanAvancarRequest
{
    [JsonPropertyName("asset_id")] public string AssetId { get; set; } = "";
    [JsonPropertyName("etapa_atual")] public string EtapaAtual { get; set; } = "";
    [JsonPropertyName("tecnico")] public string? Tecnico { get; set; }
}

/// <summary>Resposta do POST /api/integracao/kanban/avancar.</summary>
public sealed class ErpKanbanAvancarResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("etapa_anterior")] public string? EtapaAnterior { get; set; }
    [JsonPropertyName("etapa_nova")] public string? EtapaNova { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>
/// Corpo do POST /api/integracao/kanban/retroceder: volta a máquina UMA etapa
/// no kanban (aguardando_tecnico→check_entrada, em_andamento→aguardando_tecnico,
/// aguardando_componente→em_andamento, aguardando_aprovacao→em_andamento).
/// </summary>
public sealed class ErpKanbanRetrocederRequest
{
    [JsonPropertyName("asset_id")] public string AssetId { get; set; } = "";
    [JsonPropertyName("etapa_atual")] public string EtapaAtual { get; set; } = "";
}

/// <summary>Resposta do POST /api/integracao/kanban/retroceder.</summary>
public sealed class ErpKanbanRetrocederResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("etapa_anterior")] public string? EtapaAnterior { get; set; }
    [JsonPropertyName("etapa_nova")] public string? EtapaNova { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>Corpo do POST /api/integracao/autocheck (conclui a ordem de diagnóstico).</summary>
public sealed class ErpAutocheckRequest
{
    [JsonPropertyName("asset_id")] public string AssetId { get; set; } = "";
    [JsonPropertyName("resultado")] public string Resultado { get; set; } = "";
    [JsonPropertyName("observacoes")] public string? Observacoes { get; set; }
    [JsonPropertyName("especificacoes")] public ErpEspecificacoes? Especificacoes { get; set; }
}

/// <summary>Resposta do POST /api/integracao/autocheck.</summary>
public sealed class ErpAutocheckResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("idempotent")] public bool Idempotent { get; set; }
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("service_order_id")] public string? ServiceOrderId { get; set; }
    [JsonPropertyName("approval_id")] public string? ApprovalId { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>Bloco opcional de especificações técnicas coletado pelo app.</summary>
public sealed class ErpEspecificacoes
{
    [JsonPropertyName("processador")] public string? Processador { get; set; }
    [JsonPropertyName("geracao")] public string? Geracao { get; set; }
    [JsonPropertyName("ram_gb")] public int? RamGb { get; set; }
    [JsonPropertyName("ram_tipo")] public string? RamTipo { get; set; }
    [JsonPropertyName("ram_slots")] public int? RamSlots { get; set; }
    [JsonPropertyName("storage_gb")] public int? StorageGb { get; set; }
    [JsonPropertyName("storage_tipo")] public string? StorageTipo { get; set; }
    [JsonPropertyName("storage_health_pct")] public int? StorageHealthPct { get; set; }
    [JsonPropertyName("gpu")] public string? Gpu { get; set; }
    [JsonPropertyName("tela_polegadas")] public double? TelaPolegadas { get; set; }
    [JsonPropertyName("resolucao")] public string? Resolucao { get; set; }
    [JsonPropertyName("so")] public string? So { get; set; }
    [JsonPropertyName("licenca")] public string? Licenca { get; set; }
    [JsonPropertyName("bateria_saude_pct")] public int? BateriaSaudePct { get; set; }
    [JsonPropertyName("webcam_ok")] public bool? WebcamOk { get; set; }
    [JsonPropertyName("wifi_ok")] public bool? WifiOk { get; set; }
    [JsonPropertyName("bluetooth_ok")] public bool? BluetoothOk { get; set; }
}

/// <summary>
/// Corpo do POST /api/integracao/recebimentos (v2 — baseado em pedido de compra).
/// Fornecedor, documento e valores vêm do pedido no servidor. O NTB já nasce com
/// a máquina em branco criada pelo pedido (sequência natural da organização,
/// string de dígitos de comprimento variável) e é devolvido na resposta.
/// </summary>
public sealed class ErpRecebimentoRequest
{
    [JsonPropertyName("pedido_compra_id")] public string? PedidoCompraId { get; set; }
    /// <summary>Reivindica ESTA máquina em branco do pedido; sem ele o servidor pega a próxima.</summary>
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("modelo")] public string Modelo { get; set; } = "";
    [JsonPropertyName("linha")] public string? Linha { get; set; }
    [JsonPropertyName("brand_id")] public string? BrandId { get; set; }
    [JsonPropertyName("serial_number")] public string? SerialNumber { get; set; }
    [JsonPropertyName("condicao_estetica")] public string? CondicaoEstetica { get; set; }
    [JsonPropertyName("condicao_abaixo_minimo")] public bool? CondicaoAbaixoMinimo { get; set; }
    [JsonPropertyName("defeitos_aparentes")] public List<string>? DefeitosAparentes { get; set; }
    [JsonPropertyName("acessorios_incluidos")] public List<string>? AcessoriosIncluidos { get; set; }
    [JsonPropertyName("acessorios_faltantes")] public List<string>? AcessoriosFaltantes { get; set; }
    /// <summary>Diferenças entre a config acordada no pedido e o que o autocheck encontrou.</summary>
    [JsonPropertyName("config_divergencias")] public List<string>? ConfigDivergencias { get; set; }
    [JsonPropertyName("observacoes")] public string? Observacoes { get; set; }
    [JsonPropertyName("localizacao_inicial_id")] public string? LocalizacaoInicialId { get; set; }
    [JsonPropertyName("proximo_destino")] public string? ProximoDestino { get; set; }
    [JsonPropertyName("especificacoes")] public ErpEspecificacoes? Especificacoes { get; set; }
}

/// <summary>Resposta de sucesso do recebimento (v2). <c>ntb</c> vem só com
/// dígitos e comprimento variável; o app normaliza para "NTB…" na exibição.</summary>
public sealed class ErpRecebimentoResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("idempotent")] public bool Idempotent { get; set; }
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("codigo_interno")] public string? CodigoInterno { get; set; }
    [JsonPropertyName("ntb")] public string? Ntb { get; set; }
    [JsonPropertyName("approval_id")] public string? ApprovalId { get; set; }
    [JsonPropertyName("proximo_destino")] public string? ProximoDestino { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("pedido_compra_id")] public string? PedidoCompraId { get; set; }
    [JsonPropertyName("pedido_numero")] public string? PedidoNumero { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>Corpo do POST /api/integracao/fornecedores.</summary>
public sealed class ErpFornecedorRequest
{
    [JsonPropertyName("nome")] public string Nome { get; set; } = "";
    [JsonPropertyName("documento")] public string? Documento { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("telefone")] public string? Telefone { get; set; }
}

/// <summary>Resposta do cadastro de fornecedor.</summary>
public sealed class ErpFornecedorResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("nome")] public string? Nome { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

internal sealed class SupabaseTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
}
