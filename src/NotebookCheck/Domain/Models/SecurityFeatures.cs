using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Domain.Models;

/// <summary>
/// Agregado retornado por <c>IHardwareCollector.CollectSecurityAsync</c> contendo
/// presença e estado do TPM, Secure Boot e registro no Windows Autopilot, conforme
/// Requirements 3.1 a 3.5.
/// </summary>
/// <param name="Tpm">
/// Presença do chip TPM com um dos valores <see cref="AvailabilityFlag.Presente"/>,
/// <see cref="AvailabilityFlag.Ausente"/> ou <see cref="AvailabilityFlag.Indisponivel"/>.
/// </param>
/// <param name="TpmState">
/// Estado operacional do TPM quando presente: <see cref="AvailabilityFlag.Habilitado"/>,
/// <see cref="AvailabilityFlag.Desabilitado"/>, <see cref="AvailabilityFlag.NaoPronto"/>
/// ou <see cref="AvailabilityFlag.Indisponivel"/>.
/// </param>
/// <param name="TpmVersion">
/// Versão do TPM no formato "X.Y" (ex.: "1.2", "2.0") quando reportada pelo equipamento,
/// ou <c>null</c> quando indisponível.
/// </param>
/// <param name="SecureBoot">Estado do Secure Boot.</param>
/// <param name="Autopilot">Estado de registro no Windows Autopilot.</param>
/// <param name="UnavailabilityReason">
/// Indicação textual da causa quando algum dos campos é <see cref="AvailabilityFlag.Indisponivel"/>
/// (ex.: "permissão", "ausência do recurso" ou "tempo limite excedido"). Pode ser <c>null</c>
/// quando todos os campos foram coletados com sucesso.
/// </param>
public record SecurityFeatures(
    AvailabilityFlag Tpm,
    AvailabilityFlag TpmState,
    string? TpmVersion,
    AvailabilityFlag SecureBoot,
    AvailabilityFlag Autopilot,
    string? UnavailabilityReason,
    /// <summary>Resultado detalhado do checker de Autopilot (confiança, evidências).</summary>
    AutopilotStatus? AutopilotInfo = null);
