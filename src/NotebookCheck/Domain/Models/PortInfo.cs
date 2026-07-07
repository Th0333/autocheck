namespace NotebookCheck.Domain.Models;

/// <summary>
/// Tipo de porta/conector físico no notebook. Usado para agrupar e renderizar
/// ícones distintos na etapa "Inputs e portas".
/// </summary>
public enum PortType
{
    UsbA,
    UsbC,
    Thunderbolt,
    Hdmi,
    DisplayPort,
    AudioJack,
    Microphone,
    Rj45,
    SdCard,
    Webcam,
    WiFi,
    Bluetooth,
}

/// <summary>
/// Informação de uma porta/conector enumerada na etapa "Inputs e portas".
/// </summary>
/// <param name="Type">Tipo enumerado da porta.</param>
/// <param name="Label">Rótulo amigável.</param>
/// <param name="Symbol">Glyph/emoji para a UI.</param>
/// <param name="Total">
/// Quantidade total estimada — vem do <c>Win32_PortConnector</c> (SMBIOS)
/// quando disponível, senão usa heurísticas. <c>null</c> = não foi possível
/// determinar.
/// </param>
/// <param name="Active">
/// Quantidade detectada como "ativa" no momento da coleta (USB com
/// dispositivo plugado, monitor externo conectado, fone no jack, link UP no
/// RJ45, etc.).
/// </param>
/// <param name="Detail">Texto auxiliar livre.</param>
public record PortInfo(
    PortType Type,
    string Label,
    string Symbol,
    int? Total,
    int Active,
    string? Detail);
