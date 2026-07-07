namespace NotebookCheck.Domain.Models;

/// <summary>
/// Informações coletadas para a bateria do equipamento, com cálculo de desgaste
/// derivado pelas regras de domínio. Os campos ricos (nome, fabricante, química,
/// voltagem, taxa, carga atual) vêm da API nativa de bateria do Windows quando
/// disponível; capacidades e ciclos têm fallback para WMI/powercfg.
/// </summary>
/// <param name="DesignCapacityMwh">Capacidade de design em mWh, quando reportada.</param>
/// <param name="FullChargeCapacityMwh">
/// Capacidade MÁXIMA de carga atual em mWh (full-charge), já refletindo o
/// desgaste. NÃO é a carga do momento — para isso veja <paramref name="RemainingCapacityMwh"/>.
/// </param>
/// <param name="CycleCount">Número de ciclos no intervalo 0–10000, quando reportado.</param>
/// <param name="ChargingStatus">
/// Estado de carregamento textual (ex.: "Carregando", "Descarregando", "Cheia").
/// </param>
/// <param name="WearPercent">
/// Desgaste percentual calculado por <c>DomainRules.ComputeWear</c>, com duas
/// casas decimais. Pode ser nulo quando capacidades não estão disponíveis.
/// </param>
/// <param name="Name">Nome do device de bateria (ex.: "DELL ABC1234").</param>
/// <param name="Manufacturer">Fabricante da célula (ex.: "SMP", "LGC", "Sony").</param>
/// <param name="SerialNumber">Serial da bateria, quando exposto pelo firmware.</param>
/// <param name="Chemistry">Química da célula (ex.: "Lítio-íon").</param>
/// <param name="RemainingCapacityMwh">Carga atual em mWh (energia presente agora).</param>
/// <param name="ChargePercent">Percentual de carga atual (0–100).</param>
/// <param name="VoltageMv">Voltagem atual em milivolts.</param>
/// <param name="RateMw">
/// Taxa de carga/descarga em mW: positivo carregando, negativo descarregando.
/// </param>
/// <param name="HealthPercent">
/// Saúde da bateria (full-charge / design × 100), complemento do desgaste.
/// </param>
public record BatteryInfo(
    int? DesignCapacityMwh,
    int? FullChargeCapacityMwh,
    int? CycleCount,
    string ChargingStatus,
    decimal? WearPercent,
    string? Name = null,
    string? Manufacturer = null,
    string? SerialNumber = null,
    string? Chemistry = null,
    int? RemainingCapacityMwh = null,
    int? ChargePercent = null,
    int? VoltageMv = null,
    int? RateMw = null,
    decimal? HealthPercent = null);
