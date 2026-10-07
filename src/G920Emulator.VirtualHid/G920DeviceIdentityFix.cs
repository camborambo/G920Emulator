using System.Diagnostics;
using Microsoft.Win32;

namespace G920Emulator.VirtualHid;

/// <summary>
/// Keeps the virtual G920 Col01 friendly name / OEM-facing identity correct.
/// On modern Windows, Joystick (usage 0x04) still binds MatchingDeviceId
/// <c>HID_DEVICE_SYSTEM_GAME</c> ("HID-compliant game controller") - that is normal
/// and must NOT trigger pnputil device restarts (those orphan WinUHid VHF children).
/// </summary>
public static class G920DeviceIdentityFix
{
    public static string? LastMessage { get; private set; }

    public const string ExpectedFriendlyName = G920OemRegistration.DisplayName;

    /// <summary>
    /// True when FriendlyName/DeviceDesc still look like a generic pad name (not our G920 string).
    /// Does not treat MatchingDeviceId HID_DEVICE_SYSTEM_GAME as an error on Win10/11.
    /// </summary>
    public static bool NeedsFriendlyNameFix()
    {
        try
        {
            foreach (var inst in EnumerateVirtualCol01Keys(writable: false))
            {
                using (inst)
                {
                    var friendly = inst.GetValue("FriendlyName") as string ?? "";
                    var desc = inst.GetValue("DeviceDesc") as string ?? "";
                    if (!IsExpectedName(friendly) || LooksLikeGenericGamepadName(desc))
                        return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>Legacy name used by GHubGuard - friendly-name only, never a driver rebind.</summary>
    public static bool IsMisclassifiedAsGamepad() => NeedsFriendlyNameFix();

    public static void EnsureFriendlyNameOnly() => Apply(restartDevice: false);

    /// <param name="restartDevice">Ignored - restarts break WinUHid virtual Col01 nodes.</param>
    public static string Apply(bool restartDevice = false)
    {
        _ = restartDevice; // never restart virtual VHF children
        var fixedCount = 0;
        try
        {
            foreach (var inst in EnumerateVirtualCol01Keys(writable: true))
            {
                using (inst)
                {
                    try
                    {
                        var friendly = inst.GetValue("FriendlyName") as string ?? "";
                        var desc = inst.GetValue("DeviceDesc") as string ?? "";
                        var changed = false;

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

                        // Keep JOY id present for tools that read HardwareID, but never remove
                        // SYSTEM_GAME - Windows puts it there for Joystick usage and driver match.
                        if (EnsureJoyHardwareId(inst))
                            changed = true;

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

        LastMessage = fixedCount == 0
            ? "Virtual G920 Col01 name OK (no registry rewrite)."
            : $"Updated friendly name on {fixedCount} virtual G920 Col01 node(s).";
        return LastMessage;
    }

    /// <summary>True when the virtual Col01 instance is currently attached (Present/OK).</summary>
    public static bool IsVirtualCol01Present()
    {
        try
        {
            var output = RunPnPUtil("/enum-devices /connected /class HIDClass");
            if (string.IsNullOrEmpty(output))
                return false;

            return output.Contains("VID_046D&PID_C262&REV_9601", StringComparison.OrdinalIgnoreCase) &&
                   (output.Contains("Col01", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("COL01", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExpectedName(string name) =>
        name.Contains("G920", StringComparison.OrdinalIgnoreCase) &&
        name.Contains("Driving Force", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeGenericGamepadName(string name) =>
        name.Contains("game controller", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("HID-compliant game", StringComparison.OrdinalIgnoreCase) ||
        (name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase) &&
         !name.Contains("G920", StringComparison.OrdinalIgnoreCase));

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

    private static IEnumerable<RegistryKey> EnumerateVirtualCol01Keys(bool writable)
    {
        using var hid = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID", writable: false);
        if (hid is null)
            yield break;

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
