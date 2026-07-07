using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NotebookCheck.Infrastructure.Api;

/// <summary>
/// Payload completo enviado ao endpoint REST do checklist técnico.
/// </summary>
public sealed class ApiPayload
{
    [JsonPropertyName("test_id")] public string TestId { get; init; } = "";
    [JsonPropertyName("report_type")] public string ReportType { get; init; } = "full_checklist";
    [JsonPropertyName("tested_at")] public string TestedAt { get; init; } = "";
    [JsonPropertyName("technician_name")] public string TechnicianName { get; init; } = "";
    [JsonPropertyName("machine")] public MachinePayload Machine { get; init; } = new();
    [JsonPropertyName("storage")] public List<StoragePayload> Storage { get; init; } = new();
    [JsonPropertyName("battery")] public BatteryPayload? Battery { get; init; }
    [JsonPropertyName("tests")] public Dictionary<string, TestEntry> Tests { get; init; } = new();
    [JsonPropertyName("manual_checklist")] public Dictionary<string, ManualChecklistPayload> ManualChecklist { get; init; } = new();
    [JsonPropertyName("retested_components")] public List<string> RetestedComponents { get; init; } = new();
    [JsonPropertyName("repair_notes")] public string RepairNotes { get; init; } = "";
    [JsonPropertyName("general_notes")] public string GeneralNotes { get; init; } = "";
    [JsonPropertyName("asset_tag")] public string AssetTag { get; init; } = "";
    [JsonPropertyName("final_classification")] public string FinalClassification { get; init; } = "";
    [JsonPropertyName("final_classification_override_reason")] public string? FinalClassificationOverrideReason { get; init; }
    [JsonPropertyName("checklist_mode")] public string? ChecklistMode { get; init; }
    [JsonPropertyName("stress")] public StressMetrics? Stress { get; init; }
    [JsonPropertyName("inspection_photos")] public List<InspectionPhotoPayload> InspectionPhotos { get; init; } = new();
    [JsonPropertyName("inspection_slug")] public string? InspectionSlug { get; init; }
}

/// <summary>
/// Foto de inspeção física capturada via celular. A imagem viaja como JPEG
/// base64 (sem prefixo data URI), vinculada ao item e ao serial da máquina.
/// </summary>
public sealed class InspectionPhotoPayload
{
    [JsonPropertyName("item_key")] public string ItemKey { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("image_base64")] public string ImageBase64 { get; init; } = "";
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("captured_at")] public string CapturedAt { get; init; } = "";
}

/// <summary>
/// Métricas detalhadas da nova benchmark suite enviadas junto do relatório
/// para permitir ranking de máquinas com mesmo CPU/GPU no painel.
/// </summary>
public sealed class StressMetrics
{
    [JsonPropertyName("final_score")] public int FinalScore { get; init; }

    // CPU (4 scores)
    [JsonPropertyName("cpu_single_thread")] public int CpuSingleThread { get; init; }
    [JsonPropertyName("cpu_multi_thread")] public int CpuMultiThread { get; init; }
    [JsonPropertyName("cpu_efficiency")] public int CpuEfficiency { get; init; }
    [JsonPropertyName("cpu_threads")] public int CpuThreads { get; init; }

    // GPU (3 scores + nome)
    [JsonPropertyName("gpu_graphics")] public int GpuGraphics { get; init; }
    [JsonPropertyName("gpu_compute")] public int GpuCompute { get; init; }
    [JsonPropertyName("gpu_bandwidth")] public int GpuBandwidth { get; init; }
    [JsonPropertyName("gpu_name")] public string? GpuName { get; init; }
    [JsonPropertyName("gpu_feature_level")] public string? GpuFeatureLevel { get; init; }

    // Disco
    [JsonPropertyName("disk_score")] public int DiskScore { get; init; }
    [JsonPropertyName("disk_read_mb_s")] public double DiskReadMbPerSec { get; init; }
    [JsonPropertyName("disk_write_mb_s")] public double DiskWriteMbPerSec { get; init; }

    // VRAM
    [JsonPropertyName("vram_ok")] public bool VramOk { get; init; }
    [JsonPropertyName("vram_allocated_mb")] public int VramAllocatedMb { get; init; }
    [JsonPropertyName("vram_mismatch_count")] public long VramMismatchCount { get; init; }

    // RAM
    [JsonPropertyName("ram_ok")] public bool RamOk { get; init; }
    [JsonPropertyName("ram_allocated_mb")] public int RamAllocatedMb { get; init; }
    [JsonPropertyName("ram_error_count")] public long RamErrorCount { get; init; }
    [JsonPropertyName("ram_bandwidth_gbs")] public double RamBandwidthGbs { get; init; }

    // Geekbench 6 (opcional)
    [JsonPropertyName("geekbench_single")] public int GeekbenchSingle { get; init; }
    [JsonPropertyName("geekbench_multi")] public int GeekbenchMulti { get; init; }
    [JsonPropertyName("geekbench_version")] public string? GeekbenchVersion { get; init; }
}

public sealed class MachinePayload
{
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("serial")] public string? Serial { get; init; }
    [JsonPropertyName("hostname")] public string Hostname { get; init; } = "";
    [JsonPropertyName("cpu")] public string? Cpu { get; init; }
    [JsonPropertyName("ram_gb")] public decimal RamGb { get; init; }
    [JsonPropertyName("os")] public string Os { get; init; } = "";
    [JsonPropertyName("os_version")] public string OsVersion { get; init; } = "";
    [JsonPropertyName("mac_address")] public string? MacAddress { get; init; }
    [JsonPropertyName("tpm")] public string Tpm { get; init; } = "";
    [JsonPropertyName("tpm_version")] public string? TpmVersion { get; init; }
    [JsonPropertyName("secure_boot")] public string SecureBoot { get; init; } = "";
    [JsonPropertyName("autopilot")] public string Autopilot { get; init; } = "";
    [JsonPropertyName("autopilot_detail")] public string? AutopilotDetail { get; init; }
    [JsonPropertyName("windows_activation")] public string WindowsActivation { get; init; } = "";
    [JsonPropertyName("screen_resolution")] public string ScreenResolution { get; init; } = "";
    [JsonPropertyName("graphics_adapter")] public string? GraphicsAdapter { get; init; }
    [JsonPropertyName("graphics_adapters")] public List<string> GraphicsAdapters { get; init; } = new();
    [JsonPropertyName("network_adapters_wifi")] public int WifiAdapterCount { get; init; }
    [JsonPropertyName("network_adapters_ethernet")] public int EthernetAdapterCount { get; init; }
    [JsonPropertyName("bluetooth_version")] public string? BluetoothVersion { get; init; }
    [JsonPropertyName("bios_setup_password")] public string? BiosSetupPassword { get; init; }
    [JsonPropertyName("bios_power_on_password")] public string? BiosPowerOnPassword { get; init; }
    [JsonPropertyName("bios_hdd_password")] public string? BiosHddPassword { get; init; }
    [JsonPropertyName("computrace_module")] public string? ComputraceModule { get; init; }
    [JsonPropertyName("computrace_agent")] public string? ComputraceAgent { get; init; }
    [JsonPropertyName("computrace_version")] public string? ComputraceVersion { get; init; }
    [JsonPropertyName("ntb_code")] public string NtbCode { get; init; } = "";
    [JsonPropertyName("location")] public string Location { get; init; } = "";
    [JsonPropertyName("keyboard_backlight")] public string KeyboardBacklight { get; init; } = "";
    [JsonPropertyName("has_numeric_keypad")] public bool? HasNumericKeypad { get; set; }

    // Detalhes de CPU / RAM / GPU (v1.3.4+).
    [JsonPropertyName("cpu_cores")] public int? CpuCores { get; init; }
    [JsonPropertyName("cpu_threads")] public int? CpuThreads { get; init; }
    [JsonPropertyName("cpu_max_clock_mhz")] public int? CpuMaxClockMhz { get; init; }
    [JsonPropertyName("ram_type")] public string? RamType { get; init; }
    [JsonPropertyName("ram_speed_mhz")] public int? RamSpeedMhz { get; init; }
    [JsonPropertyName("ram_slots_used")] public int? RamSlotsUsed { get; init; }
    [JsonPropertyName("ram_slots_total")] public int? RamSlotsTotal { get; init; }
    [JsonPropertyName("ram_modules")] public List<MemoryModulePayload>? RamModules { get; init; }
    [JsonPropertyName("gpus")] public List<GpuPayload>? Gpus { get; init; }
}

public sealed class MemoryModulePayload
{
    [JsonPropertyName("locator")] public string? Locator { get; init; }
    [JsonPropertyName("capacity_gb")] public decimal? CapacityGb { get; init; }
    [JsonPropertyName("speed_mhz")] public int? SpeedMhz { get; init; }
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; init; }
    [JsonPropertyName("part_number")] public string? PartNumber { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
}

public sealed class GpuPayload
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("vram_mb")] public int? VramMb { get; init; }
    [JsonPropertyName("driver_version")] public string? DriverVersion { get; init; }
    [JsonPropertyName("driver_date")] public string? DriverDate { get; init; }
}

public sealed class StoragePayload
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("capacity_gb")] public decimal CapacityGb { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("smart_status")] public string SmartStatus { get; init; } = "";
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("life_percent_remaining")] public int? LifePercentRemaining { get; init; }
    [JsonPropertyName("power_on_hours")] public long? PowerOnHours { get; init; }
    [JsonPropertyName("power_on_count")] public long? PowerOnCount { get; init; }
    [JsonPropertyName("data_written_tb")] public double? DataWrittenTb { get; init; }
    [JsonPropertyName("data_read_tb")] public double? DataReadTb { get; init; }
    [JsonPropertyName("temperature_c")] public decimal? TemperatureC { get; init; }
    // Campos adicionais extraídos do relatório do CrystalDiskInfo (v1.x+):
    // já coletados pelo CrystalDiskInfoRunner, apenas não trafegavam até a API.
    [JsonPropertyName("firmware")] public string? Firmware { get; init; }
    [JsonPropertyName("serial_number")] public string? SerialNumber { get; init; }
    [JsonPropertyName("interface_type")] public string? InterfaceType { get; init; }
    [JsonPropertyName("drive_letter")] public string? DriveLetter { get; init; }
    [JsonPropertyName("health_label")] public string? HealthLabel { get; init; }
    [JsonPropertyName("smart_failing_attribute")] public string? SmartFailingAttribute { get; init; }
}

public sealed class BatteryPayload
{
    [JsonPropertyName("design_capacity_mwh")] public int? DesignCapacityMwh { get; init; }
    // Mantém o nome histórico: sempre carregou a capacidade MÁXIMA (full charge).
    [JsonPropertyName("current_capacity_mwh")] public int? CurrentCapacityMwh { get; init; }
    [JsonPropertyName("full_charge_capacity_mwh")] public int? FullChargeCapacityMwh { get; init; }
    [JsonPropertyName("remaining_capacity_mwh")] public int? RemainingCapacityMwh { get; init; }
    [JsonPropertyName("charge_percent")] public int? ChargePercent { get; init; }
    [JsonPropertyName("cycle_count")] public int? CycleCount { get; init; }
    [JsonPropertyName("charging_status")] public string ChargingStatus { get; init; } = "";
    [JsonPropertyName("wear_percent")] public decimal? WearPercent { get; init; }
    [JsonPropertyName("health_percent")] public decimal? HealthPercent { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; init; }
    [JsonPropertyName("serial_number")] public string? SerialNumber { get; init; }
    [JsonPropertyName("chemistry")] public string? Chemistry { get; init; }
    [JsonPropertyName("voltage_mv")] public int? VoltageMv { get; init; }
    [JsonPropertyName("rate_mw")] public int? RateMw { get; init; }
}

public sealed class TestEntry
{
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("details")] public string Details { get; init; } = "";
    [JsonPropertyName("executed_at")] public string ExecutedAt { get; init; } = "";
    /// <summary>Comentário opcional do técnico (botão "+" na grade).</summary>
    [JsonPropertyName("comment")] public string? Comment { get; init; }
}

public sealed class ManualChecklistPayload
{
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("notes")] public string Notes { get; init; } = "";
}
