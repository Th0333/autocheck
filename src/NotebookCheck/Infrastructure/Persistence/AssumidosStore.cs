using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Infrastructure.Persistence;

/// <summary>Registro de quem assumiu uma máquina no kanban.</summary>
public sealed class Assumido
{
    [JsonPropertyName("tecnico")] public string Tecnico { get; set; } = "";
    [JsonPropertyName("ntb")] public string? Ntb { get; set; }
    [JsonPropertyName("quando_utc")] public DateTime QuandoUtc { get; set; }
}

/// <summary>
/// Mapa persistente <c>asset_id → técnico</c> gravado quando alguém "assume"
/// uma máquina na janela de kanban. O checklist usa junto do arquivo de
/// identidade da máquina para preencher o nome do técnico automaticamente.
/// Arquivo JSON no diretório gravável do app (mesmo padrão dos demais stores).
/// </summary>
public sealed class AssumidosStore
{
    private readonly string _path;
    private readonly ILogger<AssumidosStore> _logger;
    private readonly object _gate = new();
    private Dictionary<string, Assumido>? _map;

    private const string LastTecnicoKey = "__ultimo_tecnico";

    public AssumidosStore(ILogger<AssumidosStore> logger, string storageDir)
    {
        _logger = logger;
        _path = Path.Combine(storageDir, "assumidos.json");
    }

    private Dictionary<string, Assumido> Map()
    {
        if (_map is not null) return _map;
        lock (_gate)
        {
            if (_map is not null) return _map;
            try
            {
                _map = File.Exists(_path)
                    ? JsonSerializer.Deserialize<Dictionary<string, Assumido>>(File.ReadAllText(_path))
                      ?? new Dictionary<string, Assumido>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, Assumido>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha lendo assumidos.json; começando vazio");
                _map = new Dictionary<string, Assumido>(StringComparer.OrdinalIgnoreCase);
            }
            return _map;
        }
    }

    /// <summary>Quem assumiu a máquina, ou null.</summary>
    public Assumido? Get(string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return null;
        var map = Map();
        lock (_gate)
        {
            return map.TryGetValue(assetId!.Trim(), out var a) && !string.IsNullOrWhiteSpace(a.Tecnico) ? a : null;
        }
    }

    /// <summary>Último nome de técnico usado (pré-preenche o prompt do kanban).</summary>
    public string? LastTecnico()
    {
        var map = Map();
        lock (_gate)
        {
            return map.TryGetValue(LastTecnicoKey, out var a) && !string.IsNullOrWhiteSpace(a.Tecnico)
                ? a.Tecnico : null;
        }
    }

    /// <summary>Registra (ou troca) o técnico responsável pela máquina e persiste.</summary>
    public void Save(string? assetId, string? tecnico, string? ntb)
    {
        if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(tecnico)) return;
        var map = Map();
        lock (_gate)
        {
            var entry = new Assumido { Tecnico = tecnico!.Trim(), Ntb = ntb, QuandoUtc = DateTime.UtcNow };
            map[assetId!.Trim()] = entry;
            map[LastTecnicoKey] = new Assumido { Tecnico = entry.Tecnico, QuandoUtc = entry.QuandoUtc };
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha gravando assumidos.json");
            }
        }
    }

    /// <summary>
    /// Esquece o técnico de uma máquina — usado quando ela retrocede para a fila
    /// (aguardando_tecnico) ou o check de entrada, onde o ERP limpa o
    /// <c>assumido_por</c>; sem isso o cache local ressuscitaria o nome antigo.
    /// </summary>
    public void Forget(string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return;
        var map = Map();
        lock (_gate)
        {
            if (!map.Remove(assetId!.Trim())) return;
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha gravando assumidos.json");
            }
        }
    }
}
