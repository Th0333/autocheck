using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Infrastructure.Persistence;

/// <summary>
/// Dados principais da máquina gravados no cadastro de estoque, usados depois
/// pelo checklist principal (NTB, serial, asset_id para atualizar status no ERP).
/// </summary>
public sealed class MachineIdentity
{
    [JsonPropertyName("serial")] public string? Serial { get; set; }
    [JsonPropertyName("ntb")] public string? Ntb { get; set; }
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("codigo_interno")] public string? CodigoInterno { get; set; }
    [JsonPropertyName("modelo")] public string? Modelo { get; set; }
    [JsonPropertyName("linha")] public string? Linha { get; set; }
    [JsonPropertyName("marca")] public string? Marca { get; set; }
    [JsonPropertyName("pedido_compra_id")] public string? PedidoCompraId { get; set; }
    [JsonPropertyName("pedido_compra_numero")] public string? PedidoCompraNumero { get; set; }
    [JsonPropertyName("cadastrado_em_utc")] public DateTime CadastradoEmUtc { get; set; }
}

/// <summary>
/// Persiste a identidade da máquina cadastrada em um arquivo NA PRÓPRIA máquina
/// (<c>C:\ProgramData\Notelet\machine-identity.json</c>), porque o app costuma
/// rodar de um pendrive: o diretório gravável do app viaja com o técnico, mas o
/// arquivo de identidade precisa ficar no computador que foi cadastrado. Se
/// ProgramData não for gravável, cai para o diretório do app como plano B.
/// O checklist principal lê este arquivo para preencher o NTB automaticamente
/// e (futuramente) atualizar o status da máquina no ERP via <c>asset_id</c>.
/// </summary>
public sealed class MachineIdentityStore
{
    private readonly ILogger<MachineIdentityStore> _logger;
    private readonly string _fallbackPath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MachineIdentityStore(ILogger<MachineIdentityStore> logger, string storageDir)
    {
        _logger = logger;
        _fallbackPath = Path.Combine(storageDir, "machine-identity.json");
    }

    private static string MachinePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Notelet", "machine-identity.json");

    /// <summary>Grava a identidade na máquina; devolve o caminho efetivo do arquivo.</summary>
    public string Save(MachineIdentity identity)
    {
        var json = JsonSerializer.Serialize(identity, JsonOpts);
        try
        {
            var path = MachinePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha gravando identidade em ProgramData; usando diretório do app");
            File.WriteAllText(_fallbackPath, json);
            return _fallbackPath;
        }
    }

    /// <summary>
    /// Lê a identidade gravada nesta máquina (ProgramData primeiro, depois o
    /// diretório do app). Null se não existir ou estiver corrompida.
    /// </summary>
    public MachineIdentity? TryRead()
    {
        foreach (var path in new[] { MachinePath, _fallbackPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var identity = JsonSerializer.Deserialize<MachineIdentity>(File.ReadAllText(path), JsonOpts);
                if (identity is not null) return identity;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha lendo identidade em {Path}", path);
            }
        }
        return null;
    }
}
