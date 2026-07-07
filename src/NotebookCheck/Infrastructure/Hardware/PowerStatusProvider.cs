using System.Runtime.InteropServices;
using NotebookCheck.Infrastructure.Abstractions;

namespace NotebookCheck.Infrastructure.Hardware;

/// <summary>
/// Implementação real de <see cref="IPowerStatusProvider"/> sobre a P/Invoke
/// <c>GetSystemPowerStatus</c> (kernel32). Usada por <c>RunChargerAsync</c>
/// (Requirement 13).
/// </summary>
public sealed class PowerStatusProvider : IPowerStatusProvider
{
    public PowerStatus GetStatus()
    {
        if (!GetSystemPowerStatus(out var s))
        {
            return PowerStatus.Unknown;
        }

        var ac = s.ACLineStatus switch
        {
            0 => AcLineStatus.Offline,
            1 => AcLineStatus.Online,
            _ => AcLineStatus.Unknown,
        };

        var hasBattery = (s.BatteryFlag & 128) == 0; // bit 7 set = no battery
        var charge = s.BatteryLifePercent <= 100 ? (int?)s.BatteryLifePercent : null;
        var flags = (BatteryFlags)s.BatteryFlag;

        return new PowerStatus(ac, hasBattery, charge, flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
