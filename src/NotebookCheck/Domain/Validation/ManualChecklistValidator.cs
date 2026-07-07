using System.Collections.Generic;
using System.Text.RegularExpressions;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;

namespace NotebookCheck.Domain.Validation;

/// <summary>
/// Validador para o checklist manual (Requirements 17.x). Foi dividido em
/// validações de identificação (apelido NTB / localização / asset tag) e
/// validações dos itens manuais propriamente ditos, para suportar o novo
/// fluxo onde o técnico informa o NTB já na entrada.
/// </summary>
public static class ManualChecklistValidator
{
    private static readonly Regex NtbCodeRegex = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    public static IReadOnlyList<string> ValidateIdentification(
        string ntbCode,
        string location,
        string assetTag)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ntbCode))
        {
            errors.Add("Informe o apelido/código NTB do equipamento");
        }
        else if (ntbCode.Length > 50 || !NtbCodeRegex.IsMatch(ntbCode))
        {
            errors.Add("Apelido/código inválido (1–50 caracteres alfanuméricos, hífen ou sublinhado)");
        }

        if (!string.IsNullOrWhiteSpace(location) && location.Length > 200)
        {
            errors.Add("Localização excede 200 caracteres");
        }

        if (assetTag is { Length: > 100 })
        {
            errors.Add("Etiqueta de patrimônio excede 100 caracteres");
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateManualItems(
        string generalNotes,
        IEnumerable<ManualCheckItem> items)
    {
        var errors = new List<string>();

        if (generalNotes is { Length: > 2000 })
        {
            errors.Add("Observações gerais excedem 2000 caracteres");
        }

        foreach (var item in items)
        {
            var requiresNotes = item.Status == ManualStatus.ComDefeito || item.Status == ManualStatus.Observacao;
            if (requiresNotes && string.IsNullOrWhiteSpace(item.Notes))
            {
                errors.Add($"Item '{item.ItemKey}' exige descrição (1–500 caracteres)");
            }
            else if (item.Notes is { Length: > 500 })
            {
                errors.Add($"Item '{item.ItemKey}' excede 500 caracteres na descrição");
            }
        }

        return errors;
    }

    /// <summary>
    /// Compatibilidade com o fluxo anterior — valida tudo de uma vez. Mantido
    /// para não quebrar consumidores existentes; novos chamadores devem usar
    /// as variantes específicas acima.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        string ntbCode,
        string location,
        string assetTag,
        string generalNotes,
        IEnumerable<ManualCheckItem> items)
    {
        var ident = ValidateIdentification(ntbCode, location, assetTag);
        var manual = ValidateManualItems(generalNotes, items);
        var merged = new List<string>(ident.Count + manual.Count);
        merged.AddRange(ident);
        merged.AddRange(manual);
        return merged;
    }
}
