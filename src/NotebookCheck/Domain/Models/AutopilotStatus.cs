using System.Collections.Generic;

namespace NotebookCheck.Domain.Models;

/// <summary>
/// Resultado da verificação de Windows Autopilot/MDM. Em vez de afirmar
/// certeza absoluta (impossível localmente — o registro no Autopilot vive no
/// tenant, do lado do servidor), o checker agrega evidências de múltiplas
/// fontes independentes e devolve um nível de confiança:
///   • High   (70–100 pontos) — evidências diretas de MDM/Autopilot
///   • Medium (30–69 pontos)  — sinais fortes (ex.: Entra ID joined)
///   • Low    (0–29 pontos)   — nenhuma evidência relevante
/// </summary>
public class AutopilotStatus
{
    /// <summary>True quando a pontuação indica provável Autopilot/MDM (≥ 30).</summary>
    public bool IsLikelyAutopilot { get; set; }

    /// <summary>"High", "Medium" ou "Low".</summary>
    public string Confidence { get; set; } = "Low";

    /// <summary>Pontuação 0–100 que originou a confiança.</summary>
    public int Score { get; set; }

    public bool AzureAdJoined { get; set; }
    public bool DomainJoined { get; set; }
    public bool WorkplaceJoined { get; set; }

    /// <summary>True se o log de eventos do Autopilot tem registros.</summary>
    public bool AutopilotEventsFound { get; set; }

    /// <summary>
    /// True quando o ARQUIVO de perfil do Autopilot foi encontrado no disco
    /// (AutopilotDDSZTDFile.json / AutopilotConfigurationFile.json) — método
    /// que funciona em PCs mais antigos provisionados, onde o arquivo do
    /// perfil baixado durante o OOBE ainda existe na máquina.
    /// </summary>
    public bool ProfileFileFound { get; set; }

    public string TenantName { get; set; } = "";
    public string TenantId { get; set; } = "";

    /// <summary>
    /// Id de registro devolvido pelo serviço ZTD da Microsoft e gravado pelo
    /// OOBE em EstablishedCorrelations. Só existe quando o serviço reconheceu o
    /// hardware hash — é o rastro local mais direto de registro no Autopilot.
    /// </summary>
    public string ZtdRegistrationId { get; set; } = "";

    /// <summary>True quando pelo menos uma fonte trouxe evidência DIRETA.</summary>
    public bool DirectEvidence { get; set; }

    /// <summary>
    /// True quando o OOBE consultou o serviço Autopilot da Microsoft e recebeu
    /// "sem perfil" (AutopilotPolicyCache com ProfileAvailable = 0): naquela
    /// data o hardware não estava registrado em tenant nenhum. É o único
    /// sinal NEGATIVO com data que a máquina guarda.
    /// </summary>
    public bool ServiceReturnedNoProfile { get; set; }

    /// <summary>Quando o serviço foi consultado (UTC), se o cache guardou a data.</summary>
    public DateTime? ServiceQueriedAt { get; set; }

    /// <summary>Quantas linhas de <see cref="Details"/> são evidência direta.</summary>
    public int DirectEvidenceCount { get; set; }

    /// <summary>
    /// Evidências encontradas (uma linha por achado), incluindo falhas de
    /// permissão — úteis para o técnico entender o veredito.
    /// </summary>
    public List<string> Details { get; set; } = new();

    /// <summary>
    /// True quando pelo menos uma fonte pôde ser consultada. False = todas as
    /// fontes falharam (sem permissão/erro) e o resultado é indeterminado.
    /// </summary>
    public bool AnySourceAvailable { get; set; }
}
