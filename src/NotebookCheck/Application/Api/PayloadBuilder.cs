using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;
using NotebookCheck.Infrastructure.Api;

namespace NotebookCheck.Application.Api;

/// <summary>
/// Mapeia <see cref="ChecklistReport"/> e <see cref="RetestReport"/> para
/// <see cref="ApiPayload"/> com strings em PT-BR conforme Requirements 30.
/// </summary>
public static class PayloadBuilder
{
    public static ApiPayload Build(ChecklistReport r)
    {
        var machinePayload = MapMachine(r.Machine);
        machinePayload.HasNumericKeypad = r.HasNumericKeypad;
        machinePayload.HasTouchScreen = r.HasTouchScreen;
        return new ApiPayload
        {
            TestId = r.TestId.ToString("D"),
            ReportType = "full_checklist",
            TestedAt = r.TestedAt.ToString("o", CultureInfo.InvariantCulture),
            TechnicianName = r.TechnicianName,
            Machine = machinePayload,
            Storage = r.Storage.Select(MapStorage).ToList(),
            Battery = r.Battery is null ? null : MapBattery(r.Battery),
            Tests = r.Tests.ToDictionary(kvp => kvp.Key, kvp => MapTest(kvp.Value)),
            ManualChecklist = r.ManualChecklist.ToDictionary(kvp => kvp.Key, kvp => MapManual(kvp.Value)),
            RetestedComponents = new List<string>(),
            RepairNotes = "",
            GeneralNotes = r.GeneralNotes ?? "",
            AssetTag = r.AssetTag ?? "",
            FinalClassification = MapFinal(r.FinalClassification),
            FinalClassificationOverrideReason = r.FinalClassificationOverrideReason,
            ChecklistMode = MapMode(r),
            Stress = r.Stress is null ? null : new StressMetrics
            {
                FinalScore = r.Stress.FinalScore,
                CpuSingleThread = r.Stress.CpuSingleThread,
                CpuMultiThread = r.Stress.CpuMultiThread,
                CpuEfficiency = r.Stress.CpuEfficiency,
                CpuThreads = r.Stress.CpuThreads,
                GpuGraphics = r.Stress.GpuGraphics,
                GpuCompute = r.Stress.GpuCompute,
                GpuBandwidth = r.Stress.GpuBandwidth,
                GpuName = r.Stress.GpuName,
                GpuFeatureLevel = r.Stress.GpuFeatureLevel,
                DiskScore = r.Stress.DiskScore,
                DiskReadMbPerSec = r.Stress.DiskReadMbPerSec,
                DiskWriteMbPerSec = r.Stress.DiskWriteMbPerSec,
                VramOk = r.Stress.VramOk,
                VramAllocatedMb = r.Stress.VramAllocatedMb,
                VramMismatchCount = r.Stress.VramMismatchCount,
                RamOk = r.Stress.RamOk,
                RamAllocatedMb = r.Stress.RamAllocatedMb,
                RamErrorCount = r.Stress.RamErrorCount,
                RamBandwidthGbs = r.Stress.RamBandwidthGbs,
                GeekbenchSingle = r.Stress.GeekbenchSingle,
                GeekbenchMulti = r.Stress.GeekbenchMulti,
                GeekbenchVersion = r.Stress.GeekbenchVersion,
            },
            InspectionPhotos = (r.InspectionPhotos ?? new List<InspectionPhoto>())
                .Select(p => new InspectionPhotoPayload
                {
                    ItemKey = p.ItemKey,
                    Label = InspectionCatalog.Find(p.ItemKey)?.Label ?? p.ItemKey,
                    ImageBase64 = p.ImageBase64,
                    Note = p.Note,
                    CapturedAt = p.CapturedAt.ToString("o", CultureInfo.InvariantCulture),
                }).ToList(),
            InspectionSlug = r.InspectionSlug,
        };
    }

    public static ApiPayload Build(RetestReport r)
    {
        return new ApiPayload
        {
            TestId = r.TestId.ToString("D"),
            ReportType = "retest",
            TestedAt = r.TestedAt.ToString("o", CultureInfo.InvariantCulture),
            TechnicianName = r.TechnicianName,
            Machine = MapMachine(r.Machine),
            Storage = new List<StoragePayload>(),
            Battery = null,
            Tests = r.Tests.ToDictionary(kvp => kvp.Key, kvp => MapTest(kvp.Value)),
            ManualChecklist = new Dictionary<string, ManualChecklistPayload>(),
            RetestedComponents = r.RetestedComponents.Select(c => ComponentTestMap.GetTestKey(c)).ToList(),
            RepairNotes = (r.RepairNotes ?? "").Length > 500 ? r.RepairNotes!.Substring(0, 500) : (r.RepairNotes ?? ""),
            GeneralNotes = "",
            AssetTag = "",
            FinalClassification = MapFinal(r.FinalClassification),
            FinalClassificationOverrideReason = null,
        };
    }

    private static MachinePayload MapMachine(MachineInfo m) => new()
    {
        Manufacturer = m.Manufacturer,
        Model = m.Model,
        Serial = m.Serial,
        Hostname = m.Hostname,
        Cpu = m.Cpu,
        RamGb = m.RamGb,
        Os = m.Os,
        OsVersion = m.OsVersion,
        MacAddress = m.MacAddress,
        Tpm = MapAvailability(m.Tpm),
        TpmVersion = m.TpmVersion,
        SecureBoot = MapAvailability(m.SecureBoot),
        Autopilot = MapAutopilot(m.Autopilot),
        AutopilotDetail = AppendAutopilotConfirmation(m.AutopilotDetail, m.Autopilot, m.AutopilotConfirmed),
        AutopilotConfirmed = m.AutopilotConfirmed,
        AutopilotDetectionOk = AutopilotDetectionOk(m.Autopilot, m.AutopilotConfirmed),
        WindowsActivation = MapAvailability(m.WindowsActivation),
        ScreenResolution = m.ScreenResolution,
        GraphicsAdapter = m.GraphicsAdapter,
        GraphicsAdapters = m.GraphicsAdapters?.ToList() ?? new List<string>(),
        VideoOutputs = m.VideoOutputs is { Count: > 0 } vo ? vo.ToList() : null,
        WifiAdapterCount = m.WifiAdapterCount,
        EthernetAdapterCount = m.EthernetAdapterCount,
        BluetoothVersion = m.Bluetooth?.Version,
        BiosSetupPassword = m.BiosSecurity is null ? null : MapAvailability(m.BiosSecurity.HasSetupPassword),
        BiosPowerOnPassword = m.BiosSecurity is null ? null : MapAvailability(m.BiosSecurity.HasPowerOnPassword),
        BiosHddPassword = m.BiosSecurity is null ? null : MapAvailability(m.BiosSecurity.HasHddPassword),
        // registra o PORQUÊ quando não deu para ler — "indisponível" sozinho não
        // diz se falta administrador, falta a ferramenta do fabricante, ou nada disso
        BiosPasswordMotivo = m.BiosSecurity?.Motivo switch
        {
            BiosLeituraMotivo.SemPrivilegio => "sem_privilegio",
            BiosLeituraMotivo.FerramentaOemAusente => "ferramenta_oem_ausente",
            BiosLeituraMotivo.FabricanteSemSuporte => "fabricante_sem_suporte",
            _ => null,
        },
        ComputraceModule = m.Computrace is null ? null : MapAvailability(m.Computrace.ModuleActive),
        ComputraceAgent = m.Computrace is null ? null : MapAvailability(m.Computrace.AgentInstalled),
        ComputraceVersion = m.Computrace?.Version,
        NtbCode = m.NtbCode,
        Location = m.Location,
        KeyboardBacklight = MapBacklight(m.KeyboardBacklight),
        CpuCores = m.Processor?.Cores,
        CpuThreads = m.Processor?.Threads,
        CpuMaxClockMhz = m.Processor?.MaxClockMhz,
        RamType = m.Memory?.Type,
        RamSpeedMhz = m.Memory?.SpeedMhz,
        RamSlotsUsed = m.Memory?.SlotsUsed,
        RamSlotsTotal = m.Memory?.SlotsTotal,
        RamModules = m.Memory?.Modules.Select(mm => new MemoryModulePayload
        {
            Locator = mm.Locator,
            CapacityGb = mm.CapacityGb,
            SpeedMhz = mm.SpeedMhz,
            Manufacturer = mm.Manufacturer,
            PartNumber = mm.PartNumber,
            Type = mm.Type,
        }).ToList(),
        Gpus = m.GraphicsDetails?.Select(g => new GpuPayload
        {
            Name = g.Name,
            VramMb = g.VramMb,
            DriverVersion = g.DriverVersion,
            DriverDate = g.DriverDate,
        }).ToList(),
    };

    private static StoragePayload MapStorage(StorageInfo s) => new()
    {
        Index = s.Index,
        CapacityGb = s.CapacityGb,
        Type = s.Type.ToString(),
        SmartStatus = s.SmartStatus.ToString(),
        Model = s.Model,
        LifePercentRemaining = s.LifePercentRemaining,
        PowerOnHours = s.PowerOnHours,
        PowerOnCount = s.PowerOnCount,
        DataWrittenTb = s.DataWrittenTb,
        DataReadTb = s.DataReadTb,
        TemperatureC = s.TemperatureC,
        Firmware = s.Firmware,
        SerialNumber = s.SerialNumber,
        InterfaceType = s.Interface,
        DriveLetter = s.DriveLetter,
        HealthLabel = s.HealthLabel,
        SmartFailingAttribute = s.SmartFailingAttribute,
    };

    private static BatteryPayload MapBattery(BatteryInfo b) => new()
    {
        DesignCapacityMwh = b.DesignCapacityMwh,
        CurrentCapacityMwh = b.FullChargeCapacityMwh,
        FullChargeCapacityMwh = b.FullChargeCapacityMwh,
        RemainingCapacityMwh = b.RemainingCapacityMwh,
        ChargePercent = b.ChargePercent,
        CycleCount = b.CycleCount,
        ChargingStatus = b.ChargingStatus,
        WearPercent = b.WearPercent,
        HealthPercent = b.HealthPercent,
        Name = b.Name,
        Manufacturer = b.Manufacturer,
        SerialNumber = b.SerialNumber,
        Chemistry = b.Chemistry,
        VoltageMv = b.VoltageMv,
        RateMw = b.RateMw,
    };

    private static TestEntry MapTest(TestResult t) => new()
    {
        Status = MapAuto(t.Status),
        Details = t.Details ?? "",
        ExecutedAt = t.ExecutedAt.ToString("o", CultureInfo.InvariantCulture),
        Comment = string.IsNullOrWhiteSpace(t.Comment) ? null : t.Comment,
    };

    private static ManualChecklistPayload MapManual(ManualCheckItem m) => new()
    {
        Status = MapManual(m.Status),
        Notes = m.Notes ?? "",
    };

    public static string MapAuto(AutoStatus s) => s switch
    {
        AutoStatus.OK => "OK",
        AutoStatus.Atencao => "Atenção",
        AutoStatus.Falha => "Falha",
        AutoStatus.NaoTestado => "Não testado",
        AutoStatus.NaoAplicavel => "Não aplicável",
        _ => s.ToString(),
    };

    public static string MapManual(ManualStatus s) => s switch
    {
        ManualStatus.OK => "OK",
        ManualStatus.ComDefeito => "Com defeito",
        ManualStatus.NaoTestado => "Não testado",
        ManualStatus.Observacao => "Observação",
        _ => s.ToString(),
    };

    public static string MapFinal(FinalClassification c) => c switch
    {
        FinalClassification.Aprovado => "Aprovado",
        FinalClassification.AprovadoComRessalvas => "Aprovado com ressalvas",
        FinalClassification.Reprovado => "Reprovado",
        _ => c.ToString(),
    };

    /// <summary>
    /// Veredito do checker de Autopilot em texto: o checker trabalha com nível
    /// de confiança, não certeza — por isso "Provável/Possível/Improvável".
    /// </summary>
    public static string MapAutopilot(AvailabilityFlag f) => f switch
    {
        AvailabilityFlag.Registrado => "Provável",
        AvailabilityFlag.NaoDeterminado => "Possível",
        AvailabilityFlag.NaoRegistrado => "Improvável",
        _ => "Indisponível",
    };

    /// <summary>
    /// A detecção automática acertou? Só dá para julgar quando ela foi
    /// conclusiva (Provável ou Improvável) E o técnico respondeu.
    /// </summary>
    public static bool? AutopilotDetectionOk(AvailabilityFlag auto, bool? confirmed)
    {
        if (confirmed is not bool c) return null;
        return auto switch
        {
            AvailabilityFlag.Registrado => c,
            AvailabilityFlag.NaoRegistrado => !c,
            _ => null,
        };
    }

    /// <summary>
    /// Anexa a confirmação do técnico ao texto do detalhe, para o painel/ERP
    /// mostrarem a resposta sem precisar conhecer os campos novos.
    /// </summary>
    public static string? AppendAutopilotConfirmation(string? detail, AvailabilityFlag auto, bool? confirmed)
    {
        if (confirmed is not bool c) return detail;
        var ok = AutopilotDetectionOk(auto, confirmed);
        var suffix = $"técnico confirmou: {(c ? "COM Autopilot" : "SEM Autopilot")}"
                   + (ok is bool o ? (o ? " (detecção acertou)" : " (detecção ERROU)") : "");
        return string.IsNullOrWhiteSpace(detail) ? suffix : $"{detail} • {suffix}";
    }

    public static string MapAvailability(AvailabilityFlag f) => f switch
    {
        AvailabilityFlag.Presente => "Presente",
        AvailabilityFlag.Ausente => "Ausente",
        AvailabilityFlag.Habilitado => "Habilitado",
        AvailabilityFlag.Desabilitado => "Desabilitado",
        AvailabilityFlag.Indisponivel => "Indisponível",
        AvailabilityFlag.NaoPronto => "Não pronto",
        AvailabilityFlag.Registrado => "Sim",
        AvailabilityFlag.NaoRegistrado => "Não",
        AvailabilityFlag.NaoDeterminado => "Indeterminado",
        AvailabilityFlag.Ativado => "Ativado",
        AvailabilityFlag.NaoAtivado => "Não ativado",
        _ => f.ToString(),
    };

    public static string MapBacklight(KeyboardBacklight k) => k switch
    {
        KeyboardBacklight.Sim => "sim",
        KeyboardBacklight.Nao => "nao",
        KeyboardBacklight.Indisponivel => "indisponivel",
        _ => "indisponivel",
    };

    private static string MapMode(ChecklistReport r) => r.Mode switch
    {
        ChecklistMode.Basico => "basico",
        ChecklistMode.Padrao => "padrao",
        ChecklistMode.Detalhado => "detalhado",
        ChecklistMode.Desktop => "desktop",
        _ => "padrao",
    };
}
