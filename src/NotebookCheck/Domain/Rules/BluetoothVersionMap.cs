namespace NotebookCheck.Domain.Rules;

/// <summary>
/// Mapeia a versão LMP (Link Manager Protocol) de um rádio Bluetooth para a
/// versão comercial correspondente. Fonte única de verdade — antes existiam
/// dois mapas divergentes (um no coletor de hardware, outro no motor de testes).
/// </summary>
public static class BluetoothVersionMap
{
    /// <summary>
    /// Retorna a versão comercial (ex.: "5.3") para um número LMP, ou <c>null</c>
    /// quando o valor é desconhecido/nulo.
    /// </summary>
    public static string? FromLmp(int? lmp) => lmp switch
    {
        0 => "1.0b",
        1 => "1.1",
        2 => "1.2",
        3 => "2.0 + EDR",
        4 => "2.1 + EDR",
        5 => "3.0 + HS",
        6 => "4.0",
        7 => "4.1",
        8 => "4.2",
        9 => "5.0",
        10 => "5.1",
        11 => "5.2",
        12 => "5.3",
        13 => "5.4",
        14 => "6.0",
        _ => null,
    };
}
