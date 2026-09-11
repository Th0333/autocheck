using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NotebookCheck.Infrastructure.Abstractions;

namespace NotebookCheck.Infrastructure.Hardware;

/// <summary>
/// Implementação real de <see cref="IDisplayEnumerator"/> baseada em
/// <c>EnumDisplayMonitors</c> + <c>EnumDisplayDevices</c> (user32) para a
/// geometria, e <c>QueryDisplayConfig</c> para saber por QUAL CONECTOR cada
/// monitor está ligado (HDMI, DisplayPort, DVI, VGA, painel interno).
///
/// Antes a classificação interno/externo era "primário = interno": num
/// desktop o único monitor é sempre primário, então ele virava "tela
/// interna", o HDMI nunca contava e o site mostrava o desktop com "tela
/// Full HD". Agora o conector decide: LVDS/eDP/INTERNAL = painel do
/// notebook; qualquer outro = monitor externo.
///
/// Limite: o Windows só enumera saídas COM monitor ligado. Portas HDMI/DP
/// vazias não aparecem em API nenhuma — por isso o app fala em "saídas em
/// uso", não em "portas disponíveis".
/// </summary>
public sealed class DisplayEnumerator : IDisplayEnumerator
{
    public IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();

        // Mapa \\.\DISPLAYn → saída (conector) para classificar de verdade.
        Dictionary<string, VideoOutputInfo> byGdi;
        try
        {
            byGdi = EnumerateOutputs()
                .Where(o => !string.IsNullOrWhiteSpace(o.GdiDeviceName))
                .GroupBy(o => o.GdiDeviceName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            byGdi = new Dictionary<string, VideoOutputInfo>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr lParam) =>
            {
                var info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(info);
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    var width = info.rcMonitor.Right - info.rcMonitor.Left;
                    var height = info.rcMonitor.Bottom - info.rcMonitor.Top;
                    var isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;

                    string deviceName = info.szDevice;
                    string? friendly = null;
                    var kind = MonitorKind.Unknown;
                    try
                    {
                        var dd = new DISPLAY_DEVICE();
                        dd.cb = Marshal.SizeOf(dd);
                        if (EnumDisplayDevices(deviceName, 0, ref dd, 0))
                        {
                            friendly = dd.DeviceString;
                            // Heurística antiga (primário = interno) só como último recurso.
                            kind = isPrimary ? MonitorKind.Internal : MonitorKind.External;
                        }
                    }
                    catch
                    {
                        // ignora — mantém kind Unknown
                    }

                    if (byGdi.TryGetValue(deviceName, out var output))
                    {
                        kind = output.IsInternal ? MonitorKind.Internal : MonitorKind.External;
                        if (!string.IsNullOrWhiteSpace(output.MonitorName)
                            && (string.IsNullOrWhiteSpace(friendly)
                                || friendly.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) >= 0
                                || friendly.IndexOf("Genérico", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            friendly = output.MonitorName;
                        }
                    }

                    monitors.Add(new MonitorInfo(deviceName, friendly, width, height, isPrimary, kind));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // se a P/Invoke falhar, devolve o que já foi coletado (possivelmente vazio)
        }

        return monitors;
    }

    public IReadOnlyList<VideoOutputInfo> EnumerateOutputs()
    {
        var list = new List<VideoOutputInfo>();
        try
        {
            const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
                return list;
            if (pathCount == 0) return list;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[Math.Max(modeCount, 1)];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return list;

            for (var i = 0; i < pathCount; i++)
            {
                var p = paths[i];

                // Nome do monitor + tecnologia do conector (DISPLAYCONFIG_TARGET_DEVICE_NAME).
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                target.header.adapterId = p.targetInfo.adapterId;
                target.header.id = p.targetInfo.id;
                var friendly = "";
                var tech = p.targetInfo.outputTechnology;
                if (DisplayConfigGetDeviceInfo(ref target) == 0)
                {
                    friendly = (target.monitorFriendlyDeviceName ?? "").Trim();
                    tech = target.outputTechnology;
                }

                // \\.\DISPLAYn correspondente (DISPLAYCONFIG_SOURCE_DEVICE_NAME).
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                source.header.adapterId = p.sourceInfo.adapterId;
                source.header.id = p.sourceInfo.id;
                var gdi = DisplayConfigGetDeviceInfo(ref source) == 0 ? (source.viewGdiDeviceName ?? "").Trim() : "";

                // Resolução do modo de origem.
                int w = 0, h = 0;
                var idx = p.sourceInfo.modeInfoIdx;
                if (idx < modeCount && modes[idx].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                {
                    w = (int)modes[idx].sourceWidth;
                    h = (int)modes[idx].sourceHeight;
                }

                list.Add(new VideoOutputInfo(
                    Connector: ConnectorLabel(tech),
                    MonitorName: string.IsNullOrWhiteSpace(friendly) ? "Monitor" : friendly,
                    Width: w,
                    Height: h,
                    IsInternal: IsInternalTechnology(tech),
                    GdiDeviceName: gdi));
            }
        }
        catch
        {
            // Sem QueryDisplayConfig (sessão remota, driver básico): devolve o que deu.
        }
        return list;
    }

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY
    private static bool IsInternalTechnology(uint tech) => tech is 6 or 11 or 13 or 0x80000000;

    private static string ConnectorLabel(uint tech) => tech switch
    {
        0 => "VGA",
        1 => "S-Video",
        2 => "Composite",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        6 => "Painel interno",
        9 => "SDI",
        10 => "DisplayPort",
        11 => "Painel interno",
        12 => "UDI",
        13 => "Painel interno",
        15 => "Miracast",
        16 => "USB (indireto)",
        17 => "Virtual",
        18 => "DisplayPort (USB-C)",
        0x80000000 => "Painel interno",
        _ => "Outro",
    };

    // ---------------------------------------------------------------- P/Invoke

    private const int MONITORINFOF_PRIMARY = 0x00000001;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>
    /// Só os campos que lemos (modo de ORIGEM: largura/altura). A união
    /// nativa tem 48 bytes e começa no offset 16; o struct inteiro tem 64.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public uint sourceWidth;
        [FieldOffset(20)] public uint sourceHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME deviceName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME deviceName);
}
