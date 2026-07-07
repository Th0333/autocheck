using System;

namespace NotebookCheck.Infrastructure.Abstractions;

/// <summary>
/// Observador de eventos USB em tempo real baseado em <c>WM_DEVICECHANGE</c>
/// (<c>DBT_DEVICEARRIVAL</c> e <c>DBT_DEVICEREMOVECOMPLETE</c>) registrados
/// via <c>RegisterDeviceNotification</c>. A View <c>UsbView</c> consome os
/// eventos para popular um <c>ObservableCollection&lt;UsbDeviceEvent&gt;</c>
/// (Requirement 11).
/// </summary>
public interface IUsbWatcher : IDisposable
{
    /// <summary>
    /// Disparado sempre que um dispositivo USB é conectado ou desconectado da
    /// máquina. Os assinantes recebem o evento na thread da janela WPF
    /// proprietária do hook (a implementação SHALL fazer marshaling
    /// adequado).
    /// </summary>
    event EventHandler<UsbDeviceEvent>? DeviceChanged;

    /// <summary>
    /// Inicia a escuta acoplando-se ao loop de mensagens da janela WPF
    /// indicada pelo <paramref name="windowHandle"/> (HWND obtido via
    /// <c>HwndSource</c>). Ao chamar <see cref="IDisposable.Dispose"/> a
    /// inscrição SHALL ser desfeita.
    /// </summary>
    void Start(IntPtr windowHandle);

    /// <summary>Encerra a escuta sem destruir o objeto.</summary>
    void Stop();
}

/// <summary>Tipo de mudança reportado por <see cref="IUsbWatcher"/>.</summary>
public enum UsbDeviceEventKind
{
    /// <summary>Dispositivo recém-conectado.</summary>
    Arrival,
    /// <summary>Dispositivo desconectado.</summary>
    Removal,
}

/// <summary>
/// Snapshot imutável de um dispositivo USB observado por
/// <see cref="IUsbWatcher"/>. Os campos de identificação são best-effort: o
/// Windows nem sempre expõe nome amigável imediatamente após o arrival, então
/// implementações SHOULD enriquecer com <c>SetupDi*</c> quando possível.
/// </summary>
/// <param name="Kind">Arrival ou Removal.</param>
/// <param name="DeviceId">
/// Identificador único do dispositivo (DevicePath / Instance ID).
/// </param>
/// <param name="FriendlyName">Nome amigável, quando disponível.</param>
/// <param name="VendorId">ID de vendor (VID), em hexadecimal sem prefixo.</param>
/// <param name="ProductId">ID de produto (PID), em hexadecimal sem prefixo.</param>
/// <param name="OccurredAt">Data/hora local do evento.</param>
public readonly record struct UsbDeviceEvent(
    UsbDeviceEventKind Kind,
    string DeviceId,
    string? FriendlyName,
    string? VendorId,
    string? ProductId,
    DateTime OccurredAt);
