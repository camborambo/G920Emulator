using SteeringWheelEmulator.Core.Models;
using Microsoft.Win32;

namespace SteeringWheelEmulator.VirtualHid;

/// <summary>
/// DirectInput OEM joystick identity for virtual Fanatec DD1 (PC Comp <c>0EB7:0004</c>).
/// Reuses <see cref="G920OemRegistration.OemFfbClsid"/> (<c>emuffb.dll</c>) so DI OEM FFB
/// still feeds the existing SHM mixer.
/// </summary>
public static class FanatecOemRegistration
{
    /// <summary>Same CLSID as G920 OEM FFB — shared emuffb EffectDriver.</summary>
    public const string OemFfbClsid = G920OemRegistration.OemFfbClsid;

    private static readonly byte[] OemData = [0x43, 0x00, 0x08, 0x10, 0x12, 0x00, 0x00, 0x00];

    public static string? LastMessage { get; private set; }

    public static void EnsureRegistered(EmulatedDeviceKind kind)
    {
        if (kind != EmulatedDeviceKind.FanatecDd1PcComp)
        {
            LastMessage = "Fanatec OEM skipped (not PC Comp).";
            return;
        }

        // Ensure COM server for emuffb is registered (shared with G920 path). No Logitech SDK pin.
        G920OemRegistration.EnsureRegistered(forceRewrite: false, installSdk: false);

        var profile = EmulatedDeviceProfiles.Get(kind);
        WriteOemTree(Registry.CurrentUser, profile);
        WriteOemTree(Registry.LocalMachine, profile);
        LastMessage = $"Fanatec OEM registered → {profile.OemKeyName} CLSID {OemFfbClsid}";
        if (!IsPcCompOemReady(out var missing))
            LastMessage += " — WARNING incomplete: " + missing;
    }

    /// <summary>True when VID_0EB7&amp;PID_0004 OEMForceFeedback points at emuffb.</summary>
    public static bool IsPcCompOemReady(out string missing)
    {
        const string key = "VID_0EB7&PID_0004";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var ff = root.OpenSubKey(
                    @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\" +
                    key + @"\OEMForceFeedback");
                var clsid = ff?.GetValue("CLSID") as string;
                if (clsid is not null &&
                    clsid.Contains("A920FFB0", StringComparison.OrdinalIgnoreCase))
                {
                    missing = "";
                    return true;
                }
            }
            catch { /* ignore */ }
        }

        missing = $"OEM tree {key}\\OEMForceFeedback CLSID missing";
        return false;
    }

    public static void RemoveOemTrees()
    {
        var keyName = EmulatedDeviceProfiles.Get(EmulatedDeviceKind.FanatecDd1PcComp).OemKeyName;
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                var parentPath =
                    @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";
                using var parent = root.OpenSubKey(parentPath, writable: true);
                parent?.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
            }
            catch
            {
                // best-effort
            }
        }
    }

    private static void WriteOemTree(RegistryKey root, IEmulatedDeviceProfile profile)
    {
        try
        {
            var relative =
                @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\" +
                profile.OemKeyName;
            using var key = root.CreateSubKey(relative, writable: true);
            if (key is null) return;

            key.SetValue("OEMName", profile.Product, RegistryValueKind.String);
            key.SetValue("OEMData", OemData, RegistryValueKind.Binary);
            key.SetValue("Version", 1, RegistryValueKind.DWord);

            // Match live PC Comp DI: Y=Combined, Z=Accel, Rz=Brake, Slider=Clutch, Dial=Handbrake.
            WriteAxis(key, "0", "Wheel Axis", [0x01, 0x81, 0x00, 0x00], [0x0A, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00]);
            WriteAxis(key, "1", "Combined Pedals", [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x31, 0x00], null);
            WriteAxis(key, "2", "Accelerator", [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x32, 0x00], null);
            WriteAxis(key, "5", "Brake", [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x35, 0x00], null);
            WriteAxis(key, "6", "Clutch", [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x36, 0x00], null);
            WriteAxis(key, "7", "Handbrake", [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x37, 0x00], null);

            for (var i = 0; i < 32; i++)
            {
                using var btn = key.CreateSubKey($@"Buttons\{i}", writable: true);
                btn?.SetValue("Attributes", new byte[] { 0x02, 0x80, 0x00, 0x00 }, RegistryValueKind.Binary);
            }

            using var ff = key.CreateSubKey("OEMForceFeedback", writable: true);
            if (ff is null) return;

            ff.SetValue("Attributes",
                new byte[] { 0x00, 0x00, 0x00, 0x00, 0xE8, 0x03, 0x00, 0x00, 0xE8, 0x03, 0x00, 0x00 },
                RegistryValueKind.Binary);
            ff.SetValue("CLSID", OemFfbClsid, RegistryValueKind.String);

            WriteEffect(ff, "{13541C20-8E33-11D0-9AD0-00A0C9A06E35}", "Constant Force",
                [0x00, 0x00, 0x00, 0x00, 0x01, 0x86, 0x00, 0x00, 0xED, 0x03, 0x00, 0x00, 0xED, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
            WriteEffect(ff, "{13541C21-8E33-11D0-9AD0-00A0C9A06E35}", "Ramp Force",
                [0x01, 0x00, 0x00, 0x00, 0x02, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
            WriteEffect(ff, "{13541C22-8E33-11D0-9AD0-00A0C9A06E35}", "Square Wave",
                [0x02, 0x00, 0x00, 0x00, 0x03, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
            WriteEffect(ff, "{13541C23-8E33-11D0-9AD0-00A0C9A06E35}", "Sine Wave",
                [0x03, 0x00, 0x00, 0x00, 0x03, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
            WriteEffect(ff, "{13541C24-8E33-11D0-9AD0-00A0C9A06E35}", "Triangle Wave",
                [0x04, 0x00, 0x00, 0x00, 0x03, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
            WriteEffect(ff, "{13541C25-8E33-11D0-9AD0-00A0C9A06E35}", "Sawtooth Up Wave",
                [0x05, 0x00, 0x00, 0x00, 0x03, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
            WriteEffect(ff, "{13541C26-8E33-11D0-9AD0-00A0C9A06E35}", "Sawtooth Down Wave",
                [0x06, 0x00, 0x00, 0x00, 0x03, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
            WriteEffect(ff, "{13541C27-8E33-11D0-9AD0-00A0C9A06E35}", "Spring Force",
                [0x07, 0x00, 0x00, 0x00, 0x04, 0xD8, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
            WriteEffect(ff, "{13541C28-8E33-11D0-9AD0-00A0C9A06E35}", "Damper Force",
                [0x08, 0x00, 0x00, 0x00, 0x04, 0xD8, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
            WriteEffect(ff, "{13541C29-8E33-11D0-9AD0-00A0C9A06E35}", "Inertia Force",
                [0x09, 0x00, 0x00, 0x00, 0x04, 0xD8, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
            WriteEffect(ff, "{13541C2A-8E33-11D0-9AD0-00A0C9A06E35}", "Friction Force",
                [0x0A, 0x00, 0x00, 0x00, 0x04, 0xD8, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
        }
        catch
        {
            // best-effort
        }
    }

    private static void WriteAxis(RegistryKey oem, string index, string name, byte[] attributes, byte[]? ffAttributes)
    {
        using var axis = oem.CreateSubKey($@"Axes\{index}", writable: true);
        if (axis is null) return;
        axis.SetValue("OEMName", name, RegistryValueKind.String);
        axis.SetValue("Attributes", attributes, RegistryValueKind.Binary);
        if (ffAttributes is not null)
            axis.SetValue("FFAttributes", ffAttributes, RegistryValueKind.Binary);
    }

    private static void WriteEffect(RegistryKey ff, string guid, string name, byte[] attributes)
    {
        using var effect = ff.CreateSubKey($@"Effects\{guid}", writable: true);
        if (effect is null) return;
        effect.SetValue("OEMName", name, RegistryValueKind.String);
        effect.SetValue("Attributes", attributes, RegistryValueKind.Binary);
    }
}
