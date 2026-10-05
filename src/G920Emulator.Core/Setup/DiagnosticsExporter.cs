using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using G920Emulator.Core.Ffb;
using G920Emulator.Core.Input;
using G920Emulator.Core.Profiles;
using Microsoft.Win32;

namespace G920Emulator.Core.Setup;

/// <summary>
/// Builds a zip of logs + environment + live FFB/device state for support.
/// </summary>
public static class DiagnosticsExporter
{
    public const string GitHubIssuesUrl = "https://github.com/camborambo/G920Emulator/issues";
    public const string GitHubRepoUrl = "https://github.com/camborambo/G920Emulator";

    private static readonly string[] TempLogNames =
    [
        "g920emulator-winuhid-install.log",
        "g920emulator-testsigning.log",
        "g920emulator-start.log",
        "g920ffb-effects.log",
        "g920ffb-effects.log.old",
        "g920-hidpp-ingress.log",
    ];

    /// <summary>
    /// Writes a diagnostics zip to <paramref name="destinationZipPath"/>.
    /// Returns the path written.
    /// </summary>
    public static string ExportToZip(
        string destinationZipPath,
        string appVersion,
        Func<(bool Installed, string Detail)>? probeWinUHid = null,
        DiagnosticsLiveSnapshot? live = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);

        var staging = Path.Combine(Path.GetTempPath(), "g920emulator-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            WriteSummary(staging, appVersion, probeWinUHid, live);
            WriteDevices(staging);
            WriteFfbSnapshot(staging, live);
            WriteHidHide(staging);
            WriteOemRegistry(staging);
            CopyTempLogs(staging);
            WriteGameFfbAnalysis(staging);
            CopyProfilesAndSettings(staging);
            File.WriteAllText(
                Path.Combine(staging, "HOW-TO-SEND.txt"),
                BuildHowToSend(),
                Encoding.UTF8);

            var dir = Path.GetDirectoryName(destinationZipPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            if (File.Exists(destinationZipPath))
                File.Delete(destinationZipPath);

            ZipFile.CreateFromDirectory(staging, destinationZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return destinationZipPath;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* ignore */ }
        }
    }

    private static string BuildHowToSend() =>
        "Attach this zip to a GitHub issue:\r\n" +
        "  " + GitHubIssuesUrl + "\r\n\r\n" +
        "How this zip was captured:\r\n" +
        "  1. Click Debug on the main window (starts OEM / HID++ file logging).\r\n" +
        "  2. Start bridge, launch the game, reproduce the issue.\r\n" +
        "  3. Stop debug, then Export log…\r\n\r\n" +
        "For Fanatec vs Simucube FFB comparison, do the same on both PCs with the same build.\r\n\r\n" +
        "Key files in this zip:\r\n" +
        "  summary.txt       — machine, deps, processes, g920ffb.dll stamp\r\n" +
        "  devices.txt       — every DirectInput game device (incl. virtual G920 + FFB flag)\r\n" +
        "  ffb-snapshot.txt  — live OEM mix / bridge attach / gains at export time\r\n" +
        "  hidhide.txt       — cloak, apps whitelist, hidden devices (via HidHideCLI)\r\n" +
        "  oem-registry.txt  — OEMForceFeedback CLSID / Effects / DLL path for VID_046D&PID_C262\r\n" +
        "  game-ffb-analysis.txt — OEM race signature (Triangle/CF vs spring-only / Vibration)\r\n" +
        "  logs\\g920ffb-effects.log — game OEM calls (SESSION / CALL / EFFECT / MIX)\r\n" +
        "Repository: " + GitHubRepoUrl + "\r\n";

    private static void WriteSummary(
        string staging,
        string appVersion,
        Func<(bool Installed, string Detail)>? probeWinUHid,
        DiagnosticsLiveSnapshot? live)
    {
        var sb = new StringBuilder();
        sb.AppendLine("G920 Emulator diagnostics");
        sb.AppendLine("Generated (local): " + DateTime.Now.ToString("o"));
        sb.AppendLine("Machine: " + Environment.MachineName);
        sb.AppendLine("User: " + Environment.UserName);
        sb.AppendLine("App version: " + appVersion);
        sb.AppendLine("OS: " + Environment.OSVersion);
        sb.AppendLine("64-bit OS: " + Environment.Is64BitOperatingSystem);
        sb.AppendLine("64-bit process: " + Environment.Is64BitProcess);
        sb.AppendLine("User interactive: " + Environment.UserInteractive);
        sb.AppendLine("App directory: " + AppContext.BaseDirectory);
        AppendDllStamp(sb, "g920ffb.dll");
        AppendDllStamp(sb, "WinUHid.dll");
        sb.AppendLine();

        var setup = new WinUHidSetupService();
        sb.AppendLine("WinUHid setup");
        sb.AppendLine("  Test signing: " + (setup.IsTestSigningEnabled() ? "ON" : "OFF"));
        sb.AppendLine("  WinUHid.dll: " + (File.Exists(setup.LocalDllPath) ? "present" : "missing"));
        sb.AppendLine("  Elevated (this process): " + (WinUHidSetupService.IsAdministrator() ? "yes" : "no"));
        sb.AppendLine();

        try
        {
            var probe = probeWinUHid ?? (() => (false, "probe not provided"));
            var report = DependencyChecker.CheckAll(probe);
            sb.AppendLine("Dependencies");
            foreach (var item in report.Items)
            {
                sb.AppendLine($"  {item.Name}: {item.StatusLabel}");
                sb.AppendLine($"    {item.Detail}");
            }
            sb.AppendLine("  Ready for games: " + report.ReadyForGames);
        }
        catch (Exception ex)
        {
            sb.AppendLine("Dependencies: failed — " + ex.Message);
        }

        sb.AppendLine();
        try
        {
            var profiles = new ProfileStore();
            sb.AppendLine("Profiles storage");
            sb.AppendLine("  Portable: " + profiles.UsesPortableStorage);
            sb.AppendLine("  Directory: " + profiles.ProfilesDirectory);
            sb.AppendLine("  FFB directory: " + profiles.FfbProfilesDirectory);
            sb.AppendLine("  Settings: " + profiles.SettingsPath);
            var names = profiles.ListProfiles();
            sb.AppendLine("  Input profiles (" + names.Count + "): " + (names.Count == 0 ? "(none)" : string.Join(", ", names)));
            var ffbNames = profiles.ListFfbProfiles();
            sb.AppendLine("  FFB profiles (" + ffbNames.Count + "): " + (ffbNames.Count == 0 ? "(none)" : string.Join(", ", ffbNames)));
            var settings = profiles.LoadSettings();
            sb.AppendLine("  Last input profile: " + (settings.LastProfileName ?? "(none)"));
            sb.AppendLine("  Last FFB profile: " + (settings.LastFfbProfileName ?? "(none)"));
            sb.AppendLine("  Hidden devices (UI list): " + settings.HiddenDeviceIds.Count);
        }
        catch (Exception ex)
        {
            sb.AppendLine("Profiles: failed — " + ex.Message);
        }

        sb.AppendLine();
        AppendLiveBridge(sb, live);
        sb.AppendLine();
        AppendEnvironment(sb);

        File.WriteAllText(Path.Combine(staging, "summary.txt"), sb.ToString(), Encoding.UTF8);
    }

    private static void AppendDllStamp(StringBuilder sb, string fileName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path))
            {
                sb.AppendLine(fileName + ": missing");
                return;
            }

            var info = new FileInfo(path);
            sb.AppendLine($"{fileName}: {info.Length} bytes, written {info.LastWriteTime:o}");
        }
        catch (Exception ex)
        {
            sb.AppendLine(fileName + ": " + ex.Message);
        }
    }

    private static void AppendLiveBridge(StringBuilder sb, DiagnosticsLiveSnapshot? live)
    {
        sb.AppendLine("Bridge / FFB (at export)");
        if (live is null)
        {
            sb.AppendLine("  (no live snapshot — export was not opened from the main window)");
            return;
        }

        sb.AppendLine("  Bridge running: " + live.BridgeRunning);
        sb.AppendLine("  Link status: " + (string.IsNullOrWhiteSpace(live.LinkStatus) ? "OK" : live.LinkStatus));
        sb.AppendLine("  Virtual preview: " + live.VirtualPreviewMode);
        sb.AppendLine("  Virtual Col01 present: " + live.VirtualCol01Present);
        sb.AppendLine("  Virtual error: " + (live.VirtualDeviceError ?? "(none)"));
        sb.AppendLine("  Host path hint: " + (string.IsNullOrWhiteSpace(live.HostPathHint) ? "(none)" : live.HostPathHint));
        sb.AppendLine("  Input profile: " + (live.ActiveInputProfile ?? "(none)"));
        sb.AppendLine("  FFB profile: " + (live.ActiveFfbProfile ?? "(none)"));
        sb.AppendLine("  FFB source id: " + (live.FfbSourceDeviceId ?? "(none)"));
        sb.AppendLine("  FFB source name: " + (live.FfbSourceDeviceName ?? "(none)"));
        sb.AppendLine($"  Master gain: {live.MasterGain:0.##}  Invert: {live.FfbInvert}");

        if (live.Ffb is { } d)
        {
            sb.AppendLine("  FFB attached: " + d.IsAttached);
            sb.AppendLine("  FFB vendor: " + d.VendorProfile);
            sb.AppendLine("  FFB device: " + (d.DeviceName ?? "(none)"));
            sb.AppendLine("  FFB coop: " + d.CooperativeLevel);
            sb.AppendLine("  FFB status: " + d.Status);
            sb.AppendLine($"  OEM playing: {d.OemFfbPlaying}  torque={d.OemFfbTorque:+0.00;-0.00;0.00}  dl={d.OemFfbDownloadCount}");
            sb.AppendLine("  OEM types seen: " + (string.IsNullOrEmpty(d.OemFfbTypesSeen) ? "(none)" : d.OemFfbTypesSeen));
            sb.AppendLine("  OEM types playing: " + (string.IsNullOrEmpty(d.OemFfbTypesPlaying) ? "(none)" : d.OemFfbTypesPlaying));
            sb.AppendLine("  OEM status: " + d.OemFfbStatus);
            sb.AppendLine($"  Rim (spring): {d.FfbRimSteer:+0.00;-0.00;0.00}");
            sb.AppendLine($"  Apply count: {d.ApplyCount}  last mag: {d.LastMagnitude}");
            sb.AppendLine($"  Host writes: {d.HostWriteCount}  last: {d.LastHostWriteHex}");
        }
    }

    // Context only — SimHub, FanatecService (shifter), and True Drive are normal on mixed
    // rigs (e.g. Simucube base + Fanatec shifter). Listed so support can see what's present;
    // they are not treated as FFB failures by themselves.
    private static readonly string[] InterestingProcessPrefixes =
    [
        "lghub", "LGHUB", "LCore", "LogiOptions", "logioptionsplus",
        "steam", "SimHub", "TrueDrive", "Simucube", "FanaLab", "Fanatec",
        "MOZA", "PitHouse", "SimPro", "DS4Windows", "x360ce", "vJoy", "HidHide",
        "NeedForSpeed", "NFS",
    ];

    private static void AppendEnvironment(StringBuilder sb)
    {
        sb.AppendLine("Environment");
        try
        {
            var running = System.Diagnostics.Process.GetProcesses()
                .Select(p => p.ProcessName)
                .Where(n => InterestingProcessPrefixes.Any(prefix =>
                    n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            sb.AppendLine("  Relevant processes running: " +
                          (running.Count == 0 ? "(none)" : string.Join(", ", running)));
        }
        catch (Exception ex)
        {
            sb.AppendLine("  Processes: failed — " + ex.Message);
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        AppendInstalled(sb, "Logitech G HUB", Path.Combine(pf, "LGHUB"));
        AppendInstalled(sb, "Logitech Gaming Software", Path.Combine(pf, "Logitech Gaming Software"));
        AppendInstalled(sb, "Simucube True Drive",
            Path.Combine(pf, "Simucube", "Simucube True Drive"), Path.Combine(pf86, "Simucube"));
        AppendInstalled(sb, "Fanatec driver", Path.Combine(pf, "Fanatec"), Path.Combine(pf86, "Fanatec"));
        AppendInstalled(sb, "SimHub",
            Path.Combine(pf, "SimHub"), Path.Combine(pf86, "SimHub"));
    }

    private static void AppendInstalled(StringBuilder sb, string name, params string[] paths)
    {
        var found = paths.FirstOrDefault(Directory.Exists);
        sb.AppendLine($"  {name}: " + (found is null ? "not found" : "installed (" + found + ")"));
    }

    private static void WriteDevices(string staging)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DirectInput game controllers (AttachedOnly) as seen by this process.");
        sb.AppendLine("Includes the virtual G920 when the bridge is running.");
        sb.AppendLine("If a physical FFB base still appears here while HidHide is on, games may see it too");
        sb.AppendLine("(this process is usually whitelisted — compare with hidhide.txt app-list).");
        sb.AppendLine();
        try
        {
            var rows = InputHub.EnumerateAllAttachedForDiagnostics();
            sb.AppendLine("Count: " + rows.Count);
            sb.AppendLine();
            var i = 0;
            foreach (var d in rows)
            {
                i++;
                sb.AppendLine($"[{i}] {d.Name}");
                sb.AppendLine($"    Product: {d.ProductName}");
                sb.AppendLine($"    InstanceId: {d.InstanceId}");
                sb.AppendLine($"    ProductId:  {d.ProductId}");
                sb.AppendLine($"    FFB: {d.SupportsForceFeedback}  axes={d.AxisCount} buttons={d.ButtonCount} hats={d.HatCount}");
                sb.AppendLine($"    VirtualG920: {d.IsVirtualG920}");
                if (!string.IsNullOrWhiteSpace(d.OpenError))
                    sb.AppendLine($"    Open error: {d.OpenError}");
                sb.AppendLine();
            }

            var ffb = rows.Where(r => r.SupportsForceFeedback).ToList();
            sb.AppendLine("FFB-capable devices: " + ffb.Count);
            foreach (var d in ffb)
                sb.AppendLine($"  - {(d.IsVirtualG920 ? "[virtual G920] " : "")}{d.Name}");

            sb.AppendLine();
            sb.AppendLine("=== Competing FFB vs HidHide ===");
            sb.AppendLine("This process is HidHide-whitelisted, so it STILL lists hidden devices (including vJoy).");
            sb.AppendLine("That does not mean games see them. Authoritative check: joy.cpl (not whitelisted).");
            sb.AppendLine("If vJoy is absent from joy.cpl, Unbound cannot use it for FFB.");
            sb.AppendLine("Steam may stay running; OEM torque is accepted only from the game process.");
            try
            {
                var hid = DependencyChecker.CaptureHidHideDiagnostics();
                var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in Regex.Matches(hid, "--dev-hide\\s+\"([^\"]+)\""))
                    hidden.Add(m.Groups[1].Value);

                var competitors = ffb.Where(d => !d.IsVirtualG920).ToList();
                if (competitors.Count == 0)
                {
                    sb.AppendLine("No non-virtual FFB devices visible to this process.");
                }
                else
                {
                    // vJoy may be hidden as ROOT\VID_1234&PID_BEAD or as a generic HID\HIDCLASS\… node.
                    var vjoyVidHidden = hidden.Any(h =>
                        h.Contains("VID_1234&PID_BEAD", StringComparison.OrdinalIgnoreCase));
                    var hidClassHidden = hidden.Any(h =>
                        h.Contains(@"HID\HIDCLASS\", StringComparison.OrdinalIgnoreCase));
                    foreach (var d in competitors)
                    {
                        var isVjoy =
                            d.Name.Contains("vJoy", StringComparison.OrdinalIgnoreCase) ||
                            d.ProductName.Contains("vJoy", StringComparison.OrdinalIgnoreCase) ||
                            d.ProductId.Contains("bead1234", StringComparison.OrdinalIgnoreCase);
                        if (isVjoy)
                        {
                            if (vjoyVidHidden)
                            {
                                sb.AppendLine(
                                    "  OK  vJoy path VID_1234&PID_BEAD is on HidHide hide list (emulator still sees it via whitelist).");
                            }
                            else if (hidClassHidden)
                            {
                                sb.AppendLine(
                                    "  OK? vJoy still listed here (whitelist), but a HID\\HIDCLASS hide entry is present — often that is vJoy.");
                                sb.AppendLine(
                                    "      Confirm with joy.cpl: if vJoy is absent there, games cannot see it.");
                            }
                            else
                            {
                                sb.AppendLine(
                                    "  WARN no VID_1234&PID_BEAD / HIDCLASS hide entry found — verify in joy.cpl that vJoy is absent.");
                            }
                        }
                        else
                        {
                            sb.AppendLine(
                                $"  FFB device visible here: {d.Name} (expected if hidden + emulator whitelisted; confirm absent from joy.cpl).");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("HidHide cross-check failed: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("Enumerate failed: " + ex.Message);
        }

        File.WriteAllText(Path.Combine(staging, "devices.txt"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteFfbSnapshot(string staging, DiagnosticsLiveSnapshot? live)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FFB snapshot at export time");
        sb.AppendLine("Generated: " + DateTime.Now.ToString("o"));
        sb.AppendLine();

        sb.AppendLine("=== OEM shared memory (g920ffb.dll → bridge) ===");
        if (OemFfbSharedMemory.TryRead(out var oem, out var oemErr))
        {
            sb.AppendLine($"Torque (game): {oem.Torque:+0.000;-0.000;0.000}  Aux: {oem.AuxTorque:+0.000;-0.000;0.000}  Combined: {oem.CombinedTorque:+0.000;-0.000;0.000}");
            sb.AppendLine($"Playing: {oem.Playing}  AuxPlaying: {oem.AuxPlaying}");
            sb.AppendLine($"Sequence: {oem.Sequence}");
            sb.AppendLine($"DownloadCount: {oem.DownloadCount}");
            sb.AppendLine($"TypesSeen: {OemFfbSharedMemory.FormatTypeMask(oem.TypesSeen)} (0x{oem.TypesSeen:X4})");
            sb.AppendLine($"TypesPlaying (game): {OemFfbSharedMemory.FormatTypeMask(oem.TypesPlaying)} (0x{oem.TypesPlaying:X4})");
            sb.AppendLine($"TypesPlaying (aux):  {OemFfbSharedMemory.FormatTypeMask(oem.AuxTypesPlaying)} (0x{oem.AuxTypesPlaying:X4})");
            sb.AppendLine($"TypesPlaying (combined): {OemFfbSharedMemory.FormatTypeMask(oem.CombinedTypesPlaying)} (0x{oem.CombinedTypesPlaying:X4})");
            sb.AppendLine($"LastEffectType: {oem.LastEffectType}  LastFlags: 0x{oem.LastFlags:X8}");
            sb.AppendLine($"TickMs: {oem.TickMs}  AuxTickMs: {oem.AuxTickMs}  Stale: {oem.IsStale()}");
            sb.AppendLine();
            sb.AppendLine("Read guide:");
            sb.AppendLine("  Combined TypesPlaying = Spring only → virtual G920 is not getting CF/rumble from any host.");
            sb.AppendLine("  Combined includes Constant/Sine/Triangle → effects are on the emulator path; check physical apply.");
            sb.AppendLine("  Aux non-zero while game is Spring-only → Steam/overlay layered rumble under the game channel.");
        }
        else
        {
            sb.AppendLine("Unavailable: " + (oemErr ?? "unknown"));
        }

        sb.AppendLine();
        sb.AppendLine("=== Active FFB profile / gains ===");
        if (live is null)
        {
            sb.AppendLine("(no live snapshot)");
        }
        else
        {
            sb.AppendLine("Input profile: " + (live.ActiveInputProfile ?? "(none)"));
            sb.AppendLine("FFB profile: " + (live.ActiveFfbProfile ?? "(none)"));
            sb.AppendLine($"Master: {live.MasterGain:0.##}  Invert: {live.FfbInvert}");
            sb.AppendLine("FFB source: " + (live.FfbSourceDeviceName ?? live.FfbSourceDeviceId ?? "(none)"));
            var g = live.EffectGains ?? FfbEffectGains.CreateDefault();
            sb.AppendLine(
                $"Effect gains: CF={g.ConstantForce:0.##} Spring={g.SpringForce:0.##} Damper={g.DamperForce:0.##} " +
                $"Friction={g.FrictionForce:0.##} Inertia={g.InertiaForce:0.##} Periodic={g.Periodic:0.##} Ramp={g.RampForce:0.##}");
            var f = live.OutputFeel ?? FfbOutputFeel.CreateDefault();
            sb.AppendLine(
                $"Feel: smooth={f.SmoothingMs:0}ms peak={f.PeakSoftStart:0.##} softStart={f.SoftStartMs:0} " +
                $"dead={f.Deadband:0.###} slew={f.MaxSlewPerSecond:0} spike={f.MaxSpikeStep:0.##} eps={f.MagnitudeEpsilon:0}");
            sb.AppendLine(
                $"OEM mix: invertCF={f.InvertConstantForce} dampVel={f.DamperVelocityScale:0.##} " +
                $"dampDead={f.DamperDeadbandScale:0.##}");
            sb.AppendLine(
                $"Force center spring: {f.ForceCenterSpring} strength={f.CenterSpringStrength:0.##} " +
                $"range={f.CenterSpringRange:0.##} deadzone={f.CenterSpringDeadzone:0.###}");
        }

        sb.AppendLine();
        sb.AppendLine("=== Physical FFB attach (bridge) ===");
        if (live?.Ffb is { } d)
        {
            sb.AppendLine($"Attached: {d.IsAttached}  Vendor: {d.VendorProfile}");
            sb.AppendLine($"Device: {d.DeviceName ?? "(none)"} ({d.DeviceId ?? ""})");
            sb.AppendLine($"Coop: {d.CooperativeLevel}");
            sb.AppendLine($"Axis: {d.AxisInfo}");
            sb.AppendLine($"Status: {d.Status}");
            sb.AppendLine($"Last error: {d.LastError ?? "(none)"}");
            sb.AppendLine($"Incoming: {d.LastIncomingTorque:+0.00;-0.00;0.00} ({d.IncomingUpdateCount} updates)");
            sb.AppendLine($"Applied mag: {d.LastMagnitude}  applyCount={d.ApplyCount}");
            sb.AppendLine($"Rim: {d.FfbRimSteer:+0.00;-0.00;0.00}");
            sb.AppendLine($"TestOverride: {d.TestOverrideActive}  AutoCenterTest: {d.TestAutoCenterActive}");
        }
        else
        {
            sb.AppendLine("(no live FFB diagnostics)");
        }

        File.WriteAllText(Path.Combine(staging, "ffb-snapshot.txt"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteHidHide(string staging)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(staging, "hidhide.txt"),
                DependencyChecker.CaptureHidHideDiagnostics(),
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(staging, "hidhide.txt"),
                "HidHide capture failed: " + ex.Message,
                Encoding.UTF8);
        }
    }

    private static void WriteOemRegistry(string staging)
    {
        var sb = new StringBuilder();
        sb.AppendLine("OEMForceFeedback registration for Logitech G920 VID_046D&PID_C262");
        sb.AppendLine("Expected CLSID: {A920FFB0-E7DB-4329-8C13-A966D84A289F} → g920ffb.dll");
        sb.AppendLine();

        var relative = @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C262";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(relative);
            if (key is null)
            {
                sb.AppendLine("Key not found: HKLM\\" + relative);
            }
            else
            {
                sb.AppendLine("HKLM\\" + relative);
                foreach (var name in key.GetValueNames())
                    sb.AppendLine($"  {name} = {FormatRegValue(key.GetValue(name))}");

                using var ff = key.OpenSubKey("OEMForceFeedback");
                if (ff is null)
                {
                    sb.AppendLine("  OEMForceFeedback: (missing)");
                }
                else
                {
                    sb.AppendLine("  OEMForceFeedback:");
                    foreach (var name in ff.GetValueNames())
                        sb.AppendLine($"    {name} = {FormatRegValue(ff.GetValue(name))}");

                    using var effects = ff.OpenSubKey("Effects");
                    if (effects is null)
                    {
                        sb.AppendLine("    Effects: (missing)");
                    }
                    else
                    {
                        var sub = effects.GetSubKeyNames();
                        sb.AppendLine($"    Effects: {sub.Length} entries");
                        foreach (var effectId in sub)
                        {
                            using var ek = effects.OpenSubKey(effectId);
                            var effectName = ek?.GetValue("")?.ToString() ?? "(unnamed)";
                            sb.AppendLine($"      {effectId} = {effectName}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("Read failed: " + ex.Message);
        }

        File.WriteAllText(Path.Combine(staging, "oem-registry.txt"), sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Summarize Unbound (and similar) OEM race signature vs spring-only / zero-mag rumble.
    /// </summary>
    private static void WriteGameFfbAnalysis(string staging)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Game FFB analysis (NFS Unbound race signature)");
        sb.AppendLine("Generated: " + DateTime.Now.ToString("o"));
        sb.AppendLine();
        sb.AppendLine("Expected in-race OEM sequence (working):");
        sb.AppendLine("  GetEffectStatus → DestroyEffect (boot Sine) → Triangle create/start → CF stream");
        sb.AppendLine("  MIX shows non-zero cf / periodic / damper (not spring alone with zero rumble).");
        sb.AppendLine("Menus often show Spring-only; that is normal.");
        sb.AppendLine("Unbound: Accessibility → Controls → Controller Vibration must be On.");
        sb.AppendLine();

        var logPath = Path.Combine(staging, "logs", "g920ffb-effects.log");
        if (!File.Exists(logPath))
        {
            sb.AppendLine("No logs\\g920ffb-effects.log in this export.");
            File.WriteAllText(Path.Combine(staging, "game-ffb-analysis.txt"), sb.ToString(), Encoding.UTF8);
            return;
        }

        var triangleCreates = 0;
        var getStatus = 0;
        var unboundSessions = 0;
        var steamHostSessions = 0;
        string? lastUnbound = null;
        string? lastTriangle = null;
        var springOnlyMixAfterUnbound = 0;
        var richMaskMixAfterUnbound = 0;
        var nonzeroRumbleMixAfterUnbound = 0;
        var sawUnbound = false;
        var mixValue = new Regex(
            @"cf=(?<cf>-?\d+)\s+periodic=(?<per>-?\d+)\s+spring=(?<spr>-?\d+)\s+damper=(?<dmp>-?\d+)",
            RegexOptions.CultureInvariant);

        foreach (var line in File.ReadLines(logPath))
        {
            if (line.Contains("NeedForSpeedUnbound.exe", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("SESSION", StringComparison.Ordinal))
            {
                unboundSessions++;
                lastUnbound = line;
                sawUnbound = true;
            }
            if (line.Contains("SESSION HOST", StringComparison.Ordinal) &&
                line.Contains("steam.exe", StringComparison.OrdinalIgnoreCase))
                steamHostSessions++;
            if (line.Contains("type=4(Triangle)", StringComparison.Ordinal) &&
                line.Contains("flags=0x000003FF", StringComparison.Ordinal))
            {
                triangleCreates++;
                lastTriangle = line;
            }
            if (line.Contains("CALL GetEffectStatus", StringComparison.Ordinal))
                getStatus++;
            if (sawUnbound && line.Contains(" MIX ", StringComparison.Ordinal))
            {
                // playing=0x0080 is Spring only; richer masks include CF/periodic bits.
                if (line.Contains("playing=0x0080", StringComparison.Ordinal))
                    springOnlyMixAfterUnbound++;
                else if (Regex.IsMatch(line, @"playing=0x0*[1-9a-fA-F][0-9a-fA-F]*"))
                    richMaskMixAfterUnbound++;

                var m = mixValue.Match(line);
                if (m.Success)
                {
                    var cf = int.Parse(m.Groups["cf"].Value);
                    var per = int.Parse(m.Groups["per"].Value);
                    var dmp = int.Parse(m.Groups["dmp"].Value);
                    if (cf != 0 || per != 0 || dmp != 0)
                        nonzeroRumbleMixAfterUnbound++;
                }
            }
        }

        sb.AppendLine($"Unbound SESSION lines: {unboundSessions}");
        sb.AppendLine($"Steam SESSION HOST (DI ok; AuxTorque only): {steamHostSessions}");
        sb.AppendLine($"Triangle full-creates (0x3FF): {triangleCreates}");
        sb.AppendLine($"GetEffectStatus calls: {getStatus}");
        sb.AppendLine($"MIX spring-only mask (0x0080) after Unbound: {springOnlyMixAfterUnbound}");
        sb.AppendLine($"MIX richer playing mask after Unbound: {richMaskMixAfterUnbound}");
        sb.AppendLine($"MIX with non-zero cf/periodic/damper after Unbound: {nonzeroRumbleMixAfterUnbound}");
        sb.AppendLine();
        if (lastUnbound is not null)
            sb.AppendLine("Last Unbound session: " + lastUnbound);
        if (lastTriangle is not null)
            sb.AppendLine("Last Triangle create: " + lastTriangle);
        else
            sb.AppendLine("Last Triangle create: (none in this log)");
        sb.AppendLine();

        if (triangleCreates == 0 && unboundSessions > 0)
        {
            sb.AppendLine("VERDICT: Unbound attached but never created Triangle — game did not enter race rumble path.");
            sb.AppendLine("Confirm Controller Vibration ON, drive in an actual race, hit a wall, export again.");
        }
        else if (triangleCreates > 0 && nonzeroRumbleMixAfterUnbound == 0)
        {
            sb.AppendLine("VERDICT: Race effects were created but cf/periodic/damper stayed 0 in MIX.");
            sb.AppendLine("Almost always Unbound Accessibility → Controls → Controller Vibration = Off.");
            sb.AppendLine("Turn Vibration On and retest in-race (walls/curbs); the emulator cannot invent rumble the game never streams.");
        }
        else if (triangleCreates > 0 && nonzeroRumbleMixAfterUnbound > 0)
        {
            sb.AppendLine("VERDICT: Race rumble present in OEM MIX (non-zero CF/periodic/damper).");
            sb.AppendLine("If the rim still feels dead, check bridge attach, gains, and HidHide (game must see virtual G920 only).");
        }
        else if (triangleCreates > 0)
        {
            sb.AppendLine("VERDICT: Triangle was created at least once; check whether that session matches the reported play time.");
        }
        else
        {
            sb.AppendLine("VERDICT: No Unbound activity in this log yet.");
        }

        File.WriteAllText(Path.Combine(staging, "game-ffb-analysis.txt"), sb.ToString(), Encoding.UTF8);
    }

    private static string FormatRegValue(object? value) => value switch
    {
        null => "(null)",
        string[] multi => string.Join(" | ", multi),
        byte[] bytes => Convert.ToHexString(bytes),
        _ => value.ToString() ?? "",
    };

    private static void CopyTempLogs(string staging)
    {
        var logsDir = Path.Combine(staging, "logs");
        Directory.CreateDirectory(logsDir);
        var temp = Path.GetTempPath();
        var any = false;
        foreach (var name in TempLogNames)
        {
            var src = Path.Combine(temp, name);
            if (!File.Exists(src)) continue;
            try
            {
                File.Copy(src, Path.Combine(logsDir, name), overwrite: true);
                any = true;
            }
            catch
            {
                // ignore locked files
            }
        }

        if (!any)
        {
            File.WriteAllText(
                Path.Combine(logsDir, "README.txt"),
                "No G920 Emulator log files were found in %TEMP% yet.\r\n" +
                "Reproduce the issue (Start bridge, run the game, hit a wall), then export again.\r\n",
                Encoding.UTF8);
        }
    }

    private static void CopyProfilesAndSettings(string staging)
    {
        try
        {
            var profiles = new ProfileStore();
            var dest = Path.Combine(staging, "profiles");
            Directory.CreateDirectory(dest);
            if (Directory.Exists(profiles.ProfilesDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(profiles.ProfilesDirectory, "*.json"))
                {
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
                }
            }

            var ffbDest = Path.Combine(staging, "ffb-profiles");
            Directory.CreateDirectory(ffbDest);
            if (Directory.Exists(profiles.FfbProfilesDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(profiles.FfbProfilesDirectory, "*.json"))
                {
                    File.Copy(file, Path.Combine(ffbDest, Path.GetFileName(file)), overwrite: true);
                }
            }

            if (File.Exists(profiles.SettingsPath))
            {
                File.Copy(profiles.SettingsPath, Path.Combine(staging, "settings.json"), overwrite: true);
            }
        }
        catch
        {
            // best-effort
        }
    }
}
