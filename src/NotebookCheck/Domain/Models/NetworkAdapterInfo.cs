namespace NotebookCheck.Domain.Models;

/// <summary>
/// Resumo de um adaptador de rede físico detectado pelo sistema operacional.
/// Inclui nome amigável, status operacional, endereço MAC, velocidade do link
/// e tipo de mídia para distinguir Ethernet de Wi-Fi.
/// </summary>
/// <param name="Name">Nome amigável (ex.: "Wi-Fi", "Ethernet 2").</param>
/// <param name="Description">Descrição do dispositivo (modelo do chip).</param>
/// <param name="MacAddress">Endereço MAC formatado, quando disponível.</param>
/// <param name="Status">Status operacional (Up, Down, Disabled, Disconnected).</param>
/// <param name="LinkSpeed">Velocidade do link em texto (ex.: "1 Gbps").</param>
/// <param name="Kind">Tipo classificado: Ethernet, WiFi ou Outros.</param>
public record NetworkAdapterInfo(
    string Name,
    string? Description,
    string? MacAddress,
    string Status,
    string? LinkSpeed,
    NetworkAdapterKind Kind);

public enum NetworkAdapterKind
{
    Outros = 0,
    Ethernet = 1,
    WiFi = 2,
    Bluetooth = 3,
}
