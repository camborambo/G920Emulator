using System.IO.Compression;
using System.Text;
using G920Emulator.Core.Profiles;

namespace G920Emulator.Core.Setup;

/// <summary>
/// Builds a zip of logs + environment summary for GitHub Issues / support.
/// </summary>
public static class DiagnosticsExporter
{
    public const string SupportEmail = "obert@stachenscale.com";
    public const string GitHubIssuesUrl = "https://github.com/camborambo/G920Emulator/issues";

    private static readonly string[] TempLogNames =
    [
        "g920emulator-winuhid-install.log",
        "g920emulator-testsigning.log",
        "g920ffb-effects.log",
        "g920-hidpp-ingress.log",
    ];

    /// <summary>
    /// Writes a diagnostics zip to <paramref name="destinationZipPath"/>.
    /// Returns the path written.
    /// </summary>
    public static string ExportToZip(
        string destinationZipPath,
        string appVersion,
        Func<(bool Installed, string Detail)>? probeWinUHid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);

        var staging = Path.Combine(Path.GetTempPath(), "g920emulator-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            WriteSummary(staging, appVersion, probeWinUHid);
            CopyTempLogs(staging);
            CopyProfilesAndSettings(staging);
            File.WriteAllText(
                Path.Combine(staging, "HOW-TO-SEND.txt"),
                "Email this zip to:\r\n" +
                "  " + SupportEmail + "\r\n\r\n" +
                "Include a short description of what went wrong, your wheel/pad, and the game if relevant.\r\n\r\n" +
                "You can also open a GitHub issue (optional):\r\n" +
                "  " + GitHubIssuesUrl + "\r\n",
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

    private static void WriteSummary(
        string staging,
        string appVersion,
        Func<(bool Installed, string Detail)>? probeWinUHid)
    {
        var sb = new StringBuilder();
        sb.AppendLine("G920 Emulator diagnostics");
        sb.AppendLine("Generated (local): " + DateTime.Now.ToString("o"));
        sb.AppendLine("App version: " + appVersion);
        sb.AppendLine("OS: " + Environment.OSVersion);
        sb.AppendLine("64-bit OS: " + Environment.Is64BitOperatingSystem);
        sb.AppendLine("64-bit process: " + Environment.Is64BitProcess);
        sb.AppendLine("User interactive: " + Environment.UserInteractive);
        sb.AppendLine("App directory: " + AppContext.BaseDirectory);
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
            sb.AppendLine("  Settings: " + profiles.SettingsPath);
            var names = profiles.ListProfiles();
            sb.AppendLine("  Profiles (" + names.Count + "): " + (names.Count == 0 ? "(none)" : string.Join(", ", names)));
            var settings = profiles.LoadSettings();
            sb.AppendLine("  Last profile: " + (settings.LastProfileName ?? "(none)"));
            sb.AppendLine("  Hidden devices: " + settings.HiddenDeviceIds.Count);
        }
        catch (Exception ex)
        {
            sb.AppendLine("Profiles: failed — " + ex.Message);
        }

        File.WriteAllText(Path.Combine(staging, "summary.txt"), sb.ToString(), Encoding.UTF8);
    }

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
                "Reproduce the issue (Install WinUHid, Start bridge, run the game), then export again.\r\n",
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
