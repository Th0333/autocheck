using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Infrastructure.Persistence;

/// <summary>
/// Mapa persistente <c>marca/modelo → linha</c> usado no cadastro de estoque.
/// A linha ("ThinkPad", "Latitude", "IdeaPad"…) é sugerida a partir do hardware;
/// quando o técnico confirma (ou corrige) e cadastra, o valor é aprendido e
/// reaproveitado nas próximas máquinas da mesma linha. Arquivo JSON simples no
/// diretório gravável do app, no mesmo padrão do <see cref="SerialNtbStore"/>.
/// </summary>
public sealed class LinhaStore
{
    private readonly string _path;
    private readonly ILogger<LinhaStore> _logger;
    private readonly object _gate = new();
    private Dictionary<string, string>? _map;

    public LinhaStore(ILogger<LinhaStore> logger, string storageDir)
    {
        _logger = logger;
        _path = Path.Combine(storageDir, "linha-map.json");
    }

    private static string KeyExact(string? manufacturer, string? model) =>
        $"{Norm(manufacturer)}|{Norm(model)}";

    private static string KeyFamily(string? manufacturer, string? model) =>
        $"{Norm(manufacturer)}|{Norm(FirstMeaningfulToken(manufacturer, model))}";

    private static string Norm(string? s) => (s ?? "").Trim().ToUpperInvariant();

    private Dictionary<string, string> Map()
    {
        if (_map is not null) return _map;
        lock (_gate)
        {
            if (_map is not null) return _map;
            try
            {
                _map = File.Exists(_path)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                      ?? new Dictionary<string, string>()
                    : new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha lendo linha-map.json; começando vazio");
                _map = new Dictionary<string, string>();
            }
            return _map;
        }
    }

    /// <summary>
    /// Linha aprendida para esta máquina: primeiro tenta o modelo exato, depois a
    /// família (primeiro token significativo do modelo). Null se nunca aprendida.
    /// </summary>
    public string? Lookup(string? manufacturer, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var map = Map();
        lock (_gate)
        {
            if (map.TryGetValue(KeyExact(manufacturer, model), out var exact) && !string.IsNullOrWhiteSpace(exact))
                return exact;
            if (map.TryGetValue(KeyFamily(manufacturer, model), out var fam) && !string.IsNullOrWhiteSpace(fam))
                return fam;
            return null;
        }
    }

    /// <summary>
    /// Aprende a linha confirmada pelo técnico para o modelo exato e para a
    /// família, de modo que "ThinkPad T480" ensine também "ThinkPad X1".
    /// </summary>
    public void Learn(string? manufacturer, string? model, string? linha)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(linha)) return;
        var map = Map();
        lock (_gate)
        {
            map[KeyExact(manufacturer, model)] = linha.Trim();
            var famToken = FirstMeaningfulToken(manufacturer, model);
            if (!string.IsNullOrWhiteSpace(famToken))
                map[KeyFamily(manufacturer, model)] = linha.Trim();
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha gravando linha-map.json");
            }
        }
    }

    /// <summary>
    /// Sugestão heurística quando nada foi aprendido: o primeiro token do modelo
    /// que pareça um nome de linha ("ThinkPad", "Latitude", "ProBook"), ignorando
    /// o nome do fabricante e códigos de SKU ("20L5CTO1WW", "T480").
    /// </summary>
    public static string? Suggest(string? manufacturer, string? model) =>
        FirstMeaningfulToken(manufacturer, model);

    private static string? FirstMeaningfulToken(string? manufacturer, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var man = Norm(manufacturer);
        foreach (var raw in model.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            var up = Norm(token);
            if (up.Length < 3) continue;
            if (man.Length > 0 && (man.Contains(up) || up.Contains(man))) continue; // é o fabricante
            if (!char.IsLetter(token[0])) continue;                                 // SKU tipo "20L5..."
            var letters = token.Count(char.IsLetter);
            if (letters < 3 || letters * 2 < token.Length) continue;               // mais dígitos que letras
            return token;
        }
        return null;
    }
}
