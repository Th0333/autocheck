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
    string? Source,
    BiosLeituraMotivo Motivo = BiosLeituraMotivo.Lido);

/// <summary>
/// Por que a leitura de senha de BIOS não veio — a diferença importa, porque
/// cada caso tem uma saída diferente para o técnico.
///
/// A versão anterior tratava tudo como "indisponível" e dizia que a leitura só
/// funcionava em HP/Dell/Lenovo. Não era verdade: o motivo mais comum é o app
/// estar rodando SEM privilégio de administrador — as classes WMI do fabricante
/// existem, mas negam acesso a usuário comum. Como o manifesto pede
/// <c>highestAvailable</c> (e não <c>requireAdministrator</c>), em conta de
/// usuário padrão o app abre normalmente e falha calado em toda máquina,
/// inclusive nas Dell.
/// </summary>
public enum BiosLeituraMotivo
{
    /// <summary>Leitura feita com sucesso.</summary>
    Lido,
    /// <summary>Rodando sem elevação: o WMI do fabricante negou acesso.</summary>
    SemPrivilegio,
    /// <summary>Fabricante conhecido, mas a ferramenta de gestão não está instalada.</summary>
    FerramentaOemAusente,
    /// <summary>
    /// É uma Dell e falta o módulo <c>DellBIOSProvider</c> — este caso tem
    /// conserto de um clique: o app instala o módulo do PSGallery e testa de novo.
    /// </summary>
    DellSemProvider,
    /// <summary>Fabricante sem interface WMI de senha de BIOS.</summary>
    FabricanteSemSuporte,
}

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
