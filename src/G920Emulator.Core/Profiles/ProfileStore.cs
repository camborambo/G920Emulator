using System.Text.Json;
using G920Emulator.Core.Models;

namespace G920Emulator.Core.Profiles;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string ProfilesDirectory { get; }
    public string SettingsPath { get; }

    /// <summary>
    /// True when profiles/settings live next to the exe; false when falling back to AppData
    /// (e.g. install directory is not writable).
    /// </summary>
    public bool UsesPortableStorage { get; }

    public ProfileStore()
    {
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "G920Emulator");

        var portableRoot = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        UsesPortableStorage = TryEnsureWritableProfilesDir(portableRoot);

        var root = UsesPortableStorage ? portableRoot : appDataRoot;
        ProfilesDirectory = Path.Combine(root, "profiles");
        SettingsPath = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(ProfilesDirectory);

        // Prefer local folder; pull older AppData / N4Sunbound saves in without overwriting.
        if (UsesPortableStorage)
            MigrateFolder(appDataRoot, root);
        MigrateFolder(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "N4Sunbound"), root);
    }

    private static bool TryEnsureWritableProfilesDir(string root)
    {
        try
        {
            var profiles = Path.Combine(root, "profiles");
            Directory.CreateDirectory(profiles);
            var probe = Path.Combine(profiles, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void MigrateFolder(string legacyRoot, string newRoot)
    {
        try
        {
            if (!Directory.Exists(legacyRoot))
                return;

            var legacyProfiles = Path.Combine(legacyRoot, "profiles");
            var newProfiles = Path.Combine(newRoot, "profiles");
            Directory.CreateDirectory(newProfiles);
            if (Directory.Exists(legacyProfiles))
            {
                foreach (var file in Directory.EnumerateFiles(legacyProfiles, "*.json"))
                {
                    var dest = Path.Combine(newProfiles, Path.GetFileName(file));
                    if (!File.Exists(dest))
                        File.Copy(file, dest);
                }
            }

            var legacySettings = Path.Combine(legacyRoot, "settings.json");
            var newSettings = Path.Combine(newRoot, "settings.json");
            if (File.Exists(legacySettings) && !File.Exists(newSettings))
                File.Copy(legacySettings, newSettings);
        }
        catch
        {
            // Best-effort migration only.
        }
    }

    public IReadOnlyList<string> ListProfiles()
    {
        return Directory.EnumerateFiles(ProfilesDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }

    public string GetPath(string profileName) =>
        Path.Combine(ProfilesDirectory, Sanitize(profileName) + ".json");

    public bool Exists(string profileName) => File.Exists(GetPath(profileName));

    public MappingProfile Load(string profileName) => MappingProfile.Load(GetPath(profileName));

    public void Save(MappingProfile profile, string profileName)
    {
        profile.Name = profileName.Trim();
        profile.Save(GetPath(profileName));
        var settings = LoadSettings();
        settings.LastProfileName = profile.Name;
        SaveSettings(settings);
    }

    public void Delete(string profileName)
    {
        var path = GetPath(profileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public MappingProfile LoadLastOrDefault()
    {
        var settings = LoadSettings();
        if (!string.IsNullOrWhiteSpace(settings.LastProfileName) && Exists(settings.LastProfileName))
            return Load(settings.LastProfileName);

        var first = ListProfiles().FirstOrDefault();
        if (first is not null)
            return Load(first);

        return MappingProfile.CreateDefault();
    }

    private static string Sanitize(string name)
    {
        var cleaned = string.Join("_", name.Trim().Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "profile" : cleaned;
    }
}

public sealed class AppSettings
{
    public string? LastProfileName { get; set; }

    /// <summary>Device instance IDs hidden from the Detected devices list until the next Refresh devices.</summary>
    public List<string> HiddenDeviceIds { get; set; } = [];
}
