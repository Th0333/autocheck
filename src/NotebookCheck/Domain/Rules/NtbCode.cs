using System.Text.RegularExpressions;

namespace NotebookCheck.Domain.Rules;

/// <summary>
/// Padronização do código NTB para o formato de estoque "NTBXXX" (sem hífen).
/// Fonte única usada tanto pelo checklist quanto pelo cadastro no estoque.
/// </summary>
public static class NtbCode
{
    /// <summary>
    /// "123" → "NTB123" • "ntb123" / "NTB 123" / "ntb-123" / "ntb_123" → "NTB123".
    /// Vazio permanece vazio (a obrigatoriedade é validada à parte).
    /// </summary>
    public static string Normalize(string? raw)
    {
        var v = (raw ?? "").Trim();
        if (v.Length == 0) return v;

        // Já tem o prefixo (qualquer caixa, com ou sem separador)? Canoniza.
        var m = Regex.Match(v, @"^ntb[\s_\-]*(.+)$", RegexOptions.IgnoreCase);
        var rest = m.Success ? m.Groups[1].Value.Trim() : v;
        if (rest.Length == 0) return "NTB";
        return "NTB" + rest;
    }
}
