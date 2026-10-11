namespace SteeringWheelEmulator.Core.Models;

/// <summary>Which virtual wheel identity the bridge presents to games.</summary>
public enum EmulatedDeviceKind
{
    /// <summary>Logitech G920 Xbox One wheel — <c>046D:C262</c> (default / known-good).</summary>
    LogitechG920 = 0,

    /// <summary>
    /// Fanatec DD1 PC Compatibility mode — <c>0EB7:0004</c>
    /// (mixed-brand / Unbound Fanatec-style profile; not Xbox GIP mode).
    /// </summary>
    FanatecDd1PcComp = 1,

    /// <summary>Legacy JSON alias for <see cref="FanatecDd1PcComp"/> (early “Xbox” label).</summary>
    FanatecDd1Xbox = 1,
}

public static class EmulatedDeviceKindInfo
{
    public const string LegacyFanatecXboxDefaultProfileName = "Fanatec DD1 (Xbox) Default";
    public const string LegacyFanatecPcCompDefaultProfileName = "Fanatec DD1 (PC Comp) Default";

    public static string DisplayName(EmulatedDeviceKind kind) => kind switch
    {
        EmulatedDeviceKind.LogitechG920 => "Logitech G920",
        EmulatedDeviceKind.FanatecDd1PcComp => "Fanatec DD1",
        _ => kind.ToString(),
    };

    public static string DefaultProfileName(EmulatedDeviceKind kind) =>
        DisplayName(kind) + " Default";

    public static string SettingsKey(EmulatedDeviceKind kind) => kind switch
    {
        EmulatedDeviceKind.LogitechG920 => "logitechG920",
        EmulatedDeviceKind.FanatecDd1PcComp => "fanatecDd1PcComp",
        _ => kind.ToString(),
    };

    /// <summary>Rename leftover Xbox / “(PC Comp)” default profile titles → “Fanatec DD1 Default”.</summary>
    public static string MigrateLegacyProfileName(string? name, EmulatedDeviceKind kind)
    {
        if (kind is EmulatedDeviceKind.FanatecDd1PcComp &&
            (string.Equals(name, LegacyFanatecXboxDefaultProfileName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(name, LegacyFanatecPcCompDefaultProfileName, StringComparison.OrdinalIgnoreCase)))
            return DefaultProfileName(EmulatedDeviceKind.FanatecDd1PcComp);
        return string.IsNullOrWhiteSpace(name) ? DefaultProfileName(kind) : name.Trim();
    }

    public static bool TryParse(string? value, out EmulatedDeviceKind kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            kind = EmulatedDeviceKind.LogitechG920;
            return false;
        }

        if (Enum.TryParse(value, ignoreCase: true, out kind) &&
            Enum.IsDefined(typeof(EmulatedDeviceKind), kind) &&
            kind is EmulatedDeviceKind.LogitechG920 or EmulatedDeviceKind.FanatecDd1PcComp)
            return true;

        var v = value.Trim();
        if (v.Equals("logitechG920", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("g920", StringComparison.OrdinalIgnoreCase))
        {
            kind = EmulatedDeviceKind.LogitechG920;
            return true;
        }

        // PC Comp + legacy keys (Xbox label, and unsupported 0006 PC → PC Comp).
        if (v.Equals("fanatecDd1PcComp", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("dd1PcComp", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("fanatecDd1Xbox", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("dd1Xbox", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("FanatecDd1Xbox", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("fanatecDd1Pc", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("dd1Pc", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("FanatecDd1Pc", StringComparison.OrdinalIgnoreCase))
        {
            kind = EmulatedDeviceKind.FanatecDd1PcComp;
            return true;
        }

        kind = EmulatedDeviceKind.LogitechG920;
        return false;
    }

    public static EmulatedDeviceKind ParseOrDefault(string? value) =>
        TryParse(value, out var kind) ? kind : EmulatedDeviceKind.LogitechG920;
}
