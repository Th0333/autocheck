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
