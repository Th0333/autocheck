using System.Collections.Generic;

namespace NotebookCheck.Domain.Models;

/// <summary>
/// Detalhes do processador além do nome: núcleos físicos, threads lógicas e
/// clock máximo. Campos nulos quando o WMI não reporta.
/// </summary>
/// <param name="Name">Nome comercial (ex.: "AMD Ryzen 7 7840HS").</param>
/// <param name="Cores">Núcleos físicos.</param>
/// <param name="Threads">Processadores lógicos (threads).</param>
/// <param name="MaxClockMhz">Clock máximo nominal em MHz.</param>
public record ProcessorInfo(
    string? Name,
    int? Cores,
    int? Threads,
    int? MaxClockMhz);

/// <summary>Um pente de memória físico instalado.</summary>
/// <param name="Locator">Slot físico (ex.: "DIMM A", "Controller0-ChannelA").</param>
/// <param name="CapacityGb">Capacidade do pente em GB.</param>
/// <param name="SpeedMhz">Velocidade efetiva configurada em MHz.</param>
/// <param name="Manufacturer">Fabricante (ex.: "Kingston", "Samsung").</param>
/// <param name="PartNumber">Part number do módulo.</param>
/// <param name="Type">Tipo (DDR4, DDR5, LPDDR5...).</param>
public record MemoryModule(
    string? Locator,
    decimal? CapacityGb,
    int? SpeedMhz,
    string? Manufacturer,
    string? PartNumber,
    string? Type);

/// <summary>
/// Detalhes da memória RAM além da quantidade total: tipo, velocidade, número
/// de pentes e slots, e a lista de módulos instalados.
/// </summary>
/// <param name="TotalGb">Total instalado (soma dos pentes) em GB.</param>
/// <param name="Type">Tipo predominante (DDR4, DDR5...).</param>
/// <param name="SpeedMhz">Velocidade efetiva (do pente mais lento) em MHz.</param>
/// <param name="SlotsUsed">Pentes instalados.</param>
/// <param name="SlotsTotal">Slots totais da placa-mãe.</param>
/// <param name="Modules">Detalhe por pente.</param>
public record MemoryInfo(
    decimal? TotalGb,
    string? Type,
    int? SpeedMhz,
    int? SlotsUsed,
    int? SlotsTotal,
    IReadOnlyList<MemoryModule> Modules);

/// <summary>
/// Detalhes de um adaptador gráfico: nome, VRAM dedicada e versão do driver.
/// </summary>
/// <param name="Name">Nome do adaptador (ex.: "NVIDIA GeForce RTX 5070").</param>
/// <param name="VramMb">VRAM dedicada em MB (lida do registro, sem o teto de 4 GB do WMI).</param>
/// <param name="DriverVersion">Versão do driver.</param>
/// <param name="DriverDate">Data do driver (yyyy-MM-dd) quando disponível.</param>
public record GraphicsInfo(
    string Name,
    int? VramMb,
    string? DriverVersion,
    string? DriverDate);
