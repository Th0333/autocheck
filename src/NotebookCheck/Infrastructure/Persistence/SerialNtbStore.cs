using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Infrastructure.Persistence;

/// <summary>
/// Mapa persistente <c>serial → NTB</c>. Gravado no cadastro de uma máquina no
/// estoque para que, ao rodar um checklist depois na MESMA máquina (mesmo número
/// de série), o NTB seja preenchido automaticamente. Arquivo JSON simples no
/// diretório gravável do app.
/// </summary>
public sealed class SerialNtbStore
{
    private readonly string _path;
    private readonly ILogger<SerialNtbStore> _logger;
    private readonly object _gate = new();
    private Dictionary<string, string>? _map;

    public SerialNtbStore(ILogger<SerialNtbStore> logger, string storageDir)
    {
        _logger = logger;
        _path = Path.Combine(storageDir, "serial-ntb.json");
    }

    private static string Norm(string serial) => serial.Trim().ToUpperInvariant();

    private Dictionary<string, string> Map()
    {
        if (_map is not null) return _map;
        lock (_gate)
        {
            if (_map is not null) return _map;
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    _map = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                           ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha lendo serial-ntb.json; começando vazio");
                _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            return _map;
        }
    }

    /// <summary>Retorna o NTB associado ao serial, ou null se não houver.</summary>
    public string? Lookup(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return null;
        var map = Map();
        lock (_gate)
        {
            return map.TryGetValue(Norm(serial), out var ntb) && !string.IsNullOrWhiteSpace(ntb) ? ntb : null;
        }
    }

    /// <summary>Associa (ou atualiza) o NTB de um serial e persiste.</summary>
    public void Save(string? serial, string? ntb)
    {
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(ntb)) return;
        var map = Map();
        lock (_gate)
        {
            map[Norm(serial)] = ntb.Trim();
            try
            {
                var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha gravando serial-ntb.json");
            }
        }
    }
}
