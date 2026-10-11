namespace SteeringWheelEmulator.Core.Models;

/// <summary>Virtual wheel identity: HID identity, report packing, and bind catalog.</summary>
public interface IEmulatedDeviceProfile
{
    EmulatedDeviceKind Kind { get; }
    string DisplayName { get; }
    ushort VendorId { get; }
    ushort ProductId { get; }
    ushort VersionNumber { get; }
    string Manufacturer { get; }
    string Product { get; }
    Guid ContainerId { get; }

    /// <summary>DirectInput OEM registry key name, e.g. <c>VID_046D&amp;PID_C262</c>.</summary>
    string OemKeyName { get; }

    IControlCatalog Catalog { get; }

    /// <summary>Build a HID input report for the virtual device.</summary>
    byte[] BuildReport(MappedG920State state);

    /// <summary>Empty / centered report used before the first mapped frame.</summary>
    byte[] BuildIdleReport();
}
