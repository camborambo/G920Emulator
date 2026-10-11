using System.Management;

namespace SteeringWheelEmulator.VirtualHid;

/// <summary>
/// Clears Device Manager → Power Management → "Allow the computer to turn off this device
/// to save power" for WinUHid / VHF nodes that host the virtual G920.
/// If VHF sleeps, Col01 goes quiet while the app can still look Running (HOST_STALE-class).
/// Best-effort; requires elevation for WMI put on most machines.
/// </summary>
public static class WinUHidPowerPolicy
{
    /// <summary>Last human-readable result (for diagnostics / host path hint).</summary>
    public static string LastStatus { get; private set; } = "";

    /// <summary>
    /// Disable selective power-off on WinUHid/VHF-related devices. Safe to call every bridge Start.
    /// </summary>
    public static int EnsurePowerManagementOff()
    {
        var cleared = 0;
        var already = 0;
        var errors = 0;
        try
        {
            // Narrow WQL so we do not enumerate every PnP device on each Start.
            using var pnp = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT DeviceID, Name, Service FROM Win32_PnPEntity WHERE " +
                "DeviceID LIKE '%WinUHid%' OR DeviceID LIKE '%VHF%' OR DeviceID LIKE '%G920Emulator%' OR " +
                "Name LIKE '%Virtual HID%' OR Service LIKE '%WinUHid%' OR Service = 'vhf'");
            foreach (ManagementObject dev in pnp.Get())
            {
                using (dev)
                {
                    var id = dev["DeviceID"] as string ?? "";
                    if (string.IsNullOrEmpty(id))
                        continue;
                    cleared += DisablePowerOff(id, ref already, ref errors);
                }
            }
        }
        catch (Exception ex)
        {
            LastStatus = "power policy: " + ex.Message;
            return 0;
        }

        if (cleared > 0)
            LastStatus = $"power policy: disabled sleep on {cleared} WinUHid/VHF device(s)";
        else if (already > 0)
            LastStatus = $"power policy: already off ({already} WinUHid/VHF device(s))";
        else if (errors > 0)
            LastStatus = "power policy: could not change (run elevated?)";
        else
            LastStatus = "power policy: no WinUHid/VHF power entries found";

        return cleared;
    }

    private static int DisablePowerOff(string deviceId, ref int alreadyOff, ref int errors)
    {
        var changed = 0;
        try
        {
            // InstanceName is DeviceID with a trailing _<index>.
            var like = deviceId.Replace("\\", "\\\\", StringComparison.Ordinal) + "%";
            using var power = new ManagementObjectSearcher(
                @"root\WMI",
                $"SELECT * FROM MSPower_DeviceEnable WHERE InstanceName LIKE '{EscapeWql(like)}'");
            foreach (ManagementObject opt in power.Get())
            {
                using (opt)
                {
                    var enabled = opt["Enable"] is bool b && b;
                    if (!enabled)
                    {
                        alreadyOff++;
                        continue;
                    }

                    opt["Enable"] = false;
                    opt.Put();
                    changed++;
                }
            }
        }
        catch
        {
            errors++;
        }

        return changed;
    }

    private static string EscapeWql(string value) =>
        value.Replace("'", "\\'", StringComparison.Ordinal);
}
