using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace G920Emulator.VirtualHid;

public enum OemSessionUiState
{
    Idle,
    Active,
    NeedsRestore,
}

/// <summary>
/// Session-scoped OEM FFB + Logitech SDK registry pins.
/// Applied on bridge Start; restored on Stop/Close, or on next launch via <see cref="RecoverIfDirty"/>.
/// </summary>
public static class OemRegistrationSession
{
    private static readonly object Gate = new();
    private static bool _activeInProcess;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string SessionFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "G920Emulator",
        "oem-session.json");

    public static bool IsActive
    {
        get
        {
            lock (Gate)
            {
                if (_activeInProcess) return true;
                var s = TryReadState();
                return s?.Active == true;
            }
        }
    }

    public static string? LastMessage { get; private set; }

    /// <summary>Snapshot baseline (once) and apply our pins.</summary>
    public static string BeginSession()
    {
        lock (Gate)
        {
            if (_activeInProcess)
            {
                G920OemRegistration.EnsureRegistered(installSdk: true);
                LastMessage = "OEM/SDK session already active; refreshed pins.";
                return LastMessage;
            }

            var existing = TryReadState();
            OemSessionState state;
            if (existing is { Active: true })
            {
                // Previous crash left Active; reuse that baseline.
                state = existing;
            }
            else
            {
                state = CaptureBaseline();
                state.Active = true;
                WriteState(state);
            }

            _activeInProcess = true;
            G920OemRegistration.EnsureSdkFilesCached();
            G920OemRegistration.EnsureRegistered(installSdk: true);
            LastMessage = "OEM/SDK session started (pins applied).";
            return LastMessage;
        }
    }

    /// <summary>Restore baseline / clear our pins. Safe if not active.</summary>
    public static string EndSession()
    {
        lock (Gate)
        {
            var state = TryReadState();
            if (state is not null)
            {
                state.Active = false;
                try { WriteState(state); } catch { /* ignore */ }
            }

            string result;
            if (state is not null)
            {
                result = RestoreFromBaseline(state);
                try { File.Delete(SessionFilePath); } catch { /* ignore */ }
            }
            else if (HasOurPinsPresent())
            {
                result = G920OemRegistration.RestoreSystemLogitechRegistration(restoreLogitechOemClsid: false);
            }
            else
            {
                result = "OEM/SDK session already idle.";
            }

            _activeInProcess = false;
            LastMessage = result;
            return result;
        }
    }

    /// <summary>
    /// On app launch: if session marked Active (crash) or leftover pins while idle, restore.
    /// Also clears any leftover SessionWatch process from older builds.
    /// </summary>
    public static string RecoverIfDirty()
    {
        lock (Gate)
        {
            KillLegacySessionWatchProcesses();

            if (_activeInProcess)
                return "Session active in this process; skip recover.";

            var state = TryReadState();
            if (state?.Active == true || HasOurPinsPresent() || HasOurOemTreeLeftover())
            {
                string msg;
                if (state is { Active: true })
                    msg = RestoreFromBaseline(state);
                else
                    msg = G920OemRegistration.RestoreSystemLogitechRegistration(restoreLogitechOemClsid: false);
                try { File.Delete(SessionFilePath); } catch { /* ignore */ }
                _activeInProcess = false;
                LastMessage = "Recovered dirty OEM/SDK registration: " + msg;
                return LastMessage;
            }

            LastMessage = "OEM/SDK idle (clean).";
            return LastMessage;
        }
    }

    public static OemSessionUiState GetUiState()
    {
        if (IsActive) return OemSessionUiState.Active;
        if (HasOurPinsPresent() || HasOurOemTreeLeftover() || TryReadState()?.Active == true)
            return OemSessionUiState.NeedsRestore;
        return OemSessionUiState.Idle;
    }

    /// <summary>
    /// One-shot cleanup for <c>G920Emulator.SessionWatch</c> left by older releases.
    /// </summary>
    public static void KillLegacySessionWatchProcesses()
    {
        foreach (var name in new[] { "G920Emulator.SessionWatch", "SessionWatch" })
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == Environment.ProcessId)
                        continue;
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(2000);
                    }
                }
                catch { /* ignore */ }
                finally
                {
                    try { p.Dispose(); } catch { /* ignore */ }
                }
            }
        }
    }

    /// <summary>True when our OEM CLSID, SDK pin, or g920ffb COM is present on the system.</summary>
    public static bool HasOurPinsPresent()
    {
        if (OemClsidIsOurs(Registry.CurrentUser) || OemClsidIsOurs(Registry.LocalMachine))
            return true;
        if (SdkPinnedToOurs(RegistryView.Registry64) || SdkPinnedToOurs(RegistryView.Registry32))
            return true;
        if (OurComRegistered())
            return true;
        return false;
    }

    /// <summary>
    /// True when the G920 OEM joystick tree still exists with our OEMName (CLSID may already be cleared).
    /// That leftover tree alone can upset games that probe VID_046D&amp;PID_C262.
    /// </summary>
    public static bool HasOurOemTreeLeftover()
    {
        return OemTreeIsOurs(Registry.CurrentUser) || OemTreeIsOurs(Registry.LocalMachine);
    }

    private static bool OemTreeIsOurs(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(
                @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C262");
            var name = key?.GetValue("OEMName") as string;
            return string.Equals(name, G920OemRegistration.DisplayName, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static bool OemClsidIsOurs(RegistryKey root)
    {
        try
        {
            using var ff = root.OpenSubKey(
                @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C262\OEMForceFeedback");
            var clsid = ff?.GetValue("CLSID") as string;
            return clsid is not null &&
                   clsid.Contains("A920FFB0", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool SdkPinnedToOurs(RegistryView view)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(
                $@"SOFTWARE\Classes\CLSID\{G920OemRegistration.SteeringWheelSdkClsid}\ServerBinary");
            var path = key?.GetValue("") as string;
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.Contains("G920Emulator", StringComparison.OrdinalIgnoreCase) &&
                   path.Contains("LogitechSDK", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool OurComRegistered()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\CLSID\{G920OemRegistration.OemFfbClsid}\InprocServer32")
                ?? Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Classes\CLSID\{G920OemRegistration.OemFfbClsid}\InprocServer32");
            return k?.GetValue("") is string;
        }
        catch { return false; }
    }

    private static OemSessionState CaptureBaseline()
    {
        // Never treat our own pins as "stock" baseline (legacy permanent installs).
        var hkcu = ReadOemClsid(Registry.CurrentUser);
        var hklm = ReadOemClsid(Registry.LocalMachine);
        var sdk64 = ReadSdk(RegistryView.Registry64);
        var sdk86 = ReadSdk(RegistryView.Registry32);
        var hkcuOurs = IsOurOemClsid(hkcu);
        var hklmOurs = IsOurOemClsid(hklm);
        var sdk64Ours = IsOurSdkPath(sdk64);
        var sdk86Ours = IsOurSdkPath(sdk86);

        return new OemSessionState
        {
            Active = false,
            CapturedUtc = DateTime.UtcNow,
            OemClsidHkcu = hkcuOurs ? null : hkcu,
            OemClsidHklm = hklmOurs ? null : hklm,
            OemClsidHkcuPresent = !hkcuOurs && OemClsidValueExists(Registry.CurrentUser),
            OemClsidHklmPresent = !hklmOurs && OemClsidValueExists(Registry.LocalMachine),
            OemTreeHkcuExisted = OemTreeExists(Registry.CurrentUser) && !hkcuOurs,
            OemTreeHklmExisted = OemTreeExists(Registry.LocalMachine) && !hklmOurs,
            SdkServerBinaryX64 = sdk64Ours ? null : sdk64,
            SdkServerBinaryX86 = sdk86Ours ? null : sdk86,
            SdkX64Present = !sdk64Ours && sdk64 is not null,
            SdkX86Present = !sdk86Ours && sdk86 is not null,
        };
    }

    private static bool IsOurOemClsid(string? clsid) =>
        clsid is not null && clsid.Contains("A920FFB0", StringComparison.OrdinalIgnoreCase);

    private static bool IsOurSdkPath(string? path) =>
        path is not null &&
        path.Contains("G920Emulator", StringComparison.OrdinalIgnoreCase) &&
        path.Contains("LogitechSDK", StringComparison.OrdinalIgnoreCase);

    private static bool OemTreeExists(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(
                @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C262");
            return key is not null;
        }
        catch { return false; }
    }

    private static string? ReadOemClsid(RegistryKey root)
    {
        try
        {
            using var ff = root.OpenSubKey(
                @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C262\OEMForceFeedback");
            return ff?.GetValue("CLSID") as string;
        }
        catch { return null; }
    }

    private static bool OemClsidValueExists(RegistryKey root)
    {
        try
        {
            using var ff = root.OpenSubKey(
                @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C262\OEMForceFeedback");
            if (ff is null) return false;
            return ff.GetValueNames().Any(n => string.Equals(n, "CLSID", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static string? ReadSdk(RegistryView view)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(
                $@"SOFTWARE\Classes\CLSID\{G920OemRegistration.SteeringWheelSdkClsid}\ServerBinary");
            return key?.GetValue("") as string;
        }
        catch { return null; }
    }

    private static string RestoreFromBaseline(OemSessionState state)
    {
        var parts = new List<string>();

        // Prefer full tree removal when the tree did not exist before our session.
        RestoreOemTree(Registry.CurrentUser, state.OemTreeHkcuExisted, state.OemClsidHkcuPresent, state.OemClsidHkcu, parts, "HKCU");
        RestoreOemTree(Registry.LocalMachine, state.OemTreeHklmExisted, state.OemClsidHklmPresent, state.OemClsidHklm, parts, "HKLM");

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                var clsidRoot = root == Registry.LocalMachine
                    ? @"SOFTWARE\Classes\CLSID\"
                    : @"Software\Classes\CLSID\";
                root.DeleteSubKeyTree(clsidRoot + G920OemRegistration.OemFfbClsid, throwOnMissingSubKey: false);
                parts.Add($"{root.Name} g920ffb COM removed");
            }
            catch (Exception ex)
            {
                parts.Add($"{root.Name} COM: {ex.Message}");
            }
        }

        RestoreSdk(RegistryView.Registry64, state.SdkX64Present, state.SdkServerBinaryX64, parts, "x64");
        RestoreSdk(RegistryView.Registry32, state.SdkX86Present, state.SdkServerBinaryX86, parts, "x86");

        return parts.Count == 0 ? "Baseline restored (no changes)." : string.Join("; ", parts);
    }

    private static void RestoreOemTree(
        RegistryKey root,
        bool treeExistedBefore,
        bool clsidWasPresent,
        string? clsidValue,
        List<string> parts,
        string label)
    {
        try
        {
            var parentPath =
                @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";

            if (!treeExistedBefore || IsOurOemClsid(clsidValue))
            {
                using var parent = root.OpenSubKey(parentPath, writable: true);
                parent?.DeleteSubKeyTree("VID_046D&PID_C262", throwOnMissingSubKey: false);
                parts.Add($"{label} OEM tree removed");
                return;
            }

            if (clsidWasPresent && !string.IsNullOrEmpty(clsidValue))
            {
                using var ff = root.CreateSubKey(parentPath + @"\VID_046D&PID_C262\OEMForceFeedback", writable: true);
                ff?.SetValue("CLSID", clsidValue, RegistryValueKind.String);
                parts.Add($"{label} OEM CLSID restored");
            }
            else
            {
                using var ff = root.OpenSubKey(parentPath + @"\VID_046D&PID_C262\OEMForceFeedback", writable: true);
                try { ff?.DeleteValue("CLSID"); } catch (ArgumentException) { /* missing */ }
                parts.Add($"{label} OEM CLSID cleared");
            }
        }
        catch (Exception ex)
        {
            parts.Add($"{label} OEM: {ex.Message}");
        }
    }

    private static void RestoreSdk(RegistryView view, bool wasPresent, string? value, List<string> parts, string label)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var parent = hklm.OpenSubKey(
                $@"SOFTWARE\Classes\CLSID\{G920OemRegistration.SteeringWheelSdkClsid}", writable: true);

            if (!wasPresent || string.IsNullOrEmpty(value))
            {
                parent?.DeleteSubKeyTree("ServerBinary", throwOnMissingSubKey: false);
                parts.Add($"SDK {label}: ServerBinary removed");
                return;
            }

            using var key = hklm.CreateSubKey(
                $@"SOFTWARE\Classes\CLSID\{G920OemRegistration.SteeringWheelSdkClsid}\ServerBinary",
                writable: true);
            key?.SetValue("", value, RegistryValueKind.String);
            parts.Add($"SDK {label}: ServerBinary restored");
        }
        catch (Exception ex)
        {
            parts.Add($"SDK {label}: {ex.Message}");
        }
    }

    private static OemSessionState? TryReadState()
    {
        try
        {
            if (!File.Exists(SessionFilePath)) return null;
            var json = File.ReadAllText(SessionFilePath);
            return JsonSerializer.Deserialize<OemSessionState>(json, JsonOpts);
        }
        catch { return null; }
    }

    private static void WriteState(OemSessionState state)
    {
        var dir = Path.GetDirectoryName(SessionFilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(SessionFilePath, JsonSerializer.Serialize(state, JsonOpts));
    }

    private sealed class OemSessionState
    {
        public bool Active { get; set; }
        public DateTime CapturedUtc { get; set; }
        public string? OemClsidHkcu { get; set; }
        public string? OemClsidHklm { get; set; }
        public bool OemClsidHkcuPresent { get; set; }
        public bool OemClsidHklmPresent { get; set; }
        public bool OemTreeHkcuExisted { get; set; }
        public bool OemTreeHklmExisted { get; set; }
        public string? SdkServerBinaryX64 { get; set; }
        public string? SdkServerBinaryX86 { get; set; }
        public bool SdkX64Present { get; set; }
        public bool SdkX86Present { get; set; }
    }
}
