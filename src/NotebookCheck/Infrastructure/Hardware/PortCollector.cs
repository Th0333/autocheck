using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Domain.Models;
using NotebookCheck.Infrastructure.Abstractions;
using NotebookCheck.Infrastructure.Wmi;

namespace NotebookCheck.Infrastructure.Hardware;

/// <summary>
/// Detecta portas físicas e seu estado de uso. Reescrito do zero pra usar
/// fontes em tempo real (NetworkInterface, MMDevice, DriveInfo, monitor enum)
/// em vez de WMI cached. Cada categoria tem sua própria função e nenhuma
/// derruba a outra.
/// </summary>
public sealed class PortCollector : IPortCollector
{
    private readonly IWmiQueryRunner _wmi;
    private readonly IDisplayEnumerator _displays;
    private readonly ILogger<PortCollector> _logger;

    public PortCollector(
        IWmiQueryRunner wmi,
        IPowerShellRunner ps,  // mantido só para compat de DI
        IDisplayEnumerator displays,
        ILogger<PortCollector> logger)
    {
        _ = ps;
        _wmi = wmi;
        _displays = displays;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PortInfo>> CollectAsync(CancellationToken ct)
    {
        // Cada coleta é isolada num try/catch que retorna sentinela. Se uma
        // explodir, o resto continua. Resultado é list direta de PortInfo —
        // sem "total" inflado por SMBIOS, mais determinístico.
        var ports = new List<PortInfo>();

        AddSafely(ports, () => CollectMonitors());
        AddSafely(ports, () => CollectUsb());
        AddSafely(ports, () => CollectEthernet());
        AddSafely(ports, () => CollectAudioJack());
        AddSafely(ports, () => CollectSdCard());
        AddSafely(ports, () => CollectWebcam());
        AddSafely(ports, () => CollectWifi());
        AddSafely(ports, () => CollectBluetooth());

        await Task.Yield();
        return ports;
    }

    private void AddSafely(List<PortInfo> ports, Func<IEnumerable<PortInfo>> producer)
    {
        try
        {
            ports.AddRange(producer());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha coletando categoria de porta");
        }
    }

    // ============================================================
    //  HDMI / DisplayPort — só conta os monitores externos detectados
    // ============================================================
    private IEnumerable<PortInfo> CollectMonitors()
    {
        var list = new List<PortInfo>();
        var monitors = _displays.EnumerateMonitors();
        var external = monitors.Where(m => m.Kind == MonitorKind.External).ToList();

        if (external.Count == 0)
        {
            // Mostra HDMI inativo só se temos algum sinal de existência (notebook
            // tem HDMI quase sempre).
            list.Add(new PortInfo(PortType.Hdmi, "HDMI / DisplayPort", "",
                Total: 1, Active: 0,
                Detail: "Nenhum monitor externo conectado"));
            return list;
        }

        foreach (var mon in external)
        {
            var label = mon.FriendlyName ?? mon.DeviceName;
            list.Add(new PortInfo(PortType.Hdmi, "Monitor externo", "",
                Total: 1, Active: 1,
                Detail: $"{mon.WidthPixels}x{mon.HeightPixels} • {label}"));
        }
        return list;
    }

    // ============================================================
    //  USB — enumera Win32_PnPEntity por DeviceID começando em USB\
    //  Quem importa é o "device pai" (cada dispositivo plugado tem 1 entrada
    //  raiz e várias filhas — pegamos só as raízes pra não duplicar).
    // ============================================================
    private IEnumerable<PortInfo> CollectUsb()
    {
        var list = new List<PortInfo>();
        try
        {
            var rows = _wmi.QueryAsync("root\\cimv2",
                "SELECT Name, DeviceID, Service, PNPClass, Manufacturer FROM Win32_PnPEntity",
                TimeSpan.FromSeconds(3), CancellationToken.None).GetAwaiter().GetResult();

            // Dedup por VID+PID puros (4 hex cada). Ignora sufixo MI_xx e instance ID.
            // Mantém para cada VID+PID o melhor candidato:
            //   - prefere PNPClass específico (Keyboard, Mouse, Image) sobre HIDClass/USB
            //   - prefere nome longo sobre nome curto/genérico
            var seen = new Dictionary<string, (string Name, string Class, int Score)>(StringComparer.OrdinalIgnoreCase);
            var vidPidRegex = new System.Text.RegularExpressions.Regex(
                @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (var r in rows)
            {
                var deviceId = r.GetString("DeviceID") ?? "";
                if (!deviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) continue;

                var match = vidPidRegex.Match(deviceId);
                if (!match.Success) continue;
                var vidPid = $"{match.Groups[1].Value}&{match.Groups[2].Value}".ToUpperInvariant();

                var name = r.GetString("Name") ?? "";
                var pnpClass = r.GetString("PNPClass") ?? "";
                var service = r.GetString("Service") ?? "";

                // Filtra hubs raiz/genéricos
                if (string.Equals(service, "USBHUB3", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(service, "USBHUB", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(service, "usbccgp", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.IndexOf("Root Hub", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (name.IndexOf("Generic USB Hub", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                // Filtra Bluetooth USB — temos card dedicado em outra categoria.
                if (string.Equals(pnpClass, "Bluetooth", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (string.Equals(service, "BTHUSB", StringComparison.OrdinalIgnoreCase)) continue;

                // Filtra integrados
                if (IsInternalDevice(name, pnpClass)) continue;

                // Score por especificidade da classe
                var classScore = pnpClass switch
                {
                    "Keyboard" => 10,
                    "Mouse" => 10,
                    "Image" or "Camera" => 10,
                    "Printer" => 10,
                    "MEDIA" or "AudioEndpoint" => 9,
                    "DiskDrive" or "USBDevice" => 8,
                    "HIDClass" => 3,
                    "USB" => 1,
                    _ => 5,
                };

                // Bonus por ter nome descritivo (não "USB Input Device" genérico)
                if (!string.IsNullOrWhiteSpace(name)
                    && name.IndexOf("USB Input Device", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("HID-compliant", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("USB Composite Device", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    classScore += 5;
                }

                if (!seen.TryGetValue(vidPid, out var existing) || classScore > existing.Score)
                {
                    seen[vidPid] = (name, pnpClass, classScore);
                }
            }

            if (seen.Count == 0)
            {
                list.Add(new PortInfo(PortType.UsbA, "USB", "",
                    Total: 1, Active: 0, Detail: "Sem dispositivos USB"));
                return list;
            }

            foreach (var kvp in seen)
            {
                var (name, pnpClass, _) = kvp.Value;
                var symbol = pnpClass switch
                {
                    "Keyboard" => "",
                    "Mouse" => "",
                    "MEDIA" or "AudioEndpoint" => "",
                    "DiskDrive" or "USBDevice" => "",
                    "Image" or "Camera" => "",
                    "Printer" => "",
                    "HIDClass" => "",
                    _ => "",
                };
                var shortName = ShortenDeviceName(name);
                list.Add(new PortInfo(PortType.UsbA, "USB", symbol,
                    Total: 1, Active: 1, Detail: shortName));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "USB enum falhou");
        }
        return list;
    }

    private static string ShortenDeviceName(string name)
    {
        // Encurta nomes longos do Windows tipo "Logitech USB Optical Mouse (USB Composite Device)"
        var s = name.Trim();
        var paren = s.IndexOf('(');
        if (paren > 0) s = s.Substring(0, paren).Trim();
        return s.Length > 24 ? s.Substring(0, 24) + "…" : s;
    }

    private static bool IsInternalDevice(string name, string pnpClass)
    {
        // Markers de coisas SEMPRE internas. Periféricos USB externos do mesmo
        // tipo (ex.: webcam USB externa) ainda passam.
        string[] markers =
        {
            "Integrated Camera", "HD User Facing", "IR Camera",
            "Synaptics Fingerprint", "Goodix Fingerprint", "Validity Fingerprint",
            "ThinkPad UltraNav", "TouchPad",
            "HID-compliant touch screen",
            "Wireless Radio Switch",
            "Realtek PCIE CardReader", "Realtek Card Reader",
        };
        return markers.Any(m => name.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    // ============================================================
    //  Ethernet — usa NetworkInterface (em tempo real, sem WMI cache)
    // ============================================================
    private IEnumerable<PortInfo> CollectEthernet()
    {
        var list = new List<PortInfo>();
        var ifaces = NetworkInterface.GetAllNetworkInterfaces();
        var ethernets = ifaces.Where(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                                       || i.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
            .Where(i => !IsVirtualEthernet(i))
            .ToList();

        if (ethernets.Count == 0)
        {
            return list; // sem porta ethernet física — não mostra nada
        }

        foreach (var nic in ethernets)
        {
            // Status oficial do NDIS: Up/Down via OperationalStatus.
            var isUp = nic.OperationalStatus == OperationalStatus.Up;
            list.Add(new PortInfo(PortType.Rj45, "Ethernet", "",
                Total: 1, Active: isUp ? 1 : 0,
                Detail: isUp
                    ? $"Link UP • {nic.Speed / 1_000_000} Mbps"
                    : "Sem cabo conectado"));
        }
        return list;
    }

    private static bool IsVirtualEthernet(NetworkInterface nic)
    {
        var desc = nic.Description ?? "";
        var name = nic.Name ?? "";
        // Bluetooth PAN aparece como Ethernet 100Mbps no .NET — filtramos por nome.
        string[] virt =
        {
            "Hyper-V", "vEthernet", "WSL", "VirtualBox", "VMware",
            "Bluetooth", "Loopback", "TAP", "TUN", "WireGuard",
            "Bluetooth Device (Personal Area Network)", "PAN",
        };
        return virt.Any(v =>
            desc.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    // ============================================================
    //  Áudio P2 — usa o DEFAULT RENDER ENDPOINT do Windows.
    //  Quando o fone é plugado, o Windows muda o default pra Headphones.
    //  Quando desplugado, volta pro Speakers interno. Esse é o sinal real.
    // ============================================================
    private IEnumerable<PortInfo> CollectAudioJack()
    {
        var list = new List<PortInfo>();
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();

            // Default endpoint atual do sistema
            NAudio.CoreAudioApi.MMDevice? defaultRender = null;
            try
            {
                defaultRender = enumerator.GetDefaultAudioEndpoint(
                    NAudio.CoreAudioApi.DataFlow.Render,
                    NAudio.CoreAudioApi.Role.Multimedia);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GetDefaultAudioEndpoint falhou");
            }

            if (defaultRender is null) return list;

            var name = defaultRender.FriendlyName ?? "";
            var lname = name.ToLowerInvariant();

            // Filtra HDMI/Display audio — não é o jack que estamos avaliando.
            if (lname.Contains("hdmi") || lname.Contains("displayport")
                || lname.Contains("digital output") || lname.Contains("monitor"))
            {
                // Algum monitor está como default. Olha se existe Headphones/
                // Speakers físicos não-HDMI ativos pra pelo menos mostrar a porta.
                ShowJackFromAvailable(enumerator, list);
                return list;
            }

            // Nome do default contém Headphones/Headset/Fone = fone plugado.
            bool isHeadphones =
                lname.Contains("headphone") || lname.Contains("headset")
                || lname.Contains("fone");

            list.Add(new PortInfo(PortType.AudioJack, "Áudio P2", "",
                Total: 1,
                Active: isHeadphones ? 1 : 0,
                Detail: isHeadphones ? name : $"Speakers internos ({name})"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio jack detection falhou");
        }
        return list;
    }

    private static void ShowJackFromAvailable(NAudio.CoreAudioApi.MMDeviceEnumerator enumerator, List<PortInfo> list)
    {
        try
        {
            var devs = enumerator.EnumerateAudioEndPoints(
                NAudio.CoreAudioApi.DataFlow.Render,
                NAudio.CoreAudioApi.DeviceState.Active);
            foreach (var d in devs)
            {
                var n = (d.FriendlyName ?? "").ToLowerInvariant();
                if (n.Contains("hdmi") || n.Contains("displayport")) continue;
                bool isPhones = n.Contains("headphone") || n.Contains("headset") || n.Contains("fone");
                list.Add(new PortInfo(PortType.AudioJack, "Áudio P2", "",
                    Total: 1,
                    Active: isPhones ? 1 : 0,
                    Detail: isPhones ? d.FriendlyName! : $"Speakers internos"));
                return;
            }
        }
        catch { /* ignore */ }
    }

    // ============================================================
    //  Leitor SD — checa drives removíveis com letra
    // ============================================================
    private IEnumerable<PortInfo> CollectSdCard()
    {
        var list = new List<PortInfo>();
        try
        {
            // Detecta presença do reader via WMI (uma vez, raramente muda).
            var pnp = _wmi.QueryAsync("root\\cimv2",
                "SELECT Name FROM Win32_PnPEntity WHERE Service = 'sdbus' OR Service = 'rtsuer' OR Service = 'rtsx_pci' OR Service = 'rtsx_usb' OR Name LIKE '%Card Reader%' OR Name LIKE '%SDXC%'",
                TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
            if (pnp.Count == 0) return list;

            // Active = pelo menos um drive removível com cartão dentro.
            // DriveInfo.IsReady distingue "letra atribuída sem mídia" de "tem cartão".
            bool active = false;
            string? cardName = null;
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Removable) continue;
                if (!d.IsReady) continue;
                active = true;
                cardName = $"{d.Name} ({d.VolumeLabel})";
                break;
            }

            list.Add(new PortInfo(PortType.SdCard, "Leitor SD", "",
                Total: 1, Active: active ? 1 : 0,
                Detail: active ? cardName ?? "Cartão presente" : "Sem cartão"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SD reader detection falhou");
        }
        return list;
    }

    // ============================================================
    //  Webcam — presença
    // ============================================================
    private IEnumerable<PortInfo> CollectWebcam()
    {
        var list = new List<PortInfo>();
        try
        {
            var rows = _wmi.QueryAsync("root\\cimv2",
                "SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image' OR Service = 'usbvideo' OR Service = 'KSCamera'",
                TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();

            var cams = rows.Where(r =>
            {
                var name = r.GetString("Name") ?? "";
                return name.Length > 0 && name.IndexOf("Scanner", StringComparison.OrdinalIgnoreCase) < 0;
            }).ToList();
            if (cams.Count == 0) return list;

            var anyOk = cams.Any(c => string.Equals(c.GetString("Status"), "OK", StringComparison.OrdinalIgnoreCase));
            list.Add(new PortInfo(PortType.Webcam, "Webcam", "",
                Total: 1, Active: anyOk ? 1 : 0,
                Detail: anyOk ? "Disponível" : "Driver com erro"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Webcam detection falhou");
        }
        return list;
    }

    // ============================================================
    //  Wi-Fi — presença e conectividade real
    // ============================================================
    private IEnumerable<PortInfo> CollectWifi()
    {
        var list = new List<PortInfo>();
        var ifaces = NetworkInterface.GetAllNetworkInterfaces();
        var wifis = ifaces.Where(i => i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211).ToList();

        if (wifis.Count == 0) return list;

        foreach (var nic in wifis)
        {
            var isUp = nic.OperationalStatus == OperationalStatus.Up;
            list.Add(new PortInfo(PortType.WiFi, "Wi-Fi", "",
                Total: 1, Active: isUp ? 1 : 0,
                Detail: isUp ? $"{nic.Speed / 1_000_000} Mbps" : "Desconectado"));
        }
        return list;
    }

    // ============================================================
    //  Bluetooth — presença e estado do rádio (uma única linha)
    // ============================================================
    private IEnumerable<PortInfo> CollectBluetooth()
    {
        var list = new List<PortInfo>();
        try
        {
            var rows = _wmi.QueryAsync("root\\cimv2",
                "SELECT Name, Status, Service FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'",
                TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
            if (rows.Count == 0) return list;

            // O Win32_PnPEntity retorna o rádio + serviços (LE, A2DP, HFP, etc.).
            // Queremos exatamente o rádio. Critério, em ordem:
            //   1. Service = BTHUSB (rádio USB) ou BthEnum (rádio integrado).
            //   2. Nome contém "radio" / "adapter" / "controller".
            //   3. Primeiro item.
            var radio = rows.FirstOrDefault(r =>
            {
                var s = r.GetString("Service") ?? "";
                return string.Equals(s, "BTHUSB", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s, "BthEnum", StringComparison.OrdinalIgnoreCase);
            });
            radio ??= rows.FirstOrDefault(r =>
            {
                var name = r.GetString("Name") ?? "";
                return name.IndexOf("Radio", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Adapter", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Host", StringComparison.OrdinalIgnoreCase) >= 0;
            });
            radio ??= rows[0];

            var status = radio.GetString("Status") ?? "";
            var isOk = string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase);
            list.Add(new PortInfo(PortType.Bluetooth, "Bluetooth", "",
                Total: 1, Active: isOk ? 1 : 0,
                Detail: isOk ? "Habilitado" : $"Desabilitado ({status})"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bluetooth detection falhou");
        }
        return list;
    }
}
