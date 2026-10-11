namespace SteeringWheelEmulator.Core.Models;

/// <summary>Fanatec DD1 virtual identity (PC Comp) and physical USB mode helpers.</summary>
public static class FanatecDd1Identity
{
    public const ushort VendorId = 0x0EB7;

    /// <summary>
    /// Emulator-only USB revision for WinUHid Hardware IDs (<c>REV_E001</c>),
    /// same idea as G920 <c>REV_9601</c> — keeps virtual nodes distinct from a physical base.
    /// </summary>
    public const ushort VersionNumber = 0xE001;

    /// <summary>
    /// Fanatec <b>PC Compatibility</b> mode:
    /// USB <c>VID_0EB7&amp;PID_0004</c> with DI GameControl faces (steer/FFB).
    /// </summary>
    public const ushort ProductIdPcComp = 0x0004;

    /// <summary>Legacy Xbox PID used in early planning.</summary>
    public const ushort ProductIdXboxLegacy = 0x0F54;

    /// <summary>Physical base in Xbox mode: USB <c>VID_0EB7&amp;PID_0F50</c>.</summary>
    public const ushort ProductIdXboxModeHardware = 0x0F50;

    public static readonly int DirectInputProductData1PcComp = VendorId | (ProductIdPcComp << 16);
    public static readonly int DirectInputProductData1XboxHardware =
        VendorId | (ProductIdXboxModeHardware << 16);

    public static bool IsFanatecVendor(Guid productGuid)
    {
        var data1 = BitConverter.ToInt32(productGuid.ToByteArray(), 0);
        return (data1 & 0xFFFF) == VendorId;
    }

    public static ushort GetProductId(Guid productGuid)
    {
        var data1 = BitConverter.ToInt32(productGuid.ToByteArray(), 0);
        return (ushort)((data1 >> 16) & 0xFFFF);
    }

    public static bool IsFanatecPcComp(Guid productGuid) =>
        IsFanatecVendor(productGuid) && GetProductId(productGuid) == ProductIdPcComp;

    /// <summary>Physical base in Xbox / GIP mode (<c>0F50</c> or legacy <c>0F54</c>).</summary>
    public static bool IsFanatecXboxModeHardware(Guid productGuid)
    {
        if (!IsFanatecVendor(productGuid))
            return false;
        var pid = GetProductId(productGuid);
        return pid is ProductIdXboxModeHardware or ProductIdXboxLegacy;
    }

    public static bool IsFanatecDd1VirtualProduct(Guid productGuid)
    {
        var data1 = BitConverter.ToInt32(productGuid.ToByteArray(), 0);
        return data1 == DirectInputProductData1PcComp;
    }

    public static bool IsAnyEmulatedVirtualProduct(Guid productGuid) =>
        G920Identity.IsVirtualG920Product(productGuid) || IsFanatecDd1VirtualProduct(productGuid);
}
