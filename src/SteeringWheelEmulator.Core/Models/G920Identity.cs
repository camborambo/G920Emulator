namespace SteeringWheelEmulator.Core.Models;

/// <summary>Logitech G920 HID identity used by the virtual device (046D:C262).</summary>
public static class G920Identity
{
    public const ushort VendorId = 0x046D;
    public const ushort ProductId = 0xC262;

    /// <summary>
    /// DirectInput packs VID/PID into the first 4 bytes of <c>ProductGuid</c> as
    /// <c>MAKELONG(vid, pid)</c> (= vid | (pid &lt;&lt; 16)).
    /// </summary>
    public static readonly int DirectInputProductData1 = VendorId | (ProductId << 16);

    public static bool IsVirtualG920Product(Guid productGuid)
    {
        var data1 = BitConverter.ToInt32(productGuid.ToByteArray(), 0);
        return data1 == DirectInputProductData1;
    }

    public static bool LooksLikeVirtualG920Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var n = name.ToLowerInvariant();
        return n.Contains("g920", StringComparison.Ordinal) &&
               (n.Contains("driving force", StringComparison.Ordinal) ||
                n.Contains("xbox one", StringComparison.Ordinal));
    }
}
