using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Infrastructure.Persistence;

/// <summary>
/// Conjunto persistente de máquinas (asset_id) que o técnico "removeu do kanban"
/// no app. É um ocultamento LOCAL (o ERP não tem endpoint de remoção) — some do
/// quadro deste dispositivo, mas continua existindo no ERP. Arquivo JSON no
/// diretório gravável do app, no padrão dos demais stores.
/// </summary>
public sealed class KanbanHiddenStore
{
    private readonly string _path;
    private readonly ILogger<KanbanHiddenStore> _logger;
    private readonly object _gate = new();
    private HashSet<string>? _set;

    public KanbanHiddenStore(ILogger<KanbanHiddenStore> logger, string storageDir)
    {
        _logger = logger;
        _path = Path.Combine(storageDir, "kanban-ocultos.json");
    }

    private HashSet<string> Set()
    {
        if (_set is not null) return _set;
        lock (_gate)
        {
            if (_set is not null) return _set;
            try
            {
                _set = File.Exists(_path)
                    ? new HashSet<string>(
                        JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? new(),
                        StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha lendo kanban-ocultos.json; começando vazio");
                _set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            return _set;
        }
    }

    private void Persist()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_set!.ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha gravando kanban-ocultos.json");
        }
    }

    public bool IsHidden(string? assetId) =>
        !string.IsNullOrWhiteSpace(assetId) && Set().Contains(assetId!.Trim());

    public int Count { get { lock (_gate) return Set().Count; } }

    public void Hide(string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return;
        var set = Set();
        lock (_gate)
        {
            if (set.Add(assetId!.Trim())) Persist();
        }
    }

    /// <summary>Volta a mostrar tudo (limpa a lista de ocultos).</summary>
    public void ClearAll()
    {
        var set = Set();
        lock (_gate)
        {
            if (set.Count == 0) return;
            set.Clear();
            Persist();
        }
    }
}
