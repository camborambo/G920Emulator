namespace SteeringWheelEmulator.Core.Models;

/// <summary>
/// Distinguishes WinUHid virtual wheels from physical USB devices that may share VID/PID.
/// Physical Fanatec must never be filtered from app device detect / bind / FFB combo.
/// </summary>
public static class VirtualDeviceIdentity
{
    public static bool LooksLikeWinUHidPath(string? instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath))
            return false;
        return instancePath.Contains("WINUHID", StringComparison.OrdinalIgnoreCase) ||
               instancePath.Contains(@"ROOT\WINUHID", StringComparison.OrdinalIgnoreCase) ||
               instancePath.Contains(@"VHF\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPhysicalUsbPath(string? instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath))
            return false;
        return instancePath.Contains(@"USB\", StringComparison.OrdinalIgnoreCase) ||
               instancePath.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// HidHide: keep only our WinUHid virtual wheel visible to games.
    /// Physical bases share VID/PID but appear as plain <c>HID\VID_…&amp;PID_…</c> (not USB\) —
    /// those must stay hideable. Never treat "not USB\" as virtual.
    /// </summary>
    public static bool IsVirtualKeepVisible(string instancePath, string textBlob, ushort vendorId, ushort productId)
    {
        var blob = $"{instancePath} {textBlob}";
        if (LooksLikeWinUHidPath(blob))
            return true;

        var token = $"VID_{vendorId:X4}&PID_{productId:X4}";
        if (!instancePath.Contains(token, StringComparison.OrdinalIgnoreCase) &&
            !blob.Contains(token, StringComparison.OrdinalIgnoreCase))
            return false;

        // Our WinUHid Hardware IDs always include a distinct REV (physical Fanatec/G920 do not).
        if (blob.Contains("REV_9601", StringComparison.OrdinalIgnoreCase) || // virtual G920
            blob.Contains("REV_E001", StringComparison.OrdinalIgnoreCase) || // virtual Fanatec
            blob.Contains("REV_0100", StringComparison.OrdinalIgnoreCase) || // legacy virtual Fanatec
            blob.Contains("G920Emulator", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static bool MatchesVidPid(Guid productGuid, ushort vendorId, ushort productId)
    {
        var data1 = BitConverter.ToInt32(productGuid.ToByteArray(), 0);
        var expected = vendorId | (productId << 16);
        return data1 == expected;
    }

    public static bool IsEmulatedVirtualProduct(Guid productGuid, EmulatedDeviceKind kind)
    {
        var profile = EmulatedDeviceProfiles.Get(kind);
        return MatchesVidPid(productGuid, profile.VendorId, profile.ProductId);
    }

    public static bool LooksLikeEmulatedVirtualName(string? name, EmulatedDeviceKind kind)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var n = name.ToLowerInvariant();
        return kind switch
        {
            EmulatedDeviceKind.LogitechG920 =>
                n.Contains("g920", StringComparison.Ordinal) &&
                (n.Contains("driving force", StringComparison.Ordinal) ||
                 n.Contains("xbox one", StringComparison.Ordinal)),
            EmulatedDeviceKind.FanatecDd1PcComp =>
                n.Contains("fanatec", StringComparison.Ordinal) &&
                (n.Contains("dd1", StringComparison.Ordinal) ||
                 n.Contains("podium", StringComparison.Ordinal) ||
                 n.Contains("pc comp", StringComparison.Ordinal)),
            _ => false,
        };
    }
}
