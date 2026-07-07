using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Domain.Models;

/// <summary>
/// Estado das proteções de BIOS / firmware do equipamento.
/// </summary>
/// <param name="HasSetupPassword">
/// Indica se a senha de Setup do BIOS está configurada.
/// </param>
/// <param name="HasPowerOnPassword">
/// Indica se a senha de boot (Power-On Password) está configurada.
/// </param>
/// <param name="HasHddPassword">
/// Indica se há senha configurada no(s) disco(s) — quando reportado pela BIOS.
/// </param>
/// <param name="Source">
/// De onde a informação foi extraída (HP_BIOSSetting, DCIM_BIOSPassword,
/// Lenovo_BiosPasswordSettings, ou "indisponível").
/// </param>
public record BiosSecurity(
    AvailabilityFlag HasSetupPassword,
    AvailabilityFlag HasPowerOnPassword,
    AvailabilityFlag HasHddPassword,
    string? Source);

/// <summary>
/// Estado do agente Absolute (anteriormente Computrace) — sistema
/// anti-furto persistente em firmware da BIOS.
/// </summary>
/// <param name="ModuleActive">
/// Indica se o módulo Absolute na BIOS está ativo (chamando "phone home").
/// </param>
/// <param name="AgentInstalled">
/// Indica se o agente Windows está presente como serviço (rpcnetp).
/// </param>
/// <param name="ServiceStatus">
/// Status do serviço Windows quando presente (Running, Stopped, etc.).
/// </param>
/// <param name="Version">Versão do agente reportada pelo registro, quando disponível.</param>
public record ComputraceInfo(
    AvailabilityFlag ModuleActive,
    AvailabilityFlag AgentInstalled,
    string? ServiceStatus,
    string? Version);
