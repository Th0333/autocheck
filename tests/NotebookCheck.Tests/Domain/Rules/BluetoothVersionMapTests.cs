namespace NotebookCheck.Tests;

/// <summary>
/// Testes para <see cref="BluetoothVersionMap.FromLmp"/>: mapeamento de valores LMP
/// (0–14) para a versão comercial correspondente, e comportamento fora do domínio
/// conhecido.
/// </summary>
public class BluetoothVersionMapTests
{
    [Theory]
    [InlineData(0, "1.0b")]
    [InlineData(1, "1.1")]
    [InlineData(2, "1.2")]
    [InlineData(3, "2.0 + EDR")]
    [InlineData(4, "2.1 + EDR")]
    [InlineData(5, "3.0 + HS")]
    [InlineData(6, "4.0")]
    [InlineData(7, "4.1")]
    [InlineData(8, "4.2")]
    [InlineData(9, "5.0")]
    [InlineData(10, "5.1")]
    [InlineData(11, "5.2")]
    [InlineData(12, "5.3")]
    [InlineData(13, "5.4")]
    [InlineData(14, "6.0")]
    public void FromLmp_KnownValues_ReturnsExpectedCommercialVersion(int lmp, string expected)
    {
        BluetoothVersionMap.FromLmp(lmp).Should().Be(expected);
    }

    [Fact]
    public void FromLmp_NullInput_ReturnsNull()
    {
        BluetoothVersionMap.FromLmp(null).Should().BeNull();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(15)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void FromLmp_ValuesOutsideKnownRange_ReturnsNull(int lmp)
    {
        BluetoothVersionMap.FromLmp(lmp).Should().BeNull();
    }

    /// <summary>
    /// Propriedade derivada diretamente do switch da implementação: os únicos
    /// valores mapeados são 0..14 (inclusive); qualquer outro inteiro é null.
    /// </summary>
    [Property]
    public void FromLmp_IsDefinedExactlyForZeroToFourteenRange(int lmp)
    {
        var result = BluetoothVersionMap.FromLmp(lmp);

        if (lmp is >= 0 and <= 14)
        {
            result.Should().NotBeNullOrEmpty();
        }
        else
        {
            result.Should().BeNull();
        }
    }

    /// <summary>Função pura e determinística: mesma entrada sempre produz mesma saída.</summary>
    [Property]
    public void FromLmp_IsDeterministic(int lmp)
    {
        BluetoothVersionMap.FromLmp(lmp).Should().Be(BluetoothVersionMap.FromLmp(lmp));
    }
}
