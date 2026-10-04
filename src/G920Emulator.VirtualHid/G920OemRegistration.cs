using Microsoft.Win32;
using System.Reflection;

namespace G920Emulator.VirtualHid;

/// <summary>
/// Writes the DirectInput OEM joystick identity for VID_046D&amp;PID_C262 and registers
/// our <c>g920ffb.dll</c> as the OEMForceFeedback COM server (bypasses Logitech HID++).
/// </summary>
public static class G920OemRegistration
{
    public const string OemKeyName = @"VID_046D&PID_C262";
    public const string DisplayName = "Logitech G920 Driving Force Racing Wheel USB";

    /// <summary>Our IDirectInputEffectDriver CLSID (native g920ffb.dll).</summary>
    public const string OemFfbClsid = "{A920FFB0-E7DB-4329-8C13-A966D84A289F}";

    private static readonly byte[] OemData = [0x43, 0x00, 0x08, 0x10, 0x12, 0x00, 0x00, 0x00];

    private const string RelativeOem =
        @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\" + OemKeyName;

    public static string? LastDllPath { get; private set; }
    public static string? LastMessage { get; private set; }

    /// <param name="installSdk">
    /// True: also install the Logitech SDK if it isn't yet (Start bridge).
    /// False: only re-pin an SDK that is already installed (background guard).
    /// </param>
    public static void EnsureRegistered(bool forceRewrite = false, bool installSdk = true)
    {
        var dll = ResolveG920FfbDllPath();
        LastDllPath = dll;
        if (dll is not null)
            RegisterComServer(dll);

        // Same as last-known-good: overwrite OEM values in place.
        // Do NOT delete the OEM tree — that was a post-G-HUB experiment and can briefly
        // strip Axes/Buttons Heat uses for wheel layout detection.
        _ = forceRewrite;
        WriteOemTree(Registry.CurrentUser);
        WriteOemTree(Registry.LocalMachine);

        // Always pin CLSID to g920ffb (G HUB loves to put Logitech's back).
        ForceOemFfbClsid(Registry.CurrentUser);
        ForceOemFfbClsid(Registry.LocalMachine);

        if (installSdk)
            InstallSteeringWheelSdk();
        else
            RepinSteeringWheelSdk();

        LastMessage = dll is null
            ? "OEM registry written; g920ffb.dll not found beside EXE."
            : $"OEM + COM registered → {dll}";
    }

    /// <summary>
    /// Games built on the Logitech Steering Wheel SDK (NFS Heat, etc.) load the SDK DLL from
    /// <c>HKLM\SOFTWARE\Classes\CLSID\{63BD165D-…}\ServerBinary</c>. Without it they never
    /// identify a Logitech wheel and fall back to generic controller handling.
    /// G HUB's installer repoints it at G HUB's SDK (which only sees wheels G HUB owns) and its
    /// uninstaller deletes the key, so we keep a private copy of the standalone LGS SDK and pin to it.
    /// </summary>
    public const string SteeringWheelSdkClsid = "{63BD165D-1584-4E75-AB56-08330350545F}";

    private const string SdkDllName = "LogitechSteeringWheel.dll";

    public static string SteeringWheelSdkCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "G920Emulator", "LogitechSDK");

    private static string CachedSdkPath(bool x64) =>
        Path.Combine(SteeringWheelSdkCacheDir, x64 ? "x64" : "x86", SdkDllName);

    /// <summary>Our private SDK copy exists (x64 + x86).</summary>
    public static bool IsSteeringWheelSdkInstalled =>
        File.Exists(CachedSdkPath(x64: true)) && File.Exists(CachedSdkPath(x64: false));

    /// <summary>
    /// True when the SDK isn't installed yet (nothing to guard) or both registry views point at our copy.
    /// </summary>
    public static bool IsSteeringWheelSdkPinned() =>
        !IsSteeringWheelSdkInstalled ||
        (IsServerBinaryPinned(RegistryView.Registry64, x64: true) &&
         IsServerBinaryPinned(RegistryView.Registry32, x64: false));

    /// <summary>
    /// Copies the bundled SDK (or a standalone LGS SDK on disk) to ProgramData and registers it
    /// for 64- and 32-bit games. Requires elevation.
    /// </summary>
    public static string InstallSteeringWheelSdk()
    {
        var errors = new List<string>();
        foreach (var x64 in new[] { true, false })
        {
            var arch = x64 ? "x64" : "x86";
            var cached = CachedSdkPath(x64);
            var source = FindStandaloneSdkDll(x64);
            try
            {
                if (source is not null && !SameFile(source, cached))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
                    File.Copy(source, cached, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{arch} copy: {ex.Message}");
            }

            if (!File.Exists(cached))
            {
                errors.Add($"{arch}: no SDK DLL found (bundled logisdk\\{arch} missing)");
                continue;
            }

            if (!WriteServerBinary(x64 ? RegistryView.Registry64 : RegistryView.Registry32, cached, out var err))
                errors.Add($"{arch} registry: {err}");
        }

        return errors.Count == 0
            ? $"Logitech Steering Wheel SDK installed → {SteeringWheelSdkCacheDir}"
            : "Logitech Steering Wheel SDK install incomplete: " + string.Join("; ", errors);
    }

    /// <summary>Re-points the registry at our copy if something (G HUB) changed or deleted it.</summary>
    public static void RepinSteeringWheelSdk()
    {
        if (!IsSteeringWheelSdkInstalled)
            return;
        if (!IsServerBinaryPinned(RegistryView.Registry64, x64: true))
            WriteServerBinary(RegistryView.Registry64, CachedSdkPath(x64: true), out _);
        if (!IsServerBinaryPinned(RegistryView.Registry32, x64: false))
            WriteServerBinary(RegistryView.Registry32, CachedSdkPath(x64: false), out _);
    }

    private static bool SameFile(string a, string b)
    {
        try
        {
            if (!File.Exists(b)) return false;
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsServerBinaryPinned(RegistryView view, bool x64)
    {
        var current = ReadServerBinary(view);
        return current is not null &&
               string.Equals(Path.GetFullPath(current), Path.GetFullPath(CachedSdkPath(x64)), StringComparison.OrdinalIgnoreCase);
    }

    private static bool WriteServerBinary(RegistryView view, string target, out string? error)
    {
        error = null;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.CreateSubKey($@"SOFTWARE\Classes\CLSID\{SteeringWheelSdkClsid}\ServerBinary", writable: true);
            key?.SetValue("", target, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message; // requires elevation
            return false;
        }
    }

    private static string? ReadServerBinary(RegistryView view)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey($@"SOFTWARE\Classes\CLSID\{SteeringWheelSdkClsid}\ServerBinary");
            return key?.GetValue("") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindStandaloneSdkDll(bool x64)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var bundled = Path.Combine(AppContext.BaseDirectory, "logisdk", x64 ? "x64" : "x86", SdkDllName);
        var candidates = x64
            ? new[]
            {
                bundled,
                Path.Combine(pf, @"Logitech\Gaming Software\SDKs", SdkDllName),
            }
            : new[]
            {
                bundled,
                Path.Combine(pf, @"Logitech\Gaming Software\SDKs\32", SdkDllName),
                Path.Combine(pf86, @"Logitech\Gaming Software\SDKs", SdkDllName),
            };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// True when every registry value G HUB is known to touch still matches what we write:
    /// OEM name/data/axes (HKLM + HKCU), OEM FFB CLSID, our COM server, and the SDK pin.
    /// </summary>
    public static bool IsIntact()
    {
        return IsOemTreeIntact(Registry.LocalMachine) &&
               IsOemTreeIntact(Registry.CurrentUser) &&
               IsComServerIntact() &&
               IsSteeringWheelSdkPinned();
    }

    private static bool IsOemTreeIntact(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(RelativeOem);
            if (key is null) return false;
            if (key.GetValue("OEMName") as string != DisplayName) return false;
            if (key.GetValue("OEMData") is not byte[] data || !data.AsSpan().SequenceEqual(OemData)) return false;
            using var wheel = key.OpenSubKey(@"Axes\0");
            if (wheel is null) return false;
            using var ff = key.OpenSubKey("OEMForceFeedback");
            return string.Equals(ff?.GetValue("CLSID") as string, OemFfbClsid, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsComServerIntact()
    {
        var dll = ResolveG920FfbDllPath();
        if (dll is null)
            return true;
        try
        {
            using var inproc = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{OemFfbClsid}\InprocServer32")
                               ?? Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{OemFfbClsid}\InprocServer32");
            return inproc?.GetValue("") is string path && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static void ForceOemFfbClsid(RegistryKey root)
    {
        try
        {
            using var ff = root.CreateSubKey(RelativeOem + @"\OEMForceFeedback", writable: true);
            ff?.SetValue("CLSID", OemFfbClsid, RegistryValueKind.String);
        }
        catch
        {
            // ignore
        }
    }

    private static string? ResolveG920FfbDllPath()
    {
        var candidates = new List<string>();
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(exeDir))
                candidates.Add(Path.Combine(exeDir, "g920ffb.dll"));
        }
        catch { /* ignore */ }

        try
        {
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(asmDir))
                candidates.Add(Path.Combine(asmDir, "g920ffb.dll"));
        }
        catch { /* ignore */ }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "g920ffb.dll"));

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(c))
                return Path.GetFullPath(c);
        }

        return null;
    }

    private static void RegisterComServer(string dllPath)
    {
        // HKCU CLSID is enough for the current user; also try HKLM when elevated.
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                var clsidRoot = root == Registry.CurrentUser
                    ? @"Software\Classes\CLSID\"
                    : @"Software\Classes\CLSID\";
                // HKLM Classes is under HKLM\SOFTWARE\Classes
                if (root == Registry.LocalMachine)
                    clsidRoot = @"SOFTWARE\Classes\CLSID\";

                using var clsid = root.CreateSubKey(clsidRoot + OemFfbClsid, writable: true);
                clsid?.SetValue("", "G920 Emulator Force Feedback Driver");
                using var inproc = clsid?.CreateSubKey("InprocServer32", writable: true);
                if (inproc is not null)
                {
                    inproc.SetValue("", dllPath);
                    inproc.SetValue("ThreadingModel", "Both");
                }
            }
            catch
            {
                // HKLM may fail without elevation.
            }
        }
    }

    private static void WriteOemTree(RegistryKey root)
    {
        try
        {
            using var key = root.CreateSubKey(RelativeOem, writable: true);
            if (key is null) return;

            key.SetValue("OEMName", DisplayName, RegistryValueKind.String);
            key.SetValue("OEMData", OemData, RegistryValueKind.Binary);
            key.SetValue("Version", 1, RegistryValueKind.DWord);

            WriteAxis(key, "0", "Wheel", [0x01, 0x81, 0x00, 0x00], ffAttributes: [0x0A, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00]);
            WriteAxis(key, "2", "Accelerator", [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x31, 0x00], ffAttributes: null);
            WriteAxis(key, "5", "Brake", [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x32, 0x00], ffAttributes: null);
            WriteAxis(key, "6", "Clutch", [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x35, 0x00], ffAttributes: null);

            for (var i = 0; i <= 17; i++)
            {
                using var btn = key.CreateSubKey($@"Buttons\{i}", writable: true);
                btn?.SetValue("Attributes", new byte[] { 0x02, 0x80, 0x00, 0x00 }, RegistryValueKind.Binary);
            }

            using var ff = key.CreateSubKey("OEMForceFeedback", writable: true);
            if (ff is not null)
            {
                ff.SetValue("Attributes", new byte[] { 0x00, 0x00, 0x00, 0x00, 0xE8, 0x03, 0x00, 0x00, 0xE8, 0x03, 0x00, 0x00 }, RegistryValueKind.Binary);
                ff.SetValue("CLSID", OemFfbClsid, RegistryValueKind.String);
                WriteEffect(ff, "{13541C20-8E33-11D0-9AD0-00A0C9A06E35}", "Constant Force", [0x00, 0x00, 0x00, 0x00, 0x01, 0x86, 0x00, 0x00, 0xED, 0x03, 0x00, 0x00, 0xED, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
                WriteEffect(ff, "{13541C21-8E33-11D0-9AD0-00A0C9A06E35}", "Ramp Force", [0x01, 0x00, 0x00, 0x00, 0x02, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
                WriteEffect(ff, "{13541C22-8E33-11D0-9AD0-00A0C9A06E35}", "Square Wave", [0x02, 0x00, 0x00, 0x00, 0x03, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
                WriteEffect(ff, "{13541C23-8E33-11D0-9AD0-00A0C9A06E35}", "Sine Wave", [0x03, 0x00, 0x00, 0x00, 0x03, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
                WriteEffect(ff, "{13541C24-8E33-11D0-9AD0-00A0C9A06E35}", "Triangle Wave", [0x04, 0x00, 0x00, 0x00, 0x03, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
                WriteEffect(ff, "{13541C25-8E33-11D0-9AD0-00A0C9A06E35}", "Sawtooth Up Wave", [0x05, 0x00, 0x00, 0x00, 0x03, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
                WriteEffect(ff, "{13541C26-8E33-11D0-9AD0-00A0C9A06E35}", "Sawtooth Down Wave", [0x06, 0x00, 0x00, 0x00, 0x03, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
                WriteEffect(ff, "{13541C27-8E33-11D0-9AD0-00A0C9A06E35}", "Spring Force", [0x07, 0x00, 0x00, 0x00, 0x04, 0xD8, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
                WriteEffect(ff, "{13541C28-8E33-11D0-9AD0-00A0C9A06E35}", "Damper Force", [0x08, 0x00, 0x00, 0x00, 0x04, 0xD8, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
                WriteEffect(ff, "{13541C29-8E33-11D0-9AD0-00A0C9A06E35}", "Inertia Force", [0x09, 0x00, 0x00, 0x00, 0x04, 0xD8, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
                WriteEffect(ff, "{13541C2A-8E33-11D0-9AD0-00A0C9A06E35}", "Friction Force", [0x0A, 0x00, 0x00, 0x00, 0x04, 0xD8, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
            }
        }
        catch
        {
            // HKLM may fail if not elevated; HKCU still helps for the current user.
        }
    }

    private static void WriteAxis(RegistryKey oem, string index, string name, byte[] attributes, byte[]? ffAttributes)
    {
        using var axis = oem.CreateSubKey($@"Axes\{index}", writable: true);
        if (axis is null) return;
        axis.SetValue("", name, RegistryValueKind.String);
        axis.SetValue("Attributes", attributes, RegistryValueKind.Binary);
        if (ffAttributes is not null)
            axis.SetValue("FFAttributes", ffAttributes, RegistryValueKind.Binary);
    }

    private static void WriteEffect(RegistryKey ffRoot, string effectClsid, string name, byte[] attributes)
    {
        using var effect = ffRoot.CreateSubKey($@"Effects\{effectClsid}", writable: true);
        if (effect is null) return;
        effect.SetValue("", name, RegistryValueKind.String);
        effect.SetValue("Attributes", attributes, RegistryValueKind.Binary);
    }
}
