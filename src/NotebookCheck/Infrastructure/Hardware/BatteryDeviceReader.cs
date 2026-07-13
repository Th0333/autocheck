using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NotebookCheck.Infrastructure.Hardware;

/// <summary>
/// Dados ricos de uma bateria física, lidos diretamente da API oficial de
/// bateria do Windows (mesma fonte usada por utilitários como o BatteryInfoView):
/// enumera os devices da classe <c>GUID_DEVCLASS_BATTERY</c> via SetupAPI e
/// consulta cada um com <c>DeviceIoControl</c> + <c>IOCTL_BATTERY_QUERY_*</c>.
/// </summary>
public sealed record BatteryDeviceData(
    string? Name,
    string? Manufacturer,
    string? SerialNumber,
    string? Chemistry,
    int? DesignCapacityMwh,
    int? FullChargeCapacityMwh,
    int? RemainingCapacityMwh,
    int? VoltageMv,
    int? RateMw,
    int? CycleCount,
    bool OnLine,
    bool Charging,
    bool Discharging,
    bool Critical,
    DateTime? ManufactureDate = null);

/// <summary>
/// Leitor da API nativa de bateria do Windows. Totalmente isolado em P/Invoke
/// com degradação graciosa: qualquer falha resulta em lista vazia, deixando os
/// fallbacks WMI/powercfg assumirem.
/// </summary>
public static class BatteryDeviceReader
{
    // GUID_DEVCLASS_BATTERY {72631e54-78a4-11d0-bcf7-00aa00b7b32a}
    private static readonly Guid GuidDeviceBattery =
        new(0x72631e54, 0x78a4, 0x11d0, 0xbc, 0xf7, 0x00, 0xaa, 0x00, 0xb7, 0xb3, 0x2a);

    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;

    // IOCTLs (FILE_DEVICE_BATTERY = 0x29, METHOD_BUFFERED = 0).
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    private const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
    private const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404c;

    // BATTERY_QUERY_INFORMATION_LEVEL
    private const int BatteryInformation = 0;
    private const int BatteryDeviceName = 4;
    private const int BatteryManufactureDate = 5;
    private const int BatteryManufactureName = 6;
    private const int BatterySerialNumber = 8;

    // PowerState bits
    private const uint BATTERY_POWER_ON_LINE = 0x00000001;
    private const uint BATTERY_DISCHARGING = 0x00000002;
    private const uint BATTERY_CHARGING = 0x00000004;
    private const uint BATTERY_CRITICAL = 0x00000008;

    private const uint BATTERY_UNKNOWN_CAPACITY = 0xFFFFFFFF;
    private const uint BATTERY_UNKNOWN_VOLTAGE = 0xFFFFFFFF;
    private const int BATTERY_UNKNOWN_RATE = unchecked((int)0x80000000);

    /// <summary>
    /// Lê todas as baterias físicas presentes. Nunca lança: em erro retorna
    /// lista vazia.
    /// </summary>
    public static IReadOnlyList<BatteryDeviceData> ReadAll()
    {
        var result = new List<BatteryDeviceData>();
        var guid = GuidDeviceBattery;
        var hdev = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hdev == new IntPtr(-1)) return result;

        try
        {
            for (uint i = 0; ; i++)
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(hdev, IntPtr.Zero, ref guid, i, ref did))
                {
                    break; // ERROR_NO_MORE_ITEMS encerra a enumeração
                }

                var path = GetDevicePath(hdev, ref did);
                if (string.IsNullOrEmpty(path)) continue;

                try
                {
                    var data = ReadOne(path!);
                    if (data is not null) result.Add(data);
                }
                catch
                {
                    // Uma bateria problemática não derruba as demais.
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hdev);
        }

        return result;
    }

    private static BatteryDeviceData? ReadOne(string path)
    {
        using var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        // 1. Tag da bateria (necessário para todas as outras queries).
        uint tag = 0;
        uint wait = 0;
        if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG,
                ref wait, sizeof(uint), out tag, sizeof(uint), out _, IntPtr.Zero) || tag == 0)
        {
            return null; // Sem bateria neste slot (tag 0 = ausente).
        }

        // 2. BATTERY_INFORMATION (capacidades, química, ciclos).
        var info = QueryInformation(h, tag);
        int? design = null, full = null, cycles = null;
        string? chem = null;
        if (info.HasValue)
        {
            var bi = info.Value;
            design = bi.DesignedCapacity > 0 ? (int)bi.DesignedCapacity : null;
            full = bi.FullChargedCapacity > 0 ? (int)bi.FullChargedCapacity : null;
            cycles = bi.CycleCount > 0 ? (int)bi.CycleCount : null;
            chem = DecodeChemistry(bi.Chemistry);
        }

        // 3. BATTERY_STATUS (estado, carga atual, voltagem, taxa).
        int? remaining = null, voltage = null, rate = null;
        bool onLine = false, charging = false, discharging = false, critical = false;
        var st = QueryStatus(h, tag);
        if (st.HasValue)
        {
            var bs = st.Value;
            if (bs.Capacity != BATTERY_UNKNOWN_CAPACITY) remaining = (int)bs.Capacity;
            if (bs.Voltage != BATTERY_UNKNOWN_VOLTAGE) voltage = (int)bs.Voltage;
            if (bs.Rate != BATTERY_UNKNOWN_RATE) rate = bs.Rate;
            onLine = (bs.PowerState & BATTERY_POWER_ON_LINE) != 0;
            charging = (bs.PowerState & BATTERY_CHARGING) != 0;
            discharging = (bs.PowerState & BATTERY_DISCHARGING) != 0;
            critical = (bs.PowerState & BATTERY_CRITICAL) != 0;
        }

        // 4. Strings (nome do device, fabricante, serial).
        var name = QueryString(h, tag, BatteryDeviceName);
        var manufacturer = QueryString(h, tag, BatteryManufactureName);
        var serial = QueryString(h, tag, BatterySerialNumber);

        // 5. Data de fabricação (nem todo pack expõe — null é normal).
        var manufactureDate = QueryManufactureDate(h, tag);

        return new BatteryDeviceData(
            Name: name,
            Manufacturer: manufacturer,
            SerialNumber: serial,
            Chemistry: chem,
            DesignCapacityMwh: design,
            FullChargeCapacityMwh: full,
            RemainingCapacityMwh: remaining,
            VoltageMv: voltage,
            RateMw: rate,
            CycleCount: cycles,
            OnLine: onLine,
            Charging: charging,
            Discharging: discharging,
            Critical: critical,
            ManufactureDate: manufactureDate);
    }

    /// <summary>
    /// BATTERY_MANUFACTURE_DATE (nível 5). Sinal importante na perícia de
    /// bateria recondicionada: data antiga com contador de ciclos zerado
    /// indica controlador resetado.
    /// </summary>
    private static DateTime? QueryManufactureDate(SafeFileHandle h, uint tag)
    {
        var q = new BATTERY_QUERY_INFORMATION
        {
            BatteryTag = tag,
            InformationLevel = BatteryManufactureDate,
            AtRate = 0,
        };
        int size = Marshal.SizeOf<BATTERY_MANUFACTURE_DATE>();
        var inPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BATTERY_QUERY_INFORMATION>());
        var outPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(q, inPtr, false);
            if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION,
                    inPtr, (uint)Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(),
                    outPtr, (uint)size, out var returned, IntPtr.Zero) || returned == 0)
            {
                return null;
            }
            var d = Marshal.PtrToStructure<BATTERY_MANUFACTURE_DATE>(outPtr);
            if (d.Year < 1990 || d.Year > 2100 || d.Month is < 1 or > 12 || d.Day is < 1 or > 31)
            {
                return null; // pack não preenche ou devolve lixo
            }
            try
            {
                return new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc);
            }
            catch
            {
                return null; // dia inválido para o mês (ex.: 31/02)
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            Marshal.FreeHGlobal(outPtr);
        }
    }

    private static BATTERY_INFORMATION? QueryInformation(SafeFileHandle h, uint tag)
    {
        var q = new BATTERY_QUERY_INFORMATION
        {
            BatteryTag = tag,
            InformationLevel = BatteryInformation,
            AtRate = 0,
        };
        int size = Marshal.SizeOf<BATTERY_INFORMATION>();
        var outPtr = Marshal.AllocHGlobal(size);
        var inPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BATTERY_QUERY_INFORMATION>());
        try
        {
            Marshal.StructureToPtr(q, inPtr, false);
            if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION,
                    inPtr, (uint)Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(),
                    outPtr, (uint)size, out _, IntPtr.Zero))
            {
                return null;
            }
            return Marshal.PtrToStructure<BATTERY_INFORMATION>(outPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(outPtr);
            Marshal.FreeHGlobal(inPtr);
        }
    }

    private static BATTERY_STATUS? QueryStatus(SafeFileHandle h, uint tag)
    {
        var ws = new BATTERY_WAIT_STATUS { BatteryTag = tag };
        int size = Marshal.SizeOf<BATTERY_STATUS>();
        var inPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BATTERY_WAIT_STATUS>());
        var outPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(ws, inPtr, false);
            if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_STATUS,
                    inPtr, (uint)Marshal.SizeOf<BATTERY_WAIT_STATUS>(),
                    outPtr, (uint)size, out _, IntPtr.Zero))
            {
                return null;
            }
            return Marshal.PtrToStructure<BATTERY_STATUS>(outPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            Marshal.FreeHGlobal(outPtr);
        }
    }

    private static string? QueryString(SafeFileHandle h, uint tag, int level)
    {
        var q = new BATTERY_QUERY_INFORMATION
        {
            BatteryTag = tag,
            InformationLevel = level,
            AtRate = 0,
        };
        const int bufSize = 512; // bytes; nomes de bateria são curtos
        var inPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BATTERY_QUERY_INFORMATION>());
        var outPtr = Marshal.AllocHGlobal(bufSize);
        try
        {
            Marshal.StructureToPtr(q, inPtr, false);
            if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION,
                    inPtr, (uint)Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(),
                    outPtr, bufSize, out var returned, IntPtr.Zero) || returned == 0)
            {
                return null;
            }
            var s = Marshal.PtrToStringUni(outPtr);
            return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            Marshal.FreeHGlobal(outPtr);
        }
    }

    /// <summary>Decodifica o código de química de 4 chars ASCII do BATTERY_INFORMATION.</summary>
    private static string? DecodeChemistry(byte[] chem)
    {
        if (chem is null || chem.Length < 2) return null;
        var code = System.Text.Encoding.ASCII.GetString(chem).TrimEnd('\0', ' ').ToUpperInvariant();
        return code switch
        {
            "PBAC" => "Chumbo-ácido",
            "LION" or "LI-I" => "Lítio-íon",
            "LIP" or "LI-P" => "Lítio-polímero",
            "NICD" => "Níquel-cádmio",
            "NIMH" => "Níquel-metal hidreto",
            "NIZN" => "Níquel-zinco",
            "RAM" => "RAM (alcalina recarregável)",
            "" => null,
            _ => code,
        };
    }

    private static string? GetDevicePath(IntPtr hdev, ref SP_DEVICE_INTERFACE_DATA did)
    {
        // Primeira chamada descobre o tamanho necessário.
        SetupDiGetDeviceInterfaceDetail(hdev, ref did, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0) return null;

        var detail = Marshal.AllocHGlobal((int)required);
        try
        {
            // cbSize do header SP_DEVICE_INTERFACE_DETAIL_DATA: 8 em x64 (4 + 4 padding),
            // 6 em x86. Como compilamos win-x64, usamos 8.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(hdev, ref did, detail, required, out _, IntPtr.Zero))
            {
                return null;
            }
            // O caminho começa logo após o campo cbSize (offset 4).
            return Marshal.PtrToStringUni(detail + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    // ---- structs nativas ----

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_QUERY_INFORMATION
    {
        public uint BatteryTag;
        public int InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_INFORMATION
    {
        public uint Capabilities;
        public byte Technology;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] Chemistry;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_MANUFACTURE_DATE
    {
        public byte Day;
        public byte Month;
        public ushort Year;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_WAIT_STATUS
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_STATUS
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    // ---- P/Invoke ----

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint detailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode,
        ref uint inBuffer, int inBufferSize,
        out uint outBuffer, int outBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode,
        IntPtr inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);
}
