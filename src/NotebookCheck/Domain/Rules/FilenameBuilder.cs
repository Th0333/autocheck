using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;

namespace NotebookCheck.Domain.Rules;

/// <summary>
/// Construtor de nomes de arquivo dos relatórios (checklist completo e reteste) e do
/// caminho do PDF derivado, conforme Requirements 22.2, 22.3, 22.4 e 32.11.
/// </summary>
/// <remarks>
/// <para>
/// O nome do arquivo de checklist completo segue o padrão:
/// <c>CHECKLIST_&lt;serial|SEMSERIAL&gt;_YYYYMMDD_HHMMSS.json</c>
/// e respeita o regex <c>^CHECKLIST_(?&lt;s&gt;[A-Z0-9]+|SEMSERIAL)_\d{8}_\d{6}\.json$</c>.
/// </para>
/// <para>
/// O nome do arquivo de reteste segue o padrão:
/// <c>RETESTE_&lt;serial|SEMSERIAL&gt;_&lt;COMPS&gt;_YYYYMMDD_HHMMSS.json</c>,
/// onde <c>&lt;COMPS&gt;</c> é a concatenação dos identificadores dos componentes
/// retestados em letras maiúsculas, separados pelo caractere <c>+</c>, na ordem de seleção,
/// e respeita o regex
/// <c>^RETESTE_(?&lt;s&gt;[A-Z0-9]+|SEMSERIAL)_(?&lt;c&gt;[A-Z]+(?:\+[A-Z]+)*)_\d{8}_\d{6}\.json$</c>.
/// </para>
/// <para>
/// O número de série é sanitizado convertendo-se para letras maiúsculas e removendo
/// quaisquer caracteres não alfanuméricos. Quando o serial é <c>null</c>, vazio,
/// composto apenas por espaços em branco, ou quando a sanitização resulta em string vazia,
/// o valor literal <c>SEMSERIAL</c> é usado em seu lugar.
/// </para>
/// </remarks>
public static class FilenameBuilder
{
    /// <summary>
    /// Valor literal usado no lugar do número de série quando este não está disponível
    /// ou contém apenas caracteres não alfanuméricos.
    /// </summary>
    public const string NoSerialPlaceholder = "SEMSERIAL";

    /// <summary>
    /// Formato de data/hora utilizado nos nomes de arquivo (ano-mês-dia, hora-minuto-segundo).
    /// </summary>
    private const string TimestampFormat = "yyyyMMdd_HHmmss";

    /// <summary>
    /// Constrói o nome do arquivo JSON do relatório de checklist completo.
    /// </summary>
    /// <param name="serial">Número de série do equipamento. Pode ser <c>null</c>, vazio ou
    /// composto apenas por espaços em branco; nesse caso, é substituído por <c>SEMSERIAL</c>.</param>
    /// <param name="ts">Timestamp do teste. Os componentes de data e hora são formatados em
    /// <c>YYYYMMDD_HHMMSS</c>.</param>
    /// <returns>Nome do arquivo JSON, sem caminho de diretório. Exemplo:
    /// <c>CHECKLIST_ABC123_20250115_103045.json</c>.</returns>
    public static string BuildChecklistFileName(string? serial, DateTime ts)
    {
        var sanitizedSerial = SanitizeSerial(serial);
        var timestamp = ts.ToString(TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);
        return $"CHECKLIST_{sanitizedSerial}_{timestamp}.json";
    }

    /// <summary>
    /// Constrói o nome do arquivo JSON do relatório de reteste.
    /// </summary>
    /// <param name="serial">Número de série do equipamento. Pode ser <c>null</c>, vazio ou
    /// composto apenas por espaços em branco; nesse caso, é substituído por <c>SEMSERIAL</c>.</param>
    /// <param name="components">Lista ordenada (ordem de seleção) dos componentes retestados.
    /// Não pode ser <c>null</c> nem vazia.</param>
    /// <param name="ts">Timestamp do teste. Os componentes de data e hora são formatados em
    /// <c>YYYYMMDD_HHMMSS</c>.</param>
    /// <returns>Nome do arquivo JSON, sem caminho de diretório. Exemplo:
    /// <c>RETESTE_ABC123_TELA+CARREGADOR_20250115_103045.json</c>.</returns>
    /// <exception cref="ArgumentNullException">Quando <paramref name="components"/> é <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Quando <paramref name="components"/> está vazio.</exception>
    public static string BuildRetestFileName(string? serial, IReadOnlyList<ComponentId> components, DateTime ts)
    {
        if (components is null)
        {
            throw new ArgumentNullException(nameof(components));
        }
        if (components.Count == 0)
        {
            throw new ArgumentException(
                "É necessário informar pelo menos um componente para o reteste.",
                nameof(components));
        }

        var sanitizedSerial = SanitizeSerial(serial);
        var componentsToken = BuildComponentsToken(components);
        var timestamp = ts.ToString(TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);
        return $"RETESTE_{sanitizedSerial}_{componentsToken}_{timestamp}.json";
    }

    /// <summary>
    /// Deriva o caminho do arquivo PDF a partir do caminho do arquivo JSON do relatório,
    /// preservando o mesmo nome base e diretório, conforme Requirement 22.4.
    /// </summary>
    /// <param name="jsonPath">Caminho absoluto ou relativo do arquivo JSON. Não pode ser
    /// <c>null</c>, vazio ou composto apenas por espaços em branco.</param>
    /// <returns>Caminho do arquivo PDF correspondente (mesmo diretório e nome base, com
    /// extensão <c>.pdf</c>).</returns>
    /// <exception cref="ArgumentException">Quando <paramref name="jsonPath"/> é nulo, vazio
    /// ou apenas espaços em branco.</exception>
    public static string BuildPdfPath(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            throw new ArgumentException(
                "O caminho do arquivo JSON não pode ser nulo, vazio ou apenas espaços em branco.",
                nameof(jsonPath));
        }

        return Path.ChangeExtension(jsonPath, ".pdf");
    }

    /// <summary>
    /// Sanitiza o número de série retornando o valor em letras maiúsculas com apenas
    /// caracteres alfanuméricos ASCII, ou <see cref="NoSerialPlaceholder"/> quando o serial
    /// é nulo, vazio, espaços em branco ou não contém qualquer caractere alfanumérico.
    /// </summary>
    private static string SanitizeSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return NoSerialPlaceholder;
        }

        var builder = new StringBuilder(serial.Length);
        foreach (var ch in serial)
        {
            if (IsAsciiDigit(ch))
            {
                builder.Append(ch);
            }
            else if (IsAsciiLetter(ch))
            {
                builder.Append(ToAsciiUpper(ch));
            }
        }

        return builder.Length == 0 ? NoSerialPlaceholder : builder.ToString();
    }

    /// <summary>
    /// Concatena os identificadores dos componentes em letras maiúsculas, separados por
    /// <c>+</c>, na ordem fornecida.
    /// </summary>
    private static string BuildComponentsToken(IReadOnlyList<ComponentId> components)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < components.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('+');
            }

            var key = ComponentTestMap.GetTestKey(components[i]);
            builder.Append(key.ToUpperInvariant());
        }

        return builder.ToString();
    }

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    private static char ToAsciiUpper(char c) => (c >= 'a' && c <= 'z') ? (char)(c - ('a' - 'A')) : c;
}
