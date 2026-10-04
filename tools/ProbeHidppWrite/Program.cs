using System.Runtime.InteropServices;

internal static class Program
{
    const ushort Vid = 0x046D;
    const ushort Pid = 0xC262;
    const uint DIGCF_PRESENT = 0x2;
    const uint DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 0x1;
    const uint FILE_SHARE_WRITE = 0x2;
    const uint OPEN_EXISTING = 3;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    static int Main()
    {
        var paths = EnumHidPaths().Where(p =>
            p.Contains("VID_046D", StringComparison.OrdinalIgnoreCase) &&
            p.Contains("PID_C262", StringComparison.OrdinalIgnoreCase)).ToList();

        Console.WriteLine($"Found {paths.Count} HID path(s) for C262:");
        foreach (var p in paths) Console.WriteLine("  " + p);

        if (paths.Count == 0)
        {
            Console.WriteLine("FAIL: no C262 HID interfaces. Start the bridge first.");
            return 1;
        }

        var candidates = paths
            .OrderByDescending(p => p.Contains("Col02", StringComparison.OrdinalIgnoreCase) ||
                                    p.Contains("COL02", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.Contains("Col03", StringComparison.OrdinalIgnoreCase) ||
                                   p.Contains("COL03", StringComparison.OrdinalIgnoreCase))
            .ToList();

        byte[] ping =
        [
            0x11, 0xFF, 0x00, 0x11, 0x00, 0x00, 0xAA,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        ];

        foreach (var path in candidates)
        {
            Console.WriteLine($"\nTrying: {path}");
            var handle = CreateFileW(path, GENERIC_WRITE | GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == INVALID_HANDLE_VALUE)
            {
                Console.WriteLine($"  CreateFile failed: {Marshal.GetLastWin32Error()}");
                continue;
            }

            try
            {
                if (HidD_SetOutputReport(handle, ping, ping.Length))
                    Console.WriteLine("  HidD_SetOutputReport 0x11 OK");
                else
                    Console.WriteLine($"  HidD_SetOutputReport 0x11 failed: {Marshal.GetLastWin32Error()}");

                if (WriteFile(handle, ping, ping.Length, out var written, IntPtr.Zero))
                    Console.WriteLine($"  WriteFile 0x11 OK ({written} bytes)");
                else
                    Console.WriteLine($"  WriteFile 0x11 failed: {Marshal.GetLastWin32Error()}");

                var longPing = new byte[64];
                longPing[0] = 0x12;
                longPing[1] = 0xFF;
                longPing[2] = 0x00;
                longPing[3] = 0x11;
                longPing[6] = 0xBB;
                if (HidD_SetOutputReport(handle, longPing, longPing.Length))
                    Console.WriteLine("  HidD_SetOutputReport 0x12 OK");
                else
                    Console.WriteLine($"  HidD_SetOutputReport 0x12 failed: {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        Console.WriteLine("\nCheck emulator FFB diagnostics for Host writes / HID++ writes.");
        return 0;
    }

    static IEnumerable<string> EnumHidPaths()
    {
        var hidGuid = Guid.Empty;
        HidD_GetHidGuid(ref hidGuid);
        var info = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (info == INVALID_HANDLE_VALUE) yield break;

        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
            {
                uint needed = 0;
                SetupDiGetDeviceInterfaceDetail(info, ref iface, IntPtr.Zero, 0, ref needed, IntPtr.Zero);
                if (needed == 0) continue;
                var detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(info, ref iface, detail, needed, ref needed, IntPtr.Zero))
                        continue;
                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (!string.IsNullOrEmpty(path))
                        yield return path;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(info); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(ref Guid hidGuid);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetOutputReport(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, ref uint requiredSize, IntPtr deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);
}
