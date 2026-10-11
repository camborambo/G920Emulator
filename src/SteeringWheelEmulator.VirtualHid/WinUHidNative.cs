using System.Runtime.InteropServices;

namespace SteeringWheelEmulator.VirtualHid;

public static class WinUHidNative
{
    public const string DllName = "WinUHid.dll";

    [Flags]
    public enum EventType : int
    {
        None = 0,
        GetFeature = 0x1,
        SetFeature = 0x2,
        WriteReport = 0x4,
        ReadReport = 0x8,
    }

    // Must match WINUHID_DEVICE_CONFIG in WinUHid.h (pshpack1).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DeviceConfig
    {
        public EventType SupportedEvents;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
        public ushort ReportDescriptorLength;
        public IntPtr ReportDescriptor;
        public Guid ContainerId;
        public IntPtr InstanceID;
        public IntPtr HardwareIDs;
        public uint ReadReportPeriodUs;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCallback(IntPtr callbackContext, IntPtr device, IntPtr evt);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern uint WinUHidGetDriverInterfaceVersion();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern IntPtr WinUHidCreateDevice(ref DeviceConfig config);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUHidSubmitInputReport(IntPtr device, byte[] report, uint reportSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUHidStartDevice(IntPtr device, EventCallback? callback, IntPtr callbackContext);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void WinUHidCompleteWriteEvent(IntPtr device, IntPtr evt, [MarshalAs(UnmanagedType.Bool)] bool success);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void WinUHidCompleteReadEvent(IntPtr device, IntPtr evt, byte[]? data, uint dataLength);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void WinUHidStopDevice(IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void WinUHidDestroyDevice(IntPtr device);

    public static bool TryProbeDriver(out string message)
    {
        try
        {
            var version = WinUHidGetDriverInterfaceVersion();
            if (version == 0)
            {
                var err = Marshal.GetLastWin32Error();
                message = err switch
                {
                    5 => "WinUHid is installed but access was denied. Run Steering Wheel Emulator as Administrator (required by the WinUHid device ACL).",
                    2 => "WinUHid device not found. Click Install WinUHid, then Recheck.",
                    _ => $"WinUHid driver not available (error {err}). See docs/driver-install.md.",
                };
                return false;
            }

            message = $"WinUHid driver interface v{version}";
            return true;
        }
        catch (DllNotFoundException)
        {
            message = "WinUHid.dll not found. Copy it next to SteeringWheelEmulator.exe and install the WinUHid driver. See docs/driver-install.md.";
            return false;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }
}
