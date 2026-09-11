namespace NotebookCheck.Domain.Models;

/// <summary>
/// Informações sobre os dispositivos de exibição do equipamento, retornadas
/// por <c>IHardwareCollector.CollectDisplayAsync</c>.
/// </summary>
/// <param name="Resolution">
/// Resolução atual do display primário no formato <c>"{w}x{h}"</c> ou
/// <c>"Indisponível"</c> quando não pôde ser determinada.
/// </param>
/// <param name="GraphicsAdapter">
/// Modelo do adaptador gráfico ativo, ou <c>null</c> quando indisponível.
/// Mantido para compatibilidade com o payload existente; quando há múltiplos
/// adaptadores, traz o primeiro detectado.
/// </param>
/// <param name="GraphicsAdapters">
/// Lista de todos os adaptadores gráficos detectados (CPU integrada + dGPU,
/// adaptadores virtuais, etc.). Vazia quando indisponível.
/// </param>
/// <param name="ConnectedMonitors">
/// Lista imutável dos identificadores dos monitores físicos detectados via
/// <c>EnumDisplayMonitors</c> / <c>EnumDisplayDevices</c>; vazia quando apenas
/// o display interno está conectado.
/// </param>
public record DisplayInfo(
    string Resolution,
    string? GraphicsAdapter,
    IReadOnlyList<string> ConnectedMonitors,
    IReadOnlyList<string>? GraphicsAdapters = null,
    /// <summary>Detalhes por adaptador (VRAM dedicada e versão do driver).</summary>
    IReadOnlyList<GraphicsInfo>? GraphicsDetails = null,
    /// <summary>
    /// Saídas de vídeo em uso, uma linha por monitor ligado, com o conector
    /// (ex.: "HDMI — LG ULTRAGEAR 1920x1080", "Painel interno 1920x1080").
    /// </summary>
    IReadOnlyList<string>? VideoOutputs = null);
