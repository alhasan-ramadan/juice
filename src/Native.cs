using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Juice;

/// <summary>Windows-Aufrufe für HID-Zugriff und Geräteeigenschaften (Configuration Manager).</summary>
static class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint ShareReadWrite = 0x3;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000;

    const int CrSuccess = 0;
    const int CrBufferSmall = 0x1A;
    const int CmGetIdListFilterEnumerator = 0x1;
    const int CmGetIdListFilterPresent = 0x100;
    const int CmGetDeviceInterfaceListPresent = 0x0;
    public const int HidpStatusSuccess = 0x00110000;

    public const uint DevPropTypeBoolean = 0x11;

    static Guid hidInterface = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    public struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DevPropKey(Guid fmtid, uint pid)
    {
        public Guid Fmtid = fmtid;
        public uint Pid = pid;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll")]
    static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);

    [DllImport("hid.dll")]
    static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr data, out HidpCaps caps);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid interfaceClass, string? deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, char[] buffer, int length, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_List_SizeW(out int length, string? filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, int length, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, byte[]? buffer, ref int size, int flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    /// <summary>Pfade aller angeschlossenen HID-Schnittstellen.</summary>
    public static List<string> HidInterfacePaths()
    {
        if (CM_Get_Device_Interface_List_SizeW(out int length, ref hidInterface, null, CmGetDeviceInterfaceListPresent) != CrSuccess || length <= 1)
            return [];
        var buffer = new char[length];
        if (CM_Get_Device_Interface_ListW(ref hidInterface, null, buffer, length, CmGetDeviceInterfaceListPresent) != CrSuccess)
            return [];
        return SplitMultiString(buffer);
    }

    /// <summary>Instanz-IDs aller angeschlossenen Geräte eines Enumerators (z. B. "BTHENUM").</summary>
    public static List<string> PresentDeviceIds(string enumerator)
    {
        const int flags = CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;
        if (CM_Get_Device_ID_List_SizeW(out int length, enumerator, flags) != CrSuccess || length <= 1)
            return [];
        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW(enumerator, buffer, length, flags) != CrSuccess)
            return [];
        return SplitMultiString(buffer);
    }

    /// <summary>Liest eine Geräteeigenschaft roh; null, wenn das Gerät sie nicht hat.</summary>
    public static byte[]? DeviceProperty(string deviceId, DevPropKey key, out uint type)
    {
        type = 0;
        if (CM_Locate_DevNodeW(out uint devInst, deviceId, 0) != CrSuccess)
            return null;
        int size = 0;
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out type, null, ref size, 0) != CrBufferSmall || size == 0)
            return null;
        var buffer = new byte[size];
        return CM_Get_DevNode_PropertyW(devInst, ref key, out type, buffer, ref size, 0) == CrSuccess ? buffer : null;
    }

    public static string? DeviceStringProperty(string deviceId, DevPropKey key)
    {
        var data = DeviceProperty(deviceId, key, out _);
        return data is null ? null : System.Text.Encoding.Unicode.GetString(data).TrimEnd('\0');
    }

    static readonly DevPropKey ParentKey = new(new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), 8);

    /// <summary>Instanz-ID des übergeordneten Geräteknotens.</summary>
    public static string? ParentOf(string deviceId) => DeviceStringProperty(deviceId, ParentKey);

    /// <summary>
    /// Instanz-ID zu einem Schnittstellenpfad:
    /// \\?\hid#vid_1b1c&amp;pid_1bdc&amp;mi_02#9&amp;22d58272&amp;0&amp;0000#{guid}  ->  HID\VID_1B1C&amp;PID_1BDC&amp;MI_02\9&amp;22D58272&amp;0&amp;0000
    /// </summary>
    public static string? InstanceIdOf(string interfacePath)
    {
        var parts = interfacePath[4..].Split('#');
        return parts.Length < 3 ? null : $@"{parts[0]}\{parts[1]}\{parts[2]}".ToUpperInvariant();
    }

    /// <summary>Usage-Page, Usage und Report-Längen einer HID-Schnittstelle, ohne Lese- oder Schreibrechte.</summary>
    public static HidpCaps? HidCaps(string path)
    {
        using var handle = CreateFile(path, 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out var data))
            return null;
        try
        {
            return HidP_GetCaps(data, out var caps) == HidpStatusSuccess ? caps : null;
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }

    static List<string> SplitMultiString(char[] buffer) =>
        new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
}
