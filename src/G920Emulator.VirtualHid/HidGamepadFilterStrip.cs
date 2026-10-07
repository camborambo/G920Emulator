using System.Diagnostics;
using Microsoft.Win32;

namespace G920Emulator.VirtualHid;

/// <summary>
/// Windows 11 attaches Microsoft's <c>hidgamepad</c> upper-filter extension to every
/// <c>HID_DEVICE_SYSTEM_GAME</c> collection (joystick usage). On our virtual G920 that can
/// expose a second gamepad-style view (stuck Z/Rx/Ry, Heat pad UI, ghost presses).
/// Strip it from virtual REV_9601 Col01 only — never touch DualSense or other devices.
/// </summary>
public static class HidGamepadFilterStrip
{
    public static string? LastMessage { get; private set; }

    /// <param name="restartDevice">
    /// When true (Start bridge), disable/enable Col01 so the filter unloads.
    /// When false (background guard), only clear the registry value to avoid flapping.
    /// </param>
    public static string Apply(bool restartDevice = true)
    {
        var stripped = 0;
        var restarted = 0;
        try
        {
            using var hid = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID", writable: false);
            if (hid is null)
            {
                LastMessage = "Enum\\HID not found.";
                return LastMessage;
            }

            foreach (var vidPid in hid.GetSubKeyNames())
            {
                if (!vidPid.Contains("VID_046D", StringComparison.OrdinalIgnoreCase) ||
                    !vidPid.Contains("PID_C262", StringComparison.OrdinalIgnoreCase) ||
                    !vidPid.Contains("REV_9601", StringComparison.OrdinalIgnoreCase) ||
                    !vidPid.Contains("Col01", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var device = hid.OpenSubKey(vidPid, writable: false);
                if (device is null) continue;

                foreach (var instance in device.GetSubKeyNames())
                {
                    var filterPath =
                        $@"SYSTEM\CurrentControlSet\Enum\HID\{vidPid}\{instance}\Filters\*Upper";
                    if (TryDeleteHidGamepadValue(filterPath))
                    {
                        stripped++;
                        if (restartDevice)
                        {
                            var instanceId = $@"HID\{vidPid}\{instance}";
                            if (TryRestartDevice(instanceId))
                                restarted++;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LastMessage = "hidgamepad strip failed: " + ex.Message;
            return LastMessage;
        }

        LastMessage = stripped == 0
            ? "No hidgamepad upper-filter on virtual G920 Col01 (or already clear)."
            : restartDevice
                ? $"Removed hidgamepad filter from {stripped} virtual Col01 node(s); restarted {restarted}."
                : $"Cleared hidgamepad filter registry on {stripped} virtual Col01 node(s).";
        return LastMessage;
    }

    public static bool IsPresentOnVirtualCol01()
    {
        try
        {
            using var hid = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID", writable: false);
            if (hid is null) return false;

            foreach (var vidPid in hid.GetSubKeyNames())
            {
                if (!vidPid.Contains("VID_046D", StringComparison.OrdinalIgnoreCase) ||
                    !vidPid.Contains("PID_C262", StringComparison.OrdinalIgnoreCase) ||
                    !vidPid.Contains("REV_9601", StringComparison.OrdinalIgnoreCase) ||
                    !vidPid.Contains("Col01", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var device = hid.OpenSubKey(vidPid, writable: false);
                if (device is null) continue;

                foreach (var instance in device.GetSubKeyNames())
                {
                    var filterPath =
                        $@"SYSTEM\CurrentControlSet\Enum\HID\{vidPid}\{instance}\Filters\*Upper";
                    using var key = Registry.LocalMachine.OpenSubKey(filterPath, writable: false);
                    if (key is null) continue;
                    foreach (var name in key.GetValueNames())
                    {
                        if (name.Equals("hidgamepad", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool TryDeleteHidGamepadValue(string relativePath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(relativePath, writable: true);
            if (key is null) return false;

            var found = false;
            foreach (var name in key.GetValueNames())
            {
                if (!name.Equals("hidgamepad", StringComparison.OrdinalIgnoreCase))
                    continue;
                key.DeleteValue(name, throwOnMissingValue: false);
                found = true;
            }

            return found;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRestartDevice(string instanceId)
    {
        try
        {
            // Prefer disable/enable — softer than pnputil /restart-device for VHF children.
            if (TryPnPUtil($"/disable-device \"{instanceId}\"") &&
                TryPnPUtil($"/enable-device \"{instanceId}\""))
                return true;

            return TryPnPUtil($"/restart-device \"{instanceId}\"");
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPnPUtil(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pnputil.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        using var p = Process.Start(psi);
        if (p is null) return false;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(20_000);
        var text = stdout + stderr;
        return p.ExitCode == 0 &&
               !text.Contains("Failed", StringComparison.OrdinalIgnoreCase) &&
               !text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);
    }
}
