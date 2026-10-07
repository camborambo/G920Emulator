using System.Diagnostics;
using System.Text.RegularExpressions;

namespace G920Emulator.VirtualHid;

/// <summary>
/// Helpers around G HUB's <c>logi_joy_hid</c> filter on Col01.
/// That filter must NOT own our virtual Col01 — it swallows HID++ FFB so WinUHid
/// never sees WriteReport (diagnostics show HID++ writes = 0).
/// </summary>
public static class LogiJoyHidBinder
{
    public static string? LastMessage { get; private set; }

    /// <summary>
    /// True when any Col01 instance for C262 is bound to Logitech's filter
    /// (not Microsoft input.inf). Includes legacy no-REV nodes from older builds.
    /// </summary>
    public static bool IsCol01BoundToLogitech()
    {
        try
        {
            var output = RunPnPUtil("/enum-devices /connected /class HIDClass /drivers");
            if (string.IsNullOrEmpty(output))
                return false;

            // Look for our VID/PID Col01 with Logitech / logi_joy_hid in the same block.
            var blocks = Regex.Split(output, @"\r?\n\r?\n");
            foreach (var block in blocks)
            {
                if (!block.Contains("VID_046D&PID_C262", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!block.Contains("Col01", StringComparison.OrdinalIgnoreCase) &&
                    !block.Contains("COL01", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Regex.IsMatch(block, @"logi_joy_hid|Provider:\s*Logitech", RegexOptions.IgnoreCase))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Remove Col01 instances still owned by logi_joy_hid so the next enumeration
    /// uses Microsoft HID (and HID++ WriteReports reach WinUHid).
    /// </summary>
    public static bool TryRemoveLogitechCol01()
    {
        try
        {
            var output = RunPnPUtil("/enum-devices /connected /class HIDClass");
            if (string.IsNullOrEmpty(output))
            {
                LastMessage = "No HID devices enumerated.";
                return false;
            }

            var instanceIds = new List<string>();
            foreach (Match m in Regex.Matches(
                         output,
                         @"Instance ID:\s*(HID\\VID_046D&PID_C262[^\r\n]+)",
                         RegexOptions.IgnoreCase))
            {
                var id = m.Groups[1].Value.Trim();
                if (id.Contains("Col01", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("COL01", StringComparison.OrdinalIgnoreCase))
                    instanceIds.Add(id);
            }

            if (instanceIds.Count == 0)
            {
                LastMessage = "No C262 Col01 instances to remove.";
                return true;
            }

            // Only remove nodes that are actually on Logitech's driver.
            var drivers = RunPnPUtil("/enum-devices /connected /class HIDClass /drivers");
            var removed = 0;
            foreach (var id in instanceIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(drivers) &&
                    !drivers.Contains(id, StringComparison.OrdinalIgnoreCase))
                {
                    // Fall through — still try remove if VID match is enough.
                }

                var blockHasLogi = drivers is not null &&
                    Regex.IsMatch(
                        drivers,
                        Regex.Escape(id) + @".{0,800}?(logi_joy_hid|Provider:\s*Logitech)",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);

                // Also remove legacy no-REV Col01 (always wrong for FFB path).
                var isLegacyNoRev = id.Contains("PID_C262&Col01", StringComparison.OrdinalIgnoreCase) ||
                                    id.Contains("PID_C262&COL01", StringComparison.OrdinalIgnoreCase);

                if (!blockHasLogi && !isLegacyNoRev)
                    continue;

                var result = RunPnPUtil($"/remove-device \"{id}\"");
                removed++;
                LastMessage = $"remove-device {id}: {result?.Trim()}";
            }

            if (removed == 0)
                LastMessage = "No Logitech/legacy Col01 nodes needed removal.";
            return true;
        }
        catch (Exception ex)
        {
            LastMessage = ex.Message;
            return false;
        }
    }

    private static string? RunPnPUtil(string arguments)
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
        p.WaitForExit(20_000);
        return stdout + stderr;
    }
}
