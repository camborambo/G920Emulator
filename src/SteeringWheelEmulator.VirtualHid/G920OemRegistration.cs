using Microsoft.Win32;
using System.Reflection;

namespace SteeringWheelEmulator.VirtualHid;

/// <summary>
/// Writes the DirectInput OEM joystick identity for VID_046D&amp;PID_C262 and registers
/// our <c>emuffb.dll</c> as the OEMForceFeedback COM server (bypasses Logitech HID++).
/// </summary>
public static class G920OemRegistration
{
    public const string OemKeyName = @"VID_046D&PID_C262";
    public const string DisplayName = "Logitech G920 Driving Force Racing Wheel USB";

    /// <summary>Our IDirectInputEffectDriver CLSID (native emuffb.dll).</summary>
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
        var dll = ResolveEmuffbDllPath();
        LastDllPath = dll;

        // Never register the install-folder DLL — Steam/games would lock Desktop\G920Emulator.
        if (dll is not null && IsProgramDataEmuffbPath(dll))
            RegisterComServer(dll);
        else if (File.Exists(EmuffbCachedDllPath))
        {
            dll = EmuffbCachedDllPath;
            LastDllPath = dll;
            RegisterComServer(dll);
        }

        // Rewrite any stale InprocServer32 still pointing at the install folder.
        var rewritten = RewriteStaleInstallFolderComServers();

        // Same as last-known-good: overwrite OEM values in place.
        // Do NOT delete the OEM tree - that was a post-G-HUB experiment and can briefly
        // strip Axes/Buttons Heat uses for wheel layout detection.
        _ = forceRewrite;
        WriteOemTree(Registry.CurrentUser);
        WriteOemTree(Registry.LocalMachine);

        // Always pin CLSID to emuffb (G HUB loves to put Logitech's back).
        ForceOemFfbClsid(Registry.CurrentUser);
        ForceOemFfbClsid(Registry.LocalMachine);

        if (installSdk)
        {
            EnsureSdkFilesCached();
            PinSteeringWheelSdk();
        }
        else
            PinSteeringWheelSdk();

        if (dll is null)
        {
            LastMessage = "OEM registry written; emuffb.dll cache unavailable (not beside EXE / copy failed).";
        }
        else
        {
            LastMessage = rewritten > 0
                ? $"OEM + COM registered → {dll} (rewrote {rewritten} stale install-folder InprocServer32)"
                : $"OEM + COM registered → {dll}";
        }
    }

    /// <summary>Copy bundled SDK DLLs to ProgramData only - does not write registry.</summary>
    public static string EnsureSdkFilesCached()
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
                errors.Add($"{arch}: no SDK DLL found (bundled logisdk\\{arch} missing)");
        }

        return errors.Count == 0
            ? $"Logitech SDK files cached → {SteeringWheelSdkCacheDir}"
            : "Logitech SDK cache incomplete: " + string.Join("; ", errors);
    }

    /// <summary>Pin HKLM ServerBinary to our cached SDK (session only).</summary>
    public static string PinSteeringWheelSdk()
    {
        var errors = new List<string>();
        foreach (var x64 in new[] { true, false })
        {
            var arch = x64 ? "x64" : "x86";
            var cached = CachedSdkPath(x64);
            if (!File.Exists(cached))
            {
                errors.Add($"{arch}: cached DLL missing");
                continue;
            }

            if (!WriteServerBinary(x64 ? RegistryView.Registry64 : RegistryView.Registry32, cached, out var err))
                errors.Add($"{arch} registry: {err}");
        }

        return errors.Count == 0
            ? $"Logitech SDK pinned → {SteeringWheelSdkCacheDir}"
            : "Logitech SDK pin incomplete: " + string.Join("; ", errors);
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

    public static string SteeringWheelSdkCacheDir =>
        Path.Combine(Core.AppPaths.ProgramDataRoot, "LogitechSDK");

    private static string CachedSdkPath(bool x64) =>
        Path.Combine(SteeringWheelSdkCacheDir, x64 ? "x64" : "x86", SdkDllName);

    /// <summary>Our private SDK copy exists (x64 + x86).</summary>
    public static bool IsSteeringWheelSdkInstalled =>
        File.Exists(CachedSdkPath(x64: true)) && File.Exists(CachedSdkPath(x64: false));

    /// <summary>True when both registry views point at our cached SDK copy.</summary>
    public static bool IsSteeringWheelSdkPinned() =>
        IsSteeringWheelSdkInstalled &&
        IsServerBinaryPinned(RegistryView.Registry64, x64: true) &&
        IsServerBinaryPinned(RegistryView.Registry32, x64: false);

    /// <summary>
    /// Legacy name: cache files + pin. Prefer session <see cref="OemRegistrationSession.BeginSession"/>;
    /// kept for any remaining call sites.
    /// </summary>
    public static string InstallSteeringWheelSdk()
    {
        var cache = EnsureSdkFilesCached();
        var pin = PinSteeringWheelSdk();
        return cache + " · " + pin;
    }

    /// <summary>Re-points the registry at our copy if something (G HUB) changed or deleted it.</summary>
    public static void RepinSteeringWheelSdk() => PinSteeringWheelSdk();

    /// <summary>
    /// Deletes our private Logitech SDK DLL cache under ProgramData (files only; registry via restore).
    /// </summary>
    public static string RemoveCachedSdkFiles() =>
        RemoveProgramDataCacheDir(SteeringWheelSdkCacheDir, "SDK cache");

    /// <summary>
    /// Deletes the runtime emuffb.dll cache under ProgramData (COM InprocServer copy),
    /// plus the legacy <c>g920ffb</c> cache folder if present.
    /// </summary>
    public static string RemoveCachedEmuffbFiles()
    {
        var primary = RemoveProgramDataCacheDir(EmuffbCacheDir, "emuffb cache");
        var legacy = RemoveProgramDataCacheDir(LegacyG920FfbCacheDir, "legacy g920ffb cache");
        return primary + " · " + legacy;
    }

    private static string RemoveProgramDataCacheDir(string dir, string label)
    {
        try
        {
            if (!Directory.Exists(dir))
                return label + ": already absent";

            Directory.Delete(dir, recursive: true);

            // Remove empty ProgramData\G920Emulator if nothing else remains.
            var parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent) &&
                Directory.Exists(parent) &&
                !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                try { Directory.Delete(parent); } catch { /* ignore */ }
            }

            return label + " removed: " + dir;
        }
        catch (Exception ex)
        {
            return label + ": " + ex.Message;
        }
    }

    /// <summary>
    /// Undoes global OEM FFB + Logitech SDK pins left by <see cref="EnsureRegistered"/>.
    /// Removes the whole G920 OEM tree we write (not just CLSID), unregisters emuffb COM,
    /// and clears SDK ServerBinary. Needed for Forza Horizon and similar titles.
    /// </summary>
    public static string RestoreSystemLogitechRegistration(bool restoreLogitechOemClsid = false)
    {
        const string logitechOemFfbClsid = "{62B43F0E-E7DB-4329-8C13-A966D84A289F}";
        var parts = new List<string>();

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                if (restoreLogitechOemClsid)
                {
                    using var ff = root.CreateSubKey(RelativeOem + @"\OEMForceFeedback", writable: true);
                    ff?.SetValue("CLSID", logitechOemFfbClsid, RegistryValueKind.String);
                    parts.Add($"{root.Name} OEM CLSID → Logitech");
                }
                else
                {
                    // Delete the entire OEM VID/PID tree we created - leaving Axes/Effects
                    // with a blank CLSID can still break games that probe G920 OEM data.
                    var parentPath =
                        @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";
                    using var parent = root.OpenSubKey(parentPath, writable: true);
                    if (parent is not null)
                    {
                        parent.DeleteSubKeyTree(OemKeyName, throwOnMissingSubKey: false);
                        parts.Add($"{root.Name} OEM tree {OemKeyName} removed");
                    }
                }
            }
            catch (Exception ex)
            {
                parts.Add($"{root.Name} OEM: {ex.Message}");
            }
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    baseKey.DeleteSubKeyTree($@"SOFTWARE\Classes\CLSID\{OemFfbClsid}", throwOnMissingSubKey: false);
                    parts.Add($"{hive}/{view} emuffb COM removed");
                }
                catch (Exception ex)
                {
                    parts.Add($"{hive}/{view} COM: {ex.Message}");
                }
            }
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var parent = hklm.OpenSubKey($@"SOFTWARE\Classes\CLSID\{SteeringWheelSdkClsid}", writable: true);
                if (parent is null)
                {
                    parts.Add($"SDK {view}: already absent");
                    continue;
                }
                parent.DeleteSubKeyTree("ServerBinary", throwOnMissingSubKey: false);
                parts.Add($"SDK {view}: ServerBinary removed");
            }
            catch (Exception ex)
            {
                parts.Add($"SDK {view}: {ex.Message}");
            }
        }

        return string.Join("; ", parts);
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
        if (!File.Exists(EmuffbCachedDllPath))
            return ResolveEmuffbDllPath() is null; // no DLL expected yet

        foreach (var entry in EnumerateComInprocEntries())
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
                continue;
            if (IsInstallFolderDll(entry.Path))
                return false;
            if (!IsProgramDataEmuffbPath(entry.Path) || !File.Exists(entry.Path))
                return false;
        }

        // At least one view should point at ProgramData when cache exists.
        return EnumerateComInprocEntries().Any(e =>
            !string.IsNullOrWhiteSpace(e.Path) && IsProgramDataEmuffbPath(e.Path));
    }

    /// <summary>
    /// True when a 64-bit game can load emuffb and the G920 OEM ForceFeedback tree exists.
    /// </summary>
    public static bool IsGameFfbRegistrationReady(out string missing)
    {
        var problems = new List<string>();

        if (!File.Exists(EmuffbCachedDllPath))
            problems.Add("emuffb.dll cache missing under ProgramData");

        var hasCom64 = false;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var inproc = baseKey.OpenSubKey(
                    $@"SOFTWARE\Classes\CLSID\{OemFfbClsid}\InprocServer32");
                var path = inproc?.GetValue("") as string;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    hasCom64 = true;
                    break;
                }
            }
            catch { /* ignore */ }
        }

        if (!hasCom64)
            problems.Add("64-bit emuffb COM InprocServer32 absent (64-bit games cannot load EffectDriver)");

        var hasOem = IsOemTreeIntact(Registry.CurrentUser) || IsOemTreeIntact(Registry.LocalMachine);
        if (!hasOem)
            problems.Add("G920 OEMForceFeedback CLSID missing");

        missing = problems.Count == 0 ? "" : string.Join("; ", problems);
        return problems.Count == 0;
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

    /// <summary>
    /// Runtime copy of emuffb.dll. Steam/games load the COM InprocServer from this path;
    /// keeping it out of the install folder lets users delete the Desktop install folder after exit.
    /// </summary>
    public static string EmuffbCacheDir => Path.Combine(Core.AppPaths.ProgramDataRoot, "emuffb");

    public static string EmuffbCachedDllPath => Path.Combine(EmuffbCacheDir, "emuffb.dll");

    /// <summary>Pre-rename ProgramData cache (<c>g920ffb.dll</c>); migrated on Start.</summary>
    public static string LegacyG920FfbCacheDir => Path.Combine(Core.AppPaths.ProgramDataRoot, "g920ffb");

    /// <summary>Pre–product-rename ProgramData cache under <c>G920Emulator\emuffb</c>.</summary>
    public static string LegacyProductEmuffbCacheDir =>
        Path.Combine(Core.AppPaths.LegacyProgramDataRoot, "emuffb");

    public static string LegacyG920FfbCachedDllPath => Path.Combine(LegacyG920FfbCacheDir, "g920ffb.dll");

    /// <summary>
    /// Resolve the DLL path for COM registration. Always ProgramData when possible —
    /// never returns the install-folder copy (Steam would lock that folder).
    /// </summary>
    private static string? ResolveEmuffbDllPath()
    {
        var source = FindBundledEmuffbDll();
        if (source is not null)
            TryUpdateEmuffbCache(source);

        return File.Exists(EmuffbCachedDllPath) ? EmuffbCachedDllPath : null;
    }

    /// <summary>
    /// Copy bundled DLL into ProgramData. Prefer temp+Replace so a Steam-loaded cache
    /// does not force us to re-register the install-folder path.
    /// </summary>
    private static void TryUpdateEmuffbCache(string source)
    {
        try
        {
            Directory.CreateDirectory(EmuffbCacheDir);
            if (SameFile(source, EmuffbCachedDllPath))
                return;

            var temp = Path.Combine(
                EmuffbCacheDir,
                "emuffb.dll." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, temp, overwrite: true);
                if (File.Exists(EmuffbCachedDllPath))
                {
                    // Replace destination; backup ignored (null). May fail if Steam has the DLL mapped.
                    File.Replace(temp, EmuffbCachedDllPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, EmuffbCachedDllPath, overwrite: true);
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
            }
        }
        catch
        {
            // Cache may already exist and be locked by Steam — keep using it; never fall back
            // to registering the install-folder DLL.
            if (!File.Exists(EmuffbCachedDllPath))
            {
                try
                {
                    Directory.CreateDirectory(EmuffbCacheDir);
                    File.Copy(source, EmuffbCachedDllPath, overwrite: false);
                }
                catch
                {
                    // leave missing; EnsureRegistered will not point COM at install folder
                }
            }
        }
    }

    private static string? FindBundledEmuffbDll()
    {
        var candidates = new List<string>();
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(exeDir))
            {
                candidates.Add(Path.Combine(exeDir, "emuffb.dll"));
                candidates.Add(Path.Combine(exeDir, "g920ffb.dll")); // one-release migration
            }
        }
        catch { /* ignore */ }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "emuffb.dll"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "g920ffb.dll"));

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(c))
                return Path.GetFullPath(c);
        }

        return null;
    }

    /// <summary>True when <paramref name="path"/> is under the running EXE / BaseDirectory.</summary>
    public static bool IsInstallFolderDll(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var full = Path.GetFullPath(path);
            foreach (var root in GetInstallFolderRoots())
            {
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    public static bool IsProgramDataEmuffbPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(EmuffbCachedDllPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> GetInstallFolderRoots()
    {
        var roots = new List<string>();
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(exeDir))
                roots.Add(EnsureTrailingSep(Path.GetFullPath(exeDir)));
        }
        catch { /* ignore */ }

        try
        {
            if (!string.IsNullOrEmpty(AppContext.BaseDirectory))
                roots.Add(EnsureTrailingSep(Path.GetFullPath(AppContext.BaseDirectory)));
        }
        catch { /* ignore */ }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSep(string dir) =>
        dir.EndsWith(Path.DirectorySeparatorChar) || dir.EndsWith(Path.AltDirectorySeparatorChar)
            ? dir
            : dir + Path.DirectorySeparatorChar;

    private static void RegisterComServer(string dllPath)
    {
        if (IsInstallFolderDll(dllPath))
            return; // hard guard — never write Desktop/install path into COM

        var full = Path.GetFullPath(dllPath);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var clsid = baseKey.CreateSubKey($@"SOFTWARE\Classes\CLSID\{OemFfbClsid}", writable: true);
                    clsid?.SetValue("", "Steering Wheel Emulator Force Feedback Driver");
                    using var inproc = clsid?.CreateSubKey("InprocServer32", writable: true);
                    if (inproc is not null)
                    {
                        inproc.SetValue("", full);
                        inproc.SetValue("ThreadingModel", "Both");
                    }
                }
                catch
                {
                    // HKLM / some views may fail without elevation.
                }
            }
        }
    }

    /// <summary>
    /// If any InprocServer32 still points at the install folder or legacy <c>g920ffb.dll</c>,
    /// rewrite to ProgramData <c>emuffb.dll</c>. Returns how many keys were rewritten.
    /// </summary>
    public static int RewriteStaleInstallFolderComServers()
    {
        if (!File.Exists(EmuffbCachedDllPath))
            return 0;

        var target = Path.GetFullPath(EmuffbCachedDllPath);
        var count = 0;
        foreach (var entry in EnumerateComInprocEntries())
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
                continue;
            if (!NeedsComPathRewrite(entry.Path, target))
                continue;
            if (TryWriteComInproc(entry.Hive, entry.View, target))
                count++;
        }

        return count;
    }

    private static bool NeedsComPathRewrite(string path, string target)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (string.Equals(full, target, StringComparison.OrdinalIgnoreCase))
                return false;
            if (IsInstallFolderDll(full))
                return true;
            if (full.EndsWith("g920ffb.dll", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(full, Path.GetFullPath(LegacyG920FfbCachedDllPath), StringComparison.OrdinalIgnoreCase))
                return true;
            // Pre–product-rename ProgramData path.
            if (full.Contains(Path.Combine(Core.AppPaths.LegacyFolderName, "emuffb"), StringComparison.OrdinalIgnoreCase) ||
                full.Contains(Path.Combine(Core.AppPaths.LegacyFolderName, "g920ffb"), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { /* ignore */ }

        return false;
    }

    private readonly record struct ComInprocEntry(RegistryHive Hive, RegistryView View, string? Path);

    private static IEnumerable<ComInprocEntry> EnumerateComInprocEntries()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string? path = null;
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var inproc = baseKey.OpenSubKey(
                        $@"SOFTWARE\Classes\CLSID\{OemFfbClsid}\InprocServer32");
                    path = inproc?.GetValue("") as string;
                }
                catch { /* ignore */ }

                yield return new ComInprocEntry(hive, view, path);
            }
        }
    }

    private static bool TryWriteComInproc(RegistryHive hive, RegistryView view, string dllPath)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var clsid = baseKey.CreateSubKey($@"SOFTWARE\Classes\CLSID\{OemFfbClsid}", writable: true);
            clsid?.SetValue("", "Steering Wheel Emulator Force Feedback Driver");
            using var inproc = clsid?.CreateSubKey("InprocServer32", writable: true);
            if (inproc is null)
                return false;
            inproc.SetValue("", dllPath);
            inproc.SetValue("ThreadingModel", "Both");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Human-readable COM InprocServer32 locations for diagnostics export.</summary>
    public static string FormatComInprocDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("emuffb COM InprocServer32 (Steam/games load this DLL path)");
        sb.AppendLine("CLSID: " + OemFfbClsid);
        sb.AppendLine("Expected ProgramData: " + EmuffbCachedDllPath);
        sb.AppendLine("Cache present: " + File.Exists(EmuffbCachedDllPath));
        sb.AppendLine("LastDllPath: " + (LastDllPath ?? "(none)"));
        sb.AppendLine("LastMessage: " + (LastMessage ?? "(none)"));
        sb.AppendLine();

        var anyInstall = false;
        foreach (var entry in EnumerateComInprocEntries())
        {
            var label = $"{entry.Hive}/{entry.View}";
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                sb.AppendLine($"  {label}: (absent)");
                continue;
            }

            string kind;
            if (IsProgramDataEmuffbPath(entry.Path))
                kind = "ProgramData (OK)";
            else if (IsInstallFolderDll(entry.Path))
            {
                kind = "INSTALL FOLDER — Steam may lock Desktop\\G920Emulator until Steam exits";
                anyInstall = true;
            }
            else
                kind = "other path";

            sb.AppendLine($"  {label}: {entry.Path}");
            sb.AppendLine($"    → {kind}");
        }

        if (anyInstall)
        {
            sb.AppendLine();
            sb.AppendLine(
                "HINT: Close Steam once after updating so it drops any already-loaded install-folder emuffb.dll.");
        }

        return sb.ToString();
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
