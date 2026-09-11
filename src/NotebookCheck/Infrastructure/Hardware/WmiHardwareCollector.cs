using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;
using NotebookCheck.Infrastructure.Abstractions;
using NotebookCheck.Infrastructure.Wmi;

namespace NotebookCheck.Infrastructure.Hardware;

/// <summary>
/// Coletor de hardware completo via WMI + PowerShell + display enumerator.
/// Cada operação tem timeout próprio e retorna sentinelas em caso de falha
/// (Requirements 2, 3, 5, 6, 7.4, 7.5, 26).
/// </summary>
public sealed class WmiHardwareCollector : IHardwareCollector
{
    private readonly IWmiQueryRunner _wmi;
    private readonly IPowerShellRunner _ps;
    private readonly IDisplayEnumerator _displays;
    private readonly CrystalDiskInfoRunner _crystalDiskInfo;
    private readonly DellBiosPasswordReader _dellBios;
    private readonly ILogger<WmiHardwareCollector> _logger;

    public WmiHardwareCollector(
        IWmiQueryRunner wmi,
        IPowerShellRunner ps,
        IDisplayEnumerator displays,
        CrystalDiskInfoRunner crystalDiskInfo,
        DellBiosPasswordReader dellBios,
        ILogger<WmiHardwareCollector> logger)
    {
        _wmi = wmi;
        _ps = ps;
        _displays = displays;
        _crystalDiskInfo = crystalDiskInfo;
        _dellBios = dellBios;
        _logger = logger;
    }

    public async Task<MachineInfo> CollectMachineAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        var token = cts.Token;

        string? manufacturer = null, model = null, serial = null, cpu = null, mac = null;
        decimal ramGb = 0m;
        string os = "Indisponível", osVersion = "Indisponível";

        var cs = await SafeQueryAsync("root\\cimv2", "SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem", TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
        if (cs.Count > 0)
        {
            manufacturer = cs[0].GetString("Manufacturer");
            model = cs[0].GetString("Model");
            var bytes = cs[0].GetULong("TotalPhysicalMemory");
            if (bytes.HasValue)
            {
                ramGb = Math.Round((decimal)bytes.Value / (1024m * 1024m * 1024m), 1, MidpointRounding.AwayFromZero);
            }
        }

        // "Brand name" (linha comercial) da máquina: SystemFamily do SMBIOS;
        // fallback Win32_ComputerSystemProduct.Version (nome amigável nos Lenovo).
        // Consulta separada para não derrubar a query principal em SOs antigos.
        string? family = null;
        var fam = await SafeQueryAsync("root\\cimv2", "SELECT SystemFamily FROM Win32_ComputerSystem", TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        if (fam.Count > 0) family = CleanBrandName(fam[0].GetString("SystemFamily"));
        if (family is null)
        {
            var csp = await SafeQueryAsync("root\\cimv2", "SELECT Version FROM Win32_ComputerSystemProduct", TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            if (csp.Count > 0) family = CleanBrandName(csp[0].GetString("Version"));
        }

        var bios = await SafeQueryAsync("root\\cimv2", "SELECT SerialNumber FROM Win32_BIOS", TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        if (bios.Count > 0) serial = bios[0].GetString("SerialNumber");

        // Tenta sempre Win32_ComputerSystemProduct (IdentifyingNumber) — que é a fonte que
        // OEMs como Dell, HP e Lenovo usam pra service tag oficial. Em muitos casos o
        // Win32_BIOS retorna em branco mas o ComputerSystemProduct entrega.
        if (IsBogusSerial(serial))
        {
            serial = await TryGetSerialAsync(token).ConfigureAwait(false) ?? serial;
        }
        _logger.LogInformation("Serial detectado: {Serial}", string.IsNullOrEmpty(serial) ? "(vazio)" : serial);

        ProcessorInfo? processor = null;
        var proc = await SafeQueryAsync("root\\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor", TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        if (proc.Count > 0)
        {
            cpu = proc[0].GetString("Name");
            // Soma cores/threads de TODOS os sockets (servidores/workstations
            // multi-CPU); clock máximo = o maior reportado.
            var cores = 0; var threads = 0; var maxClock = 0;
            foreach (var pr in proc)
            {
                cores += pr.GetInt("NumberOfCores") ?? 0;
                threads += pr.GetInt("NumberOfLogicalProcessors") ?? 0;
                maxClock = Math.Max(maxClock, pr.GetInt("MaxClockSpeed") ?? 0);
            }
            processor = new ProcessorInfo(
                cpu,
                cores > 0 ? cores : null,
                threads > 0 ? threads : null,
                maxClock > 0 ? maxClock : null);
        }

        // Memória detalhada (tipo, velocidade, pentes/slots).
        var memory = await CollectMemoryAsync(token).ConfigureAwait(false);

        // RAM instalada de verdade = soma dos pentes físicos. TotalPhysicalMemory
        // desconta reservas de hardware (iGPU etc.) e reporta 15,x numa máquina
        // de 16 GB; o valor do SO fica só como fallback quando a query por pente
        // não retorna nada.
        if (memory?.TotalGb is decimal instaladaGb && instaladaGb > 0)
            ramGb = Math.Round(instaladaGb, 1, MidpointRounding.AwayFromZero);

        var osRows = await SafeQueryAsync("root\\cimv2", "SELECT Caption, Version FROM Win32_OperatingSystem", TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        if (osRows.Count > 0)
        {
            os = osRows[0].GetString("Caption") ?? os;
            osVersion = osRows[0].GetString("Version") ?? osVersion;
        }

        // Adaptadores de rede via Get-NetAdapter (mais confiável que Win32_NetworkAdapter)
        var (adapters, mainMac) = await CollectNetworkAdaptersAsync(token).ConfigureAwait(false);
        mac = mainMac;
        var wifiCount = adapters.Count(a => a.Kind == NetworkAdapterKind.WiFi);
        var ethCount = adapters.Count(a => a.Kind == NetworkAdapterKind.Ethernet);

        // Display
        var display = await CollectDisplayAsync(token).ConfigureAwait(false);

        // Bluetooth com versão derivada do LMP
        var bt = await CollectBluetoothAsync(token).ConfigureAwait(false);

        // Activation
        var activation = AvailabilityFlag.Indisponivel;
        try
        {
            var lic = await _wmi.QueryAsync("root\\cimv2", "SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL AND ApplicationId='55c92734-d682-4d71-983e-d6ec3f16059f'", TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            foreach (var row in lic)
            {
                var status = row.GetInt("LicenseStatus");
                if (status == 1) { activation = AvailabilityFlag.Ativado; break; }
                if (status.HasValue) { activation = AvailabilityFlag.NaoAtivado; }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha consultando ativação Windows");
        }

        var sec = await CollectSecurityAsync(token).ConfigureAwait(false);
        var biosSec = await CollectBiosSecurityAsync(token).ConfigureAwait(false);
        var computrace = await CollectComputraceAsync(token).ConfigureAwait(false);

        return new MachineInfo(
            Manufacturer: manufacturer,
            Model: model,
            Serial: serial,
            Hostname: Environment.MachineName,
            Cpu: cpu,
            RamGb: ramGb,
            Os: os,
            OsVersion: osVersion,
            MacAddress: mac,
            Tpm: sec.Tpm,
            TpmVersion: sec.TpmVersion,
            SecureBoot: sec.SecureBoot,
            Autopilot: sec.Autopilot,
            WindowsActivation: activation,
            ScreenResolution: display.Resolution,
            GraphicsAdapter: display.GraphicsAdapter,
            CpuTemperatureC: null,
            NtbCode: "",
            Location: "",
            KeyboardBacklight: KeyboardBacklight.Indisponivel,
            KeyboardBacklightDetected: KeyboardBacklight.Indisponivel,
            CollectedAt: DateTime.Now,
            GraphicsAdapters: display.GraphicsAdapters,
            NetworkAdapters: adapters,
            WifiAdapterCount: wifiCount,
            EthernetAdapterCount: ethCount,
            Bluetooth: bt,
            BiosSecurity: biosSec,
            Computrace: computrace,
            AutopilotDetail: BuildAutopilotDetail(sec.AutopilotInfo),
            AutopilotEvidence: sec.AutopilotInfo?.Details,
            Processor: processor,
            Memory: memory,
            GraphicsDetails: display.GraphicsDetails,
            Family: family,
            VideoOutputs: display.VideoOutputs);
    }

    /// <summary>
    /// Normaliza o "brand name" SMBIOS, descartando os placeholders que OEMs
    /// deixam quando não preenchem o campo ("To Be Filled By O.E.M.", etc.).
    /// </summary>
    private static string? CleanBrandName(string? raw)
    {
        var v = (raw ?? "").Trim();
        if (v.Length < 2) return null;
        var low = v.ToLowerInvariant();
        if (low is "none" or "n/a" or "na" or "-") return null;
        string[] bogus = { "to be filled", "o.e.m", "default string", "system family", "system version", "not applicable", "invalid" };
        foreach (var b in bogus)
            if (low.Contains(b)) return null;
        return v;
    }

    /// <summary>
    /// Coleta detalhes da RAM via Win32_PhysicalMemory (por pente) e
    /// Win32_PhysicalMemoryArray (slots totais). Mais rico e mais preciso que o
    /// TotalPhysicalMemory do Win32_ComputerSystem (que exclui reservas de HW).
    /// </summary>
    private async Task<MemoryInfo?> CollectMemoryAsync(CancellationToken ct)
    {
        try
        {
            var rows = await SafeQueryAsync("root\\cimv2",
                "SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, MemoryType, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory",
                TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            if (rows.Count == 0) return null;

            var modules = new List<MemoryModule>();
            decimal totalGb = 0m;
            int minSpeed = int.MaxValue;
            string? predominantType = null;

            foreach (var r in rows)
            {
                var bytes = r.GetULong("Capacity") ?? 0;
                decimal? capGb = bytes > 0
                    ? Math.Round((decimal)bytes / (1024m * 1024m * 1024m), 1, MidpointRounding.AwayFromZero)
                    : null;
                if (capGb.HasValue) totalGb += capGb.Value;

                // ConfiguredClockSpeed = velocidade real rodando; Speed = nominal.
                var speed = r.GetInt("ConfiguredClockSpeed") ?? 0;
                if (speed <= 0) speed = r.GetInt("Speed") ?? 0;
                if (speed > 0) minSpeed = Math.Min(minSpeed, speed);

                var type = MapMemoryType(r.GetInt("SMBIOSMemoryType"), r.GetInt("MemoryType"));
                predominantType ??= type;

                modules.Add(new MemoryModule(
                    Locator: r.GetString("DeviceLocator"),
                    CapacityGb: capGb,
                    SpeedMhz: speed > 0 ? speed : null,
                    Manufacturer: CleanText(r.GetString("Manufacturer")),
                    PartNumber: CleanText(r.GetString("PartNumber")),
                    Type: type));
            }

            int? slotsTotal = null;
            try
            {
                var arr = await SafeQueryAsync("root\\cimv2",
                    "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray",
                    TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
                if (arr.Count > 0)
                {
                    var devs = arr.Sum(a => a.GetInt("MemoryDevices") ?? 0);
                    if (devs > 0) slotsTotal = devs;
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Win32_PhysicalMemoryArray falhou"); }

            return new MemoryInfo(
                TotalGb: totalGb > 0 ? totalGb : null,
                Type: predominantType,
                SpeedMhz: minSpeed != int.MaxValue ? minSpeed : null,
                SlotsUsed: modules.Count,
                SlotsTotal: slotsTotal,
                Modules: modules);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CollectMemoryAsync falhou");
            return null;
        }
    }

    /// <summary>Mapeia SMBIOSMemoryType (preferido) ou MemoryType para texto.</summary>
    private static string? MapMemoryType(int? smbios, int? legacy)
    {
        // SMBIOS Memory Device — Type (SMBIOS spec 7.18.2).
        var t = smbios.GetValueOrDefault();
        var byType = t switch
        {
            18 => "DDR",
            19 => "DDR2",
            20 => "DDR2 FB-DIMM",
            24 => "DDR3",
            26 => "DDR4",
            27 => "LPDDR",
            28 => "LPDDR2",
            29 => "LPDDR3",
            30 => "LPDDR4",
            34 => "DDR5",
            35 => "LPDDR5",
            _ => null,
        };
        if (byType is not null) return byType;

        // Fallback para o campo legado MemoryType (frequentemente 0 em HW novo).
        return legacy switch
        {
            20 => "DDR",
            21 => "DDR2",
            24 => "DDR3",
            26 => "DDR4",
            _ => null,
        };
    }

    /// <summary>Limpa strings WMI com padding/placeholder ("", espaços, "Unknown").</summary>
    private static string? CleanText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (t.Length == 0) return null;
        if (string.Equals(t, "Unknown", StringComparison.OrdinalIgnoreCase)) return null;
        if (t.All(c => c == '0' || c == ' ')) return null;
        return t;
    }

    /// <summary>
    /// Resumo legível do resultado do checker de Autopilot para UI/relatório.
    /// Fala em RASTROS, não em porcentagem: o antigo "Confiança baixa (0/100)"
    /// era lido pelos técnicos como "0% de chance de ter Autopilot", o que é
    /// falso — uma máquina formatada do zero não deixa rastro nenhum e ainda
    /// assim pode estar presa no tenant do antigo dono.
    /// Ex.: "2 evidências diretas • tenant contoso.com • Entra ID".
    /// </summary>
    private static string? BuildAutopilotDetail(AutopilotStatus? s)
    {
        if (s is null) return null;
        if (!s.AnySourceAvailable) return "Fontes indisponíveis";

        var text = s.Confidence switch
        {
            "High" => s.DirectEvidenceCount > 1
                ? $"{s.DirectEvidenceCount} evidências diretas"
                : "evidência direta encontrada",
            "Medium" => "sinais fortes, sem evidência direta",
            _ => s.ServiceReturnedNoProfile
                ? $"nenhum rastro local; o serviço Autopilot foi consultado{(s.ServiceQueriedAt is DateTime q ? $" em {q.ToLocalTime():dd/MM/yyyy}" : "")} e não devolveu perfil (confirme na inspeção)"
                : "nenhum rastro local (não prova que está livre — confirme na inspeção)",
        };
        var tenant = !string.IsNullOrWhiteSpace(s.TenantName) ? s.TenantName
                   : !string.IsNullOrWhiteSpace(s.TenantId) ? s.TenantId : null;
        if (tenant is not null) text += $" • tenant {tenant}";
        if (!string.IsNullOrWhiteSpace(s.ZtdRegistrationId)) text += " • hardware hash reconhecido pelo serviço Autopilot";
        if (s.ProfileFileFound) text += " • arquivo de perfil no disco";
        if (s.AzureAdJoined) text += " • Entra ID";
        if (s.AutopilotEventsFound) text += " • eventos de Autopilot";
        return text;
    }

    private async Task<(IReadOnlyList<NetworkAdapterInfo> adapters, string? mainMac)> CollectNetworkAdaptersAsync(CancellationToken ct)
    {
        var list = new List<NetworkAdapterInfo>();
        try
        {
            // NdisPhysicalMedium é o sinal mais confiável para distinguir tipos:
            //  0  = Unspecified
            //  1  = Wireless WAN
            //  2  = Cable Modem
            //  4  = WirelessLan (Wi-Fi)
            //  9  = Native 802.11 (Wi-Fi novo)
            //  10/11 = Bluetooth
            //  14 = 802.3 (Ethernet)
            // BT-PAN também reporta 14, então filtramos por nome antes.
            //
            // Não usamos -Physical porque ele descarta vários adaptadores via dock/USB-C.
            // Filtramos virtuais por nome (vEthernet, Hyper-V, etc.) e pelo flag Virtual.
            const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue |
    ForEach-Object {
        [pscustomobject]@{
            Name                = $_.Name
            Description         = $_.InterfaceDescription
            Mac                 = $_.MacAddress
            Status              = $_.Status
            LinkSpeed           = $_.LinkSpeed
            MediaType           = $_.MediaType
            PhysicalMediaType   = $_.PhysicalMediaType
            NdisPhysicalMedium  = [int]$_.NdisPhysicalMedium
            InterfaceType       = [int]$_.InterfaceType
            HardwareInterface   = [bool]$_.HardwareInterface
            Virtual             = [bool]$_.Virtual
            ComponentID         = $_.ComponentID
        }
    }
";
            var rows = await _ps.InvokeAsync(script, null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                var virtualFlag = row.GetBool("Virtual") ?? false;
                var name = row.GetString("Name") ?? "";
                var desc = row.GetString("Description");
                var componentId = row.GetString("ComponentID") ?? "";

                // Filtra adaptadores claramente sintéticos (Hyper-V, WSL, Microsoft Wi-Fi Direct,
                // WAN Miniport, Loopback, Kernel Debug, TAP, VirtualBox, VMware).
                if (virtualFlag) continue;
                if (IsSyntheticAdapter(name, desc, componentId)) continue;

                var macRaw = row.GetString("Mac");
                var status = row.GetString("Status") ?? "Unknown";
                var linkSpeed = row.GetString("LinkSpeed");
                var media = row.GetString("MediaType") ?? "";
                var phys = row.GetString("PhysicalMediaType") ?? "";
                var ndis = row.GetInt("NdisPhysicalMedium") ?? 0;
                var iface = row.GetInt("InterfaceType") ?? 0;

                var kind = ClassifyAdapter(desc, media, phys, ndis, iface);
                if (kind == NetworkAdapterKind.Outros) continue;

                list.Add(new NetworkAdapterInfo(
                    Name: name,
                    Description: desc,
                    MacAddress: NormalizeMac(macRaw),
                    Status: status,
                    LinkSpeed: linkSpeed,
                    Kind: kind));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Get-NetAdapter falhou — caindo para WMI");
        }

        // Fallback WMI
        if (list.Count == 0)
        {
            try
            {
                var rows = await SafeQueryAsync("root\\cimv2",
                    "SELECT Name, NetConnectionID, NetConnectionStatus, MACAddress, AdapterTypeID, Description, PhysicalAdapter, ServiceName FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE",
                    TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

                foreach (var row in rows)
                {
                    var name = row.GetString("NetConnectionID") ?? row.GetString("Name") ?? "";
                    var desc = row.GetString("Description") ?? row.GetString("Name");
                    var mac = row.GetString("MACAddress");
                    var typeId = row.GetInt("AdapterTypeID") ?? -1;
                    var service = row.GetString("ServiceName") ?? "";

                    if (IsSyntheticAdapter(name, desc, service)) continue;

                    // Bluetooth detection antes de tudo
                    if ((desc ?? "").IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0
                        || (name ?? "").IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0
                        || string.Equals(service, "BTHPAN", StringComparison.OrdinalIgnoreCase))
                    {
                        // Bluetooth PAN — não conta como adaptador de rede operacional.
                        continue;
                    }

                    var kind = typeId switch
                    {
                        0 => NetworkAdapterKind.Ethernet,
                        7 => NetworkAdapterKind.WiFi,
                        _ => ClassifyAdapter(desc, "", "", 0, 0),
                    };
                    if (kind == NetworkAdapterKind.Outros) continue;

                    var statusInt = row.GetInt("NetConnectionStatus") ?? 0;
                    var status = statusInt switch
                    {
                        0 => "Disconnected",
                        1 => "Connecting",
                        2 => "Up",
                        3 => "Disconnecting",
                        4 => "Hardware not present",
                        5 => "Hardware disabled",
                        7 => "Media disconnected",
                        _ => "Unknown",
                    };

                    list.Add(new NetworkAdapterInfo(name ?? "", desc, NormalizeMac(mac), status, null, kind));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Win32_NetworkAdapter fallback falhou");
            }
        }

        // MAC principal
        string? mainMac = null;
        try
        {
            const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$ip = Get-NetIPConfiguration -ErrorAction SilentlyContinue |
    Where-Object { $_.IPv4DefaultGateway -ne $null } |
    Select-Object -First 1
if ($ip) {
    $a = Get-NetAdapter -InterfaceIndex $ip.InterfaceIndex -ErrorAction SilentlyContinue
    if ($a) { [pscustomobject]@{ Mac = [string]$a.MacAddress } }
}
";
            var rows = await _ps.InvokeAsync(script, null, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                mainMac = NormalizeMac(rows[0].GetString("Mac"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha resolvendo MAC do adaptador com gateway");
        }

        if (mainMac is null)
        {
            mainMac = list.FirstOrDefault(a => string.Equals(a.Status, "Up", StringComparison.OrdinalIgnoreCase))?.MacAddress
                  ?? list.FirstOrDefault()?.MacAddress;
        }

        return (list, mainMac);
    }

    /// <summary>
    /// Identifica adaptadores sintéticos/virtuais que não devem ser contados:
    /// Bluetooth PAN, Hyper-V vSwitch, WSL, WAN Miniport, Microsoft Wi-Fi Direct
    /// Virtual Adapter, Microsoft Kernel Debug Network Adapter, TAP-Windows,
    /// VirtualBox, VMware, Loopback.
    /// </summary>
    private static bool IsSyntheticAdapter(string? name, string? description, string componentId)
    {
        var n = name ?? "";
        var d = description ?? "";
        var c = componentId ?? "";

        // Bluetooth PAN é o caso clássico — emula Ethernet 802.3 mas é Bluetooth.
        if (n.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0
            || c.IndexOf("BthPan", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        string[] syntheticPatterns =
        {
            "vEthernet", "Hyper-V", "Virtual Adapter", "Virtual Switch",
            "Microsoft Wi-Fi Direct", "WAN Miniport", "Microsoft Kernel Debug",
            "TAP-", "TAP-Windows", "VirtualBox", "VMware", "Loopback Pseudo",
            "Microsoft Teredo", "Microsoft 6to4", "ISATAP",
            "Microsoft Network Adapter Multiplexor",
            "WSL", "WireGuard", "OpenVPN", "Tailscale",
        };
        foreach (var pattern in syntheticPatterns)
        {
            if (n.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (d.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }

        return false;
    }

    private static NetworkAdapterKind ClassifyAdapter(string? description, string media, string phys, int ndisPhysicalMedium, int interfaceType)
    {
        var d = description ?? "";

        // 1. Bluetooth ANTES de qualquer outra coisa (BT-PAN emula 802.3).
        if (d.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return NetworkAdapterKind.Bluetooth;
        }
        if (ndisPhysicalMedium == 10 || ndisPhysicalMedium == 11)
        {
            return NetworkAdapterKind.Bluetooth;
        }

        // 2. Wi-Fi
        switch (ndisPhysicalMedium)
        {
            case 1:  // Wireless WAN
            case 4:  // WirelessLan
            case 9:  // Native 802.11
                return NetworkAdapterKind.WiFi;
        }
        if (interfaceType == 71) return NetworkAdapterKind.WiFi;

        if (d.IndexOf("Wi-Fi", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("WLAN", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("802.11", StringComparison.OrdinalIgnoreCase) >= 0
            || media.IndexOf("Native 802.11", StringComparison.OrdinalIgnoreCase) >= 0
            || phys.IndexOf("Native 802.11", StringComparison.OrdinalIgnoreCase) >= 0
            || phys.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return NetworkAdapterKind.WiFi;
        }

        // 3. Ethernet
        if (ndisPhysicalMedium == 14) return NetworkAdapterKind.Ethernet;
        if (interfaceType == 6) return NetworkAdapterKind.Ethernet;

        if (media.IndexOf("802.3", StringComparison.OrdinalIgnoreCase) >= 0
            || phys.IndexOf("802.3", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("GbE", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("Realtek PCIe", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("Realtek USB GbE", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("Intel(R) Ethernet", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("Killer E", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("ASIX AX", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("DisplayLink", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return NetworkAdapterKind.Ethernet;
        }
        return NetworkAdapterKind.Outros;
    }

    private static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        // Get-NetAdapter retorna "AA-BB-CC-...". Normaliza para "AA:BB:CC:...".
        return mac.Replace('-', ':').Trim().ToUpperInvariant();
    }

    private async Task<BluetoothInfo?> CollectBluetoothAsync(CancellationToken ct)
    {
        try
        {
            // Estratégia em duas partes:
            //  1. Lista os rádios Bluetooth (Get-PnpDevice classe Bluetooth, filtrando enumerator/host).
            //  2. Tenta obter o LMP via Get-PnpDeviceProperty usando o nome canônico
            //     "DEVPKEY_Bluetooth_RadioLmpVersion". Quando o cmdlet/SDK não suporta o nome,
            //     caímos para um fallback baseado no ID do device (tudo dentro de try/catch).
            const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$radios = @()
try {
    $radios = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Service -eq 'BTHUSB' -or
            $_.Service -eq 'BthEnum' -or
            $_.FriendlyName -match 'radio|adapter|controller|host'
        }
    if (-not $radios) {
        $radios = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue
    }
} catch { $radios = @() }

if (-not $radios -or $radios.Count -eq 0) {
    return
}

$lmp = $null
foreach ($r in $radios) {
    if (-not $r -or [string]::IsNullOrEmpty($r.InstanceId)) { continue }
    try {
        $p = Get-PnpDeviceProperty -InstanceId $r.InstanceId -KeyName 'DEVPKEY_Bluetooth_RadioLmpVersion' -ErrorAction Stop
        if ($p -and $p.Data -ne $null) {
            $lmp = [int]$p.Data
            break
        }
    } catch {
        # Cmdlet pode não suportar o nome de propriedade; ignoramos e seguimos.
    }
}

$first = $radios | Select-Object -First 1
if ($first) {
    [pscustomobject]@{
        Name = if ($first.FriendlyName) { [string]$first.FriendlyName } else { '' }
        Status = if ($first.Status) { [string]$first.Status } else { '' }
        Lmp = $lmp
        Count = @($radios).Count
    }
}
";
            var rows = await _ps.InvokeAsync(script, null, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            if (rows.Count == 0) return new BluetoothInfo(false, null, null, null, null);

            var first = rows[0];
            var name = first.GetString("Name");
            var status = first.GetString("Status");
            var lmp = first.GetInt("Lmp");
            var version = MapLmpToVersion(lmp);
            return new BluetoothInfo(true, name, status, lmp, version);
        }
        catch (Exception ex)
        {
            // Uma falha do cmdlet PowerShell com mensagem "value cannot be null"
            // ainda chega aqui — fazemos fallback para WMI e retornamos algo útil.
            _logger.LogDebug(ex, "Falha coletando Bluetooth via PowerShell, tentando WMI");
            return await CollectBluetoothWmiFallbackAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<BluetoothInfo?> CollectBluetoothWmiFallbackAsync(CancellationToken ct)
    {
        try
        {
            var rows = await SafeQueryAsync(
                "root\\cimv2",
                "SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'",
                TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

            if (rows.Count == 0) return new BluetoothInfo(false, null, null, null, null);

            var name = rows[0].GetString("Name");
            var status = rows[0].GetString("Status");
            return new BluetoothInfo(true, name, status, null, null);
        }
        catch
        {
            return new BluetoothInfo(false, null, null, null, null);
        }
    }

    private static string? MapLmpToVersion(int? lmp) => Domain.Rules.BluetoothVersionMap.FromLmp(lmp);

    public async Task<IReadOnlyList<StorageInfo>> CollectStorageAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var token = cts.Token;

        var disks = await SafeQueryAsync("root\\cimv2", "SELECT Index, Size, MediaType, Model, InterfaceType FROM Win32_DiskDrive", TimeSpan.FromSeconds(10), token).ConfigureAwait(false);

        // Saúde por disco via MSFT_PhysicalDisk.HealthStatus — fonte canônica do Windows.
        // DeviceId (MSFT) corresponde ao Index (Win32_DiskDrive). HealthStatus:
        //   0 = Healthy, 1 = Warning, 2 = Unhealthy, 3+ = Unknown.
        var healthByDeviceId = new Dictionary<int, int>();
        var msft = await SafeQueryAsync("root\\Microsoft\\Windows\\Storage",
            "SELECT DeviceId, HealthStatus, MediaType, BusType FROM MSFT_PhysicalDisk",
            TimeSpan.FromSeconds(5), token).ConfigureAwait(false);

        var mediaByDeviceId = new Dictionary<int, (int media, int bus)>();
        foreach (var row in msft)
        {
            var did = row.GetInt("DeviceId");
            var mt = row.GetInt("MediaType") ?? 0;
            var bt = row.GetInt("BusType") ?? 0;
            var hs = row.GetInt("HealthStatus");
            if (did.HasValue)
            {
                mediaByDeviceId[did.Value] = (mt, bt);
                if (hs.HasValue) healthByDeviceId[did.Value] = hs.Value;
            }
        }

        // Fallback: MSStorageDriver_FailurePredictStatus quando MSFT_PhysicalDisk
        // falha (raro em Windows 10+, comum só em VMs antigas).
        var failurePredictByIndex = new Dictionary<int, bool>();
        if (healthByDeviceId.Count == 0)
        {
            try
            {
                var smartRows = await SafeQueryAsync("root\\wmi",
                    "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus",
                    TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                foreach (var row in smartRows)
                {
                    var inst = row.GetString("InstanceName") ?? "";
                    var fail = row.GetBool("PredictFailure") ?? false;
                    // Tenta extrair índice numérico da instância (PHYSICALDRIVE<N> ou _<N>)
                    var idx = ExtractDiskIndex(inst);
                    if (idx >= 0) failurePredictByIndex[idx] = fail;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MSStorageDriver_FailurePredictStatus falhou");
            }
        }

        var list = new List<StorageInfo>();
        var idx2 = 0;

        // Saúde via CrystalDiskInfo REAL (binário oficial embutido). Lê o
        // relatório SMART completo (vida útil, horas, TBW, temperatura).
        IReadOnlyList<CrystalDiskInfoDisk> cdiDisks;
        try
        {
            cdiDisks = await _crystalDiskInfo.CollectAsync(token).ConfigureAwait(false);
        }
        catch
        {
            cdiDisks = System.Array.Empty<CrystalDiskInfoDisk>();
        }

        foreach (var d in disks.Take(16))
        {
            var sizeBytes = d.GetULong("Size") ?? 0;
            // Capacidade de armazenamento usa convenção decimal (GB = 1000^3 bytes), igual ao
            // fabricante/marketing (ex.: "1TB" = 1.000.000.000.000 bytes ≈ 1000.2 GB), e não
            // binária (GiB = 1024^3), que é o valor que o Explorer do Windows exibe (ex.: 931.5 GB).
            var capacityGb = Math.Round((decimal)sizeBytes / (1000m * 1000m * 1000m), 1, MidpointRounding.AwayFromZero);
            var diskIndex = d.GetInt("Index") ?? idx2;
            var model = d.GetString("Model");

            // Ignora mídia USB (ex.: o próprio pen drive de onde o app roda) — BusType 7
            // (STORAGE_BUS_TYPE) = USB. Só pulamos quando MSFT_PhysicalDisk confirma o
            // barramento; se a consulta falhou (dict vazio), preferimos manter o disco a
            // arriscar descartar um disco interno por engano.
            if (mediaByDeviceId.TryGetValue(diskIndex, out var busCheck) && busCheck.bus == 7)
            {
                continue;
            }

            var type = StorageType.Indisponivel;
            if (mediaByDeviceId.TryGetValue(diskIndex, out var mb))
            {
                type = mb.media switch
                {
                    3 => StorageType.HDD,
                    4 => mb.bus == 17 ? StorageType.SSD_NVMe : StorageType.SSD_SATA,
                    _ => StorageType.Indisponivel,
                };
            }
            else
            {
                var mt = d.GetString("MediaType");
                if (mt is not null && mt.Contains("Fixed", StringComparison.OrdinalIgnoreCase)) type = StorageType.HDD;
            }

            // Status SMART: prioriza HealthStatus do MSFT, depois PredictFailure, senão indisponível.
            var smartStatus = SmartStatus.Indisponivel;
            if (healthByDeviceId.TryGetValue(diskIndex, out var hs))
            {
                smartStatus = hs switch
                {
                    0 => SmartStatus.OK,
                    1 => SmartStatus.Aviso,
                    2 => SmartStatus.Falha,
                    _ => SmartStatus.Indisponivel,
                };
            }
            else if (failurePredictByIndex.TryGetValue(diskIndex, out var failPred))
            {
                smartStatus = failPred ? SmartStatus.Falha : SmartStatus.OK;
            }

            // Casa com o disco do CrystalDiskInfo: por modelo (substring) e,
            // como fallback, por ordem.
            var cdi = MatchCdiDisk(cdiDisks, model, idx2);

            // CrystalDiskInfo refina o status SMART, mas NUNCA rebaixa a severidade:
            // se o Windows (MSFT_PhysicalDisk) ou o PredictFailure já indicaram Falha,
            // o disco continua Falha mesmo com health alto. "Pior vence". O label
            // (Good/Caution/Bad) do CDI é mais confiável que o % puro para detectar
            // setores realocados/critical warning que não consomem vida útil.
            SmartStatus? cdiStatus = null;
            if (cdi?.HealthLabel is { } hl)
            {
                cdiStatus = hl.ToLowerInvariant() switch
                {
                    "good" => SmartStatus.OK,
                    "caution" => SmartStatus.Aviso,
                    "bad" => SmartStatus.Falha,
                    _ => null,
                };
            }
            if (cdiStatus is null && cdi?.HealthPercent is int hp)
            {
                cdiStatus = hp switch
                {
                    >= 90 => SmartStatus.OK,
                    >= 50 => SmartStatus.Aviso,
                    _ => SmartStatus.Falha,
                };
            }
            if (cdiStatus is { } cs)
            {
                smartStatus = WorstSmart(smartStatus, cs);
            }

            // Vida útil restante = Health % do CrystalDiskInfo (Percentage Used invertido).
            decimal? tempC = cdi?.TemperatureC is int tc and > 0 ? tc : null;
            var displayModel = cdi?.Model ?? (string.IsNullOrWhiteSpace(model) ? null : model.Trim());

            list.Add(new StorageInfo(
                idx2, capacityGb, type, smartStatus, null, tempC,
                Model: displayModel,
                LifePercentRemaining: cdi?.HealthPercent,
                PowerOnHours: cdi?.PowerOnHours,
                PowerOnCount: cdi?.PowerOnCount,
                DataWrittenTb: cdi?.HostWritesGb,
                DataReadTb: cdi?.HostReadsGb,
                Firmware: cdi?.Firmware,
                SerialNumber: cdi?.SerialNumber,
                Interface: cdi?.Interface,
                DriveLetter: cdi?.DriveLetter,
                HealthLabel: cdi?.HealthLabel));
            idx2++;
        }

        return list;
    }

    /// <summary>
    /// Combina dois status SMART preservando a maior severidade
    /// (Indisponível &lt; OK &lt; Aviso &lt; Falha). Nunca rebaixa uma Falha.
    /// </summary>
    private static SmartStatus WorstSmart(SmartStatus a, SmartStatus b)
    {
        static int Rank(SmartStatus s) => s switch
        {
            SmartStatus.Indisponivel => 0,
            SmartStatus.OK => 1,
            SmartStatus.Aviso => 2,
            SmartStatus.Falha => 3,
            _ => 0,
        };
        return Rank(b) > Rank(a) ? b : a;
    }

    /// <summary>
    /// Casa um disco do Win32_DiskDrive com o disco correspondente do
    /// CrystalDiskInfo. Tenta por modelo (substring tolerante) e cai para
    /// índice ordinal.
    /// </summary>
    private static CrystalDiskInfoDisk? MatchCdiDisk(
        IReadOnlyList<CrystalDiskInfoDisk> cdiDisks, string? model, int ordinalIndex)
    {
        if (cdiDisks.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(model))
        {
            var normModel = model.Trim();
            // Match por modelo: o nome do Win32 costuma conter o do CDI ou vice-versa.
            foreach (var c in cdiDisks)
            {
                if (string.IsNullOrWhiteSpace(c.Model)) continue;
                if (normModel.Contains(c.Model, StringComparison.OrdinalIgnoreCase)
                    || c.Model.Contains(normModel, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }
        }

        // Fallback: índice ordinal (CDI lista na mesma ordem que o Win32 em geral).
        return ordinalIndex < cdiDisks.Count ? cdiDisks[ordinalIndex] : null;
    }

    /// <summary>
    /// Extrai um índice de disco de um InstanceName WMI, p.ex.
    /// "IDE\Disk... \5&3da2c2c8&0&0.0.0_0" → 0,
    /// "SCSI\Disk... \... PHYSICALDRIVE0" → 0.
    /// Retorna -1 se nada confiável puder ser extraído.
    /// </summary>
    private static int ExtractDiskIndex(string instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return -1;
        var match = System.Text.RegularExpressions.Regex.Match(instanceName, @"PHYSICALDRIVE(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var n)) return n;
        // Caso "..._<N>" no fim — fragile mas é o que tinha antes; só usado como último recurso
        match = System.Text.RegularExpressions.Regex.Match(instanceName, @"_(\d+)$");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var m)) return m;
        return -1;
    }

    public async Task<BatteryInfo?> CollectBatteryAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(12));
        var token = cts.Token;

        // ── Fonte primária: API nativa de bateria do Windows (IOCTL_BATTERY_*),
        //    a mesma usada por utilitários como o BatteryInfoView. Traz nome,
        //    fabricante, química, voltagem, taxa de carga/descarga e carga atual.
        //    Soma múltiplas baterias (notebooks dual-battery).
        IReadOnlyList<BatteryDeviceData> devices;
        try
        {
            devices = await Task.Run(BatteryDeviceReader.ReadAll, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Leitura da API nativa de bateria falhou");
            devices = Array.Empty<BatteryDeviceData>();
        }

        // Indicador independente de WMI: o Windows sabe se há bateria via
        // GetSystemPowerStatus. Usado para distinguir "WMI falhou" de "sem bateria".
        bool? hardwareHasBattery = TryDetectBatteryPresence();

        int? designMwh = null, fullMwh = null, cycles = null;
        int? remainingMwh = null, voltageMv = null, rateMw = null, chargePercent = null;
        string? name = null, manufacturer = null, serial = null, chemistry = null;
        bool sawDevice = devices.Count > 0;
        bool anyCharging = false, anyDischarging = false, anyOnLine = false, anyCritical = false;

        if (sawDevice)
        {
            // Agrega capacidades de todas as baterias físicas.
            int dSum = 0, fSum = 0, rSum = 0;
            foreach (var d in devices)
            {
                if (d.DesignCapacityMwh is int dc) dSum += dc;
                if (d.FullChargeCapacityMwh is int fc) fSum += fc;
                if (d.RemainingCapacityMwh is int rc) rSum += rc;
                anyCharging |= d.Charging;
                anyDischarging |= d.Discharging;
                anyOnLine |= d.OnLine;
                anyCritical |= d.Critical;
            }
            if (dSum > 0) designMwh = dSum;
            if (fSum > 0) fullMwh = fSum;
            if (rSum > 0) remainingMwh = rSum;

            // Identidade/voltagem: usa a primeira bateria como representativa.
            var first = devices[0];
            name = first.Name;
            manufacturer = first.Manufacturer;
            serial = first.SerialNumber;
            chemistry = first.Chemistry;
            voltageMv = first.VoltageMv;
            cycles = devices.Select(d => d.CycleCount).Where(c => c is > 0).Sum(c => c);
            if (cycles == 0) cycles = null;
            // Taxa total (soma das taxas, mantendo sinal: + carga, − descarga).
            var rates = devices.Where(d => d.RateMw.HasValue).ToList();
            if (rates.Count > 0) rateMw = rates.Sum(d => d.RateMw!.Value);
        }

        // ── Fallback WMI Win32_Battery (estado de carga textual + capacidades
        //    quando a API nativa não trouxe). Distingue erro de "sem bateria".
        var (wmiRows, wmiFailed) = await SafeQueryWithStatusAsync(
            "root\\cimv2",
            "SELECT BatteryStatus, EstimatedChargeRemaining, DesignCapacity, FullChargeCapacity FROM Win32_Battery",
            TimeSpan.FromSeconds(5), token).ConfigureAwait(false);

        string charging = "Indisponível";
        if (wmiRows.Count > 0)
        {
            var r = wmiRows[0];
            designMwh ??= r.GetInt("DesignCapacity") is int dd and > 0 ? dd : null;
            fullMwh ??= r.GetInt("FullChargeCapacity") is int ff and > 0 ? ff : null;
            chargePercent ??= r.GetInt("EstimatedChargeRemaining");
            charging = MapBatteryStatus(r.GetInt("BatteryStatus") ?? 0);
        }

        // Estado derivado da API nativa tem prioridade (é instantâneo e preciso).
        if (sawDevice)
        {
            charging = anyCharging ? "Carregando"
                : anyDischarging ? "Descarregando"
                : anyOnLine ? "Cheia (na tomada)"
                : "Indisponível";
            if (anyCritical) charging = "Crítica";
        }

        // Capacidades/ciclos via classes root\wmi (complementam quando faltam).
        designMwh = await TryReadWmiBatteryIntAsync("BatteryStaticData", "DesignedCapacity", designMwh, token).ConfigureAwait(false);
        fullMwh = await TryReadWmiBatteryIntAsync("BatteryFullChargedCapacity", "FullChargedCapacity", fullMwh, token).ConfigureAwait(false);
        cycles = await TryReadWmiBatteryIntAsync("BatteryCycleCount", "CycleCount", cycles, token).ConfigureAwait(false);

        // Última fonte: powercfg /batteryreport (XML) — soma TODAS as baterias.
        try
        {
            var (pcgDesign, pcgFull, pcgCycles) = await ReadPowerCfgBatteryAsync(token).ConfigureAwait(false);
            if (pcgDesign is > 0) designMwh ??= pcgDesign;
            if (pcgFull is > 0) fullMwh ??= pcgFull;
            if (pcgCycles is > 0) cycles ??= pcgCycles;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "powercfg battery report indisponível"); }

        // Carga % atual: prioriza cálculo direto (remaining/full), depois WMI.
        if (chargePercent is null && remainingMwh is > 0 && fullMwh is > 0)
        {
            chargePercent = (int)Math.Round(100m * remainingMwh.Value / fullMwh.Value, MidpointRounding.AwayFromZero);
        }
        if (chargePercent.HasValue) chargePercent = Math.Clamp(chargePercent.Value, 0, 100);

        // Desgaste e saúde (uma única vez, no fim, com os melhores valores).
        decimal? wear = null, health = null;
        if (designMwh is > 0 && fullMwh is >= 0)
        {
            wear = Domain.Rules.DomainRules.ComputeWear(designMwh.Value, fullMwh.Value);
            health = Math.Round(100m - wear.Value, 2, MidpointRounding.AwayFromZero);
        }

        // ── Decisão "tem bateria?": só conclui ausência quando NENHUMA fonte viu
        //    bateria E o hardware confirma ausência (ou não sabemos e tudo veio vazio).
        bool noData = !sawDevice && wmiRows.Count == 0 && designMwh is null && fullMwh is null;
        if (noData)
        {
            if (hardwareHasBattery == true)
            {
                // O Windows vê bateria mas as fontes de dados falharam → reporta
                // presença com status indisponível em vez de "sem bateria".
                return new BatteryInfo(
                    DesignCapacityMwh: null, FullChargeCapacityMwh: null, CycleCount: null,
                    ChargingStatus: "Dados indisponíveis", WearPercent: null,
                    Name: name, Manufacturer: manufacturer);
            }
            if (hardwareHasBattery == false && !wmiFailed)
            {
                return null; // Confirmadamente sem bateria (desktop).
            }
            if (wmiFailed && hardwareHasBattery != false)
            {
                // WMI falhou e não temos confirmação de ausência → não afirmar nada.
                return new BatteryInfo(null, null, null, "Dados indisponíveis", null);
            }
            return null;
        }

        return new BatteryInfo(
            DesignCapacityMwh: designMwh,
            FullChargeCapacityMwh: fullMwh,
            CycleCount: cycles,
            ChargingStatus: charging,
            WearPercent: wear,
            Name: name,
            Manufacturer: manufacturer,
            SerialNumber: serial,
            Chemistry: chemistry,
            RemainingCapacityMwh: remainingMwh,
            ChargePercent: chargePercent,
            VoltageMv: voltageMv,
            RateMw: rateMw,
            HealthPercent: health);
    }

    /// <summary>
    /// Mapeia <c>Win32_Battery.BatteryStatus</c> (1–11) para texto PT-BR,
    /// incluindo 10 (Indefinido) e 11 (Parcialmente carregada) — este último
    /// comum em notebooks com limite de carga de firmware.
    /// </summary>
    private static string MapBatteryStatus(int status) => status switch
    {
        1 => "Descarregando",
        2 => "AC",
        3 => "Cheia",
        4 => "Baixa",
        5 => "Crítica",
        6 => "Carregando",
        7 => "Carregando e alta",
        8 => "Carregando e baixa",
        9 => "Carregando e crítica",
        10 => "Indefinido",
        11 => "Parcialmente carregada",
        _ => "Indisponível",
    };

    /// <summary>
    /// Lê um inteiro de uma classe de bateria do namespace <c>root\wmi</c>,
    /// preservando o valor atual <paramref name="current"/> quando já está
    /// preenchido ou quando a consulta falha. Substitui três blocos try/catch
    /// quase idênticos.
    /// </summary>
    private async Task<int?> TryReadWmiBatteryIntAsync(string className, string field, int? current, CancellationToken ct)
    {
        if (current is > 0) return current;
        try
        {
            var rows = await _wmi.QueryAsync("root\\wmi",
                $"SELECT {field} FROM {className}", TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                var v = rows[0].GetInt(field);
                if (v.HasValue && v.Value > 0) return v;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "{Class} indisponível", className); }
        return current;
    }

    /// <summary>
    /// Detecta presença de bateria via <c>GetSystemPowerStatus</c> (kernel32),
    /// independente do WMI. Retorna <c>null</c> se a API falhar.
    /// </summary>
    private static bool? TryDetectBatteryPresence()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return null;
            if (s.BatteryFlag == 255) return null;           // status desconhecido
            return (s.BatteryFlag & 128) == 0;               // bit 7 setado = sem bateria
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    /// <summary>
    /// Gera o relatório de bateria via <c>powercfg /batteryreport /xml</c> e
    /// extrai DesignCapacity, FullChargeCapacity e CycleCount. Esses valores
    /// são mais confiáveis que as classes WMI em muitos notebooks.
    /// </summary>
    private async Task<(int? design, int? full, int? cycles)> ReadPowerCfgBatteryAsync(CancellationToken ct)
    {
        var xmlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"battreport_{Guid.NewGuid():N}.xml");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = $"/batteryreport /xml /output \"{xmlPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                if (proc is null) return (null, null, null);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
                try { await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
                catch { try { if (!proc.HasExited) proc.Kill(); } catch { } }
            }

            if (!System.IO.File.Exists(xmlPath)) return (null, null, null);
            var xml = await System.IO.File.ReadAllTextAsync(xmlPath, ct).ConfigureAwait(false);

            // O XML tem <DesignCapacity>, <FullChargeCapacity> e <CycleCount>.
            int? design = ExtractXmlInt(xml, "DesignCapacity");
            int? full = ExtractXmlInt(xml, "FullChargeCapacity");
            int? cycles = ExtractXmlInt(xml, "CycleCount");
            return (design, full, cycles);
        }
        finally
        {
            try { if (System.IO.File.Exists(xmlPath)) System.IO.File.Delete(xmlPath); } catch { }
        }
    }

    private static int? ExtractXmlInt(string xml, string tag)
    {
        // Captura o primeiro <tag ...>valor</tag> (com ou sem atributos).
        var m = System.Text.RegularExpressions.Regex.Match(
            xml, $"<{tag}[^>]*>\\s*([0-9]+)\\s*</{tag}>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var v) && v > 0) return v;
        return null;
    }

    public async Task<SecurityFeatures> CollectSecurityAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var token = cts.Token;

        var tpm = AvailabilityFlag.Indisponivel;
        var tpmState = AvailabilityFlag.Indisponivel;
        string? tpmVersion = null;
        var secureBoot = AvailabilityFlag.Indisponivel;
        var autopilot = AvailabilityFlag.Indisponivel;
        string? reason = null;

        // TPM via WMI
        try
        {
            var rows = await _wmi.QueryAsync("root\\CIMV2\\Security\\MicrosoftTpm", "SELECT IsActivated_InitialValue, IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm", TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                tpm = AvailabilityFlag.Presente;
                var enabled = rows[0].GetBool("IsEnabled_InitialValue") ?? false;
                tpmState = enabled ? AvailabilityFlag.Habilitado : AvailabilityFlag.Desabilitado;
                tpmVersion = rows[0].GetString("SpecVersion");
            }
            else
            {
                tpm = AvailabilityFlag.Ausente;
            }
        }
        catch (WmiQueryException ex)
        {
            reason = ex.Cause switch
            {
                WmiFailureCause.AccessDenied => "permissão",
                WmiFailureCause.NotPresent => "ausência do recurso",
                WmiFailureCause.Timeout => "tempo limite excedido",
                _ => "indisponível",
            };
            _logger.LogDebug(ex, "Falha coletando TPM via WMI");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha coletando TPM");
        }

        // Secure Boot — lê direto do registro, fonte que o msinfo32 usa
        // (HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled).
        try
        {
            var (state, sbReason) = ReadSecureBootFromRegistry();
            secureBoot = state;
            if (state == AvailabilityFlag.Indisponivel && sbReason is not null)
            {
                reason ??= sbReason;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha lendo SecureBoot do registro");
        }

        // Autopilot — checker multi-fonte com nível de confiança (WMI MDM bridge
        // → dsregcmd → event log → registro). Veja AutopilotChecker para a
        // pontuação. Mapeamos a confiança para o flag exibido:
        //   High   → Registrado       (evidência direta de MDM/Autopilot)
        //   Medium → NaoDeterminado   (sinais fortes, sem evidência direta)
        //   Low    → NaoRegistrado    (nenhuma evidência relevante)
        //   fontes indisponíveis → Indisponivel
        AutopilotStatus? apInfo = null;
        try
        {
            var checker = new AutopilotChecker(_wmi, _logger);
            apInfo = await checker.CheckAsync(token).ConfigureAwait(false);
            if (!apInfo.AnySourceAvailable)
            {
                autopilot = AvailabilityFlag.Indisponivel;
                reason ??= "fontes de Autopilot indisponíveis";
            }
            else
            {
                autopilot = apInfo.Confidence switch
                {
                    "High" => AvailabilityFlag.Registrado,
                    "Medium" => AvailabilityFlag.NaoDeterminado,
                    _ => AvailabilityFlag.NaoRegistrado,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha no checker de Autopilot");
            autopilot = AvailabilityFlag.Indisponivel;
        }

        return new SecurityFeatures(tpm, tpmState, tpmVersion, secureBoot, autopilot, reason, apInfo);
    }

    /// <summary>
    /// Detecta serial numbers placeholder/inválidos comuns em SMBIOS:
    /// "To be filled by O.E.M.", "System Serial Number", "Default string",
    /// "0", "None", "Not Available", "Not Specified", strings vazias.
    /// </summary>
    private static bool IsBogusSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return true;
        var s = serial.Trim();
        if (s.Length < 3) return true;
        string[] bogus =
        {
            "To be filled by O.E.M.", "System Serial Number", "Default string",
            "None", "0", "Not Available", "Not Specified", "Chassis Serial Number",
            "OEM", "INVALID", "0123456789",
        };
        foreach (var b in bogus)
        {
            if (string.Equals(s, b, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Tenta obter o serial via múltiplas fontes WMI/CIM em ordem de
    /// confiabilidade. Retorna o primeiro valor válido (não "bogus").
    /// </summary>
    private async Task<string?> TryGetSerialAsync(CancellationToken ct)
    {
        // 1. Win32_ComputerSystemProduct.IdentifyingNumber (fonte canônica nos OEMs)
        try
        {
            var rows = await _wmi.QueryAsync("root\\cimv2",
                "SELECT IdentifyingNumber, UUID FROM Win32_ComputerSystemProduct",
                TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                var s = r.GetString("IdentifyingNumber");
                if (!IsBogusSerial(s)) return s;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Win32_ComputerSystemProduct falhou"); }

        // 2. Win32_SystemEnclosure.SerialNumber
        try
        {
            var rows = await _wmi.QueryAsync("root\\cimv2",
                "SELECT SerialNumber, SMBIOSAssetTag FROM Win32_SystemEnclosure",
                TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                var s = r.GetString("SerialNumber");
                if (!IsBogusSerial(s)) return s;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Win32_SystemEnclosure falhou"); }

        // 3. Win32_BaseBoard.SerialNumber (placa-mãe)
        try
        {
            var rows = await _wmi.QueryAsync("root\\cimv2",
                "SELECT SerialNumber FROM Win32_BaseBoard",
                TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                var s = r.GetString("SerialNumber");
                if (!IsBogusSerial(s)) return s;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Win32_BaseBoard falhou"); }

        // 4. PowerShell: alguns drivers OEM (Dell Command, HP CMI) só respondem via
        //    cmdlets PnP. Última cartada antes de desistir.
        try
        {
            const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$x = Get-CimInstance -ClassName Win32_BIOS -ErrorAction SilentlyContinue
if ($x -and $x.SerialNumber) {
    [pscustomobject]@{ Serial = [string]$x.SerialNumber }
} else {
    $y = Get-CimInstance -ClassName Win32_ComputerSystemProduct -ErrorAction SilentlyContinue
    if ($y -and $y.IdentifyingNumber) {
        [pscustomobject]@{ Serial = [string]$y.IdentifyingNumber }
    }
}
";
            var rows = await _ps.InvokeAsync(script, null, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                var s = rows[0].GetString("Serial");
                if (!IsBogusSerial(s)) return s;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Get-CimInstance fallback falhou"); }

        return null;
    }

    /// <summary>
    /// Lê o estado do Secure Boot do registro do Windows — mesma fonte
    /// consultada pelo <c>msinfo32</c>:
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled</c>.
    /// </summary>
    /// <returns>
    /// (Habilitado/Desabilitado/Ausente, motivo) onde <c>Ausente</c> indica
    /// equipamento em modo BIOS legado (chave inexistente) e
    /// <c>Indisponivel</c> indica falha de leitura.
    /// </returns>
    private static (AvailabilityFlag State, string? Reason) ReadSecureBootFromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key is null)
            {
                // Sem a chave => firmware em modo BIOS legado, sem suporte a Secure Boot
                return (AvailabilityFlag.Ausente, "BIOS_LEGACY");
            }

            var raw = key.GetValue("UEFISecureBootEnabled");
            if (raw is null)
            {
                return (AvailabilityFlag.Ausente, "valor UEFISecureBootEnabled ausente");
            }

            // O valor canonicamente é REG_DWORD; aceitamos string numérica também por segurança.
            var enabled = raw switch
            {
                int i => i == 1,
                long l => l == 1,
                string s => int.TryParse(s, out var parsed) && parsed == 1,
                _ => false,
            };

            return (enabled ? AvailabilityFlag.Habilitado : AvailabilityFlag.Desabilitado, null);
        }
        catch (System.Security.SecurityException)
        {
            return (AvailabilityFlag.Indisponivel, "permissão");
        }
        catch (UnauthorizedAccessException)
        {
            return (AvailabilityFlag.Indisponivel, "permissão");
        }
        catch (Exception)
        {
            return (AvailabilityFlag.Indisponivel, "indisponível");
        }
    }

    public Task<DisplayInfo> CollectDisplayAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            string resolution = "Indisponível";
            var monitorIds = new List<string>();
            try
            {
                var monitors = _displays.EnumerateMonitors();
                if (monitors.Count > 0)
                {
                    var primary = monitors.FirstOrDefault(m => m.IsPrimary);
                    if (!primary.Equals(default(MonitorInfo)) && primary.WidthPixels > 0)
                    {
                        resolution = $"{primary.WidthPixels}x{primary.HeightPixels}";
                    }
                    else
                    {
                        var first = monitors[0];
                        if (first.WidthPixels > 0)
                        {
                            resolution = $"{first.WidthPixels}x{first.HeightPixels}";
                        }
                    }
                    foreach (var m in monitors)
                    {
                        monitorIds.Add(m.FriendlyName ?? m.DeviceName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EnumerateMonitors falhou");
            }

            // Saídas em uso com o conector (HDMI/DP/DVI/VGA) — é o que
            // descreve a "tela" de um desktop.
            var videoOutputs = new List<string>();
            try
            {
                foreach (var o in _displays.EnumerateOutputs()) videoOutputs.Add(o.Describe());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EnumerateOutputs falhou");
            }

            // Todos os adaptadores de vídeo via WMI + Win32_PnPEntity (cobre dGPU
            // desligada por Optimus/Switchable Graphics).
            var allAdapters = new List<string>();
            var gpuDetails = new List<GraphicsInfo>();
            // VRAM real por GPU (sem o teto de 4 GB do AdapterRAM uint32), lida do
            // registro das classes de display.
            var vramByName = ReadVramFromRegistry();
            try
            {
                var t = _wmi.QueryAsync("root\\cimv2",
                    "SELECT Name, AdapterCompatibility, AdapterRAM, DriverVersion, DriverDate FROM Win32_VideoController WHERE Name IS NOT NULL",
                    TimeSpan.FromSeconds(4), ct);
                if (t.Wait(TimeSpan.FromSeconds(5)) && t.IsCompletedSuccessfully)
                {
                    foreach (var row in t.Result)
                    {
                        var name = row.GetString("Name");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (IsBogusGpuName(name)) continue;
                        if (!allAdapters.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            allAdapters.Add(name);

                            // VRAM: prefere o registro (64-bit), cai para AdapterRAM.
                            int? vramMb = null;
                            var regVram = vramByName.FirstOrDefault(kv =>
                                name!.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0
                                || kv.Key.IndexOf(name!, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (regVram.Value > 0)
                            {
                                vramMb = (int)(regVram.Value / (1024 * 1024));
                            }
                            else
                            {
                                var ar = row.GetLong("AdapterRAM");
                                if (ar is > 0) vramMb = (int)(ar.Value / (1024 * 1024));
                            }

                            gpuDetails.Add(new GraphicsInfo(
                                Name: name!,
                                VramMb: vramMb,
                                DriverVersion: CleanText(row.GetString("DriverVersion")),
                                DriverDate: FormatWmiDate(row.GetString("DriverDate"))));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Win32_VideoController falhou");
            }

            // Suplementa com Win32_PnPEntity para dGPUs desligadas por Optimus.
            try
            {
                var t = _wmi.QueryAsync("root\\cimv2",
                    "SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Display'",
                    TimeSpan.FromSeconds(3), ct);
                if (t.Wait(TimeSpan.FromSeconds(4)) && t.IsCompletedSuccessfully)
                {
                    foreach (var row in t.Result)
                    {
                        var name = row.GetString("Name");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (IsBogusGpuName(name)) continue;
                        if (!allAdapters.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            allAdapters.Add(name);
                            // dGPU desligada (Optimus): sem VRAM/driver via WMI,
                            // mas registra o nome para não sumir da lista.
                            if (!gpuDetails.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
                            {
                                gpuDetails.Add(new GraphicsInfo(name!, null, null, null));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Win32_PnPEntity Display falhou");
            }

            // Adaptador "principal" mostrado em destaque: prefere a dedicada (RTX/GTX/RX)
            // sobre integrada (UHD/Iris/Vega/Radeon Graphics).
            string? primaryAdapter = null;
            int bestPriority = -1;
            foreach (var adapter in allAdapters)
            {
                var priority = GpuPriorityForDisplay(adapter);
                if (priority > bestPriority)
                {
                    bestPriority = priority;
                    primaryAdapter = adapter;
                }
            }
            primaryAdapter ??= allAdapters.FirstOrDefault();
            return new DisplayInfo(resolution, primaryAdapter, monitorIds, allAdapters, gpuDetails, videoOutputs);
        }, ct);
    }

    /// <summary>
    /// Lê a VRAM dedicada real (64-bit) de cada GPU do registro — contorna o
    /// teto de 4 GB do AdapterRAM (uint32) do WMI. Cada classe de display tem
    /// HardwareInformation.qwMemorySize (QWORD) + DriverDesc (nome).
    /// </summary>
    private Dictionary<string, long> ReadVramFromRegistry()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is null) return result;

            foreach (var sub in classKey.GetSubKeyNames())
            {
                if (!sub.StartsWith("0", StringComparison.Ordinal) && sub.Length != 4) continue;
                try
                {
                    using var k = classKey.OpenSubKey(sub);
                    if (k is null) continue;
                    var desc = k.GetValue("DriverDesc") as string;
                    if (string.IsNullOrWhiteSpace(desc)) continue;
                    var raw = k.GetValue("HardwareInformation.qwMemorySize");
                    long vram = raw switch
                    {
                        long l => l,
                        int i => i,
                        byte[] b when b.Length == 8 => BitConverter.ToInt64(b, 0),
                        _ => 0,
                    };
                    if (vram > 0) result[desc!] = vram;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReadVramFromRegistry falhou");
        }
        return result;
    }

    /// <summary>Converte data WMI (CIM_DATETIME "yyyymmddHHMMSS...") para "yyyy-MM-dd".</summary>
    private static string? FormatWmiDate(string? wmiDate)
    {
        if (string.IsNullOrWhiteSpace(wmiDate) || wmiDate.Length < 8) return null;
        var s = wmiDate.Trim();
        if (!s.Substring(0, 8).All(char.IsDigit)) return null;
        return $"{s.Substring(0, 4)}-{s.Substring(4, 2)}-{s.Substring(6, 2)}";
    }

    private static bool IsBogusGpuName(string name)
    {
        if (name.IndexOf("Microsoft Basic", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        // O Windows em português chama o mesmo adaptador de "Adaptador de Vídeo
        // Básico da Microsoft" — o filtro só pegava o nome em inglês, e numa
        // máquina SEM driver de vídeo esse fallback entrava no laudo como se
        // fosse a placa (NTB11378, Precision 7780, 21/07/2026).
        if (name.IndexOf("Vídeo Básico", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("Video Basico", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("Compatível com VGA", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("Compativel com VGA", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("Standard VGA", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("Microsoft Remote", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("Mirror", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("VirtualBox", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        // Adaptadores de display VIRTUAIS (não são placas físicas): Parsec,
        // monitores virtuais de streaming, IDD, Spacedesk, etc.
        if (name.IndexOf("Parsec", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("Virtual Display", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("Virtual Monitor", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("Spacedesk", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("IddSample", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("DisplayLink", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (name.IndexOf("Meta Virtual Monitor", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static int GpuPriorityForDisplay(string name)
    {
        if (name.IndexOf("RTX", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        if (name.IndexOf("GTX", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        if (name.IndexOf("Radeon RX", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        if (name.IndexOf("Radeon Pro", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        if (name.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.IndexOf("Arc A", StringComparison.OrdinalIgnoreCase) >= 0
            && !name.Contains("Arc Graphics", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.IndexOf("UHD Graphics", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        if (name.IndexOf("Iris", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        if (name.IndexOf("HD Graphics", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        if (name.IndexOf("Vega ", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        if (name.IndexOf("Radeon Graphics", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        return 0;
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SafeQueryAsync(
        string scope, string wql, TimeSpan timeout, CancellationToken ct)
    {
        var (rows, _) = await SafeQueryWithStatusAsync(scope, wql, timeout, ct).ConfigureAwait(false);
        return rows;
    }

    /// <summary>
    /// Como <see cref="SafeQueryAsync"/>, mas distingue "consulta vazia" (sucesso
    /// com 0 linhas) de "consulta falhou" (exceção/timeout). Necessário para não
    /// confundir falha de WMI com ausência real de hardware.
    /// </summary>
    private async Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, bool Failed)> SafeQueryWithStatusAsync(
        string scope, string wql, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var rows = await _wmi.QueryAsync(scope, wql, timeout, ct).ConfigureAwait(false);
            return (rows, false);
        }
        catch (WmiQueryException ex)
        {
            _logger.LogDebug(ex, "WMI {Scope} {Wql} falhou ({Cause})", scope, wql, ex.Cause);
            return (Array.Empty<IReadOnlyDictionary<string, object?>>(), true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WMI {Scope} {Wql} exceção", scope, wql);
            return (Array.Empty<IReadOnlyDictionary<string, object?>>(), true);
        }
    }

    /// <summary>
    /// Coleta o estado das senhas de BIOS via WMI dos namespaces dos OEMs:
    ///   - HP:     root\HP\InstrumentedBIOS  HPBIOS_BIOSPassword
    ///   - Dell:   root\dcim\sysman          DCIM_BIOSPassword  (Dell Command | Monitor)
    ///   - Lenovo: root\wmi                  Lenovo_BiosPasswordSettings
    ///
    /// Duas correções em relação à versão anterior:
    ///
    /// 1. O namespace da Lenovo é <c>root\wmi</c> — <c>root\Lenovo</c> não
    ///    existe. A leitura em Lenovo nunca funcionou.
    ///
    /// 2. A falha agora é CLASSIFICADA em vez de virar um "indisponível" genérico.
    ///    A causa mais comum não é o fabricante não ter suporte: é o app rodando
    ///    SEM elevação. As classes existem e respondem "acesso negado" para
    ///    usuário comum, e o catch genérico anterior transformava isso em
    ///    "leitura só em HP/Dell/Lenovo" — mensagem que aparecia justamente nas
    ///    Dell, onde deveria funcionar. Como o manifesto pede
    ///    <c>highestAvailable</c>, em conta de usuário padrão o app abre normal e
    ///    falha calado em toda máquina.
    /// </summary>
    private async Task<BiosSecurity?> CollectBiosSecurityAsync(CancellationToken ct)
    {
        var negouAcesso = false;
        var recursoAusente = false;

        void Classificar(WmiQueryException ex)
        {
            if (ex.Cause == WmiFailureCause.AccessDenied) negouAcesso = true;
            else if (ex.Cause is WmiFailureCause.NotPresent or WmiFailureCause.InvalidQuery) recursoAusente = true;
            _logger.LogDebug(ex, "BIOS password: {Causa} em {Scope}", ex.Cause, ex.Scope);
        }

        // HP — o estado está em HPBIOS_BIOSPassword (Name + IsSet); HP_BIOSSetting
        // não tem IsSet e a versão antiga reportava "sem senha" sempre.
        try
        {
            var rows = await _wmi.QueryAsync("root\\HP\\InstrumentedBIOS",
                "SELECT Name, IsSet FROM HPBIOS_BIOSPassword",
                TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                var setup = AvailabilityFlag.Ausente;
                var power = AvailabilityFlag.Ausente;
                foreach (var r in rows)
                {
                    var name = r.GetString("Name") ?? "";
                    var flag = CoerceIsSet(r) ? AvailabilityFlag.Habilitado : AvailabilityFlag.Desabilitado;
                    if (name.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Admin", StringComparison.OrdinalIgnoreCase) >= 0) setup = flag;
                    else if (name.IndexOf("Power-On", StringComparison.OrdinalIgnoreCase) >= 0
                          || name.IndexOf("Power On", StringComparison.OrdinalIgnoreCase) >= 0) power = flag;
                }
                return new BiosSecurity(setup, power, AvailabilityFlag.Indisponivel, "HPBIOS_BIOSPassword");
            }
        }
        catch (WmiQueryException ex) { Classificar(ex); }
        catch (Exception ex) { _logger.LogDebug(ex, "HP BIOS password indisponível"); }

        // Dell — precisa do Dell Command | Monitor instalado; sem ele o
        // namespace simplesmente não existe (NotPresent, não AccessDenied).
        try
        {
            var rows = await _wmi.QueryAsync("root\\dcim\\sysman",
                "SELECT AttributeName, IsSet FROM DCIM_BIOSPassword",
                TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                var setup = AvailabilityFlag.Ausente;
                var power = AvailabilityFlag.Ausente;
                var hdd = AvailabilityFlag.Ausente;
                foreach (var r in rows)
                {
                    var name = r.GetString("AttributeName") ?? "";
                    var flag = CoerceIsSet(r) ? AvailabilityFlag.Habilitado : AvailabilityFlag.Desabilitado;
                    if (name.IndexOf("Admin", StringComparison.OrdinalIgnoreCase) >= 0) setup = flag;
                    else if (name.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0) power = flag;
                    else if (name.IndexOf("Hdd", StringComparison.OrdinalIgnoreCase) >= 0) hdd = flag;
                }
                return new BiosSecurity(setup, power, hdd, "DCIM_BIOSPassword");
            }
        }
        catch (WmiQueryException ex) { Classificar(ex); }
        catch (Exception ex) { _logger.LogDebug(ex, "Dell BIOS password indisponível"); }

        // Lenovo — root\wmi (CORRIGIDO; root\Lenovo não existe). Mantém a
        // tentativa no namespace antigo por segurança, caso alguma linha exponha lá.
        foreach (var escopo in new[] { "root\\wmi", "root\\Lenovo" })
        {
            try
            {
                var rows = await _wmi.QueryAsync(escopo,
                    "SELECT PasswordState FROM Lenovo_BiosPasswordSettings",
                    TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
                if (rows.Count > 0)
                {
                    // PasswordState é um bitmask (doc oficial Lenovo):
                    //   bit 0 (0x01) = Power-On Password (POP)
                    //   bit 1 (0x02) = Supervisor/Admin Password (= "Setup")
                    //   bit 2 (0x04) = Hard Disk Password (user)
                    //   bit 3 (0x08) = Hard Disk Password (master)
                    var state = rows[0].GetInt("PasswordState") ?? 0;
                    AvailabilityFlag ToFlag(bool set) => set ? AvailabilityFlag.Habilitado : AvailabilityFlag.Desabilitado;
                    return new BiosSecurity(
                        HasSetupPassword: ToFlag((state & 0x02) != 0),
                        HasPowerOnPassword: ToFlag((state & 0x01) != 0),
                        HasHddPassword: ToFlag((state & 0x0C) != 0),
                        Source: $"Lenovo_BiosPasswordSettings ({escopo})");
                }
            }
            catch (WmiQueryException ex) { Classificar(ex); }
            catch (Exception ex) { _logger.LogDebug(ex, "Lenovo BIOS password indisponível em {Escopo}", escopo); }
        }

        // Dell sem o Dell Command | Monitor: tenta o Dell Command | PowerShell
        // Provider, que faz o mesmo e instala com um Install-Module. É o caso
        // comum aqui — máquina recondicionada é formatada e perde o OEM.
        var ehDell = await EhDellAsync(ct).ConfigureAwait(false);
        if (ehDell && ProcessoElevado())
        {
            if (await _dellBios.ModuloInstaladoAsync(ct).ConfigureAwait(false))
            {
                var r = await _dellBios.LerAsync(ct).ConfigureAwait(false);
                if (r.Leitura is not null) return r.Leitura;
                _logger.LogDebug("DellBIOSProvider instalado mas não leu: {Erro}", r.Erro);
            }
            else
            {
                // conserto de um clique: a tela oferece instalar e repetir
                return new BiosSecurity(
                    AvailabilityFlag.Indisponivel,
                    AvailabilityFlag.Indisponivel,
                    AvailabilityFlag.Indisponivel,
                    null,
                    BiosLeituraMotivo.DellSemProvider);
            }
        }

        // HP sem o software da HP: mesma história da Dell — o HPCMSL (HP Client
        // Management Script Library, também no PSGallery) faz a leitura.
        var ehHp = await EhFabricanteAsync("HP", ct).ConfigureAwait(false);
        if (ehHp && ProcessoElevado())
        {
            if (await _dellBios.ModuloInstaladoAsync(ct, DellBiosPasswordReader.NomeModuloHp).ConfigureAwait(false))
            {
                var r = await _dellBios.LerHpAsync(ct).ConfigureAwait(false);
                if (r.Leitura is not null) return r.Leitura;
                _logger.LogDebug("HPCMSL instalado mas não leu: {Erro}", r.Erro);
            }
            else
            {
                return new BiosSecurity(
                    AvailabilityFlag.Indisponivel,
                    AvailabilityFlag.Indisponivel,
                    AvailabilityFlag.Indisponivel,
                    null,
                    BiosLeituraMotivo.HpSemProvider);
            }
        }

        // Nada leu. O motivo muda a orientação dada ao técnico.
        var motivo = negouAcesso
            ? BiosLeituraMotivo.SemPrivilegio
            : recursoAusente
                ? BiosLeituraMotivo.FerramentaOemAusente
                : BiosLeituraMotivo.FabricanteSemSuporte;

        // Sem elevação a resposta do WMI é indistinguível de "não existe" em
        // alguns provedores, então a falta de privilégio ganha da falta de
        // ferramenta: é o problema mais provável e o mais fácil de resolver.
        if (!ProcessoElevado()) motivo = BiosLeituraMotivo.SemPrivilegio;
        else if (ehDell && motivo == BiosLeituraMotivo.FerramentaOemAusente)
        {
            motivo = BiosLeituraMotivo.DellSemProvider;
        }

        return new BiosSecurity(
            AvailabilityFlag.Indisponivel,
            AvailabilityFlag.Indisponivel,
            AvailabilityFlag.Indisponivel,
            null,
            motivo);
    }

    /// <summary>Decide se vale tentar o provider daquele fabricante.</summary>
    private async Task<bool> EhFabricanteAsync(string marca, CancellationToken ct)
    {
        try
        {
            var rows = await _wmi.QueryAsync("root\\cimv2",
                "SELECT Manufacturer FROM Win32_ComputerSystem",
                TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            var fab = rows.FirstOrDefault()?.GetString("Manufacturer") ?? "";
            return fab.IndexOf(marca, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Não deu para ler o fabricante");
            return false;
        }
    }

    private Task<bool> EhDellAsync(CancellationToken ct) => EhFabricanteAsync("Dell", ct);

    /// <summary>
    /// O processo está rodando elevado? O manifesto pede
    /// <c>highestAvailable</c>: em conta de administrador isso eleva, mas em
    /// conta de usuário padrão o app abre SEM privilégio e todo WMI de
    /// fabricante nega acesso.
    /// </summary>
    private static bool ProcessoElevado()
    {
        try
        {
            using var identidade = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identidade);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Lê o estado de "senha definida" de uma linha OEM. Os fabricantes expõem
    /// <c>IsSet</c> de formas diferentes: bool, int (0/1) ou string ("1"/"True").
    /// </summary>
    private static bool CoerceIsSet(IReadOnlyDictionary<string, object?> row)
    {
        if (!row.TryGetValue("IsSet", out var v) || v is null) return false;
        switch (v)
        {
            case bool b: return b;
            case int i: return i != 0;
            case uint u: return u != 0;
            case long l: return l != 0;
            case byte by: return by != 0;
            case string s:
                var t = s.Trim();
                if (string.Equals(t, "True", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(t, "Yes", StringComparison.OrdinalIgnoreCase)) return true;
                return int.TryParse(t, out var n) && n != 0;
            default: return false;
        }
    }

    /// <summary>
    /// Detecta o agente Absolute (Computrace). O sinal mais confiável para
    /// o módulo BIOS é o serviço Windows <c>rpcnetp</c>; o agente full
    /// registra <c>rpcnet</c>. Versão vem do registro.
    /// </summary>
    private async Task<ComputraceInfo?> CollectComputraceAsync(CancellationToken ct)
    {
        var moduleActive = AvailabilityFlag.Indisponivel;
        var agentInstalled = AvailabilityFlag.Ausente;
        string? serviceStatus = null;
        string? version = null;

        try
        {
            var rows = await _wmi.QueryAsync("root\\cimv2",
                "SELECT Name, State, StartMode FROM Win32_Service WHERE Name = 'rpcnetp' OR Name = 'rpcnet' OR Name = 'CTES' OR Name = 'CtesHostSvc'",
                TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                var name = r.GetString("Name") ?? "";
                var state = r.GetString("State") ?? "";
                if (string.Equals(name, "rpcnetp", StringComparison.OrdinalIgnoreCase))
                {
                    moduleActive = string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase)
                        ? AvailabilityFlag.Ativado : AvailabilityFlag.NaoAtivado;
                    serviceStatus ??= state;
                }
                else if (string.Equals(name, "rpcnet", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(name, "CTES", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(name, "CtesHostSvc", StringComparison.OrdinalIgnoreCase))
                {
                    agentInstalled = AvailabilityFlag.Presente;
                    serviceStatus ??= state;
                }
            }
            if (moduleActive == AvailabilityFlag.Indisponivel && agentInstalled == AvailabilityFlag.Presente)
            {
                moduleActive = AvailabilityFlag.Ativado;
            }
            else if (moduleActive == AvailabilityFlag.Indisponivel && rows.Count == 0)
            {
                moduleActive = AvailabilityFlag.NaoAtivado;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha consultando serviços Computrace");
        }

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Absolute Software\AbsoluteAgent");
            if (k is not null)
            {
                version = k.GetValue("Version")?.ToString();
                agentInstalled = AvailabilityFlag.Presente;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha lendo registro Absolute");
        }

        return new ComputraceInfo(moduleActive, agentInstalled, serviceStatus, version);
    }
}
