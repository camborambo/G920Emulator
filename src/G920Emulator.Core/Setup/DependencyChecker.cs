using System.Collections.Concurrent;
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

/// <summary>UEFI Secure Boot state from the Windows Secure Boot registry key.</summary>
public enum SecureBootStatus
{
    On,
    Off,
    /// <summary>Legacy BIOS / key missing - Secure Boot is not in effect.</summary>
    Unavailable,
    Unknown,
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
    public SecureBootStatus SecureBoot { get; init; }

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
        var secureBoot = QuerySecureBoot();
        var winUHid = CheckWinUHid(probeWinUHid);
        return new DependencyReport
        {
            SecureBoot = secureBoot,
            Items =
            [
                CheckTestSigning(winUHidReady: winUHid.IsInstalled, secureBoot),
                winUHid,
                CheckHidHide(),
            ],
        };
    }

    /// <summary>
    /// Reads <c>HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled</c>.
    /// No elevation required. Legacy BIOS machines typically have no key → <see cref="SecureBootStatus.Unavailable"/>.
    /// </summary>
    public static SecureBootStatus QuerySecureBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key is null)
                return SecureBootStatus.Unavailable;

            var value = key.GetValue("UEFISecureBootEnabled");
            if (value is null)
                return SecureBootStatus.Unavailable;

            var enabled = Convert.ToInt32(value) != 0;
            return enabled ? SecureBootStatus.On : SecureBootStatus.Off;
        }
        catch
        {
            return SecureBootStatus.Unknown;
        }
    }

    /// <summary>
    /// Test signing is only required to *install* the test-signed WinUHid package.
    /// After install it can stay off - WinUHid (UMDF) usually keeps working and Forza can launch.
    /// </summary>
    public static DependencyInfo CheckTestSigning(bool winUHidReady = false, SecureBootStatus? secureBoot = null)
    {
        var setup = new WinUHidSetupService();
        var (live, bcd) = setup.QueryTestSigningDetail();
        var pendingInstall = BundledWinUHidInstaller.HasPendingInstall;
        var sb = secureBoot ?? QuerySecureBoot();

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
            hint = sb == SecureBootStatus.On
                ? "WinUHid is installed. Secure Boot can stay on."
                : "WinUHid is installed. You can turn Secure Boot back on in UEFI/BIOS.";
            detail = "";
        }
        else if (winUHidReady && live)
        {
            ready = true; // emulator works; warn that Forza will not
            statusLabel = "Enabled";
            hint = "Disable test signing and reboot so Forza can launch.";
            detail = "";
        }
        else if (live)
        {
            ready = true;
            statusLabel = "Enabled";
            hint = pendingInstall
                ? "Test mode is on. Click Install WinUHid to finish."
                : "Ready to Install WinUHid.";
            detail = "";
        }
        else if (bcd == true || pendingInstall)
        {
            ready = false;
            statusLabel = "Reboot required";
            hint = "Reboot, then Install WinUHid.";
            detail = "";
        }
        else
        {
            ready = false;
            statusLabel = "Disabled";
            if (sb == SecureBootStatus.On)
                hint = "Turn Secure Boot off in UEFI/BIOS to Enable test signing.";
            else
                hint = "Needed only to install WinUHid.";
            detail = "";
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
            hint = "A WinUHid device is present but the app cannot open it (often a duplicate enumerator). Prefer Uninstall WinUHid, then Install once - or Recheck after a reboot.";
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
    /// Additive one-click setup: ensure inverse off + cloak on, whitelist this emulator, and
    /// hide newly detected pads/wheels (so games only see the virtual G920). Never removes
    /// apps or devices already configured in HidHide.
    /// </summary>
    public static HidHideEnsureResult ConfigureHidHideFully(params string[] extraAppPaths) =>
        EnsureHidHideForEmulator(extraAppPaths, hidePhysicalControllers: true);

    /// <summary>Full HidHide cloak / inverse / app / device list for session restore.</summary>
    public sealed class HidHideSnapshot
    {
        public bool CloakOn { get; set; } = true;
        public bool InverseOn { get; set; }
        public List<string> Apps { get; set; } = [];
        public List<string> HiddenDevices { get; set; } = [];
        public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    private static string HidHideSnapshotPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "G920Emulator",
            "hidhide-pre-session.json");

    /// <summary>
    /// Reads the user's current HidHide configuration (before we change it for a bridge session).
    /// </summary>
    public static HidHideSnapshot? TryCaptureHidHideSnapshot() =>
        TryCaptureHidHideSnapshotDetailed().Snapshot;

    /// <summary>
    /// Same as <see cref="TryCaptureHidHideSnapshot"/> but with a reason when capture fails.
    /// Retries elevated when the driver returns access denied.
    /// </summary>
    public static (HidHideSnapshot? Snapshot, string Error) TryCaptureHidHideSnapshotDetailed()
    {
        // Stop's KillOrphan leaves cancel set - clear it or WaitForHidHideChild kills capture instantly.
        BeginHidHideOps();

        var cli = FindHidHideCli();
        if (cli is null)
        {
            return (null,
                "HidHideCLI.exe was not found. Reinstall HidHide, then try again.");
        }

        var captured = CaptureHidHideSnapshotOnce(cli);
        if (captured.Snapshot is not null)
            return captured;

        if (LooksLikeAccessDenied(captured.Error))
        {
            // HidHide Client holds an exclusive handle - elevation does not help while it is open.
            if (IsHidHideClientRunning())
            {
                return (null,
                    "HidHide Client is open and has locked the driver (Access is denied / 0x0005). " +
                    "Close the Client window, then try again.");
            }

            var elevated = TryCaptureHidHideSnapshotElevated(cli);
            if (elevated.Snapshot is not null)
                return elevated;
            if (IsHidHideClientRunning())
            {
                return (null,
                    "HidHide Client is open and has locked the driver (Access is denied / 0x0005). " +
                    "Close the Client window, then try again.");
            }

            if (!string.IsNullOrWhiteSpace(elevated.Error))
                return elevated;
        }

        if (string.IsNullOrWhiteSpace(captured.Error))
        {
            return (null,
                "Could not read HidHide. Close HidHide Client and try again, or accept the UAC prompt if Windows asks.");
        }

        return captured;
    }

    /// <summary>True when HidHide Client UI is running (it locks the filter driver).</summary>
    public static bool IsHidHideClientRunning()
    {
        foreach (var name in new[] { "HidHideClient", "HidHideClient.exe" })
        {
            try
            {
                var procs = Process.GetProcessesByName(name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase));
                try
                {
                    if (procs.Length > 0)
                        return true;
                }
                finally
                {
                    foreach (var p in procs)
                    {
                        try { p.Dispose(); } catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore */ }
        }

        return false;
    }

    /// <summary>
    /// Closes HidHide Client so CLI can talk to the driver. Returns how many processes were closed.
    /// </summary>
    public static int TryCloseHidHideClient()
    {
        var closed = 0;
        Process[] procs;
        try { procs = Process.GetProcessesByName("HidHideClient"); }
        catch { return 0; }

        foreach (var p in procs)
        {
            try
            {
                if (p.HasExited)
                    continue;

                // Prefer a graceful close so the user does not lose unsaved Client edits mid-click.
                try
                {
                    if (p.CloseMainWindow())
                    {
                        if (p.WaitForExit(2_000))
                        {
                            closed++;
                            continue;
                        }
                    }
                }
                catch { /* fall through to kill */ }

                try
                {
                    p.Kill(entireProcessTree: true);
                    if (!p.HasExited)
                        p.WaitForExit(1_000);
                    closed++;
                }
                catch { /* ignore */ }
            }
            finally
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }

        if (closed > 0)
            Thread.Sleep(400);

        return closed;
    }

    private static (HidHideSnapshot? Snapshot, string Error) CaptureHidHideSnapshotOnce(string cli) =>
        CaptureHidHideSnapshotViaScript(cli, elevated: false);

    private static (HidHideSnapshot? Snapshot, string Error) TryCaptureHidHideSnapshotElevated(string cli) =>
        CaptureHidHideSnapshotViaScript(cli, elevated: true);

    private static (HidHideSnapshot? Snapshot, string Error) CaptureHidHideSnapshotSequential(string cli)
    {
        var (cloakOk, cloakOut) = RunHidHideCapture(cli, "--cloak-state");
        if (HidHideOpsCancelled) return (null, "HidHide capture cancelled.");
        var (invOk, invOut) = RunHidHideCapture(cli, "--inv-state");
        if (HidHideOpsCancelled) return (null, "HidHide capture cancelled.");
        var (appOk, appOut) = RunHidHideCapture(cli, "--app-list");
        if (HidHideOpsCancelled) return (null, "HidHide capture cancelled.");
        var (devOk, devOut) = RunHidHideCapture(cli, "--dev-list");
        if (HidHideOpsCancelled) return (null, "HidHide capture cancelled.");

        if (LooksLikeAccessDenied(cloakOut) || LooksLikeAccessDenied(invOut) ||
            LooksLikeAccessDenied(appOut) || LooksLikeAccessDenied(devOut))
        {
            return (null, FirstNonEmpty(appOut, devOut, cloakOut, invOut) ?? "Access is denied");
        }

        if (!cloakOk && !invOk && !appOk && !devOk)
        {
            return (null, FirstNonEmpty(cloakOut, invOut, appOut, devOut) ?? "CLI returned no data");
        }

        return (new HidHideSnapshot
        {
            CloakOn = cloakOk && cloakOut.Contains("--cloak-on", StringComparison.OrdinalIgnoreCase),
            InverseOn = invOk && invOut.Contains("--inv-on", StringComparison.OrdinalIgnoreCase),
            Apps = ParseQuotedCliArgs(appOut, "--app-reg"),
            HiddenDevices = ParseQuotedCliArgs(devOut, "--dev-hide"),
            CapturedAt = DateTimeOffset.UtcNow,
        }, "");
    }

    /// <summary>
    /// One .cmd runs cloak/inv/app/dev - four separate HidHideCLI process starts were the Start delay.
    /// </summary>
    private static (HidHideSnapshot? Snapshot, string Error) CaptureHidHideSnapshotViaScript(string cli, bool elevated)
    {
        string? dir = null;
        try
        {
            dir = Path.Combine(Path.GetTempPath(), "g920-hidhide-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "capture.cmd");
            // cd to TEMP first: elevated runas often ignores ProcessStartInfo.WorkingDirectory and
            // would otherwise inherit the install folder CWD (Desktop\G920Emulator stays "in use").
            var lines = new[]
            {
                "@echo off",
                "cd /d \"%TEMP%\"",
                $"\"{cli}\" --cloak-state > \"{Path.Combine(dir, "cloak.txt")}\" 2>&1",
                $"\"{cli}\" --inv-state > \"{Path.Combine(dir, "inv.txt")}\" 2>&1",
                $"\"{cli}\" --app-list > \"{Path.Combine(dir, "apps.txt")}\" 2>&1",
                $"\"{cli}\" --dev-list > \"{Path.Combine(dir, "devs.txt")}\" 2>&1",
            };
            File.WriteAllText(script, string.Join(Environment.NewLine, lines) + Environment.NewLine);

            Process? proc;
            if (elevated)
            {
                var elevatedPsi = new ProcessStartInfo
                {
                    FileName = script,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                InstallFolderGuard.ApplySafeWorkingDirectory(elevatedPsi);
                proc = Process.Start(elevatedPsi);
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"" + script + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                InstallFolderGuard.ApplySafeWorkingDirectory(psi);
                proc = Process.Start(psi);
            }

            if (proc is null)
                return (null, elevated
                    ? "Could not start elevated HidHide capture."
                    : "Could not start HidHide capture.");

            TrackHidHideChild(proc);
            WaitForHidHideChild(proc, elevated ? 12_000 : 8_000);
            if (HidHideOpsCancelled)
                return (null, "HidHide capture cancelled.");
            if (!proc.HasExited)
                return (null, elevated
                    ? "Elevated HidHide capture timed out."
                    : "HidHide capture timed out.");

            string Read(string name)
            {
                var path = Path.Combine(dir, name);
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }

            var cloakOut = Read("cloak.txt");
            var invOut = Read("inv.txt");
            var appOut = Read("apps.txt");
            var devOut = Read("devs.txt");

            var cloakOk = cloakOut.Contains("--cloak-", StringComparison.OrdinalIgnoreCase);
            var invOk = invOut.Contains("--inv-", StringComparison.OrdinalIgnoreCase);
            // Empty app/dev lists are valid; any cloak/inv state line means the driver answered.
            if (!cloakOk && !invOk && string.IsNullOrWhiteSpace(appOut) && string.IsNullOrWhiteSpace(devOut))
            {
                // Batch script produced nothing - try one-shot CLI (survives Start/Stop churn better).
                if (!elevated)
                {
                    var fallback = CaptureHidHideSnapshotSequential(cli);
                    if (fallback.Snapshot is not null)
                        return fallback;
                }

                return (null,
                    FirstNonEmpty(cloakOut, invOut, appOut, devOut)
                    ?? (elevated ? "Elevated capture returned no data." : "CLI returned no data"));
            }

            // Partial capture (cloak OK, lists access-denied) must not become a restore point -
            // that snapshot would look "empty" and Stop would wipe/skip the real config.
            if (LooksLikeAccessDenied(cloakOut) || LooksLikeAccessDenied(invOut) ||
                LooksLikeAccessDenied(appOut) || LooksLikeAccessDenied(devOut))
            {
                return (null,
                    FirstNonEmpty(appOut, devOut, cloakOut, invOut) ?? "Access is denied");
            }

            return (new HidHideSnapshot
            {
                CloakOn = cloakOut.Contains("--cloak-on", StringComparison.OrdinalIgnoreCase),
                InverseOn = invOut.Contains("--inv-on", StringComparison.OrdinalIgnoreCase),
                Apps = ParseQuotedCliArgs(appOut, "--app-reg"),
                HiddenDevices = ParseQuotedCliArgs(devOut, "--dev-hide"),
                CapturedAt = DateTimeOffset.UtcNow,
            }, "");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (null, elevated
                ? "UAC elevation cancelled - cannot read HidHide without admin rights."
                : "Could not start HidHide capture.");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
        finally
        {
            if (dir is not null)
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }

    /// <summary>Persists a pre-session snapshot so Stop/crash recovery can restore it.</summary>
    public static void SaveHidHideSnapshot(HidHideSnapshot snapshot)
    {
        var dir = Path.GetDirectoryName(HidHideSnapshotPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(HidHideSnapshotPath, json);
    }

    public static HidHideSnapshot? TryLoadHidHideSnapshot()
    {
        try
        {
            if (!File.Exists(HidHideSnapshotPath))
                return null;
            return JsonSerializer.Deserialize<HidHideSnapshot>(File.ReadAllText(HidHideSnapshotPath));
        }
        catch
        {
            return null;
        }
    }

    public static void ClearHidHideSnapshot()
    {
        try
        {
            if (File.Exists(HidHideSnapshotPath))
                File.Delete(HidHideSnapshotPath);
        }
        catch { /* ignore */ }
    }

    public static bool HasPendingHidHideSnapshot => File.Exists(HidHideSnapshotPath);

    /// <summary>
    /// Restores a previously captured HidHide configuration (cloak, inverse, apps, hide list).
    /// </summary>
    public static (bool Ok, string Message) RestoreHidHideSnapshot(HidHideSnapshot snapshot)
    {
        if (HidHideOpsCancelled)
            return (false, "HidHide restore cancelled.");

        var cli = FindHidHideCli();
        if (cli is null)
            return (false, "HidHide CLI not found - cannot restore.");

        // Must know the live lists or the diff cannot unhide session devices / unreg the emulator.
        var (listsOk, currentApps, currentDevs, listError) = ReadHidHideAppAndDeviceLists(cli);
        if (HidHideOpsCancelled)
            return (false, "HidHide restore cancelled.");
        if (!listsOk)
            return (false, "HidHide restore failed - could not read current lists. " + listError);

        var targetApps = new HashSet<string>(snapshot.Apps ?? [], StringComparer.OrdinalIgnoreCase);
        var targetDevs = new HashSet<string>(snapshot.HiddenDevices ?? [], StringComparer.OrdinalIgnoreCase);
        var currentAppSet = new HashSet<string>(currentApps, StringComparer.OrdinalIgnoreCase);
        var currentDevSet = new HashSet<string>(currentDevs, StringComparer.OrdinalIgnoreCase);

        var commands = new List<string>
        {
            snapshot.CloakOn ? "--cloak-on" : "--cloak-off",
            snapshot.InverseOn ? "--inv-on" : "--inv-off",
        };

        foreach (var app in currentAppSet)
        {
            if (!targetApps.Contains(app))
                commands.Add($"--app-unreg \"{app}\"");
        }
        foreach (var app in targetApps)
        {
            if (!currentAppSet.Contains(app))
                commands.Add($"--app-reg \"{app}\"");
        }

        foreach (var dev in currentDevSet)
        {
            if (!targetDevs.Contains(dev))
                commands.Add($"--dev-unhide \"{dev}\"");
        }
        foreach (var dev in targetDevs)
        {
            if (!currentDevSet.Contains(dev))
                commands.Add($"--dev-hide \"{dev}\"");
        }

        // Restore writes almost always need elevation; retry elevate on any failure.
        var (ok, detail) = RunHidHideCommands(cli, commands, elevateOnAnyFailure: true, timeoutMs: 20_000);
        if (HidHideOpsCancelled)
            return (false, "HidHide restore cancelled.");
        if (!ok)
            return (false, "HidHide restore failed - " + detail);

        // Never report success (and clear the snapshot) unless the live config matches.
        var (verified, verifyDetail) = VerifyHidHideMatchesSnapshot(cli, snapshot);
        if (HidHideOpsCancelled)
            return (false, "HidHide restore cancelled.");
        return verified
            ? (true, "HidHide restored to the pre-session configuration.")
            : (false, "HidHide restore did not stick - " + verifyDetail);
    }

    private static (bool Ok, List<string> Apps, List<string> Devices, string Error) ReadHidHideAppAndDeviceLists(string cli)
    {
        var outputs = RunHidHideMultiCapture(cli, ["--app-list", "--dev-list"], timeoutMs: 10_000);
        var appOut = outputs.GetValueOrDefault("--app-list") ?? "";
        var devOut = outputs.GetValueOrDefault("--dev-list") ?? "";

        var denied = LooksLikeAccessDenied(appOut) || LooksLikeAccessDenied(devOut);
        if (!denied && string.IsNullOrWhiteSpace(appOut) && string.IsNullOrWhiteSpace(devOut))
        {
            // Empty files can mean "no entries" or "CLI failed silently" - probe a cheap state flag.
            var (invOk, invOut) = RunHidHideCapture(cli, "--inv-state");
            if (invOk && !LooksLikeAccessDenied(invOut))
                return (true, [], [], "");
            denied = true;
        }

        if (denied)
        {
            // Elevated list read (same pattern as snapshot capture).
            var elevated = CaptureHidHideSnapshotViaScript(cli, elevated: true);
            if (elevated.Snapshot is not null)
            {
                return (true,
                    elevated.Snapshot.Apps ?? [],
                    elevated.Snapshot.HiddenDevices ?? [],
                    "");
            }

            return (false, [], [],
                string.IsNullOrWhiteSpace(elevated.Error)
                    ? (FirstNonEmpty(appOut, devOut) ?? "access denied reading lists")
                    : elevated.Error);
        }

        // Successful CLI list output may be empty (no apps / no hidden devices).
        return (true,
            ParseQuotedCliArgs(appOut, "--app-reg"),
            ParseQuotedCliArgs(devOut, "--dev-hide"),
            "");
    }

    private static (bool Ok, string Detail) VerifyHidHideMatchesSnapshot(string cli, HidHideSnapshot snapshot)
    {
        var (listsOk, apps, devices, listError) = ReadHidHideAppAndDeviceLists(cli);
        if (!listsOk)
            return (false, "could not re-read lists after restore. " + listError);

        var targetApps = new HashSet<string>(snapshot.Apps ?? [], StringComparer.OrdinalIgnoreCase);
        var targetDevs = new HashSet<string>(snapshot.HiddenDevices ?? [], StringComparer.OrdinalIgnoreCase);
        var liveApps = new HashSet<string>(apps, StringComparer.OrdinalIgnoreCase);
        var liveDevs = new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase);

        if (!targetApps.SetEquals(liveApps) || !targetDevs.SetEquals(liveDevs))
        {
            return (false,
                $"apps {liveApps.Count}/{targetApps.Count}, hidden devices {liveDevs.Count}/{targetDevs.Count} " +
                "(accept UAC if prompted, close HidHide Client, then Stop again).");
        }

        var (cloakOk, cloakOut) = RunHidHideCapture(cli, "--cloak-state");
        var (invOk, invOut) = RunHidHideCapture(cli, "--inv-state");
        if (cloakOk)
        {
            var cloakOn = cloakOut.Contains("--cloak-on", StringComparison.OrdinalIgnoreCase);
            if (cloakOn != snapshot.CloakOn)
                return (false, "cloak state mismatch.");
        }

        if (invOk)
        {
            var invOn = invOut.Contains("--inv-on", StringComparison.OrdinalIgnoreCase);
            if (invOn != snapshot.InverseOn)
                return (false, "inverse state mismatch.");
        }

        return (true, "ok");
    }

    /// <summary>
    /// Whitelist emulator → hide all controllers/wheels except the virtual G920.
    /// When <paramref name="saveRevertSnapshot"/> is true, captures and stores a restore point first.
    /// </summary>
    public static (bool Ok, string Message) BeginHidHideSession(
        bool saveRevertSnapshot = true,
        params string[] extraAppPaths)
    {
        if (saveRevertSnapshot)
        {
            var (snap, error) = TryCaptureHidHideSnapshotDetailed();
            if (snap is null)
                return (false, string.IsNullOrWhiteSpace(error)
                    ? "Could not read HidHide configuration."
                    : error);

            SaveHidHideSnapshot(snap);
        }
        else
        {
            ClearHidHideSnapshot();
        }

        // Whitelist/cloak only - full device hide is RefreshHidHideSessionDevices after the virtual G920 exists.
        var result = EnsureHidHideForEmulator(extraAppPaths, hidePhysicalControllers: false);
        if (!result.CliAvailable)
        {
            if (saveRevertSnapshot)
                ClearHidHideSnapshot();
            return (false, result.Message);
        }

        if (!string.IsNullOrWhiteSpace(result.Message) &&
            result.Message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            // Keep snapshot so Stop can still try to revert any partial changes.
            return (false, result.Message);
        }

        return (true, saveRevertSnapshot
            ? result.Message + " HidHide restore point saved for Stop."
            : result.Message);
    }

    /// <summary>
    /// Apply session hide using an already-captured snapshot (or none).
    /// Whitelist + cloak only here; device hide runs in <see cref="RefreshHidHideSessionDevices"/>
    /// after the virtual G920 exists (avoids scanning --dev-all twice on Start).
    /// </summary>
    public static (bool Ok, string Message) BeginHidHideSessionWithSnapshot(
        HidHideSnapshot? revertSnapshot,
        params string[] extraAppPaths)
    {
        BeginHidHideOps();
        if (revertSnapshot is not null)
            SaveHidHideSnapshot(revertSnapshot);
        else
            ClearHidHideSnapshot();

        var result = EnsureHidHideForEmulator(extraAppPaths, hidePhysicalControllers: false);
        if (!result.CliAvailable)
        {
            if (revertSnapshot is not null)
                ClearHidHideSnapshot();
            return (false, result.Message);
        }

        if (!string.IsNullOrWhiteSpace(result.Message) &&
            result.Message.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return (false, result.Message);

        return (true, revertSnapshot is not null
            ? result.Message + " HidHide restore point saved for Stop."
            : result.Message);
    }

    /// <summary>
    /// Re-apply hide/unhide after the virtual G920 enumerates (no new snapshot).
    /// </summary>
    public static (bool Ok, string Message) RefreshHidHideSessionDevices(params string[] extraAppPaths)
    {
        BeginHidHideOps();
        var result = ConfigureHidHideFully(extraAppPaths);
        if (!result.CliAvailable)
            return (false, result.Message);
        if (!string.IsNullOrWhiteSpace(result.Message) &&
            result.Message.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return (false, result.Message);
        return (true, result.Message);
    }

    /// <summary>Exit/Stop budget: Stop waits long enough for UAC + verified restore; Exit keeps a short try.</summary>
    public const int HidHideRestoreExitTimeoutMs = 8_000;
    public const int HidHideRestoreStopTimeoutMs = 45_000;

    /// <summary>Restore pre-session HidHide config if a snapshot file exists.</summary>
    public static (bool Ok, string Message) EndHidHideSession() =>
        EndHidHideSession(timeoutMs: HidHideRestoreStopTimeoutMs);

    /// <summary>
    /// Restore pre-session HidHide config, but never block longer than <paramref name="timeoutMs"/>.
    /// On timeout, orphan CLI helpers are killed and the snapshot is kept for next-launch recovery.
    /// </summary>
    public static (bool Ok, string Message) EndHidHideSession(int timeoutMs)
    {
        var snap = TryLoadHidHideSnapshot();
        if (snap is null)
            return (true, "No HidHide session snapshot to restore.");

        timeoutMs = Math.Clamp(timeoutMs, 500, 60_000);
        BeginHidHideOps();
        try
        {
            var task = Task.Run(() => RestoreHidHideSnapshot(snap));
            if (!task.Wait(timeoutMs))
            {
                CancelHidHideOps();
                KillOrphanHidHideHelpers();
                return (false,
                    "HidHide restore timed out - helpers stopped. Will retry next launch.");
            }

            var (ok, message) = task.Result;
            if (ok)
                ClearHidHideSnapshot();
            return (ok, message);
        }
        catch (Exception ex)
        {
            CancelHidHideOps();
            KillOrphanHidHideHelpers();
            return (false, "HidHide restore failed - " + ex.Message);
        }
    }

    /// <summary>
    /// Kill leftover HidHideCLI / g920-hidhide helper processes so the app folder can be deleted.
    /// Does not touch HidHide Client or HidHideWatchdog.
    /// Clears the cancel flag afterward so the next Start/Stop can talk to HidHide again.
    /// </summary>
    public static void KillOrphanHidHideHelpers()
    {
        CancelHidHideOps();

        try
        {
            while (_hidHideChildProcesses.TryTake(out var tracked))
            {
                try
                {
                    if (!tracked.HasExited)
                        tracked.Kill(entireProcessTree: true);
                }
                catch { /* ignore */ }
                finally
                {
                    try { tracked.Dispose(); } catch { /* ignore */ }
                }
            }

            foreach (var name in new[] { "HidHideCLI", "HidHideCLI64" })
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
                            p.Kill(entireProcessTree: true);
                    }
                    catch { /* ignore */ }
                    finally
                    {
                        try { p.Dispose(); } catch { /* ignore */ }
                    }
                }
            }
        }
        finally
        {
            // Without this, the next capture's WaitForHidHideChild sees cancel and aborts → "CLI returned no data".
            BeginHidHideOps();
        }
    }

    private static readonly ConcurrentBag<Process> _hidHideChildProcesses = new();
    private static int _hidHideOpsCancel;

    private static bool HidHideOpsCancelled => Volatile.Read(ref _hidHideOpsCancel) != 0;

    private static void BeginHidHideOps() => Volatile.Write(ref _hidHideOpsCancel, 0);

    private static void CancelHidHideOps() => Volatile.Write(ref _hidHideOpsCancel, 1);

    private static void TrackHidHideChild(Process? process)
    {
        if (process is null)
            return;
        _hidHideChildProcesses.Add(process);
    }

    private static void WaitForHidHideChild(Process? process, int timeoutMs)
    {
        if (process is null)
            return;
        try
        {
            var deadline = Environment.TickCount64 + Math.Max(200, timeoutMs);
            while (!process.HasExited)
            {
                if (HidHideOpsCancelled || Environment.TickCount64 >= deadline)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    return;
                }

                process.WaitForExit(200);
            }
        }
        catch { /* ignore */ }
    }

    private static List<string> ParseQuotedCliArgs(string? output, string flag)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
            return list;

        var pattern = Regex.Escape(flag) + "\\s+\"([^\"]+)\"";
        foreach (Match m in Regex.Matches(output, pattern, RegexOptions.IgnoreCase))
        {
            var value = m.Groups[1].Value.Trim();
            if (value.Length > 0)
                list.Add(value);
        }

        return list;
    }

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
                Message = "HidHide CLI not found - install HidHide first.",
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

        // If G HUB / manual config hid the virtual G920, games won't see it - unhide those paths.
        foreach (var path in DiscoverVirtualG920PathsToUnhide(cli))
            commands.Add($"--dev-unhide \"{path}\"");

        // Drop keyboards / LED / cameras we (or HidHide Client) may have cloaked by mistake.
        foreach (var path in DiscoverNonGamingPathsToUnhide(cli))
            commands.Add($"--dev-unhide \"{path}\"");

        var hidePaths = hidePhysicalControllers ? DiscoverPhysicalGamingDevicesToHide(cli) : [];
        foreach (var path in hidePaths)
            commands.Add($"--dev-hide \"{path}\"");

        var (ok, detail) = RunHidHideCommands(cli, commands);
        if (!ok)
        {
            return new HidHideEnsureResult
            {
                Message = $"HidHide configure failed - {detail}",
                CliAvailable = true,
                NeedsRelaunch = false,
                DevicesHidden = 0,
            };
        }

        var confirmed = IsCurrentProcessWhitelisted(cli, emulatorPaths);
        var needsRelaunch = !alreadyWhitelisted && confirmed && emulatorPaths.Count > 0;

        var hideNote = hidePaths.Count == 0
            ? (hidePhysicalControllers
                ? " All other controllers already hidden (or none found); virtual G920 left visible."
                : "")
            : $" HidHide: {hidePaths.Count} device path(s) hidden; virtual G920 left visible.";

        return new HidHideEnsureResult
        {
            CliAvailable = true,
            NeedsRelaunch = needsRelaunch,
            DevicesHidden = hidePaths.Count,
            Message = emulatorPaths.Count == 0
                ? "HidHide inverse off (could not find G920Emulator.exe to whitelist)." + hideNote
                : needsRelaunch
                    ? $"HidHide updated - emulator whitelisted; relaunch so devices stay visible.{hideNote}"
                    : confirmed
                        ? $"HidHide updated - inverse off, cloak on, emulator whitelisted.{hideNote}"
                        : "Could not confirm whitelist. Click Configure again and accept UAC, or add G920Emulator.exe in HidHide Client." + hideNote,
        };
    }

    /// <summary>
    /// Hides whatever HidHide Client lists under Devices with "Gaming devices only" -
    /// <c>--dev-gaming</c> only. Does not scan <c>--dev-all</c> or invent devices.
    /// Virtual G920 paths stay visible.
    /// </summary>
    private static List<string> DiscoverPhysicalGamingDevicesToHide(string cli)
    {
        var outputs = RunHidHideMultiCapture(cli, ["--dev-list", "--dev-gaming"], timeoutMs: 10_000);
        var alreadyHidden = ParseAlreadyHiddenDevicePaths(outputs.GetValueOrDefault("--dev-list") ?? "");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectHidHideGamingListPaths(outputs.GetValueOrDefault("--dev-gaming"), paths);

        paths.RemoveWhere(p =>
            alreadyHidden.Contains(p) || IsVirtualG920KeepVisible(p, p));
        return paths.ToList();
    }

    /// <summary>
    /// Paths from HidHideCLI <c>--dev-gaming</c> (same set as Client → Devices → Gaming devices only).
    /// </summary>
    private static void CollectHidHideGamingListPaths(string? output, ISet<string> paths)
    {
        if (string.IsNullOrWhiteSpace(output) || LooksLikeAccessDenied(output))
            return;

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var container in doc.RootElement.EnumerateArray())
                {
                    if (!container.TryGetProperty("devices", out var devices) ||
                        devices.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var device in devices.EnumerateArray())
                    {
                        if (device.TryGetProperty("deviceInstancePath", out var dip))
                            Add(dip.GetString());
                        if (device.TryGetProperty("xusbDeviceInstancePath", out var xusb))
                            Add(xusb.GetString());
                        // Never use baseContainerDeviceInstancePath - cloaks whole USB composites.
                    }
                }

                return;
            }
        }
        catch (JsonException)
        {
            // Fall through to regex.
        }

        foreach (Match m in Regex.Matches(output, "\"deviceInstancePath\"\\s*:\\s*\"([^\"]+)\""))
            Add(UnescapeJson(m.Groups[1].Value));
        foreach (Match m in Regex.Matches(output, "\"xusbDeviceInstancePath\"\\s*:\\s*\"([^\"]+)\""))
            Add(UnescapeJson(m.Groups[1].Value));
    }

    /// <summary>
    /// Paths currently hidden that are clearly not pads/wheels - undo false positives from older builds.
    /// </summary>
    private static List<string> DiscoverNonGamingPathsToUnhide(string cli)
    {
        var hidden = ReadAlreadyHiddenDevicePaths(cli);
        return hidden
            .Where(p => !IsVirtualG920KeepVisible(p, p) && IsLikelyNonGamingHid(p, p))
            .ToList();
    }

    private static Dictionary<string, string> RunHidHideMultiCapture(
        string cli,
        IReadOnlyList<string> argSets,
        int timeoutMs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (argSets.Count == 0)
            return map;

        string? dir = null;
        try
        {
            dir = Path.Combine(Path.GetTempPath(), "g920-hidhide-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "multi.cmd");
            var lines = new List<string>
            {
                "@echo off",
                // Elevated/inherited CWD must not be the install folder (folder-in-use after exit).
                "cd /d \"%TEMP%\"",
            };
            var fileByArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < argSets.Count; i++)
            {
                var args = argSets[i];
                var file = Path.Combine(dir, $"out{i}.txt");
                fileByArgs[args] = file;
                lines.Add($"\"{cli}\" {args} > \"{file}\" 2>&1");
            }

            File.WriteAllText(script, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            var batchPsi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + script + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            InstallFolderGuard.ApplySafeWorkingDirectory(batchPsi);
            var proc = Process.Start(batchPsi);
            if (proc is null)
                return map;

            TrackHidHideChild(proc);
            WaitForHidHideChild(proc, timeoutMs);

            foreach (var (args, file) in fileByArgs)
            {
                try
                {
                    map[args] = File.Exists(file) ? File.ReadAllText(file) : "";
                }
                catch
                {
                    map[args] = "";
                }
            }
        }
        catch
        {
            // Fall back to sequential single captures.
            foreach (var args in argSets)
            {
                var (_, output) = RunHidHideCapture(cli, args);
                map[args] = output;
            }
        }
        finally
        {
            if (dir is not null)
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            }
        }

        return map;
    }

    private static HashSet<string> ReadAlreadyHiddenDevicePaths(string cli)
    {
        var (ok, output) = RunHidHideCapture(cli, "--dev-list");
        return ok ? ParseAlreadyHiddenDevicePaths(output) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ParseAlreadyHiddenDevicePaths(string? output)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
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

    private static bool IsLikelyNonGamingHid(string instancePath, string textBlob)
    {
        var blob = $"{instancePath} {textBlob}";
        // Controllers sometimes mention "audio" in a composite name - require clear peripherals only.
        ReadOnlySpan<string> skip =
        [
            "keyboard", "mouse", "touchpad", "touch screen", "digitizer",
            "headset", "headphone", "microphone", "webcam", "camera", "streamcam",
            "hid_device_system_keyboard", "hid_device_system_mouse",
            "led controller", "aura", "lighting", "rgb controller",
            "strix scope", "keychron", "consumer control", "system control",
            "vendor-defined device", // generic HID - not a pad/wheel by itself
        ];
        foreach (var token in skip)
        {
            if (blob.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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

    private static bool LooksLikeAccessDenied(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("0x0005", StringComparison.OrdinalIgnoreCase));

    private static (bool Ok, string Detail) RunHidHideCommands(string cli, IReadOnlyList<string> commands) =>
        RunHidHideCommands(cli, commands, elevateOnAnyFailure: false, timeoutMs: 12_000);

    private static (bool Ok, string Detail) RunHidHideCommands(
        string cli,
        IReadOnlyList<string> commands,
        bool elevateOnAnyFailure,
        int timeoutMs)
    {
        // One script / one process - sequential per-flag CLI calls made Stop/Exit feel stuck.
        if (commands.Count == 0)
            return (true, "ok");
        if (HidHideOpsCancelled)
            return (false, "cancelled");

        timeoutMs = Math.Clamp(timeoutMs, 2_000, 60_000);
        var (ok, detail) = RunHidHideScript(cli, commands, elevated: false, timeoutMs: timeoutMs);
        if (ok || HidHideOpsCancelled)
            return (ok, detail);

        if (elevateOnAnyFailure || LooksLikeAccessDenied(detail))
            return RunHidHideScript(cli, commands, elevated: true, timeoutMs: timeoutMs);

        return (false, detail);
    }

    private static (bool Ok, string Detail) RunHidHideScript(
        string cli,
        IReadOnlyList<string> commands,
        bool elevated,
        int timeoutMs)
    {
        if (HidHideOpsCancelled)
            return (false, "cancelled");

        string? script = null;
        string? logPath = null;
        try
        {
            var stamp = Guid.NewGuid().ToString("N");
            script = Path.Combine(Path.GetTempPath(), $"g920-hidhide-{stamp}.cmd");
            logPath = Path.Combine(Path.GetTempPath(), $"g920-hidhide-{stamp}.log");
            // Fail the script if any CLI call fails - previously cmd returned 0 from a subshell
            // and elevated restore falsely reported success via --inv-state alone.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("@echo off");
            // runas often ignores ProcessStartInfo.WorkingDirectory - force TEMP CWD in-script.
            sb.AppendLine("cd /d \"%TEMP%\"");
            sb.AppendLine("set ERR=0");
            foreach (var args in commands)
            {
                sb.Append('"').Append(cli).Append("\" ").Append(args)
                    .Append(" >> \"").Append(logPath).AppendLine("\" 2>&1");
                sb.AppendLine("if errorlevel 1 set ERR=1");
            }
            sb.AppendLine("exit /b %ERR%");
            File.WriteAllText(script, sb.ToString());

            Process? proc;
            if (elevated)
            {
                var elevatedPsi = new ProcessStartInfo
                {
                    FileName = script,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                InstallFolderGuard.ApplySafeWorkingDirectory(elevatedPsi);
                proc = Process.Start(elevatedPsi);
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"" + script + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                InstallFolderGuard.ApplySafeWorkingDirectory(psi);
                proc = Process.Start(psi);
            }

            if (proc is null)
                return (false, "failed to start HidHide script");

            TrackHidHideChild(proc);
            WaitForHidHideChild(proc, timeoutMs);
            if (HidHideOpsCancelled)
                return (false, "cancelled");

            var log = "";
            try
            {
                if (logPath is not null && File.Exists(logPath))
                    log = File.ReadAllText(logPath);
            }
            catch { /* ignore */ }

            if (!proc.HasExited)
                return (false, LooksLikeAccessDenied(log) ? log.Trim() : "script timed out");

            if (proc.ExitCode == 0 && !LooksLikeAccessDenied(log))
                return (true, elevated ? "configured (elevated)" : "ok");

            if (LooksLikeAccessDenied(log))
                return (false, log.Trim());

            return (false, string.IsNullOrWhiteSpace(log)
                ? "script exit " + proc.ExitCode
                : log.Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, elevated ? "UAC elevation cancelled" : "failed to start HidHide script");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (script is not null)
            {
                try { File.Delete(script); } catch { /* ignore */ }
            }
            if (logPath is not null)
            {
                try { File.Delete(logPath); } catch { /* ignore */ }
            }
        }
    }

    private static (bool Ok, string Output) RunHidHideCapture(string cli, string args)
    {
        try
        {
            if (HidHideOpsCancelled)
                return (false, "cancelled");

            var psi = new ProcessStartInfo
            {
                FileName = cli,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            InstallFolderGuard.ApplySafeWorkingDirectory(psi);
            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, "failed to start CLI");

            // Read after exit to avoid stdout pipe deadlocks on larger --dev-all dumps.
            if (!proc.WaitForExit(3_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "CLI timed out");
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
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
