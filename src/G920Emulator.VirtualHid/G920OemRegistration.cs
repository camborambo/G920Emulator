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

    public static void EnsureRegistered()
    {
        var dll = ResolveG920FfbDllPath();
        LastDllPath = dll;
        if (dll is not null)
            RegisterComServer(dll);

        WriteOemTree(Registry.CurrentUser);
        WriteOemTree(Registry.LocalMachine);
        LastMessage = dll is null
            ? "OEM registry written; g920ffb.dll not found beside EXE."
            : $"OEM + COM registered → {dll}";
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
