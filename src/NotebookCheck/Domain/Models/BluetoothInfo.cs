namespace NotebookCheck.Domain.Models;

/// <summary>
/// Informações sobre o rádio Bluetooth detectado no equipamento.
/// </summary>
/// <param name="Present">Se há ao menos um adaptador Bluetooth presente.</param>
/// <param name="Name">Nome amigável do dispositivo Bluetooth principal.</param>
/// <param name="Status">Status reportado pelo Plug-and-Play (OK, Error, etc.).</param>
/// <param name="LmpVersion">
/// Valor numérico LMP retornado pelo driver (0..13). É o maior indicativo
/// confiável da geração do Bluetooth no Windows.
/// </param>
/// <param name="Version">
/// Versão Bluetooth derivada do LMP (ex.: "5.0", "4.2"). Vazia quando não
/// pôde ser determinada.
/// </param>
public record BluetoothInfo(
    bool Present,
    string? Name,
    string? Status,
    int? LmpVersion,
    string? Version);
