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

    /// <summary>
    /// Saídas de vídeo EM USO (com monitor ligado): conector (HDMI, DisplayPort,
    /// DVI, VGA, painel interno), nome do monitor e resolução. O Windows não
    /// enumera porta vazia — só o que tem monitor do outro lado.
    /// </summary>
    IReadOnlyList<VideoOutputInfo> EnumerateOutputs();
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

/// <summary>
/// Uma saída de vídeo ativa, vista por <c>QueryDisplayConfig</c>.
/// </summary>
/// <param name="Connector">Rótulo do conector: "HDMI", "DisplayPort", "DVI", "VGA", "Painel interno"...</param>
/// <param name="MonitorName">Nome amigável do monitor (EDID), ou "Monitor".</param>
/// <param name="Width">Largura do modo ativo em pixels (0 = desconhecida).</param>
/// <param name="Height">Altura do modo ativo em pixels (0 = desconhecida).</param>
/// <param name="IsInternal">True para painel de notebook (LVDS/eDP/INTERNAL).</param>
/// <param name="GdiDeviceName">Nome GDI (<c>\\.\DISPLAY1</c>) para cruzar com <see cref="MonitorInfo"/>.</param>
public readonly record struct VideoOutputInfo(
    string Connector,
    string MonitorName,
    int Width,
    int Height,
    bool IsInternal,
    string GdiDeviceName)
{
    /// <summary>Texto para relatório/UI, ex.: "HDMI — LG ULTRAGEAR 1920x1080".</summary>
    public string Describe()
    {
        var res = Width > 0 && Height > 0 ? $" {Width}x{Height}" : "";
        return IsInternal ? $"Painel interno{res}" : $"{Connector} — {MonitorName}{res}";
    }
}
