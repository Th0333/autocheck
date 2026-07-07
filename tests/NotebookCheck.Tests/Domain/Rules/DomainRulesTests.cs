namespace NotebookCheck.Tests;

/// <summary>
/// Testes extras (não solicitados explicitamente, mas incluídos como valor agregado)
/// para <see cref="DomainRules"/>: as regras puras de domínio citadas no XML doc da
/// própria classe como "base para os testes baseados em propriedades (FsCheck)".
/// Cobre <see cref="DomainRules.ComputeWear"/>, <see cref="DomainRules.ClassifyBatteryWear"/>,
/// <see cref="DomainRules.ClassifyFinal"/> e <see cref="DomainRules.WorstOf"/>.
/// </summary>
public class DomainRulesTests
{
    // ---- ComputeWear ----------------------------------------------------------------

    [Fact]
    public void ComputeWear_HappyPath_ReturnsRoundedPercentage()
    {
        DomainRules.ComputeWear(designMwh: 50000, currentMwh: 40000).Should().Be(20.00m);
    }

    [Fact]
    public void ComputeWear_RoundsAwayFromZeroToTwoDecimals()
    {
        // (1 - 1/3) * 100 = 66.6666...  -> arredonda para 66.67 (AwayFromZero).
        DomainRules.ComputeWear(designMwh: 3, currentMwh: 1).Should().Be(66.67m);
    }

    [Fact]
    public void ComputeWear_CurrentEqualsDesign_ReturnsZero()
    {
        DomainRules.ComputeWear(designMwh: 50000, currentMwh: 50000).Should().Be(0m);
    }

    [Fact]
    public void ComputeWear_CurrentGreaterThanDesign_AnomalyReturnsZero()
    {
        DomainRules.ComputeWear(designMwh: 40000, currentMwh: 45000).Should().Be(0m);
    }

    [Fact]
    public void ComputeWear_DesignZeroOrNegative_Throws()
    {
        var act = () => DomainRules.ComputeWear(designMwh: 0, currentMwh: 10);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Where(e => e.ParamName == "designMwh");
    }

    [Fact]
    public void ComputeWear_CurrentNegative_Throws()
    {
        var act = () => DomainRules.ComputeWear(designMwh: 100, currentMwh: -1);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Where(e => e.ParamName == "currentMwh");
    }

    // ---- ClassifyBatteryWear ----------------------------------------------------------

    [Theory]
    [InlineData(0, AutoStatus.OK)]
    [InlineData(20, AutoStatus.OK)] // limite inclusivo
    [InlineData(20.01, AutoStatus.Atencao)]
    [InlineData(40, AutoStatus.Atencao)] // limite inclusivo
    [InlineData(40.01, AutoStatus.Falha)]
    [InlineData(100, AutoStatus.Falha)]
    public void ClassifyBatteryWear_ReturnsExpectedStatusPerRange(double wear, AutoStatus expected)
    {
        DomainRules.ClassifyBatteryWear((decimal)wear).Should().Be(expected);
    }

    // ---- ClassifyFinal ------------------------------------------------------------------

    [Fact]
    public void ClassifyFinal_NoIssues_ReturnsAprovado()
    {
        var result = DomainRules.ClassifyFinal(
            new[] { AutoStatus.OK, AutoStatus.NaoAplicavel },
            new[] { ManualStatus.OK, ManualStatus.NaoTestado });

        result.Should().Be(FinalClassification.Aprovado);
    }

    [Fact]
    public void ClassifyFinal_AutoWarningOnly_ReturnsAprovadoComRessalvas()
    {
        var result = DomainRules.ClassifyFinal(
            new[] { AutoStatus.OK, AutoStatus.Atencao },
            Array.Empty<ManualStatus>());

        result.Should().Be(FinalClassification.AprovadoComRessalvas);
    }

    [Fact]
    public void ClassifyFinal_ManualObservationOnly_ReturnsAprovadoComRessalvas()
    {
        var result = DomainRules.ClassifyFinal(
            Array.Empty<AutoStatus>(),
            new[] { ManualStatus.Observacao });

        result.Should().Be(FinalClassification.AprovadoComRessalvas);
    }

    [Fact]
    public void ClassifyFinal_AutoFailure_ReturnsReprovadoEvenWithManualObservacao()
    {
        var result = DomainRules.ClassifyFinal(
            new[] { AutoStatus.Falha },
            new[] { ManualStatus.Observacao });

        result.Should().Be(FinalClassification.Reprovado);
    }

    [Fact]
    public void ClassifyFinal_ManualDefect_ReturnsReprovado()
    {
        var result = DomainRules.ClassifyFinal(
            new[] { AutoStatus.OK },
            new[] { ManualStatus.ComDefeito });

        result.Should().Be(FinalClassification.Reprovado);
    }

    [Fact]
    public void ClassifyFinal_EmptyInputs_ReturnsAprovado()
    {
        DomainRules.ClassifyFinal(Array.Empty<AutoStatus>(), Array.Empty<ManualStatus>())
            .Should().Be(FinalClassification.Aprovado);
    }

    [Fact]
    public void ClassifyFinal_NullAuto_Throws()
    {
        var act = () => DomainRules.ClassifyFinal(null!, Array.Empty<ManualStatus>());

        act.Should().Throw<ArgumentNullException>().Where(e => e.ParamName == "auto");
    }

    [Fact]
    public void ClassifyFinal_NullManual_Throws()
    {
        var act = () => DomainRules.ClassifyFinal(Array.Empty<AutoStatus>(), null!);

        act.Should().Throw<ArgumentNullException>().Where(e => e.ParamName == "manual");
    }

    // ---- WorstOf ------------------------------------------------------------------------

    [Fact]
    public void WorstOf_SingleValue_ReturnsThatValue()
    {
        DomainRules.WorstOf(AutoStatus.OK).Should().Be(AutoStatus.OK);
    }

    [Fact]
    public void WorstOf_MixedValues_ReturnsMostSevere()
    {
        // Ordem de severidade: NaoAplicavel < NaoTestado < OK < Atencao < Falha.
        DomainRules.WorstOf(AutoStatus.OK, AutoStatus.NaoAplicavel, AutoStatus.Atencao)
            .Should().Be(AutoStatus.Atencao);
    }

    [Fact]
    public void WorstOf_NaoTestadoIsWorseThanNaoAplicavelButBetterThanOk()
    {
        DomainRules.WorstOf(AutoStatus.NaoAplicavel, AutoStatus.NaoTestado).Should().Be(AutoStatus.NaoTestado);
        DomainRules.WorstOf(AutoStatus.NaoTestado, AutoStatus.OK).Should().Be(AutoStatus.OK);
    }

    [Fact]
    public void WorstOf_EmptyArray_Throws()
    {
        var act = () => DomainRules.WorstOf();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WorstOf_NullArray_Throws()
    {
        var act = () => DomainRules.WorstOf((AutoStatus[])null!);

        act.Should().Throw<ArgumentNullException>().Where(e => e.ParamName == "statuses");
    }

    /// <summary>
    /// Propriedade: para qualquer conjunto não vazio de status, o resultado de
    /// <see cref="DomainRules.WorstOf"/> tem severidade igual ao máximo das
    /// severidades de entrada, conforme a ordem documentada no XML doc do método
    /// (NaoAplicavel &lt; NaoTestado &lt; OK &lt; Atencao &lt; Falha).
    /// </summary>
    [Property]
    public void WorstOf_ReturnsStatusWithMaximumDocumentedSeverity(int[] seed)
    {
        if (seed.Length == 0)
        {
            return; // WorstOf exige ao menos um elemento; nada a verificar aqui.
        }

        var statuses = seed.Select(s => AllStatusesBySeverity[Math.Abs(s % AllStatusesBySeverity.Length)]).ToArray();
        var expectedSeverity = statuses.Max(s => Severity[s]);

        var worst = DomainRules.WorstOf(statuses);

        Severity[worst].Should().Be(expectedSeverity);
    }

    private static readonly IReadOnlyDictionary<AutoStatus, int> Severity = new Dictionary<AutoStatus, int>
    {
        [AutoStatus.NaoAplicavel] = 0,
        [AutoStatus.NaoTestado] = 1,
        [AutoStatus.OK] = 2,
        [AutoStatus.Atencao] = 3,
        [AutoStatus.Falha] = 4,
    };

    private static readonly AutoStatus[] AllStatusesBySeverity =
    {
        AutoStatus.NaoAplicavel, AutoStatus.NaoTestado, AutoStatus.OK, AutoStatus.Atencao, AutoStatus.Falha,
    };
}
