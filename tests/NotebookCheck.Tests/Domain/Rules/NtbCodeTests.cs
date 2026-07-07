namespace NotebookCheck.Tests;

/// <summary>
/// Testes para <see cref="NtbCode.Normalize"/>: padronização do código NTB para o
/// formato de estoque "NTBXXX" (sem hífen/espaço/underscore).
/// </summary>
public class NtbCodeTests
{
    // ---- Casos felizes documentados no próprio código -----------------------------

    [Theory]
    [InlineData("123", "NTB123")]
    [InlineData("ntb123", "NTB123")]
    [InlineData("NTB 123", "NTB123")]
    [InlineData("ntb-123", "NTB123")]
    [InlineData("ntb_123", "NTB123")]
    [InlineData("NTB123", "NTB123")]
    public void Normalize_DocumentedExamples_ReturnsExpectedFormat(string raw, string expected)
    {
        NtbCode.Normalize(raw).Should().Be(expected);
    }

    // ---- Entradas vazias / em branco ------------------------------------------------

    [Fact]
    public void Normalize_NullInput_ReturnsEmptyString()
    {
        NtbCode.Normalize(null).Should().Be("");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_EmptyOrWhitespaceInput_ReturnsEmptyString(string raw)
    {
        NtbCode.Normalize(raw).Should().Be("");
    }

    // ---- Caso extremo verificado na implementação real -----------------------------

    [Fact]
    public void Normalize_BarePrefixWithoutSuffix_DoublesThePrefix()
    {
        // Caso extremo real (não é comportamento desejável documentado, mas é o que
        // o regex `^ntb[\s_\-]*(.+)$` produz de fato): como o grupo (.+) exige ao
        // menos 1 caractere após o prefixo, "NTB" sozinho NÃO casa com o padrão de
        // prefixo já existente, então cai no ramo "sem match" (rest = v original) e
        // o prefixo "NTB" é prefixado de novo por cima do valor original.
        NtbCode.Normalize("NTB").Should().Be("NTBNTB");
    }

    // ---- Estabilidade sob reaplicação (apenas para entradas "normais") --------------

    [Theory]
    [InlineData("123")]
    [InlineData("ntb123")]
    [InlineData("NTB 123")]
    [InlineData("ntb-123")]
    [InlineData("ntb_123")]
    public void Normalize_TypicalInputs_IsStableWhenReapplied(string raw)
    {
        var once = NtbCode.Normalize(raw);
        var twice = NtbCode.Normalize(once);

        twice.Should().Be(once);
    }

    // ---- Propriedades (FsCheck) -----------------------------------------------------

    /// <summary>
    /// Propriedade universal provada pela estrutura do código: toda entrada cujo
    /// resultado trimado não é vazio produz uma string prefixada literalmente por
    /// "NTB" (todo "return" não vazio do método começa com esse literal).
    /// </summary>
    [Property]
    public void Normalize_AlwaysReturnsEmptyOrNtbPrefixedValue(string? raw)
    {
        var result = NtbCode.Normalize(raw);

        // Mensagem usa {0} (argumento do FluentAssertions), não interpolação direta,
        // para não quebrar caso o FsCheck gere um "result" contendo chaves literais.
        (result.Length == 0 || result.StartsWith("NTB", StringComparison.Ordinal))
            .Should().BeTrue("Normalize deveria retornar \"\" ou algo prefixado com \"NTB\", mas retornou {0}", result);
    }

    /// <summary>
    /// Para entradas compostas só por dígitos (nunca começam com "ntb" nem contêm
    /// separadores), o regex de prefixo nunca casa — o resultado é sempre
    /// "NTB" + entrada original, verbatim. Usa <c>long</c> na conversão para evitar
    /// overflow de <see cref="Math.Abs(int)"/> quando o FsCheck gerar <see cref="int.MinValue"/>.
    /// </summary>
    [Property]
    public void Normalize_PureDigitInput_GetsNtbPrefixVerbatim(int value)
    {
        var digits = Math.Abs((long)value).ToString();

        NtbCode.Normalize(digits).Should().Be("NTB" + digits);
    }
}
