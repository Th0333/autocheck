using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Domain.Models;

/// <summary>
/// Informações coletadas para um disco físico individual, incluindo capacidade,
/// tipo de mídia, estado SMART, temperatura e métricas de saúde estilo
/// CrystalDiskInfo (vida útil, horas ligado, dados gravados) quando disponíveis.
/// </summary>
/// <param name="Index">Índice ordinal do disco na lista coletada (0-based).</param>
/// <param name="CapacityGb">Capacidade total do disco em GB.</param>
/// <param name="Type">Tipo de mídia classificado em HDD, SSD SATA, SSD NVMe ou Indisponível.</param>
/// <param name="SmartStatus">Estado SMART consolidado.</param>
/// <param name="SmartFailingAttribute">
/// Atributo SMART responsável por estados "Aviso" ou "Falha", quando reportado.
/// </param>
/// <param name="TemperatureC">Temperatura atual do disco em °C, quando sensor disponível.</param>
/// <param name="Model">Modelo/nome do dispositivo, quando disponível.</param>
/// <param name="LifePercentRemaining">
/// Vida útil restante em % (100 = novo, 0 = fim). Derivado de 100 − percentage_used
/// (NVMe) ou 100 − Wear (WMI). Null quando indisponível.
/// </param>
/// <param name="PowerOnHours">Horas totais ligado, quando disponível.</param>
/// <param name="PowerOnCount">Número de ciclos de power-on, quando disponível.</param>
/// <param name="DataWrittenTb">Total de dados gravados (TBW) em TB, quando disponível.</param>
/// <param name="DataReadTb">Total de dados lidos em TB, quando disponível.</param>
/// <param name="Firmware">Versão de firmware do dispositivo, quando reportada pelo CrystalDiskInfo.</param>
/// <param name="SerialNumber">Número de série do dispositivo, quando reportado pelo CrystalDiskInfo.</param>
/// <param name="Interface">Interface física (ex.: "NVM Express", "Serial ATA"), quando reportada pelo CrystalDiskInfo.</param>
/// <param name="DriveLetter">Letra(s) de unidade associada(s) ao disco, quando reportada pelo CrystalDiskInfo.</param>
/// <param name="HealthLabel">Rótulo textual bruto do CrystalDiskInfo ("Good"/"Caution"/"Bad"), antes da conversão para <see cref="Enums.SmartStatus"/>.</param>
public record StorageInfo(
    int Index,
    decimal CapacityGb,
    StorageType Type,
    SmartStatus SmartStatus,
    string? SmartFailingAttribute,
    decimal? TemperatureC,
    string? Model = null,
    int? LifePercentRemaining = null,
    long? PowerOnHours = null,
    long? PowerOnCount = null,
    double? DataWrittenTb = null,
    double? DataReadTb = null,
    string? Firmware = null,
    string? SerialNumber = null,
    string? Interface = null,
    string? DriveLetter = null,
    string? HealthLabel = null);
