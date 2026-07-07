using System.Collections.Generic;

namespace NotebookCheck.Infrastructure.Abstractions;

/// <summary>
/// Abstração sobre as P/Invokes <c>EnumDisplayMonitors</c> e
/// <c>EnumDisplayDevices</c> usadas pelo teste de HDMI (Requirement 12) e
/// como fallback para coleta de resolução / adaptador gráfico
/// (Requirements 7.4, 7.5).
/// </summary>
public interface IDisplayEnumerator
{
    /// <summary>
    /// Enumera todos os monitores conectados (incluindo o painel interno).
    /// A ordem segue o iterador retornado pela API nativa.
    /// </summary>
    IReadOnlyList<MonitorInfo> EnumerateMonitors();
}

/// <summary>Tipo de conexão de um monitor enumerado.</summary>
public enum MonitorKind
{
    /// <summary>Painel interno do notebook.</summary>
    Internal,
    /// <summary>Monitor externo conectado por HDMI/DP/USB-C/VGA.</summary>
    External,
    /// <summary>Não foi possível classificar.</summary>
    Unknown,
}

/// <summary>
/// Snapshot de um monitor identificado por <see cref="IDisplayEnumerator"/>.
/// </summary>
/// <param name="DeviceName">
/// Nome do dispositivo retornado pelo Windows (ex.: <c>\\.\\DISPLAY1</c>).
/// </param>
/// <param name="FriendlyName">Nome amigável quando disponível.</param>
/// <param name="WidthPixels">Largura efetiva em pixels.</param>
/// <param name="HeightPixels">Altura efetiva em pixels.</param>
/// <param name="IsPrimary">Indica se este é o monitor primário.</param>
/// <param name="Kind">Classificação interno/externo.</param>
public readonly record struct MonitorInfo(
    string DeviceName,
    string? FriendlyName,
    int WidthPixels,
    int HeightPixels,
    bool IsPrimary,
    MonitorKind Kind);
