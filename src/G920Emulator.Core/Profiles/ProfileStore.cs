using System.Text.Json;
using G920Emulator.Core.Ffb;
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
    public string FfbProfilesDirectory { get; }
    public string SettingsPath { get; }

    /// <summary>
    /// True when profiles/settings live next to the exe (opt-in via <c>portable.txt</c>).
    /// Default is AppData so unzipping an update cannot wipe user bindings.
    /// </summary>
    public bool UsesPortableStorage { get; }

    public ProfileStore()
    {
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "G920Emulator");

        var exeRoot = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Opt-in portable mode only — default AppData survives "unzip over install" updates.
        var portableMarker = Path.Combine(exeRoot, "portable.txt");
        UsesPortableStorage = File.Exists(portableMarker) && TryEnsureWritableProfilesDir(exeRoot);

        var root = UsesPortableStorage ? exeRoot : appDataRoot;
        ProfilesDirectory = Path.Combine(root, "profiles");
        FfbProfilesDirectory = Path.Combine(root, "ffb-profiles");
        SettingsPath = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(FfbProfilesDirectory);

        if (!UsesPortableStorage)
        {
            // One-time pull from older next-to-exe / N4Sunbound layouts.
            MigrateFolder(exeRoot, appDataRoot);
            MigrateFolder(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "N4Sunbound"), appDataRoot);
        }

        EnsureDefaultProfile();
        EnsureRawFfbProfile();
        EnsureNfsUnboundHeatFfbProfile();
        RemoveLegacySeededFfbProfiles();
        MigrateNfsUnboundHeatCfPolarity();
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

    /// <summary>Create an empty Default profile in AppData (or portable root) if none exists.</summary>
    private void EnsureDefaultProfile()
    {
        try
        {
            if (Exists("default") || Exists("Default"))
                return;

            var profile = MappingProfile.CreateDefault();
            profile.Name = "Default";
            profile.FfbProfileName = "Raw";
            profile.Save(GetPath("Default"));
        }
        catch
        {
            // Best-effort seed only.
        }
    }

    private void EnsureRawFfbProfile()
    {
        try
        {
            if (FfbExists("Raw"))
                return;
            FfbProfile.CreateRaw().Save(GetFfbPath("Raw"));
        }
        catch
        {
            // Best-effort seed only.
        }
    }

    /// <summary>
    /// Seed Desktop-era NFS Unbound/Heat mix once. Never overwrite a user-edited file.
    /// </summary>
    private void EnsureNfsUnboundHeatFfbProfile()
    {
        try
        {
            var name = FfbProfile.NfsUnboundHeatProfileName;
            if (FfbExists(name))
                return;
            FfbProfile.CreateNfsUnboundHeat().Save(GetFfbPath(name));
        }
        catch
        {
            // Best-effort seed only.
        }
    }

    /// <summary>
    /// Remove superseded seed files when they still match the original seeded values
    /// so a user's own profile with the same name is kept.
    /// </summary>
    private void RemoveLegacySeededFfbProfiles()
    {
        try
        {
            // Older short "NFS Unbound" seed (gains only).
            TryDeleteSeededFfbIfUnchanged("NFS Unbound", isLegacyGainsOnly: true);
            // Brief "Classic" name from an intermediate build.
            TryDeleteSeededFfbIfUnchanged("Classic", isLegacyGainsOnly: false);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    /// <summary>
    /// Old Unbound/Heat seed used Invert Constant Force to paper over a DI→app
    /// polarity bug. With the driver fix, that toggle would double-invert — clear
    /// it when the profile still matches the original seed.
    /// </summary>
    private void MigrateNfsUnboundHeatCfPolarity()
    {
        try
        {
            var name = FfbProfile.NfsUnboundHeatProfileName;
            if (!FfbExists(name))
                return;
            var loaded = LoadFfb(name);
            var g = loaded.EffectGains;
            var f = loaded.OutputFeel ?? FfbOutputFeel.CreateDefault();
            if (!f.InvertConstantForce)
                return;
            if (Math.Abs(g.ConstantForce - 2.0) >= 0.001 ||
                Math.Abs(g.SpringForce - 0.4) >= 0.001 ||
                Math.Abs(g.DamperForce - 1.5) >= 0.001)
                return;
            if (Math.Abs(f.DamperVelocityScale - 2.0) >= 0.001 ||
                Math.Abs(f.DamperDeadbandScale - (1.0 / 3.0)) >= 0.02)
                return;

            f.InvertConstantForce = false;
            loaded.OutputFeel = f;
            loaded.Save(GetFfbPath(name));
        }
        catch
        {
            // Best-effort migration only.
        }
    }

    private void TryDeleteSeededFfbIfUnchanged(string profileName, bool isLegacyGainsOnly)
    {
        var path = GetFfbPath(profileName);
        if (!File.Exists(path))
            return;
        var loaded = FfbProfile.Load(path);
        var g = loaded.EffectGains;
        if (Math.Abs(g.ConstantForce - 2.0) >= 0.001 ||
            Math.Abs(g.SpringForce - 0.4) >= 0.001 ||
            Math.Abs(g.DamperForce - 1.5) >= 0.001)
            return;

        if (!isLegacyGainsOnly)
        {
            var f = loaded.OutputFeel ?? FfbOutputFeel.CreateDefault();
            // Classic seed had InvertConstantForce on (pre DI→app fix) plus damper scales.
            if (Math.Abs(f.DamperVelocityScale - 2.0) >= 0.001 ||
                Math.Abs(f.DamperDeadbandScale - (1.0 / 3.0)) >= 0.02)
                return;
        }

        File.Delete(path);
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
                    // Never overwrite an existing AppData profile with a leftover install copy.
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

    public MappingProfile Load(string profileName)
    {
        var profile = MappingProfile.Load(GetPath(profileName));
        ApplyLinkedFfbProfile(profile);
        return profile;
    }

    public void Save(MappingProfile profile, string profileName)
    {
        profile.Name = profileName.Trim();
        if (string.IsNullOrWhiteSpace(profile.FfbProfileName))
            profile.FfbProfileName = "Raw";
        profile.Save(GetPath(profileName));
        var settings = LoadSettings();
        settings.LastProfileName = profile.Name;
        settings.LastFfbProfileName = profile.FfbProfileName;
        SaveSettings(settings);
    }

    public void Delete(string profileName)
    {
        var path = GetPath(profileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    public IReadOnlyList<string> ListFfbProfiles()
    {
        EnsureRawFfbProfile();
        EnsureNfsUnboundHeatFfbProfile();
        return Directory.EnumerateFiles(FfbProfilesDirectory, "*.json")
            .Select(path =>
            {
                try
                {
                    var named = FfbProfile.Load(path).Name;
                    if (!string.IsNullOrWhiteSpace(named))
                        return named.Trim();
                }
                catch
                {
                    // Fall back to file name.
                }
                return Path.GetFileNameWithoutExtension(path) ?? "profile";
            })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string GetFfbPath(string profileName) =>
        Path.Combine(FfbProfilesDirectory, Sanitize(profileName) + ".json");

    public bool FfbExists(string profileName) => File.Exists(GetFfbPath(profileName));

    public FfbProfile LoadFfb(string profileName) => FfbProfile.Load(GetFfbPath(profileName));

    public void SaveFfb(FfbProfile profile, string profileName)
    {
        profile.Name = profileName.Trim();
        profile.Save(GetFfbPath(profileName));
        var settings = LoadSettings();
        settings.LastFfbProfileName = profile.Name;
        SaveSettings(settings);
    }

    public void DeleteFfb(string profileName)
    {
        if (profileName.Equals("Raw", StringComparison.OrdinalIgnoreCase))
            return; // always keep the raw preset
        var path = GetFfbPath(profileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>
    /// Load the linked FFB profile into the mapping profile's working FFB fields.
    /// Migrates legacy inline FFB settings into a new FFB profile when needed.
    /// </summary>
    public void ApplyLinkedFfbProfile(MappingProfile mapping)
    {
        mapping.NormalizeBindings();

        if (string.IsNullOrWhiteSpace(mapping.FfbProfileName))
        {
            // Legacy: FFB lived only on the mapping profile. Keep Raw for defaults;
            // otherwise snapshot into an FFB profile named after the mapping.
            if (IsDefaultFfbInline(mapping))
            {
                mapping.FfbProfileName = "Raw";
            }
            else
            {
                var name = string.IsNullOrWhiteSpace(mapping.Name) ? "Custom" : mapping.Name + " FFB";
                if (!FfbExists(name))
                {
                    var migrated = FfbProfile.FromMappingInline(
                        name, mapping.FfbGain, mapping.FfbInvert,
                        mapping.FfbEffectGains, mapping.FfbOutputFeel);
                    SaveFfb(migrated, name);
                }
                mapping.FfbProfileName = name;
            }
        }

        if (!FfbExists(mapping.FfbProfileName))
            mapping.FfbProfileName = "Raw";

        var ffb = LoadFfb(mapping.FfbProfileName);
        mapping.ApplyFfbProfile(ffb);
    }

    private static bool IsDefaultFfbInline(MappingProfile m)
    {
        if (Math.Abs(m.FfbGain - 1.0) > 0.001 || m.FfbInvert)
            return false;
        var g = m.FfbEffectGains ?? FfbEffectGains.CreateDefault();
        var f = m.FfbOutputFeel ?? FfbOutputFeel.CreateDefault();
        return Math.Abs(g.ConstantForce - 1) < 0.001 &&
               Math.Abs(g.SpringForce - 1) < 0.001 &&
               Math.Abs(g.DamperForce - 1) < 0.001 &&
               Math.Abs(g.FrictionForce - 1) < 0.001 &&
               Math.Abs(g.InertiaForce - 1) < 0.001 &&
               Math.Abs(g.Periodic - 1) < 0.001 &&
               Math.Abs(g.RampForce - 1) < 0.001 &&
               f.SmoothingMs <= 0.001 &&
               f.PeakSoftStart >= 0.999 &&
               f.SoftStartMs <= 0.001 &&
               f.Deadband <= 0.0005 &&
               f.MaxSlewPerSecond <= 0.5 &&
               f.MaxSpikeStep >= 0.999 &&
               f.MagnitudeEpsilon <= 0.5 &&
               !f.ForceCenterSpring;
    }

    public FfbProfile CaptureFfbFromMapping(MappingProfile mapping)
    {
        var name = string.IsNullOrWhiteSpace(mapping.FfbProfileName) ? "Raw" : mapping.FfbProfileName;
        return FfbProfile.FromMappingInline(
            name, mapping.FfbGain, mapping.FfbInvert,
            mapping.FfbEffectGains, mapping.FfbOutputFeel);
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

        var created = MappingProfile.CreateDefault();
        created.FfbProfileName = "Raw";
        ApplyLinkedFfbProfile(created);
        return created;
    }

    private static string Sanitize(string name)
    {
        // Keep a readable on-disk name for "Unbound / Heat" style titles.
        var cleaned = name.Trim()
            .Replace(" / ", " - ", StringComparison.Ordinal)
            .Replace('/', '-')
            .Replace('\\', '-');
        cleaned = string.Join("_", cleaned.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "profile" : cleaned;
    }
}

public sealed class AppSettings
{
    public string? LastProfileName { get; set; }
    public string? LastFfbProfileName { get; set; }

    /// <summary>Device instance IDs hidden from the Detected devices list until the next Refresh devices.</summary>
    public List<string> HiddenDeviceIds { get; set; } = [];

    /// <summary>When true, minimizing hides the window in the notification area. Close still quits.</summary>
    public bool MinimizeToSystemTray { get; set; }
}
