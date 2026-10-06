using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace G920Emulator.Core.Setup;

public enum DependencyRequirement
{
    Required,
    Recommended,
}

public sealed class DependencyInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DependencyRequirement Requirement { get; init; }
    public required bool IsInstalled { get; init; }
    public required string StatusLabel { get; init; }
    public required string Detail { get; init; }
    public string? Version { get; init; }
    public string? InstallOrSetupHint { get; init; }
}

public sealed class DependencyReport
{
    public required IReadOnlyList<DependencyInfo> Items { get; init; }

    public bool AllRequiredInstalled => Items
        .Where(i => i.Requirement == DependencyRequirement.Required)
        .All(i => i.IsInstalled);

    /// <summary>True when all Required dependencies are installed (WinUHid, HidHide). SDK pins are session-scoped on Start bridge.</summary>
    public bool ReadyForGames => AllRequiredInstalled;

    public IReadOnlyList<string> MissingRequiredNames => Items
        .Where(i => i.Requirement == DependencyRequirement.Required && !i.IsInstalled)
        .Select(i => i.Name)
        .ToList();

    public DependencyInfo? TestSigning => Items.FirstOrDefault(i => i.Id == "testsigning");
    public DependencyInfo? WinUHid => Items.FirstOrDefault(i => i.Id == "winuhid");
    public DependencyInfo? HidHide => Items.FirstOrDefault(i => i.Id == "hidhide");
    public DependencyInfo? LogitechSdk => Items.FirstOrDefault(i => i.Id == "logisdk");
}

public static class DependencyChecker
{
    public const string HidHideReleasesUrl = "https://github.com/nefarius/HidHide/releases/latest";
    public const string HidHideRepoUrl = "https://github.com/nefarius/HidHide";

    /// <param name="probeWinUHid">Returns (installed, detailMessage).</param>
    public static DependencyReport CheckAll(Func<(bool Installed, string Detail)> probeWinUHid)
    {
        var winUHid = CheckWinUHid(probeWinUHid);
        return new DependencyReport
        {
            Items =
            [
                CheckTestSigning(winUHidReady: winUHid.IsInstalled),
                winUHid,
                CheckHidHide(),
            ],
        };
    }

    /// <summary>
    /// Test signing is only required to *install* the test-signed WinUHid package.
    /// After install it can stay off — WinUHid (UMDF) usually keeps working and Forza can launch.
    /// </summary>
    public static DependencyInfo CheckTestSigning(bool winUHidReady = false)
    {
        var setup = new WinUHidSetupService();
        var (live, bcd) = setup.QueryTestSigningDetail();
        var pendingInstall = BundledWinUHidInstaller.HasPendingInstall;

        string statusLabel;
        string hint;
        string detail;
        bool ready;
        var requirement = winUHidReady ? DependencyRequirement.Recommended : DependencyRequirement.Required;

        if (winUHidReady && !live)
        {
            // Ideal Forza-friendly state after Install finishes and disables test signing.
            ready = true;
            statusLabel = "Off (OK)";
            hint = "Best state: WinUHid installed, test signing off — Forza and the emulator can both work. You can re-enable Secure Boot in UEFI/BIOS.";
            detail = bcd == true
                ? "Current boot OFF · BCD still Yes (reboot once to finish leaving test mode)."
                : "Test signing OFF. Secure Boot may be re-enabled if you disabled it for install.";
        }
        else if (winUHidReady && live)
        {
            ready = true; // emulator works; warn that Forza will not
            statusLabel = "Enabled";
            hint = "WinUHid is already installed — Disable test signing and reboot so Forza can launch. The emulator usually keeps working with test signing off.";
            detail = "Windows is in test mode. Forza Horizon 6 will not launch until you disable and reboot.";
        }
        else if (live)
        {
            ready = true;
            statusLabel = "Enabled";
            hint = pendingInstall
                ? "Test mode is on. Click Install WinUHid once — it will install the driver and turn test signing back off."
                : "Ready for Install WinUHid. Install turns test signing back off afterwards (Forza-safe).";
            detail = "Windows is running in test mode (testsigning ON).";
        }
        else if (bcd == true || pendingInstall)
        {
            ready = false;
            statusLabel = "Reboot required";
            hint = pendingInstall
                ? "Test signing was staged for WinUHid install. Reboot, then click Install WinUHid once more (installs driver + turns test signing off)."
                : "Test signing is staged in BCD but not active yet. Reboot once, then Install WinUHid.";
            detail = pendingInstall
                ? "Pending WinUHid install after reboot."
                : "bcdedit testsigning=Yes · current boot still OFF.";
        }
        else
        {
            ready = false;
            statusLabel = "Disabled";
            hint = "Needed only to install WinUHid. Secure Boot must be off in UEFI/BIOS for that step (you can turn Secure Boot back on afterward). Install WinUHid enables test signing temporarily, then turns it off again.";
            detail = bcd == false
                ? "Test signing OFF (current boot and BCD)."
                : "Test signing OFF (current boot).";
        }

        return new DependencyInfo
        {
            Id = "testsigning",
            Name = "Windows test signing",
            Requirement = requirement,
            IsInstalled = ready,
            StatusLabel = statusLabel,
            Detail = detail,
            InstallOrSetupHint = hint,
        };
    }

    public const string LogitechSteeringSdkClsid = "{63BD165D-1584-4E75-AB56-08330350545F}";

    /// <summary>Where the app caches its private SDK copy (pinned only while the bridge session is active).</summary>
    public static string LogitechSteeringSdkDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "G920Emulator", "LogitechSDK");

    public static DependencyInfo CheckWinUHid(Func<(bool Installed, string Detail)> probeWinUHid)
    {
        var setup = new WinUHidSetupService();
        var dllPresent = File.Exists(setup.LocalDllPath);
        var testSigning = setup.IsTestSigningEnabled();
        var nodePresent = setup.IsDeviceNodePresent();
        var (driverOk, probeMsg) = probeWinUHid();

        var detailParts = new List<string>
        {
            dllPresent ? "WinUHid.dll present" : "WinUHid.dll missing next to app",
            nodePresent ? "device node present" : "device node missing",
            probeMsg,
        };

        string statusLabel;
        string hint;
        if (driverOk)
        {
            statusLabel = "Installed";
            hint = "Required for exposing the virtual G920 to games. Test signing can stay off after install (Forza-friendly).";
        }
        else if (nodePresent && !testSigning && BundledWinUHidInstaller.HasPendingInstall)
        {
            statusLabel = "Reboot required";
            hint = "Finish setup: reboot into test mode if needed, then click Install WinUHid once more.";
        }
        else if (nodePresent)
        {
            statusLabel = "Not responding";
            hint = "A WinUHid device is present but the app cannot open it (often a duplicate enumerator). Prefer Uninstall WinUHid, then Install once — or Recheck after a reboot.";
        }
        else
        {
            statusLabel = "Not installed";
            hint = "Required. Install WinUHid (enables test signing only for the install, then turns it off). Secure Boot must be off during install; you can re-enable it afterward.";
        }

        return new DependencyInfo
        {
            Id = "winuhid",
            Name = "WinUHid",
            Requirement = DependencyRequirement.Required,
            // Only treat as ready when the user-mode probe works (needed to create the virtual G920).
            IsInstalled = driverOk,
            StatusLabel = statusLabel,
            Detail = string.Join(" · ", detailParts),
            InstallOrSetupHint = hint,
        };
    }

    public static DependencyInfo CheckHidHide()
    {
        var version = ReadHidHideVersion();
        var path = ReadHidHidePath();
        var serviceExists = RegistryServiceExists("HidHide");
        var installed = !string.IsNullOrWhiteSpace(version) || serviceExists || !string.IsNullOrWhiteSpace(path);

        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(version))
            detailParts.Add($"version {version}");
        if (serviceExists)
            detailParts.Add("HidHide service registered");
        if (!string.IsNullOrWhiteSpace(path))
            detailParts.Add($"install path found");
        if (detailParts.Count == 0)
            detailParts.Add("Not detected via registry or service keys");

        return new DependencyInfo
        {
            Id = "hidhide",
            Name = "HidHide",
            Requirement = DependencyRequirement.Required,
            IsInstalled = installed,
            StatusLabel = installed ? "Installed" : "Not installed",
            Version = version,
            Detail = string.Join(" · ", detailParts),
            InstallOrSetupHint = installed
                ? "Required: hide DualSense / physical pads from games so only the virtual G920 is seen. Configure in HidHide Client (or use Configure HidHide). The app does not change HidHide on Start."
                : "Required. Install HidHide, then configure it yourself (whitelist G920Emulator.exe, hide your pad, cloak on). The app does not change HidHide on Start.",
        };
    }

    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    public static bool TryOpenHidHideClient(out string message)
    {
        var path = ReadHidHidePath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            var client = Path.Combine(path, "HidHideClient.exe");
            if (File.Exists(client))
            {
                Process.Start(new ProcessStartInfo { FileName = client, UseShellExecute = true });
                message = $"Opened {client}";
                return true;
            }
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions", "HidHide", "HidHideClient.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Nefarius Software Solutions", "HidHide", "HidHideClient.exe"),
        };
        foreach (var c in candidates)
        {
            if (!File.Exists(c)) continue;
            Process.Start(new ProcessStartInfo { FileName = c, UseShellExecute = true });
            message = $"Opened {c}";
            return true;
        }

        message = "HidHide Client not found. Opening download page instead.";
        OpenUrl(HidHideReleasesUrl);
        return false;
    }

    public sealed class HidHideEnsureResult
    {
        public required string Message { get; init; }
        /// <summary>True when this process was newly whitelisted and must relaunch for HidHide to allow device access.</summary>
        public bool NeedsRelaunch { get; init; }
        public bool CliAvailable { get; init; }
        public int DevicesHidden { get; init; }
    }

    /// <summary>
    /// Configures HidHide in normal (inverse off) mode: hidden devices are visible only to
    /// whitelisted apps. Whitelists G920Emulator.exe and removes any prior game deny entries.
    /// </summary>
    public static string EnsureHidHideAppWhitelist(params string[] extraAppPaths) =>
        EnsureHidHideForEmulator(extraAppPaths).Message;

    /// <summary>
    /// <summary>
    /// Additive one-click setup: ensure inverse off + cloak on, whitelist this emulator, and
    /// hide newly detected pads/wheels (so games only see the virtual G920). Never removes
    /// apps or devices already configured in HidHide.
    /// </summary>
    public static HidHideEnsureResult ConfigureHidHideFully(params string[] extraAppPaths) =>
        EnsureHidHideForEmulator(extraAppPaths, hidePhysicalControllers: true);

    /// <summary>
    /// Same as <see cref="EnsureHidHideAppWhitelist"/>, but reports whether the app must relaunch.
    /// HidHide only applies the whitelist to processes that start after they are registered.
    /// </summary>
    public static HidHideEnsureResult EnsureHidHideForEmulator(params string[] extraAppPaths) =>
        EnsureHidHideForEmulator(extraAppPaths, hidePhysicalControllers: false);

    private static HidHideEnsureResult EnsureHidHideForEmulator(IEnumerable<string> extraAppPaths, bool hidePhysicalControllers)
    {
        var cli = FindHidHideCli();
        if (cli is null)
        {
            return new HidHideEnsureResult
            {
                Message = "HidHide CLI not found — install HidHide first.",
                CliAvailable = false,
            };
        }

        var emulatorPaths = EnumerateEmulatorExePaths(extraAppPaths).ToList();
        var alreadyWhitelisted = IsCurrentProcessWhitelisted(cli, emulatorPaths);

        // Additive only: never --app-unreg / --dev-unhide. Keep the user's existing lists intact.
        var commands = new List<string>
        {
            // Required mode for this app (does not clear whitelist/blacklist entries).
            "--inv-off",
            "--cloak-on",
        };

        foreach (var p in emulatorPaths)
            commands.Add($"--app-reg \"{p}\"");

        // If G HUB / manual config hid the virtual G920, games won't see it — unhide those paths.
        foreach (var path in DiscoverVirtualG920PathsToUnhide(cli))
            commands.Add($"--dev-unhide \"{path}\"");

        var hidePaths = hidePhysicalControllers ? DiscoverPhysicalGamingDevicesToHide(cli) : [];
        foreach (var path in hidePaths)
            commands.Add($"--dev-hide \"{path}\"");

        var (ok, detail) = RunHidHideCommands(cli, commands);
        if (!ok)
        {
            return new HidHideEnsureResult
            {
                Message = $"HidHide configure failed — {detail}",
                CliAvailable = true,
                NeedsRelaunch = false,
                DevicesHidden = 0,
            };
        }

        var confirmed = IsCurrentProcessWhitelisted(cli, emulatorPaths);
        var needsRelaunch = !alreadyWhitelisted && confirmed && emulatorPaths.Count > 0;

        var hideNote = hidePaths.Count == 0
            ? (hidePhysicalControllers
                ? " No new pads/wheels needed hiding (none detected, or already hidden)."
                : "")
            : $" Added {hidePaths.Count} physical controller/wheel HID path(s) to the hide list.";

        return new HidHideEnsureResult
        {
            CliAvailable = true,
            NeedsRelaunch = needsRelaunch,
            DevicesHidden = hidePaths.Count,
            Message = emulatorPaths.Count == 0
                ? "HidHide inverse off (could not find G920Emulator.exe to whitelist)." + hideNote
                : needsRelaunch
                    ? $"HidHide updated (existing entries kept) — emulator whitelisted; relaunch so devices stay visible.{hideNote}"
                    : confirmed
                        ? $"HidHide updated (existing entries kept) — inverse off, cloak on, emulator whitelisted.{hideNote}"
                        : "Could not confirm whitelist. Click Configure again and accept UAC, or add G920Emulator.exe in HidHide Client." + hideNote,
        };
    }

    /// <summary>
    /// Hides every gaming HID from games except the virtual G920 (which games must see).
    /// Pads and wheel bases are both hidden so the game does not get double input; the
    /// whitelisted emulator can still read them for binding and FFB.
    /// Also pulls vJoy (<c>VID_1234&amp;PID_BEAD</c>) from <c>--dev-all</c> — it is often
    /// missing from <c>--dev-gaming</c> but still appears to games as an FFB joystick.
    /// </summary>
    private static List<string> DiscoverPhysicalGamingDevicesToHide(string cli)
    {
        var alreadyHidden = ReadAlreadyHiddenDevicePaths(cli);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectHideCandidatesFromHidHideJson(cli, "--dev-gaming", paths);
        // vJoy / other root virtual joysticks may not be classified as "gaming" by HidHide.
        CollectHideCandidatesFromHidHideJson(cli, "--dev-all", paths, vJoyOnly: true);

        paths.RemoveWhere(alreadyHidden.Contains);
        return paths.ToList();
    }

    private static void CollectHideCandidatesFromHidHideJson(
        string cli,
        string args,
        ISet<string> paths,
        bool vJoyOnly = false)
    {
        var (ok, output) = RunHidHideCapture(cli, args);
        if (!ok || string.IsNullOrWhiteSpace(output))
            return;

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var container in doc.RootElement.EnumerateArray())
                {
                    var friendly = container.TryGetProperty("friendlyName", out var fn) ? fn.GetString() ?? "" : "";
                    if (!container.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var device in devices.EnumerateArray())
                    {
                        if (vJoyOnly && !LooksLikeVjoy(device, friendly))
                            continue;
                        CollectHidePaths(device, friendly, paths);
                    }
                }
                return;
            }
        }
        catch (JsonException)
        {
            // Fall through to regex extraction below.
        }

        foreach (Match m in Regex.Matches(output, "\"deviceInstancePath\"\\s*:\\s*\"([^\"]+)\""))
        {
            var path = UnescapeJson(m.Groups[1].Value);
            if (vJoyOnly && !IsVjoyInstancePath(path))
                continue;
            if (ShouldAutoHideDevice(path, output))
                paths.Add(path);
        }

        foreach (Match m in Regex.Matches(output, "\"xusbDeviceInstancePath\"\\s*:\\s*\"([^\"]+)\""))
        {
            var path = UnescapeJson(m.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (vJoyOnly && !IsVjoyInstancePath(path))
                continue;
            if (ShouldAutoHideDevice(path, output))
                paths.Add(path);
        }
    }

    private static bool LooksLikeVjoy(JsonElement device, string friendlyName)
    {
        var vendor = device.TryGetProperty("vendor", out var v) ? v.GetString() ?? "" : "";
        var product = device.TryGetProperty("product", out var p) ? p.GetString() ?? "" : "";
        var description = device.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var path = device.TryGetProperty("deviceInstancePath", out var dip) ? dip.GetString() ?? "" : "";
        var blob = $"{friendlyName} {vendor} {product} {description} {path}";
        return blob.Contains("vJoy", StringComparison.OrdinalIgnoreCase) ||
               IsVjoyInstancePath(path) ||
               (blob.Contains("VID_1234", StringComparison.OrdinalIgnoreCase) &&
                blob.Contains("PID_BEAD", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVjoyInstancePath(string? instancePath) =>
        !string.IsNullOrWhiteSpace(instancePath) &&
        instancePath.Contains("VID_1234&PID_BEAD", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> ReadAlreadyHiddenDevicePaths(string cli)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (ok, output) = RunHidHideCapture(cli, "--dev-list");
        if (!ok || string.IsNullOrWhiteSpace(output))
            return set;

        // Lines look like: --dev-hide "HID\VID_...."
        foreach (Match m in Regex.Matches(output, "--dev-hide\\s+\"([^\"]+)\""))
            set.Add(m.Groups[1].Value);

        return set;
    }

    /// <summary>
    /// Virtual G920 paths currently on the HidHide hide list (must be visible to games).
    /// </summary>
    private static List<string> DiscoverVirtualG920PathsToUnhide(string cli)
    {
        var hidden = ReadAlreadyHiddenDevicePaths(cli);
        return hidden.Where(p => IsVirtualG920KeepVisible(p, p)).ToList();
    }

    private static void CollectHidePaths(JsonElement device, string friendlyName, ISet<string> paths)
    {
        var vendor = device.TryGetProperty("vendor", out var v) ? v.GetString() ?? "" : "";
        var product = device.TryGetProperty("product", out var p) ? p.GetString() ?? "" : "";
        var description = device.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var symbolic = device.TryGetProperty("symbolicLink", out var s) ? s.GetString() ?? "" : "";
        var blob = $"{friendlyName} {vendor} {product} {description} {symbolic}";

        void Consider(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (ShouldAutoHideDevice(path, blob))
                paths.Add(path);
        }

        if (device.TryGetProperty("deviceInstancePath", out var dip))
            Consider(dip.GetString());
        if (device.TryGetProperty("xusbDeviceInstancePath", out var xusb))
            Consider(xusb.GetString());
        if (device.TryGetProperty("baseContainerDeviceInstancePath", out var basePath))
            Consider(basePath.GetString());
    }

    private static bool ShouldAutoHideDevice(string instancePath, string textBlob)
    {
        if (string.IsNullOrWhiteSpace(instancePath))
            return false;

        // Games must keep seeing the virtual G920. Physical pads/wheels are hidden instead.
        if (IsVirtualG920KeepVisible(instancePath, textBlob))
            return false;

        return true;
    }

    /// <summary>
    /// Virtual WinUHid G920 stays visible. A real USB Logitech wheel with the same VID/PID is hidden.
    /// </summary>
    private static bool IsVirtualG920KeepVisible(string instancePath, string textBlob)
    {
        var blob = $"{instancePath} {textBlob}";
        if (blob.Contains("WINUHID", StringComparison.OrdinalIgnoreCase) ||
            blob.Contains(@"ROOT\WINUHID", StringComparison.OrdinalIgnoreCase) ||
            blob.Contains(@"VHF\", StringComparison.OrdinalIgnoreCase))
            return true;

        // Physical Logitech wheels are under USB\; the virtual stack is not.
        if (instancePath.Contains("VID_046D&PID_C262", StringComparison.OrdinalIgnoreCase) ||
            instancePath.Contains("VID_046D&PID_C26D", StringComparison.OrdinalIgnoreCase))
        {
            if (instancePath.Contains(@"USB\", StringComparison.OrdinalIgnoreCase) ||
                instancePath.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        return false;
    }

    private static string UnescapeJson(string value) =>
        value.Replace("\\\"", "\"", StringComparison.Ordinal)
             .Replace("\\\\", "\\", StringComparison.Ordinal);

    private static bool IsCurrentProcessWhitelisted(string cli, IReadOnlyList<string> emulatorPaths)
    {
        if (emulatorPaths.Count == 0)
            return false;

        var (ok, output) = RunHidHideCapture(cli, "--app-list");
        if (!ok || string.IsNullOrWhiteSpace(output))
            return false;

        foreach (var path in emulatorPaths)
        {
            if (output.Contains(path, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static (bool Ok, string Detail) RunHidHideCommands(string cli, IReadOnlyList<string> commands)
    {
        // Try non-elevated first (quiet). On access denied, one UAC prompt runs the batch elevated.
        var failed = new List<string>();
        foreach (var args in commands)
        {
            var (ok, output) = RunHidHideCapture(cli, args);
            if (ok) continue;
            failed.Add($"{args} → {output}");
        }

        if (failed.Count == 0)
            return (true, "ok");

        var accessDenied = failed.Any(f =>
            f.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("0x0005", StringComparison.OrdinalIgnoreCase));

        if (!accessDenied)
            return (false, failed[0]);

        try
        {
            var script = Path.Combine(Path.GetTempPath(), $"g920-hidhide-{Guid.NewGuid():N}.cmd");
            var lines = commands.Select(a => $"\"{cli}\" {a}");
            File.WriteAllText(script, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            var elevated = Process.Start(new ProcessStartInfo
            {
                FileName = script,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            elevated?.WaitForExit(20000);
            try { File.Delete(script); } catch { /* ignore */ }

            // Verify inverse state after elevation.
            var (invOk, invOut) = RunHidHideCapture(cli, "--inv-state");
            if (invOk && invOut.Contains("--inv-off", StringComparison.OrdinalIgnoreCase))
                return (true, "configured (elevated)");

            return (elevated is { ExitCode: 0 }, "elevated configure finished");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "UAC elevation cancelled");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool Ok, string Output) RunHidHideCapture(string cli, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, "failed to start CLI");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(8000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "CLI timed out");
            }

            var text = (stdout + Environment.NewLine + stderr).Trim();
            var accessDenied = text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("0x0005", StringComparison.OrdinalIgnoreCase);
            var ok = proc.ExitCode == 0 && !accessDenied;
            return (ok, string.IsNullOrWhiteSpace(text) ? $"exit {proc.ExitCode}" : text);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static IEnumerable<string> EnumerateEmulatorExePaths(IEnumerable<string> extraAppPaths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var self = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(self))
                set.Add(self);
        }
        catch { /* ignore */ }

        foreach (var p in extraAppPaths)
        {
            if (!string.IsNullOrWhiteSpace(p))
                set.Add(p);
        }

        // Common publish / launch locations from prior sessions.
        try
        {
            var dir = AppContext.BaseDirectory;
            set.Add(Path.Combine(dir, "G920Emulator.exe"));
        }
        catch { /* ignore */ }

        return set.Where(File.Exists);
    }

    /// <summary>
    /// Capture HidHide cloak / inverse / app / hide-list state for diagnostics (via CLI; no registry admin needed).
    /// </summary>
    public static string CaptureHidHideDiagnostics()
    {
        var sb = new StringBuilder();
        var cli = FindHidHideCli();
        if (cli is null)
        {
            sb.AppendLine("HidHideCLI.exe: not found");
            return sb.ToString();
        }

        sb.AppendLine("HidHideCLI: " + cli);
        foreach (var args in new[] { "--cloak-state", "--inv-state", "--app-list", "--dev-list" })
        {
            sb.AppendLine();
            sb.AppendLine("### " + args);
            var (ok, output) = RunHidHideCapture(cli, args);
            sb.AppendLine(ok ? output : ("FAILED: " + output));
        }

        return sb.ToString();
    }

    private static string? FindHidHideCli()
    {
        var path = ReadHidHidePath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            var cli = Path.Combine(path, "x64", "HidHideCLI.exe");
            if (File.Exists(cli)) return cli;
            cli = Path.Combine(path, "HidHideCLI.exe");
            if (File.Exists(cli)) return cli;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions", "HidHide", "x64", "HidHideCLI.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions", "HidHide", "HidHideCLI.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ReadHidHideVersion()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"Installer\Dependencies\NSS.Drivers.HidHide.x64");
            var value = key?.GetValue("Version")?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            using var keyArm = Registry.ClassesRoot.OpenSubKey(@"Installer\Dependencies\NSS.Drivers.HidHide.arm64");
            return keyArm?.GetValue("Version")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadHidHidePath()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(
                @"SOFTWARE\Nefarius Software Solutions e.U.\Nefarius Software Solutions e.U. HidHide");
            return key?.GetValue("Path")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool RegistryServiceExists(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }
}
