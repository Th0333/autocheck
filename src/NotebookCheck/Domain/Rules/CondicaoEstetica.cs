namespace NotebookCheck.Domain.Rules;

/// <summary>
/// Ordenação das condições estéticas usadas no cadastro de estoque. Permite
/// comparar a condição informada pelo técnico com a condição mínima exigida
/// pelo pedido de compra (excelente &gt; boa &gt; regular &gt; ruim &gt; sucata).
/// </summary>
public static class CondicaoEstetica
{
    /// <summary>Posição na escala; -1 para valores desconhecidos/vazios.</summary>
    public static int Rank(string? valor) => valor?.Trim().ToLowerInvariant() switch
    {
        "excelente" => 4,
        "boa" => 3,
        "regular" => 2,
        "ruim" => 1,
        "sucata" => 0,
        _ => -1,
    };

    /// <summary>True quando a condição informada é pior que a mínima exigida.</summary>
    public static bool IsAbaixoDoMinimo(string? atual, string? minimo)
    {
        var a = Rank(atual);
        var m = Rank(minimo);
        return a >= 0 && m >= 0 && a < m;
    }

    /// <summary>Rótulo amigável ("boa" → "Boa"); devolve o próprio valor se desconhecido.</summary>
    public static string Label(string? valor) => valor?.Trim().ToLowerInvariant() switch
    {
        "excelente" => "Excelente",
        "boa" => "Boa",
        "regular" => "Regular",
        "ruim" => "Ruim",
        "sucata" => "Sucata",
        _ => valor ?? "",
    };
}
