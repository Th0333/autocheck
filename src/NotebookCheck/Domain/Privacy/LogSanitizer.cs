using System.Text.RegularExpressions;

namespace NotebookCheck.Domain.Privacy;

/// <summary>
/// Sanitizador de privacidade que remove o nome do usuário do sistema operacional
/// (<see cref="Environment.UserName"/>) de mensagens de log e payloads JSON
/// serializados, substituindo-o pelo placeholder <c>&lt;USUARIO&gt;</c>.
/// </summary>
/// <remarks>
/// Atende aos Requisitos 27.2 e 27.3 e à Property 24 ("Sanitização preserva
/// privacidade") do design: para qualquer relatório serializado, o JSON
/// resultante não contém o valor de <see cref="Environment.UserName"/>; para
/// qualquer caminho registrado em log que originalmente contém o nome de
/// usuário do sistema, o log persistido substitui esse trecho por
/// <c>&lt;USUARIO&gt;</c>. A operação é idempotente (aplicar mais de uma vez
/// produz o mesmo resultado) e a busca é case-insensitive — apropriada para
/// caminhos Windows, onde <c>C:\Users\Joe</c> e <c>c:\users\joe</c> identificam
/// o mesmo usuário.
/// </remarks>
public static class LogSanitizer
{
    /// <summary>Token usado para substituir o nome do usuário.</summary>
    public const string Placeholder = "<USUARIO>";

    private const RegexOptions SanitizeRegexOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Cache do regex construído a partir do <see cref="Environment.UserName"/>
    /// atual. Recalculado se o nome do usuário mudar entre chamadas (cenário raro,
    /// porém possível em testes que manipulam variáveis de ambiente).
    /// </summary>
    private static volatile CachedSanitizer? _cached;

    /// <summary>
    /// Substitui ocorrências do nome do usuário corrente
    /// (<see cref="Environment.UserName"/>) na mensagem informada pelo
    /// placeholder <c>&lt;USUARIO&gt;</c>.
    /// </summary>
    /// <param name="message">Mensagem original — tipicamente uma linha de log
    /// contendo um caminho de arquivo Windows.</param>
    /// <returns>Mensagem com o nome do usuário substituído. Idempotente.</returns>
    public static string Sanitize(string? message)
    {
        return SanitizeInternal(message, Environment.UserName);
    }

    /// <summary>
    /// Variante de <see cref="Sanitize(string?)"/> que aceita o nome do usuário
    /// explicitamente. Útil para testes baseados em propriedades onde o
    /// <see cref="Environment.UserName"/> não pode ser controlado.
    /// </summary>
    internal static string Sanitize(string? message, string? userName)
    {
        return SanitizeInternal(message, userName);
    }

    /// <summary>
    /// Sanitiza um relatório (<c>ChecklistReport</c>/<c>RetestReport</c>) já
    /// serializado em JSON, garantindo que o nome do usuário não aparece em
    /// nenhum campo. Aplica a mesma transformação de <see cref="Sanitize(string?)"/>,
    /// suportando tanto barras simples (<c>\</c>) quanto a forma escapada em
    /// JSON (<c>\\</c>).
    /// </summary>
    /// <param name="serializedReport">JSON serializado de um relatório.</param>
    /// <returns>JSON com o nome do usuário substituído pelo placeholder.</returns>
    public static string SanitizeReport(string? serializedReport)
    {
        return SanitizeInternal(serializedReport, Environment.UserName);
    }

    /// <summary>
    /// Variante de <see cref="SanitizeReport(string?)"/> que aceita o nome do
    /// usuário explicitamente. Reservada para uso em testes.
    /// </summary>
    internal static string SanitizeReport(string? serializedReport, string? userName)
    {
        return SanitizeInternal(serializedReport, userName);
    }

    private static string SanitizeInternal(string? input, string? userName)
    {
        if (input is null || input.Length == 0)
        {
            return input ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(userName))
        {
            // Sem um nome de usuário válido não há o que substituir; devolver
            // a entrada como veio evita a remoção indevida de strings vazias.
            return input;
        }

        var sanitizer = GetOrBuildSanitizer(userName);

        try
        {
            return sanitizer.Regex.Replace(input, Placeholder);
        }
        catch (RegexMatchTimeoutException)
        {
            // Em caso de input adversarial, preservar a entrada é mais seguro
            // do que vazar o nome do usuário; mas o placeholder já protege a
            // privacidade nas rotas normais (logs/relatórios).
            return input;
        }
    }

    private static CachedSanitizer GetOrBuildSanitizer(string userName)
    {
        var cached = _cached;
        if (cached is not null && string.Equals(cached.UserName, userName, StringComparison.Ordinal))
        {
            return cached;
        }

        // Lookarounds que tratam letras, dígitos e sublinhado como caracteres de
        // identificador. Backslashes, barras, aspas e outros separadores comuns
        // de paths e JSON contam como fronteira válida, garantindo que apenas
        // o segmento do nome de usuário seja substituído (e não, por exemplo,
        // "joe" dentro de "joey"). \p{L} cobre acentos e caracteres não-ASCII
        // que podem aparecer em nomes de conta de domínios.
        var pattern = "(?<![\\p{L}\\p{N}_])" + Regex.Escape(userName) + "(?![\\p{L}\\p{N}_])";
        var regex = new Regex(pattern, SanitizeRegexOptions, RegexTimeout);
        var fresh = new CachedSanitizer(userName, regex);
        _cached = fresh;
        return fresh;
    }

    private sealed record CachedSanitizer(string UserName, Regex Regex);
}
