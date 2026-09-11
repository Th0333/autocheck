namespace NotebookCheck.Tests;

/// <summary>
/// Testes para <see cref="PayloadBuilder"/>: mapeamento de <see cref="ChecklistReport"/>
/// e <see cref="RetestReport"/> para <see cref="ApiPayload"/>, e os mapeamentos textuais
/// PT-BR auxiliares (status, classificação final, disponibilidade etc.).
/// </summary>
public class PayloadBuilderTests
{
    private static MachineInfo CreateMachine(
        BluetoothInfo? bluetooth = null,
        BiosSecurity? biosSecurity = null,
        ComputraceInfo? computrace = null,
        ProcessorInfo? processor = null,
        MemoryInfo? memory = null,
        IReadOnlyList<GraphicsInfo>? graphicsDetails = null,
        IReadOnlyList<string>? graphicsAdapters = null) => new(
        Manufacturer: "Dell",
        Model: "Latitude 5420",
        Serial: "SN12345",
        Hostname: "NB-0001",
        Cpu: "Intel Core i5-1135G7",
        RamGb: 16m,
        Os: "Windows 11 Pro",
        OsVersion: "23H2",
        MacAddress: "AA:BB:CC:DD:EE:FF",
        Tpm: AvailabilityFlag.Presente,
        TpmVersion: "2.0",
        SecureBoot: AvailabilityFlag.Habilitado,
        Autopilot: AvailabilityFlag.NaoRegistrado,
        WindowsActivation: AvailabilityFlag.Ativado,
        ScreenResolution: "1920x1080",
        GraphicsAdapter: "Intel Iris Xe Graphics",
        CpuTemperatureC: 45m,
        NtbCode: "NTB123",
        Location: "Filial Centro",
        KeyboardBacklight: KeyboardBacklight.Nao,
        KeyboardBacklightDetected: KeyboardBacklight.Nao,
        CollectedAt: new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
        GraphicsAdapters: graphicsAdapters,
        Bluetooth: bluetooth,
        BiosSecurity: biosSecurity,
        Computrace: computrace,
        Processor: processor,
        Memory: memory,
        GraphicsDetails: graphicsDetails);

    private static ChecklistReport CreateChecklistReport(
        MachineInfo? machine = null,
        Guid? testId = null,
        DateTime? testedAt = null,
        IReadOnlyList<StorageInfo>? storage = null,
        BatteryInfo? battery = null,
        IReadOnlyDictionary<string, TestResult>? tests = null,
        IReadOnlyDictionary<string, ManualCheckItem>? manualChecklist = null,
        StressSnapshot? stress = null,
        IReadOnlyList<InspectionPhoto>? inspectionPhotos = null,
        bool? hasNumericKeypad = null,
        bool? hasTouchScreen = null,
        ChecklistMode mode = ChecklistMode.Padrao) => new(
        TestId: testId ?? Guid.NewGuid(),
        TestedAt: testedAt ?? new DateTime(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc),
        TechnicianName: "Fulano de Tal",
        Machine: machine ?? CreateMachine(),
        Storage: storage ?? new List<StorageInfo>(),
        Battery: battery,
        Tests: tests ?? new Dictionary<string, TestResult>(),
        ManualChecklist: manualChecklist ?? new Dictionary<string, ManualCheckItem>(),
        GeneralNotes: "",
        AssetTag: "",
        FinalClassification: FinalClassification.Aprovado,
        FinalClassificationOverrideReason: null,
        Mode: mode,
        Stress: stress,
        InspectionPhotos: inspectionPhotos,
        HasNumericKeypad: hasNumericKeypad,
        HasTouchScreen: hasTouchScreen);

    // ---- Build(ChecklistReport) — campos básicos ---------------------------------

    [Fact]
    public void Build_ChecklistReport_MapsBasicFields()
    {
        var testId = Guid.NewGuid();
        var testedAt = new DateTime(2026, 3, 15, 9, 45, 0, DateTimeKind.Utc);
        var report = CreateChecklistReport(testId: testId, testedAt: testedAt);

        var payload = PayloadBuilder.Build(report);

        payload.TestId.Should().Be(testId.ToString("D"));
        payload.ReportType.Should().Be("full_checklist");
        payload.TestedAt.Should().Be(testedAt.ToString("o", CultureInfo.InvariantCulture));
        payload.TechnicianName.Should().Be("Fulano de Tal");
        payload.ChecklistMode.Should().Be("padrao");
        payload.GeneralNotes.Should().Be("");
        payload.AssetTag.Should().Be("");
        payload.RetestedComponents.Should().BeEmpty();
        payload.RepairNotes.Should().Be("");
        payload.FinalClassification.Should().Be("Aprovado");
        payload.FinalClassificationOverrideReason.Should().BeNull();
    }

    [Theory]
    [InlineData(ChecklistMode.Basico, "basico")]
    [InlineData(ChecklistMode.Padrao, "padrao")]
    [InlineData(ChecklistMode.Detalhado, "detalhado")]
    [InlineData(ChecklistMode.Desktop, "desktop")]
    public void Build_ChecklistReport_MapsChecklistMode(ChecklistMode mode, string expected)
    {
        var report = CreateChecklistReport(mode: mode);

        var payload = PayloadBuilder.Build(report);

        payload.ChecklistMode.Should().Be(expected);
    }

    // ---- Build(ChecklistReport) — Machine: seções opcionais ausentes ------------

    [Fact]
    public void Build_ChecklistReport_OptionalMachineSectionsNull_MapToNullOrEmptyDefaults()
    {
        var machine = CreateMachine(); // bluetooth, bios, computrace, processor, memory, gpus todos null
        var report = CreateChecklistReport(machine: machine);

        var payload = PayloadBuilder.Build(report);

        payload.Machine.BluetoothVersion.Should().BeNull();
        payload.Machine.BiosSetupPassword.Should().BeNull();
        payload.Machine.BiosPowerOnPassword.Should().BeNull();
        payload.Machine.BiosHddPassword.Should().BeNull();
        payload.Machine.ComputraceModule.Should().BeNull();
        payload.Machine.ComputraceAgent.Should().BeNull();
        payload.Machine.ComputraceVersion.Should().BeNull();
        payload.Machine.CpuCores.Should().BeNull();
        payload.Machine.CpuThreads.Should().BeNull();
        payload.Machine.CpuMaxClockMhz.Should().BeNull();
        payload.Machine.RamType.Should().BeNull();
        payload.Machine.RamModules.Should().BeNull();
        payload.Machine.Gpus.Should().BeNull();
        // Diferente dos anteriores: GraphicsAdapters nunca é nulo no payload,
        // vira lista vazia (`m.GraphicsAdapters?.ToList() ?? new List<string>()`).
        payload.Machine.GraphicsAdapters.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Build_ChecklistReport_FullMachineSections_MapsAllDetailFields()
    {
        var machine = CreateMachine(
            bluetooth: new BluetoothInfo(Present: true, Name: "Intel Wireless BT", Status: "OK", LmpVersion: 9, Version: "5.0"),
            biosSecurity: new BiosSecurity(
                HasSetupPassword: AvailabilityFlag.Presente,
                HasPowerOnPassword: AvailabilityFlag.Ausente,
                HasHddPassword: AvailabilityFlag.Indisponivel,
                Source: "HP_BIOSSetting"),
            computrace: new ComputraceInfo(
                ModuleActive: AvailabilityFlag.Ausente,
                AgentInstalled: AvailabilityFlag.Presente,
                ServiceStatus: "Running",
                Version: "1.2.3"),
            processor: new ProcessorInfo(Name: "Intel Core i7", Cores: 4, Threads: 8, MaxClockMhz: 4200),
            memory: new MemoryInfo(
                TotalGb: 16m,
                Type: "DDR4",
                SpeedMhz: 3200,
                SlotsUsed: 2,
                SlotsTotal: 2,
                Modules: new[]
                {
                    new MemoryModule(Locator: "DIMM A", CapacityGb: 8m, SpeedMhz: 3200, Manufacturer: "Kingston", PartNumber: "KVR32", Type: "DDR4"),
                }),
            graphicsDetails: new[] { new GraphicsInfo(Name: "NVIDIA RTX 3050", VramMb: 4096, DriverVersion: "31.0.15.3667", DriverDate: "2024-05-01") },
            graphicsAdapters: new[] { "Intel Iris Xe Graphics", "NVIDIA RTX 3050" });
        var report = CreateChecklistReport(machine: machine);

        var payload = PayloadBuilder.Build(report);

        payload.Machine.BluetoothVersion.Should().Be("5.0");
        payload.Machine.BiosSetupPassword.Should().Be("Presente");
        payload.Machine.BiosPowerOnPassword.Should().Be("Ausente");
        payload.Machine.BiosHddPassword.Should().Be("Indisponível");
        payload.Machine.ComputraceModule.Should().Be("Ausente");
        payload.Machine.ComputraceAgent.Should().Be("Presente");
        payload.Machine.ComputraceVersion.Should().Be("1.2.3");
        payload.Machine.CpuCores.Should().Be(4);
        payload.Machine.CpuThreads.Should().Be(8);
        payload.Machine.CpuMaxClockMhz.Should().Be(4200);
        payload.Machine.RamType.Should().Be("DDR4");
        payload.Machine.RamSpeedMhz.Should().Be(3200);
        payload.Machine.RamSlotsUsed.Should().Be(2);
        payload.Machine.RamSlotsTotal.Should().Be(2);
        payload.Machine.RamModules.Should().ContainSingle()
            .Which.Locator.Should().Be("DIMM A");
        payload.Machine.Gpus.Should().ContainSingle()
            .Which.Name.Should().Be("NVIDIA RTX 3050");
        payload.Machine.GraphicsAdapters.Should().BeEquivalentTo(new[] { "Intel Iris Xe Graphics", "NVIDIA RTX 3050" });
    }

    // ---- Build(ChecklistReport) — Storage / Battery / Tests / ManualChecklist ---

    [Fact]
    public void Build_ChecklistReport_MapsStorageTestsAndManualChecklist()
    {
        var executedAt = new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc);
        var storage = new List<StorageInfo>
        {
            new(Index: 0, CapacityGb: 512m, Type: StorageType.SSD_NVMe, SmartStatus: SmartStatus.OK,
                SmartFailingAttribute: null, TemperatureC: 35m, Model: "Samsung 970",
                LifePercentRemaining: 95, PowerOnHours: 1000, PowerOnCount: 50,
                DataWrittenTb: 1.5, DataReadTb: 2.0),
        };
        var tests = new Dictionary<string, TestResult>
        {
            ["ram"] = new TestResult("ram", AutoStatus.OK, "4/4 módulos OK", executedAt, ""),
        };
        var manual = new Dictionary<string, ManualCheckItem>
        {
            ["tela"] = new ManualCheckItem("tela", ManualStatus.OK, ""),
        };
        var report = CreateChecklistReport(storage: storage, tests: tests, manualChecklist: manual, hasNumericKeypad: true, hasTouchScreen: true);

        var payload = PayloadBuilder.Build(report);

        payload.Storage.Should().ContainSingle();
        payload.Storage[0].Type.Should().Be("SSD_NVMe");
        payload.Storage[0].SmartStatus.Should().Be("OK");
        payload.Storage[0].Model.Should().Be("Samsung 970");

        payload.Tests["ram"].Status.Should().Be("OK");
        payload.Tests["ram"].Details.Should().Be("4/4 módulos OK");
        payload.Tests["ram"].Comment.Should().BeNull(); // Comment vazio vira null

        payload.ManualChecklist["tela"].Status.Should().Be("OK");
        payload.ManualChecklist["tela"].Notes.Should().Be("");

        payload.Machine.HasNumericKeypad.Should().BeTrue();
        payload.Machine.HasTouchScreen.Should().BeTrue();
    }

    [Fact]
    public void Build_ChecklistReport_NullBattery_MapsToNull()
    {
        var report = CreateChecklistReport(battery: null);

        var payload = PayloadBuilder.Build(report);

        payload.Battery.Should().BeNull();
    }

    [Fact]
    public void Build_ChecklistReport_Battery_CurrentCapacityUsesFullChargeCapacity()
    {
        // Comportamento verificado na fonte: `CurrentCapacityMwh = b.FullChargeCapacityMwh`
        // — não usa RemainingCapacityMwh, apesar do nome sugerir "carga atual".
        var battery = new BatteryInfo(
            DesignCapacityMwh: 50000,
            FullChargeCapacityMwh: 45000,
            CycleCount: 120,
            ChargingStatus: "Descarregando",
            WearPercent: 10.0m,
            Name: "DELL ABC",
            Manufacturer: "SMP",
            SerialNumber: "SN1",
            Chemistry: "Lítio-íon",
            RemainingCapacityMwh: 30000,
            ChargePercent: 66,
            VoltageMv: 11500,
            RateMw: -5000,
            HealthPercent: 90.0m);
        var report = CreateChecklistReport(battery: battery);

        var payload = PayloadBuilder.Build(report);

        payload.Battery.Should().NotBeNull();
        payload.Battery!.CurrentCapacityMwh.Should().Be(45000);
        payload.Battery.FullChargeCapacityMwh.Should().Be(45000);
        payload.Battery.RemainingCapacityMwh.Should().Be(30000);
        payload.Battery.ChargingStatus.Should().Be("Descarregando");
    }

    [Fact]
    public void Build_ChecklistReport_NotesRequiringDescription_ItemWithoutComment_CommentIsNull()
    {
        var tests = new Dictionary<string, TestResult>
        {
            ["wifi"] = new TestResult("wifi", AutoStatus.Falha, "Sem sinal detectado", DateTime.UtcNow, "   "),
        };
        var report = CreateChecklistReport(tests: tests);

        var payload = PayloadBuilder.Build(report);

        payload.Tests["wifi"].Comment.Should().BeNull(); // comentário só com espaços também vira null
        payload.Tests["wifi"].Status.Should().Be("Falha");
    }

    // ---- Build(ChecklistReport) — Stress -----------------------------------------

    [Fact]
    public void Build_ChecklistReport_NullStress_MapsToNull()
    {
        var report = CreateChecklistReport(stress: null);

        var payload = PayloadBuilder.Build(report);

        payload.Stress.Should().BeNull();
    }

    [Fact]
    public void Build_ChecklistReport_Stress_MapsAllFields()
    {
        var stress = new StressSnapshot(
            FinalScore: 8000,
            CpuSingleThread: 1500,
            CpuMultiThread: 9000,
            CpuEfficiency: 500,
            CpuThreads: 12,
            GpuGraphics: 3000,
            GpuCompute: 2500,
            GpuBandwidth: 400,
            GpuName: "NVIDIA RTX 3050",
            GpuFeatureLevel: "12_1",
            DiskScore: 700,
            DiskReadMbPerSec: 3200.5,
            DiskWriteMbPerSec: 2800.25,
            VramOk: true,
            VramAllocatedMb: 4096,
            VramMismatchCount: 0,
            RamOk: true,
            RamAllocatedMb: 8192,
            RamErrorCount: 0,
            RamBandwidthGbs: 45.6,
            GeekbenchSingle: 2200,
            GeekbenchMulti: 9800,
            GeekbenchVersion: "6.3.0");
        var report = CreateChecklistReport(stress: stress);

        var payload = PayloadBuilder.Build(report);

        payload.Stress.Should().NotBeNull();
        payload.Stress!.FinalScore.Should().Be(8000);
        payload.Stress.GpuName.Should().Be("NVIDIA RTX 3050");
        payload.Stress.VramOk.Should().BeTrue();
        payload.Stress.GeekbenchVersion.Should().Be("6.3.0");
    }

    // ---- Build(ChecklistReport) — Inspection photos ------------------------------

    [Fact]
    public void Build_ChecklistReport_NullInspectionPhotos_MapsToEmptyList()
    {
        var report = CreateChecklistReport(inspectionPhotos: null);

        var payload = PayloadBuilder.Build(report);

        payload.InspectionPhotos.Should().BeEmpty();
    }

    [Fact]
    public void Build_ChecklistReport_InspectionPhotos_KnownKeyUsesCatalogLabel_UnknownKeyFallsBackToItemKey()
    {
        var capturedAt = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
        var photos = new List<InspectionPhoto>
        {
            new(ItemKey: "tela", ImageBase64: "base64-tela", Note: null, CapturedAt: capturedAt),
            new(ItemKey: "chave_desconhecida", ImageBase64: "base64-x", Note: "nota", CapturedAt: capturedAt),
        };
        var report = CreateChecklistReport(inspectionPhotos: photos);

        var payload = PayloadBuilder.Build(report);

        payload.InspectionPhotos.Should().HaveCount(2);
        payload.InspectionPhotos[0].Label.Should().Be("Tela"); // vem do InspectionCatalog
        payload.InspectionPhotos[0].CapturedAt.Should().Be(capturedAt.ToString("o", CultureInfo.InvariantCulture));
        payload.InspectionPhotos[1].Label.Should().Be("chave_desconhecida"); // fallback para o próprio ItemKey
        payload.InspectionPhotos[1].Note.Should().Be("nota");
    }

    // ---- Build(RetestReport) ------------------------------------------------------

    [Fact]
    public void Build_RetestReport_MapsBasicFieldsAndRetestedComponents()
    {
        var testId = Guid.NewGuid();
        var testedAt = new DateTime(2026, 4, 1, 14, 0, 0, DateTimeKind.Utc);
        var report = new RetestReport(
            TestId: testId,
            TestedAt: testedAt,
            TechnicianName: "Ciclana",
            Machine: CreateMachine(),
            RetestedComponents: new[] { ComponentId.Bateria, ComponentId.Wifi },
            RepairNotes: "Troca de bateria",
            Tests: new Dictionary<string, TestResult>(),
            FinalClassification: FinalClassification.AprovadoComRessalvas);

        var payload = PayloadBuilder.Build(report);

        payload.TestId.Should().Be(testId.ToString("D"));
        payload.ReportType.Should().Be("retest");
        payload.TestedAt.Should().Be(testedAt.ToString("o", CultureInfo.InvariantCulture));
        payload.RetestedComponents.Should().BeEquivalentTo(new[] { "bateria", "wifi" }, o => o.WithStrictOrdering());
        payload.RepairNotes.Should().Be("Troca de bateria");
        payload.Storage.Should().BeEmpty();
        payload.Battery.Should().BeNull();
        payload.ManualChecklist.Should().BeEmpty();
        payload.GeneralNotes.Should().Be("");
        payload.AssetTag.Should().Be("");
        payload.FinalClassification.Should().Be("Aprovado com ressalvas");
        payload.FinalClassificationOverrideReason.Should().BeNull();
    }

    [Fact]
    public void Build_RetestReport_RepairNotesLongerThan500Chars_IsTruncated()
    {
        var longNotes = new string('r', 600);
        var report = new RetestReport(
            TestId: Guid.NewGuid(),
            TestedAt: DateTime.UtcNow,
            TechnicianName: "Ciclana",
            Machine: CreateMachine(),
            RetestedComponents: Array.Empty<ComponentId>(),
            RepairNotes: longNotes,
            Tests: new Dictionary<string, TestResult>(),
            FinalClassification: FinalClassification.Aprovado);

        var payload = PayloadBuilder.Build(report);

        payload.RepairNotes.Should().HaveLength(500);
        payload.RepairNotes.Should().Be(longNotes.Substring(0, 500));
    }

    [Fact]
    public void Build_RetestReport_NullRepairNotes_MapsToEmptyString()
    {
        var report = new RetestReport(
            TestId: Guid.NewGuid(),
            TestedAt: DateTime.UtcNow,
            TechnicianName: "Ciclana",
            Machine: CreateMachine(),
            RetestedComponents: Array.Empty<ComponentId>(),
            RepairNotes: null!,
            Tests: new Dictionary<string, TestResult>(),
            FinalClassification: FinalClassification.Aprovado);

        var payload = PayloadBuilder.Build(report);

        payload.RepairNotes.Should().Be("");
    }

    // ---- Mapeamentos textuais auxiliares (públicos e estáticos) -------------------

    [Theory]
    [InlineData(AutoStatus.OK, "OK")]
    [InlineData(AutoStatus.Atencao, "Atenção")]
    [InlineData(AutoStatus.Falha, "Falha")]
    [InlineData(AutoStatus.NaoTestado, "Não testado")]
    [InlineData(AutoStatus.NaoAplicavel, "Não aplicável")]
    public void MapAuto_KnownStatuses_ReturnsExpectedText(AutoStatus status, string expected)
    {
        PayloadBuilder.MapAuto(status).Should().Be(expected);
    }

    [Fact]
    public void MapAuto_UnknownEnumValue_FallsBackToToString()
    {
        var invalid = (AutoStatus)999;

        PayloadBuilder.MapAuto(invalid).Should().Be("999");
    }

    [Theory]
    [InlineData(ManualStatus.OK, "OK")]
    [InlineData(ManualStatus.ComDefeito, "Com defeito")]
    [InlineData(ManualStatus.NaoTestado, "Não testado")]
    [InlineData(ManualStatus.Observacao, "Observação")]
    public void MapManual_KnownStatuses_ReturnsExpectedText(ManualStatus status, string expected)
    {
        PayloadBuilder.MapManual(status).Should().Be(expected);
    }

    [Fact]
    public void MapManual_UnknownEnumValue_FallsBackToToString()
    {
        var invalid = (ManualStatus)999;

        PayloadBuilder.MapManual(invalid).Should().Be("999");
    }

    [Theory]
    [InlineData(FinalClassification.Aprovado, "Aprovado")]
    [InlineData(FinalClassification.AprovadoComRessalvas, "Aprovado com ressalvas")]
    [InlineData(FinalClassification.Reprovado, "Reprovado")]
    public void MapFinal_KnownValues_ReturnsExpectedText(FinalClassification value, string expected)
    {
        PayloadBuilder.MapFinal(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(AvailabilityFlag.Registrado, "Provável")]
    [InlineData(AvailabilityFlag.NaoDeterminado, "Possível")]
    [InlineData(AvailabilityFlag.NaoRegistrado, "Improvável")]
    public void MapAutopilot_KnownFlags_ReturnsConfidenceText(AvailabilityFlag flag, string expected)
    {
        PayloadBuilder.MapAutopilot(flag).Should().Be(expected);
    }

    [Fact]
    public void MapAutopilot_FlagOutsideAutopilotDomain_FallsBackToIndisponivel()
    {
        // AvailabilityFlag.Presente é válido no enum, mas não é um dos três
        // valores tratados especificamente por MapAutopilot — cai no default.
        PayloadBuilder.MapAutopilot(AvailabilityFlag.Presente).Should().Be("Indisponível");
    }

    [Theory]
    [InlineData(AvailabilityFlag.Presente, "Presente")]
    [InlineData(AvailabilityFlag.Ausente, "Ausente")]
    [InlineData(AvailabilityFlag.Habilitado, "Habilitado")]
    [InlineData(AvailabilityFlag.Desabilitado, "Desabilitado")]
    [InlineData(AvailabilityFlag.Indisponivel, "Indisponível")]
    [InlineData(AvailabilityFlag.NaoPronto, "Não pronto")]
    [InlineData(AvailabilityFlag.Registrado, "Sim")]
    [InlineData(AvailabilityFlag.NaoRegistrado, "Não")]
    [InlineData(AvailabilityFlag.NaoDeterminado, "Indeterminado")]
    [InlineData(AvailabilityFlag.Ativado, "Ativado")]
    [InlineData(AvailabilityFlag.NaoAtivado, "Não ativado")]
    public void MapAvailability_AllKnownFlags_ReturnsExpectedText(AvailabilityFlag flag, string expected)
    {
        PayloadBuilder.MapAvailability(flag).Should().Be(expected);
    }

    [Fact]
    public void MapAvailability_UnknownEnumValue_FallsBackToToString()
    {
        var invalid = (AvailabilityFlag)999;

        PayloadBuilder.MapAvailability(invalid).Should().Be("999");
    }

    [Theory]
    [InlineData(KeyboardBacklight.Sim, "sim")]
    [InlineData(KeyboardBacklight.Nao, "nao")]
    [InlineData(KeyboardBacklight.Indisponivel, "indisponivel")]
    public void MapBacklight_KnownValues_ReturnsExpectedText(KeyboardBacklight value, string expected)
    {
        PayloadBuilder.MapBacklight(value).Should().Be(expected);
    }

    [Fact]
    public void MapBacklight_UnknownEnumValue_FallsBackToIndisponivelLiteral()
    {
        // Diferente de MapAuto/MapManual/MapAvailability: o default aqui é o
        // literal "indisponivel", não `value.ToString()`.
        var invalid = (KeyboardBacklight)999;

        PayloadBuilder.MapBacklight(invalid).Should().Be("indisponivel");
    }

    [Theory]
    [InlineData(AvailabilityFlag.Registrado, true, true)]
    [InlineData(AvailabilityFlag.Registrado, false, false)]
    [InlineData(AvailabilityFlag.NaoRegistrado, false, true)]
    [InlineData(AvailabilityFlag.NaoRegistrado, true, false)]
    public void AutopilotDetectionOk_ConclusiveDetection_ComparesWithTechnician(AvailabilityFlag auto, bool confirmed, bool expected)
    {
        PayloadBuilder.AutopilotDetectionOk(auto, confirmed).Should().Be(expected);
    }

    [Theory]
    [InlineData(AvailabilityFlag.NaoDeterminado)]
    [InlineData(AvailabilityFlag.Indisponivel)]
    public void AutopilotDetectionOk_InconclusiveDetection_IsNull(AvailabilityFlag auto)
    {
        PayloadBuilder.AutopilotDetectionOk(auto, true).Should().BeNull();
    }

    [Fact]
    public void AutopilotDetectionOk_NoAnswer_IsNull()
    {
        PayloadBuilder.AutopilotDetectionOk(AvailabilityFlag.Registrado, null).Should().BeNull();
    }

    [Fact]
    public void AppendAutopilotConfirmation_AppendsTechnicianVerdictToDetail()
    {
        PayloadBuilder.AppendAutopilotConfirmation("nenhum rastro local", AvailabilityFlag.NaoRegistrado, true)
            .Should().Be("nenhum rastro local • técnico confirmou: COM Autopilot (detecção ERROU)");
        PayloadBuilder.AppendAutopilotConfirmation("x", AvailabilityFlag.Registrado, true)
            .Should().Be("x • técnico confirmou: COM Autopilot (detecção acertou)");
        PayloadBuilder.AppendAutopilotConfirmation(null, AvailabilityFlag.NaoDeterminado, false)
            .Should().Be("técnico confirmou: SEM Autopilot");
        PayloadBuilder.AppendAutopilotConfirmation("x", AvailabilityFlag.Registrado, null).Should().Be("x");
    }
}
