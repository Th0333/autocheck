namespace NotebookCheck.Infrastructure.Abstractions;

/// <summary>
/// Abstração sobre a P/Invoke <c>GetSystemPowerStatus</c> (kernel32) usada pelo
/// teste do carregador (Requirement 13). Encapsular a chamada nativa permite
/// que <c>RunChargerAsync</c> seja exercitado em testes unitários e PBT sem
/// depender de hardware físico.
/// </summary>
public interface IPowerStatusProvider
{
    /// <summary>
    /// Retorna o estado atual do sistema de energia. Em caso de falha da API
    /// nativa, a implementação SHALL devolver <see cref="PowerStatus.Unknown"/>.
    /// </summary>
    PowerStatus GetStatus();
}

/// <summary>
/// Estado consolidado do sistema de energia retornado pela
/// <c>GetSystemPowerStatus</c>. Reflete os campos relevantes do struct
/// <c>SYSTEM_POWER_STATUS</c> mantendo apenas o que o teste do carregador
/// precisa avaliar.
/// </summary>
/// <param name="AcLine">Estado da linha AC (carregador conectado ou não).</param>
/// <param name="HasBattery">Indica se há bateria instalada e detectada.</param>
/// <param name="BatteryChargePercent">
/// Percentual de carga (0–100) ou <c>null</c> quando indisponível.
/// </param>
/// <param name="BatteryFlags">Flags adicionais reportados pelo Windows.</param>
public readonly record struct PowerStatus(
    AcLineStatus AcLine,
    bool HasBattery,
    int? BatteryChargePercent,
    BatteryFlags BatteryFlags)
{
    /// <summary>Valor padrão usado quando a P/Invoke falha.</summary>
    public static PowerStatus Unknown { get; } = new(AcLineStatus.Unknown, false, null, BatteryFlags.Unknown);
}

/// <summary>
/// Estado da linha AC reportado por <c>SYSTEM_POWER_STATUS.ACLineStatus</c>.
/// </summary>
public enum AcLineStatus : byte
{
    /// <summary>Notebook desligado da tomada (somente bateria).</summary>
    Offline = 0,
    /// <summary>Carregador conectado.</summary>
    Online = 1,
    /// <summary>Estado desconhecido / API falhou.</summary>
    Unknown = 255,
}

/// <summary>
/// Flags adicionais de bateria reportados por
/// <c>SYSTEM_POWER_STATUS.BatteryFlag</c>.
/// </summary>
[Flags]
public enum BatteryFlags : byte
{
    /// <summary>Sem flags identificadas.</summary>
    None = 0,
    /// <summary>Carga acima de 66%.</summary>
    High = 1,
    /// <summary>Carga abaixo de 33%.</summary>
    Low = 2,
    /// <summary>Carga crítica (abaixo de 5%).</summary>
    Critical = 4,
    /// <summary>Bateria carregando.</summary>
    Charging = 8,
    /// <summary>Sem bateria instalada.</summary>
    NoBattery = 128,
    /// <summary>API falhou ou estado desconhecido.</summary>
    Unknown = 255,
}
