using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NotebookCheck.Infrastructure.Abstractions;

namespace NotebookCheck.Infrastructure.Hardware;

/// <summary>
/// Implementação real de <see cref="IDisplayEnumerator"/> baseada em
/// <c>EnumDisplayMonitors</c> + <c>EnumDisplayDevices</c> (user32). Atende
/// Requirements 7.4, 7.5 e 12.
/// </summary>
public sealed class DisplayEnumerator : IDisplayEnumerator
{
    public IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();
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
                            // primary + sem flag de attached só costuma estar no painel interno;
                            // heurística simples: primário = interno; demais = externos.
                            kind = isPrimary ? MonitorKind.Internal : MonitorKind.External;
                        }
                    }
                    catch
                    {
                        // ignora — mantém kind Unknown
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

    private const int MONITORINFOF_PRIMARY = 0x00000001;

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

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}
