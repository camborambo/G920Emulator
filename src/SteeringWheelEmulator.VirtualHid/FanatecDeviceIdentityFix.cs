using System.Diagnostics;
using SteeringWheelEmulator.Core.Models;
using Microsoft.Win32;

namespace SteeringWheelEmulator.VirtualHid;

/// <summary>
/// Keeps virtual Fanatec DD1 (PC / PC Comp) WinUHid nodes looking like a DI joystick.
/// Without this, Windows often leaves FriendlyName as "HID-compliant game controller"
/// and Fanatec drivers may attach filters to VID_0EB7 — Forza then never lists a wheel.
/// Only touches <c>REV_E001</c> (emulator) nodes — never physical USB Fanatec.
/// </summary>
public static class FanatecDeviceIdentityFix
{
    public static string? LastMessage { get; private set; }

    public const string ExpectedFriendlyName = "FANATEC Podium Wheel Base DD1";

    /// <summary>True when a connected HIDClass node matches virtual Fanatec REV_E001.</summary>
    public static bool IsVirtualPresent()
    {
        try
        {
            var output = RunPnPUtil("/enum-devices /connected /class HIDClass");
            if (string.IsNullOrEmpty(output))
                return false;

            return output.Contains("VID_0EB7", StringComparison.OrdinalIgnoreCase) &&
                   output.Contains("REV_E001", StringComparison.OrdinalIgnoreCase) &&
                   output.Contains("PID_0004", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Friendly name + JOY HardwareID + strip Fanatec/hidgamepad filters on REV_E001 only.
    /// </summary>
    public static string Apply()
    {
        var fixedCount = 0;
        var filtersCleared = 0;
        try
        {
            foreach (var inst in EnumerateVirtualFanatecKeys(writable: true))
            {
                using (inst)
                {
                    try
                    {
                        var changed = false;
                        var friendly = inst.GetValue("FriendlyName") as string ?? "";
                        var desc = inst.GetValue("DeviceDesc") as string ?? "";

                        if (!IsExpectedName(friendly))
                        {
                            inst.SetValue("FriendlyName", ExpectedFriendlyName, RegistryValueKind.String);
                            changed = true;
                        }

                        if (LooksLikeGenericGamepadName(desc) || string.IsNullOrWhiteSpace(desc))
                        {
                            inst.SetValue("DeviceDesc", ExpectedFriendlyName, RegistryValueKind.String);
                            changed = true;
                        }

                        if (EnsureJoyHardwareId(inst))
                            changed = true;

                        if (ClearVendorFilters(inst))
                        {
                            filtersCleared++;
                            changed = true;
                        }

                        if (changed)
                            fixedCount++;
                    }
                    catch
                    {
                        // ignore per-instance failures
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LastMessage = ex.Message;
            return LastMessage;
        }

        LastMessage = fixedCount == 0 && filtersCleared == 0
            ? "Virtual Fanatec name/filters OK (no registry rewrite)."
            : $"Updated {fixedCount} virtual Fanatec node(s); cleared vendor filters on {filtersCleared}.";
        return LastMessage;
    }

    private static bool IsExpectedName(string name) =>
        name.Contains("FANATEC", StringComparison.OrdinalIgnoreCase) &&
        name.Contains("Podium", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeGenericGamepadName(string name) =>
        name.Contains("game controller", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("HID-compliant game", StringComparison.OrdinalIgnoreCase) ||
        (name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase) &&
         !name.Contains("FANATEC", StringComparison.OrdinalIgnoreCase));

    private static bool EnsureJoyHardwareId(RegistryKey inst)
    {
        var hw = ReadMulti(inst, "HardwareID");
        if (hw.Length == 0) return false;
        if (hw.Any(h => h.Equals("HID_DEVICE_SYSTEM_JOY", StringComparison.OrdinalIgnoreCase)))
            return false;

        var next = hw.ToList();
        next.Add("HID_DEVICE_SYSTEM_JOY");
        inst.SetValue("HardwareID", next.ToArray(), RegistryValueKind.MultiString);
        return true;
    }

    /// <summary>Drop Fanatec / FAW / hidgamepad filters so Microsoft HID owns the virtual node.</summary>
    private static bool ClearVendorFilters(RegistryKey inst)
    {
        var changed = false;
        foreach (var valueName in new[] { "UpperFilters", "LowerFilters" })
        {
            var filters = ReadMulti(inst, valueName);
            if (filters.Length == 0)
                continue;

            var kept = filters
                .Where(f => !LooksLikeVendorFilter(f))
                .ToArray();
            if (kept.Length == filters.Length)
                continue;

            if (kept.Length == 0)
                inst.DeleteValue(valueName, throwOnMissingValue: false);
            else
                inst.SetValue(valueName, kept, RegistryValueKind.MultiString);
            changed = true;
        }

        return changed;
    }

    private static bool LooksLikeVendorFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return false;
        var f = filter.ToLowerInvariant();
        return f.Contains("fanatec", StringComparison.Ordinal) ||
               f.Contains("faw", StringComparison.Ordinal) ||
               f.Contains("endor", StringComparison.Ordinal) ||
               f.Contains("hidgamepad", StringComparison.Ordinal) ||
               f.Contains("hid_fanatec", StringComparison.Ordinal);
    }

    private static IEnumerable<RegistryKey> EnumerateVirtualFanatecKeys(bool writable)
    {
        using var hid = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID", writable: false);
        if (hid is null)
            yield break;

        foreach (var vidPid in hid.GetSubKeyNames())
        {
            if (!vidPid.Contains("VID_0EB7", StringComparison.OrdinalIgnoreCase) ||
                !vidPid.Contains("REV_E001", StringComparison.OrdinalIgnoreCase))
                continue;

            var isPcComp = vidPid.Contains($"PID_{FanatecDd1Identity.ProductIdPcComp:X4}", StringComparison.OrdinalIgnoreCase);
            if (!isPcComp)
                continue;

            using var device = hid.OpenSubKey(vidPid, writable: false);
            if (device is null) continue;

            foreach (var instance in device.GetSubKeyNames())
            {
                var inst = device.OpenSubKey(instance, writable);
                if (inst is null) continue;
                yield return inst;
            }
        }
    }

    private static string[] ReadMulti(RegistryKey key, string name)
    {
        var raw = key.GetValue(name);
        return raw switch
        {
            string[] arr => arr,
            string s => s.Split('\0', StringSplitOptions.RemoveEmptyEntries),
            _ => [],
        };
    }

    private static string? RunPnPUtil(string arguments)
    {
        try
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
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);
            return stdout + stderr;
        }
        catch
        {
            return null;
        }
    }
}
