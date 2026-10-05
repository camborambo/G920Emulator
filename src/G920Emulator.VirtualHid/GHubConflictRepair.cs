using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace G920Emulator.VirtualHid;

/// <summary>
/// Repairs system state after Logitech G HUB install/uninstall so games can see
/// the virtual G920 again (OEM FFB CLSID, Logi HID filters, stale DI cache).
/// </summary>
public static class GHubConflictRepair
{
    /// <summary>Logitech stock G920 OEMForceFeedback CLSID from G HUB / logi_joy_hid.</summary>
    public const string LogitechOemFfbClsid = "{62B43F0E-E7DB-4329-8C13-A966D84A289F}";

    public static string LastSummary { get; private set; } = "";

    /// <summary>
    /// Full repair safe to run on every Start bridge (app is already elevated).
    /// </summary>
    public static string Repair()
    {
        var log = new List<string>();

        try
        {
            var filters = ClearLogitechFiltersOnC262Nodes();
            log.Add(filters);
        }
        catch (Exception ex)
        {
            log.Add("Filter cleanup: " + ex.Message);
        }

        try
        {
            LogiJoyHidBinder.TryRemoveLogitechCol01();
            log.Add(LogiJoyHidBinder.LastMessage ?? "Device cleanup done.");
        }
        catch (Exception ex)
        {
            log.Add("Device cleanup: " + ex.Message);
        }

        try
        {
            G920OemRegistration.EnsureRegistered(forceRewrite: true);
            log.Add(G920OemRegistration.LastMessage ?? "OEM registered.");
            var oemCheck = VerifyOemPointsToUs();
            log.Add(oemCheck);
        }
        catch (Exception ex)
        {
            log.Add("OEM register: " + ex.Message);
        }

        try
        {
            log.Add(ClearStaleDirectInputCache());
        }
        catch (Exception ex)
        {
            log.Add("DI cache: " + ex.Message);
        }

        try
        {
            RunPnPUtil("/scan-devices");
            log.Add("PnP scan-devices requested.");
        }
        catch (Exception ex)
        {
            log.Add("PnP scan: " + ex.Message);
        }

        try
        {
            // Friendly name only — never pnputil /restart-device (orphans WinUHid Col01).
            log.Add(G920DeviceIdentityFix.Apply(restartDevice: false));
        }
        catch (Exception ex)
        {
            log.Add("Identity fix: " + ex.Message);
        }

        try
        {
            // G HUB uninstall often leaves virtual-HID driver + hidpp FFB DLL behind.
            log.Add(RemoveGHubLeftoverPackages());
        }
        catch (Exception ex)
        {
            log.Add("G HUB leftover packages: " + ex.Message);
        }

        try
        {
            log.Add(RemoveOrphanVirtualC262Nodes());
        }
        catch (Exception ex)
        {
            log.Add("Orphan C262 cleanup: " + ex.Message);
        }

        // Never remove WinUHid root enumerators here — that made the driver look "missing"
        // (user-mode probe error 2) even when ROOT\WINUHID still showed Started.

        LastSummary = string.Join(" | ", log.Where(s => !string.IsNullOrWhiteSpace(s)));
        return LastSummary;
    }

    /// <summary>
    /// Uninstall leftover G HUB driver packages / disable services that survive "uninstall".
    /// Does not touch Microsoft hidgamepad or the user's DualSense.
    /// </summary>
    private static string RemoveGHubLeftoverPackages()
    {
        var notes = new List<string>();

        // 1) Logitech G HUB Virtual HID driver (logi_joy_vir_hid) — demand-start leftover.
        try
        {
            using var svc = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\logi_joy_vir_hid", writable: true);
            if (svc is not null)
            {
                // SERVICE_DISABLED = 4
                svc.SetValue("Start", 4, RegistryValueKind.DWord);
                notes.Add("disabled logi_joy_vir_hid service");
            }
        }
        catch (Exception ex)
        {
            notes.Add("logi_joy_vir_hid service: " + ex.Message);
        }

        // 2) Delete published Logitech G HUB packages from the driver store.
        foreach (var oem in FindPublishedDriverNames("logi_joy_vir_hid", "logi_joy_hid", "logi_generic_hid"))
        {
            var output = RunPnPUtilCapture($"/delete-driver {oem} /uninstall /force");
            notes.Add($"delete-driver {oem}: {(string.IsNullOrWhiteSpace(output) ? "ok" : output.Trim().Split('\n')[0])}");
        }

        // 3) Neutralize leftover Logitech DI FFB COM DLL path if still on disk (G HUB gone).
        //    Games/SDK probes sometimes find this even when CLSID points at g920ffb.
        try
        {
            var ffbDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Logitech", "Direct Input Force Feedback");
            var dll = Path.Combine(ffbDir, "1_1_13", "hidpp_forcefeedback_x64.dll");
            if (File.Exists(dll))
            {
                var disabled = dll + ".g920emulator-disabled";
                if (File.Exists(disabled))
                    File.Delete(disabled);
                File.Move(dll, disabled);
                notes.Add("renamed hidpp_forcefeedback_x64.dll (disabled)");
            }
        }
        catch (Exception ex)
        {
            notes.Add("hidpp_forcefeedback: " + ex.Message);
        }

        // 4) Drop stale LGHUB machine keys (uninstall leftovers).
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Logitech\LGHUB", throwOnMissingSubKey: false);
            notes.Add("cleared HKLM\\SOFTWARE\\Logitech\\LGHUB");
        }
        catch
        {
            // ignore
        }

        return notes.Count == 0
            ? "No G HUB leftover packages found."
            : string.Join("; ", notes);
    }

    /// <summary>
    /// Remove disconnected virtual C262 nodes left after failed disable/enable cycles.
    /// Safe to call on every Start — never touches Status=Started devices.
    /// </summary>
    public static string RemoveDisconnectedVirtualNodes() => RemoveOrphanVirtualC262Nodes();

    /// <summary>
    /// Remove disconnected/Unknown virtual C262 nodes left after failed restarts so the next
    /// Start bridge gets a clean enumeration.
    /// </summary>
    private static string RemoveOrphanVirtualC262Nodes()
    {
        var output = RunPnPUtilCapture("/enum-devices /class HIDClass");
        if (string.IsNullOrWhiteSpace(output))
            return "No HID enum for orphan cleanup.";

        var removed = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     output,
                     @"Instance ID:\s*(HID\\VID_046D&PID_C262&REV_9601[^\r\n]+)",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var id = m.Groups[1].Value.Trim();
            var blockMatch = System.Text.RegularExpressions.Regex.Match(
                output,
                System.Text.RegularExpressions.Regex.Escape(id) + @".{0,400}?Status:\s*(\w+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            var status = blockMatch.Success ? blockMatch.Groups[1].Value : "";

            // Only remove clearly dead nodes. Never touch Started / OK — some Windows
            // builds report live WinUHid children as OK, and deleting those makes the
            // next WinUHidCreateDevice fail (InstanceID G920Emulator still reserved).
            if (!IsRemovableOrphanStatus(status))
                continue;

            RunPnPUtil($"/remove-device \"{id}\"");
            removed++;
        }

        return removed == 0
            ? "No orphan virtual C262 nodes to remove."
            : $"Removed {removed} orphan virtual C262 node(s).";
    }

    private static bool IsRemovableOrphanStatus(string status) =>
        status.Equals("Disconnected", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Disabled", StringComparison.OrdinalIgnoreCase);

    private static List<string> FindPublishedDriverNames(params string[] originalNameContains)
    {
        var found = new List<string>();
        var output = RunPnPUtilCapture("/enum-drivers");
        if (string.IsNullOrWhiteSpace(output))
            return found;

        string? currentPublished = null;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Published Name:", StringComparison.OrdinalIgnoreCase))
            {
                currentPublished = line["Published Name:".Length..].Trim();
                continue;
            }

            if (currentPublished is null)
                continue;

            if (line.StartsWith("Original Name:", StringComparison.OrdinalIgnoreCase))
            {
                var original = line["Original Name:".Length..].Trim();
                if (originalNameContains.Any(n => original.Contains(n, StringComparison.OrdinalIgnoreCase)))
                    found.Add(currentPublished);
            }
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string RunPnPUtilCapture(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pnputil.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return "";
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return stdout + stderr;
    }

    private static string VerifyOemPointsToUs()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C262\OEMForceFeedback");
            var clsid = key?.GetValue("CLSID") as string;
            if (string.IsNullOrWhiteSpace(clsid))
                return "OEM HKLM CLSID: (missing)";
            if (clsid.Contains(G920OemRegistration.OemFfbClsid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(clsid, G920OemRegistration.OemFfbClsid, StringComparison.OrdinalIgnoreCase))
                return "OEM HKLM CLSID: ours (OK)";
            if (clsid.Contains("62B43F0E", StringComparison.OrdinalIgnoreCase))
                return "OEM HKLM CLSID: still Logitech — rewrite failed";
            return "OEM HKLM CLSID: " + clsid;
        }
        catch (Exception ex)
        {
            return "OEM verify: " + ex.Message;
        }
    }

    /// <summary>
    /// Strip logi_* UpperFilters / LowerFilters from C262 HID nodes (especially virtual / non-USB).
    /// </summary>
    private static string ClearLogitechFiltersOnC262Nodes()
    {
        var cleared = 0;
        using var hid = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID", writable: false);
        if (hid is null)
            return "Enum\\HID: not found";

        foreach (var vidPid in hid.GetSubKeyNames())
        {
            if (!vidPid.Contains("VID_046D", StringComparison.OrdinalIgnoreCase) ||
                !vidPid.Contains("PID_C262", StringComparison.OrdinalIgnoreCase))
                continue;

            using var device = hid.OpenSubKey(vidPid, writable: false);
            if (device is null) continue;

            foreach (var instance in device.GetSubKeyNames())
            {
                using var inst = device.OpenSubKey(instance, writable: true);
                if (inst is null) continue;

                // Prefer cleaning virtual / non-USB nodes; still clean USB if Logi filters present
                // so a phantom G HUB binding cannot steal the VID/PID from games.
                if (StripFilterValue(inst, "UpperFilters"))
                    cleared++;
                if (StripFilterValue(inst, "LowerFilters"))
                    cleared++;
            }
        }

        return cleared == 0
            ? "No Logitech HID filters to clear on C262."
            : $"Cleared Logitech filter values on {cleared} C262 registry entries.";
    }

    private static bool StripFilterValue(RegistryKey key, string valueName)
    {
        try
        {
            var raw = key.GetValue(valueName);
            if (raw is null) return false;

            string[] parts;
            if (raw is string[] multi)
                parts = multi;
            else if (raw is string s)
                parts = s.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            else
                return false;

            var kept = parts
                .Where(p => !p.Contains("logi_", StringComparison.OrdinalIgnoreCase) &&
                            !p.Contains("logitech", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (kept.Length == parts.Length)
                return false;

            if (kept.Length == 0)
                key.DeleteValue(valueName, throwOnMissingValue: false);
            else
                key.SetValue(valueName, kept, RegistryValueKind.MultiString);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ClearStaleDirectInputCache()
    {
        var removed = 0;
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            removed += DeleteMatchingSubkeys(
                root,
                @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput",
                "VID_046D&PID_C262");
            removed += DeleteMatchingSubkeys(
                root,
                @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput",
                "VID_046D&PID_C262");
        }

        return removed == 0
            ? "No stale DirectInput cache keys."
            : $"Removed {removed} stale DirectInput cache key(s).";
    }

    private static int DeleteMatchingSubkeys(RegistryKey root, string path, string nameContains)
    {
        try
        {
            using var parent = root.OpenSubKey(path, writable: true);
            if (parent is null) return 0;
            var count = 0;
            foreach (var name in parent.GetSubKeyNames())
            {
                if (!name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    parent.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                    count++;
                }
                catch
                {
                    // ignore locked keys
                }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static void RunPnPUtil(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pnputil.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return;
        _ = p.StandardOutput.ReadToEnd();
        _ = p.StandardError.ReadToEnd();
        p.WaitForExit(20_000);
    }
}
