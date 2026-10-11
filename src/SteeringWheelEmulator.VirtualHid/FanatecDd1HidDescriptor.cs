using SteeringWheelEmulator.Core.Models;

namespace SteeringWheelEmulator.VirtualHid;

/// <summary>
/// Bootstrap HID report descriptors for virtual Fanatec DD1 PC Comp.
/// Matches <see cref="SteeringWheelEmulator.Core.Mapping.FanatecDd1ReportBuilder"/> packing.
/// </summary>
public static class FanatecDd1HidDescriptor
{
    public const ushort VendorId = FanatecDd1Identity.VendorId;
    public const ushort ProductIdPcComp = FanatecDd1Identity.ProductIdPcComp;

    public const ushort VersionNumber = FanatecDd1Identity.VersionNumber;

    public const string Manufacturer = "Fanatec";
    /// <summary>Match live physical DI product string.</summary>
    public const string ProductPcComp = "FANATEC Podium Wheel Base DD1";

    public static readonly Guid ContainerIdPcComp = new("0EB70004-DD01-4C01-9A01-0EB700040001");

    // Joystick report id 1: 32 buttons + hat + axes matching live PC Comp DI:
    //   X Wheel, Y Combined Pedals, Z Accelerator, Rz Brake, Slider Clutch, Dial Handbrake.
    // Vendor output report id 2 in a separate application collection (G920-style).
    // Unit/Physical reset after hat — Eng Rot left on 16-bit axes → WinUHid err=552.
    public static readonly byte[] Bytes =
    [
        0x05, 0x01,       // Usage Page (Generic Desktop)
        0x09, 0x04,       // Usage (Joystick)
        0xA1, 0x01,       // Collection (Application)
        0x85, 0x01,       //   Report ID 1
        0x05, 0x09,       //   Usage Page (Button)
        0x19, 0x01,       //   Usage Minimum (1)
        0x29, 0x20,       //   Usage Maximum (32)
        0x15, 0x00,       //   Logical Minimum (0)
        0x25, 0x01,       //   Logical Maximum (1)
        0x75, 0x01,       //   Report Size (1)
        0x95, 0x20,       //   Report Count (32)
        0x81, 0x02,       //   Input (Data,Var,Abs)
        0x05, 0x01,       //   Usage Page (Generic Desktop)
        0x09, 0x39,       //   Usage (Hat switch)
        0x15, 0x00,       //   Logical Minimum (0)
        0x25, 0x07,       //   Logical Maximum (7)
        0x35, 0x00,       //   Physical Minimum (0)
        0x46, 0x3B, 0x01, //   Physical Maximum (315)
        0x65, 0x14,       //   Unit (Eng Rot: Angular Pos)
        0x75, 0x04,       //   Report Size (4)
        0x95, 0x01,       //   Report Count (1)
        0x81, 0x42,       //   Input (Data,Var,Abs,Null)
        0x75, 0x04,       //   Report Size (4) padding
        0x95, 0x01,       //   Report Count (1)
        0x81, 0x01,       //   Input (Const)
        0x65, 0x00,       //   Unit (None)
        0x35, 0x00,       //   Physical Minimum (0)
        0x45, 0x00,       //   Physical Maximum (0)
        0x09, 0x30,       //   Usage (X) Wheel
        0x09, 0x31,       //   Usage (Y) Combined Pedals
        0x09, 0x32,       //   Usage (Z) Accelerator
        0x09, 0x35,       //   Usage (Rz) Brake
        0x09, 0x36,       //   Usage (Slider) Clutch
        0x09, 0x37,       //   Usage (Dial) Handbrake → DI Slider1
        0x15, 0x00,       //   Logical Minimum (0)
        0x27, 0xFF, 0xFF, 0x00, 0x00, // Logical Maximum (65535)
        0x75, 0x10,       //   Report Size (16)
        0x95, 0x06,       //   Report Count (6)
        0x81, 0x02,       //   Input (Data,Var,Abs)
        0xC0,             // End Collection

        0x06, 0x00, 0xFF, // Usage Page (Vendor)
        0x09, 0x01,       // Usage (1)
        0xA1, 0x01,       // Collection (Application)
        0x85, 0x02,       //   Report ID 2
        0x15, 0x00,       //   Logical Minimum (0)
        0x26, 0xFF, 0x00, //   Logical Maximum (255)
        0x75, 0x08,       //   Report Size (8)
        0x95, 0x3F,       //   Report Count (63)
        0x09, 0x01,       //   Usage (1)
        0x91, 0x02,       //   Output (Data,Var,Abs)
        0x09, 0x01,       //   Usage (1)
        0x81, 0x02,       //   Input (Data,Var,Abs) echo
        0xC0,             // End Collection
    ];

    public static ushort ProductIdFor(EmulatedDeviceKind kind) =>
        kind == EmulatedDeviceKind.FanatecDd1PcComp
            ? ProductIdPcComp
            : throw new ArgumentOutOfRangeException(nameof(kind));

    public static string ProductNameFor(EmulatedDeviceKind kind) =>
        kind == EmulatedDeviceKind.FanatecDd1PcComp
            ? ProductPcComp
            : throw new ArgumentOutOfRangeException(nameof(kind));

    public static Guid ContainerIdFor(EmulatedDeviceKind kind) =>
        kind == EmulatedDeviceKind.FanatecDd1PcComp
            ? ContainerIdPcComp
            : throw new ArgumentOutOfRangeException(nameof(kind));
}
