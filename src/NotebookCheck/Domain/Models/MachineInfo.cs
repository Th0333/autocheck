using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Domain.Models;

/// <summary>
/// Identificação completa do equipamento coletada pelo Coletor_de_Hardware,
/// combinando dados de fabricante, sistema operacional, segurança e inputs manuais
/// do técnico (código NTB, localização e confirmação de teclado retroiluminado).
/// </summary>
public record MachineInfo(
    string? Manufacturer,
    string? Model,
    string? Serial,
    string Hostname,
    string? Cpu,
    decimal RamGb,
    string Os,
    string OsVersion,
    string? MacAddress,
    AvailabilityFlag Tpm,
    string? TpmVersion,
    AvailabilityFlag SecureBoot,
    AvailabilityFlag Autopilot,
    AvailabilityFlag WindowsActivation,
    string ScreenResolution,
    string? GraphicsAdapter,
    decimal? CpuTemperatureC,
    string NtbCode,
    string Location,
    KeyboardBacklight KeyboardBacklight,
    KeyboardBacklight KeyboardBacklightDetected,
    DateTime CollectedAt,
    /// <summary>Lista de todos os adaptadores gráficos (iGPU + dGPU + virtuais).</summary>
    IReadOnlyList<string>? GraphicsAdapters = null,
    /// <summary>Lista de adaptadores de rede físicos (Ethernet + Wi-Fi).</summary>
    IReadOnlyList<NetworkAdapterInfo>? NetworkAdapters = null,
    /// <summary>Quantidade de adaptadores Wi-Fi presentes (independente de status).</summary>
    int WifiAdapterCount = 0,
    /// <summary>Quantidade de adaptadores Ethernet presentes.</summary>
    int EthernetAdapterCount = 0,
    /// <summary>Informações do rádio Bluetooth.</summary>
    BluetoothInfo? Bluetooth = null,
    /// <summary>Estado das senhas configuradas no BIOS.</summary>
    BiosSecurity? BiosSecurity = null,
    /// <summary>Estado do agente Absolute (Computrace).</summary>
    ComputraceInfo? Computrace = null,
    /// <summary>
    /// Detalhe legível do checker de Autopilot (confiança, pontuação, tenant),
    /// ex.: "Confiança alta (85/100) • tenant contoso.com".
    /// </summary>
    string? AutopilotDetail = null,
    /// <summary>Evidências do checker de Autopilot (uma linha por achado).</summary>
    IReadOnlyList<string>? AutopilotEvidence = null,
    /// <summary>Detalhes do processador (núcleos, threads, clock).</summary>
    ProcessorInfo? Processor = null,
    /// <summary>Detalhes da memória RAM (tipo, velocidade, pentes).</summary>
    MemoryInfo? Memory = null,
    /// <summary>Detalhes dos adaptadores gráficos (VRAM, driver).</summary>
    IReadOnlyList<GraphicsInfo>? GraphicsDetails = null,
    /// <summary>
    /// "Brand name"/família comercial reportada pela máquina (SMBIOS
    /// SystemFamily, ex.: "ThinkPad", "Latitude"). Null quando o OEM não
    /// preenche ou deixa placeholder.
    /// </summary>
    string? Family = null);
