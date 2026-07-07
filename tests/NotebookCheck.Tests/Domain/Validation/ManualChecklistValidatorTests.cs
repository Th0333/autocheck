namespace NotebookCheck.Tests;

/// <summary>
/// Testes para <see cref="ManualChecklistValidator"/>, cobrindo as validações de
/// identificação (Requirements 17.x) e as validações dos itens manuais.
/// </summary>
public class ManualChecklistValidatorTests
{
    // ---- ValidateIdentification ------------------------------------------------

    [Fact]
    public void ValidateIdentification_ValidInputs_ReturnsNoErrors()
    {
        var errors = ManualChecklistValidator.ValidateIdentification(
            ntbCode: "NTB123",
            location: "Sala 3 - Filial Centro",
            assetTag: "AT-0001");

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateIdentification_MissingNtbCode_ReturnsRequiredError(string? ntbCode)
    {
        var errors = ManualChecklistValidator.ValidateIdentification(
            ntbCode: ntbCode!,
            location: "",
            assetTag: "");

        errors.Should().ContainSingle()
            .Which.Should().Be("Informe o apelido/código NTB do equipamento");
    }

    [Theory]
    [InlineData("NTB 123")] // espaço não é permitido pelo regex
    [InlineData("NTB@123")] // caractere especial não permitido
    [InlineData("código_ção")] // acentos não permitidos
    public void ValidateIdentification_InvalidCharacters_ReturnsFormatError(string ntbCode)
    {
        var errors = ManualChecklistValidator.ValidateIdentification(
            ntbCode: ntbCode,
            location: "",
            assetTag: "");

        errors.Should().Contain("Apelido/código inválido (1–50 caracteres alfanuméricos, hífen ou sublinhado)");
    }

    [Fact]
    public void ValidateIdentification_NtbCodeAtMaxLength_ReturnsNoFormatError()
    {
        var ntbCode = new string('a', 50);

        var errors = ManualChecklistValidator.ValidateIdentification(ntbCode, "", "");

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateIdentification_NtbCodeTooLong_ReturnsFormatError()
    {
        // 51 caracteres válidos (só letras) — falha apenas pelo limite de tamanho.
        var ntbCode = new string('a', 51);

        var errors = ManualChecklistValidator.ValidateIdentification(ntbCode, "", "");

        errors.Should().Contain("Apelido/código inválido (1–50 caracteres alfanuméricos, hífen ou sublinhado)");
    }

    [Fact]
    public void ValidateIdentification_LocationAtMaxLength_ReturnsNoError()
    {
        var location = new string('x', 200);

        var errors = ManualChecklistValidator.ValidateIdentification("NTB1", location, "");

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateIdentification_LocationTooLong_ReturnsLocationError()
    {
        var location = new string('x', 201);

        var errors = ManualChecklistValidator.ValidateIdentification("NTB1", location, "");

        errors.Should().Contain("Localização excede 200 caracteres");
    }

    [Fact]
    public void ValidateIdentification_NullLocation_IsIgnoredByLengthCheck()
    {
        var errors = ManualChecklistValidator.ValidateIdentification("NTB1", null!, "");

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateIdentification_AssetTagAtMaxLength_ReturnsNoError()
    {
        var assetTag = new string('y', 100);

        var errors = ManualChecklistValidator.ValidateIdentification("NTB1", "", assetTag);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateIdentification_AssetTagTooLong_ReturnsAssetTagError()
    {
        var assetTag = new string('y', 101);

        var errors = ManualChecklistValidator.ValidateIdentification("NTB1", "", assetTag);

        errors.Should().Contain("Etiqueta de patrimônio excede 100 caracteres");
    }

    [Fact]
    public void ValidateIdentification_NullAssetTag_IsIgnoredByLengthCheck()
    {
        var errors = ManualChecklistValidator.ValidateIdentification("NTB1", "", null!);

        errors.Should().BeEmpty();
    }

    // ---- ValidateManualItems -----------------------------------------------------

    [Fact]
    public void ValidateManualItems_OkStatusWithoutNotes_ReturnsNoErrors()
    {
        var items = new[]
        {
            new ManualCheckItem("carcaca", ManualStatus.OK, ""),
            new ManualCheckItem("teclado", ManualStatus.NaoTestado, ""),
        };

        var errors = ManualChecklistValidator.ValidateManualItems("", items);

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ManualStatus.ComDefeito)]
    [InlineData(ManualStatus.Observacao)]
    public void ValidateManualItems_DefectOrObservationWithoutNotes_ReturnsRequiredError(ManualStatus status)
    {
        var items = new[] { new ManualCheckItem("tela", status, "") };

        var errors = ManualChecklistValidator.ValidateManualItems("", items);

        errors.Should().ContainSingle()
            .Which.Should().Be("Item 'tela' exige descrição (1–500 caracteres)");
    }

    [Fact]
    public void ValidateManualItems_DefectWithNotes_ReturnsNoErrors()
    {
        var items = new[] { new ManualCheckItem("tela", ManualStatus.ComDefeito, "Trinca no canto superior direito") };

        var errors = ManualChecklistValidator.ValidateManualItems("", items);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateManualItems_NotesTooLong_ReturnsLengthErrorInsteadOfRequiredError()
    {
        // Notas não estão em branco (então a checagem de obrigatoriedade não
        // dispara), mas excedem 500 caracteres — cai no "else if" de tamanho.
        var longNotes = new string('n', 501);
        var items = new[] { new ManualCheckItem("tela", ManualStatus.ComDefeito, longNotes) };

        var errors = ManualChecklistValidator.ValidateManualItems("", items);

        errors.Should().ContainSingle()
            .Which.Should().Be("Item 'tela' excede 500 caracteres na descrição");
    }

    [Fact]
    public void ValidateManualItems_OkStatusWithNotesTooLong_ReturnsLengthError()
    {
        var longNotes = new string('n', 501);
        var items = new[] { new ManualCheckItem("audio", ManualStatus.OK, longNotes) };

        var errors = ManualChecklistValidator.ValidateManualItems("", items);

        errors.Should().Contain("Item 'audio' excede 500 caracteres na descrição");
    }

    [Fact]
    public void ValidateManualItems_GeneralNotesAtMaxLength_ReturnsNoError()
    {
        var notes = new string('g', 2000);

        var errors = ManualChecklistValidator.ValidateManualItems(notes, Array.Empty<ManualCheckItem>());

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateManualItems_GeneralNotesTooLong_ReturnsError()
    {
        var notes = new string('g', 2001);

        var errors = ManualChecklistValidator.ValidateManualItems(notes, Array.Empty<ManualCheckItem>());

        errors.Should().Contain("Observações gerais excedem 2000 caracteres");
    }

    // ---- Validate (compatibilidade) ----------------------------------------------

    [Fact]
    public void Validate_MergesIdentificationErrorsBeforeManualErrors()
    {
        var items = new[] { new ManualCheckItem("webcam", ManualStatus.Observacao, "") };

        var errors = ManualChecklistValidator.Validate(
            ntbCode: "",
            location: "",
            assetTag: "",
            generalNotes: "",
            items: items);

        errors.Should().HaveCount(2);
        errors[0].Should().Be("Informe o apelido/código NTB do equipamento");
        errors[1].Should().Be("Item 'webcam' exige descrição (1–500 caracteres)");
    }

    [Fact]
    public void Validate_AllValidInputs_ReturnsNoErrors()
    {
        var items = new[] { new ManualCheckItem("webcam", ManualStatus.OK, "") };

        var errors = ManualChecklistValidator.Validate(
            ntbCode: "NTB42",
            location: "Estoque",
            assetTag: "AT-42",
            generalNotes: "Sem observações",
            items: items);

        errors.Should().BeEmpty();
    }
}
