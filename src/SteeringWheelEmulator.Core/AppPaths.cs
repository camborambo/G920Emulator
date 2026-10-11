namespace SteeringWheelEmulator.Core;

/// <summary>
/// Product brand and user/system data folder names.
/// C# namespaces are <c>SteeringWheelEmulator.*</c>; on-disk product token is <see cref="FolderName"/>.
/// </summary>
public static class AppPaths
{
    public const string ProductName = "Steering Wheel Emulator";
    public const string FolderName = "SteeringWheelEmulator";
    public const string LegacyFolderName = "G920Emulator";

    public const string GitHubOwner = "camborambo";
    public const string GitHubRepo = "SteeringWheelEmulator";
    public static string GitHubRepoUrl => $"https://github.com/{GitHubOwner}/{GitHubRepo}";
    public static string GitHubIssuesUrl => $"{GitHubRepoUrl}/issues";
    public static string GitHubReleasesLatestUrl => $"{GitHubRepoUrl}/releases/latest";
    public static string GitHubApiLatestReleaseUrl =>
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    public static string AppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    public static string LegacyAppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolderName);

    public static string ProgramDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FolderName);

    public static string LegacyProgramDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), LegacyFolderName);

    public static string LocalAppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    public static string LegacyLocalAppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyFolderName);

    /// <summary>
    /// Move legacy AppData + ProgramData (+ LocalAppData scale bridge) into the new product folders,
    /// then delete the old roots when safe. Safe to call every launch; no-ops when nothing to migrate.
    /// </summary>
    public static string MigrateLegacyDataRoots()
    {
        var notes = new List<string>();
        MigrateDirectoryTree(LegacyAppDataRoot, AppDataRoot, notes, "AppData");
        MigrateDirectoryTree(LegacyProgramDataRoot, ProgramDataRoot, notes, "ProgramData");
        MigrateDirectoryTree(LegacyLocalAppDataRoot, LocalAppDataRoot, notes, "LocalAppData");
        return notes.Count == 0 ? "" : string.Join("; ", notes);
    }

    private static void MigrateDirectoryTree(string legacyRoot, string newRoot, List<string> notes, string label)
    {
        try
        {
            if (!Directory.Exists(legacyRoot))
                return;

            Directory.CreateDirectory(newRoot);
            var moved = 0;
            var skipped = 0;
            foreach (var src in Directory.EnumerateFileSystemEntries(legacyRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(legacyRoot, src);
                var dest = Path.Combine(newRoot, rel);
                try
                {
                    if (Directory.Exists(src) && !File.Exists(src))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (File.Exists(dest))
                    {
                        skipped++;
                        continue;
                    }

                    File.Move(src, dest);
                    moved++;
                }
                catch
                {
                    skipped++;
                }
            }

            TryDeleteTree(legacyRoot);
            notes.Add(Directory.Exists(legacyRoot)
                ? $"{label}: migrated {moved} file(s), {skipped} skipped; old folder still present (locked?)"
                : $"{label}: migrated {moved} file(s), {skipped} skipped; removed legacy folder");
        }
        catch (Exception ex)
        {
            notes.Add($"{label}: migrate failed — {ex.Message}");
        }
    }

    private static void TryDeleteTree(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return;
            // Remove empty dirs bottom-up; leave files that failed to move.
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { /* ignore */ }
            }

            if (!Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch { /* ignore */ }
    }

    public static bool LooksLikeProductDataPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        return path.Contains(FolderName, StringComparison.OrdinalIgnoreCase) ||
               path.Contains(LegacyFolderName, StringComparison.OrdinalIgnoreCase);
    }
}
