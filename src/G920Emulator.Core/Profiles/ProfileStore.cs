using System.Text.Json;
using G920Emulator.Core.Ffb;
using G920Emulator.Core.Models;
using G920Emulator.Core.Telemetry;

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
    public string TelemetryProfilesDirectory { get; }
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

        // Opt-in portable mode only - default AppData survives "unzip over install" updates.
        var portableMarker = Path.Combine(exeRoot, "portable.txt");
        UsesPortableStorage = File.Exists(portableMarker) && TryEnsureWritableProfilesDir(exeRoot);

        var root = UsesPortableStorage ? exeRoot : appDataRoot;
        ProfilesDirectory = Path.Combine(root, "profiles");
        FfbProfilesDirectory = Path.Combine(root, "ffb-profiles");
        TelemetryProfilesDirectory = Path.Combine(root, "telemetry-profiles");
        SettingsPath = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(FfbProfilesDirectory);
        Directory.CreateDirectory(TelemetryProfilesDirectory);

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
        EnsureDefaultTelemetryProfile();
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
    /// polarity bug. With the driver fix, that toggle would double-invert - clear
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

            CopyJsonFolder(Path.Combine(legacyRoot, "profiles"), Path.Combine(newRoot, "profiles"));
            CopyJsonFolder(Path.Combine(legacyRoot, "ffb-profiles"), Path.Combine(newRoot, "ffb-profiles"));
            CopyJsonFolder(Path.Combine(legacyRoot, "telemetry-profiles"), Path.Combine(newRoot, "telemetry-profiles"));

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

    private static void CopyJsonFolder(string legacyDir, string newDir)
    {
        if (!Directory.Exists(legacyDir))
            return;
        Directory.CreateDirectory(newDir);
        foreach (var file in Directory.EnumerateFiles(legacyDir, "*.json"))
        {
            // Never overwrite an existing AppData profile with a leftover install copy.
            var dest = Path.Combine(newDir, Path.GetFileName(file));
            if (!File.Exists(dest))
                File.Copy(file, dest);
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

    private void EnsureDefaultTelemetryProfile()
    {
        try
        {
            if (Directory.EnumerateFiles(TelemetryProfilesDirectory, "*.json").Any())
                return;

            // Seed from current settings so existing tuning isn't lost when profiles are introduced.
            AppSettings settings;
            try { settings = LoadSettings(); }
            catch { settings = new AppSettings(); }

            settings.NormalizeTelemetryTuning();
            var profile = TelemetryProfile.FromTuning(TelemetryProfile.DefaultProfileName, settings.ToTelemetryTuning());
            profile.Save(GetTelemetryPath(profile.Name));
            settings.LastTelemetryProfileName = profile.Name;
            SaveSettings(settings);
        }
        catch
        {
            // Best-effort seed only.
        }
    }

    public IReadOnlyList<string> ListTelemetryProfiles()
    {
        EnsureDefaultTelemetryProfile();
        return Directory.EnumerateFiles(TelemetryProfilesDirectory, "*.json")
            .Select(path =>
            {
                try
                {
                    var named = TelemetryProfile.Load(path).Name;
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

    public string GetTelemetryPath(string profileName) =>
        Path.Combine(TelemetryProfilesDirectory, Sanitize(profileName) + ".json");

    public bool TelemetryExists(string profileName) => File.Exists(GetTelemetryPath(profileName));

    public TelemetryProfile LoadTelemetry(string profileName) =>
        TelemetryProfile.Load(GetTelemetryPath(profileName));

    public void SaveTelemetry(TelemetryProfile profile, string profileName)
    {
        profile.Name = profileName.Trim();
        profile.Save(GetTelemetryPath(profileName));
        var settings = LoadSettings();
        settings.LastTelemetryProfileName = profile.Name;
        SaveSettings(settings);
    }

    public void DeleteTelemetry(string profileName)
    {
        if (profileName.Equals(TelemetryProfile.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            return; // always keep the Default preset
        var path = GetTelemetryPath(profileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    public TelemetryProfile CaptureTelemetryFromSettings(AppSettings settings)
    {
        settings.NormalizeTelemetryTuning();
        var name = string.IsNullOrWhiteSpace(settings.LastTelemetryProfileName)
            ? TelemetryProfile.DefaultProfileName
            : settings.LastTelemetryProfileName.Trim();
        return TelemetryProfile.FromTuning(name, settings.ToTelemetryTuning());
    }

    /// <summary>Copy a telemetry profile's tuning into app settings (host/port/rate unchanged).</summary>
    public void ApplyTelemetryProfileToSettings(AppSettings settings, TelemetryProfile profile)
    {
        profile.Normalize();
        settings.ApplyTelemetryTuning(profile.Tuning);
        settings.LastTelemetryProfileName = profile.Name;
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
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                   ?? new AppSettings();
            // Older builds only had Unload (= apply on Start + restore on Stop).
            if (settings.UnloadHidHideConfigWhenStopped &&
                !json.Contains("AutoApplyHidHideConfigOnStart", StringComparison.Ordinal))
            {
                settings.AutoApplyHidHideConfigOnStart = true;
                SaveSettings(settings);
            }
            settings.NormalizeTelemetryTuning();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        settings.NormalizeTelemetryTuning();
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
    public string? LastTelemetryProfileName { get; set; }

    /// <summary>Device instance IDs hidden from the Detected devices list until the next Refresh devices.</summary>
    public List<string> HiddenDeviceIds { get; set; } = [];

    /// <summary>When true, minimizing hides the window in the notification area. Close still quits.</summary>
    public bool MinimizeToSystemTray { get; set; }

    /// <summary>Topmost in-game FFB Debug Overlay (live G920 inputs + FFB diagnostics).</summary>
    public bool DebugOverlay { get; set; }
    public double? DebugOverlayLeft { get; set; }
    public double? DebugOverlayTop { get; set; }

    /// <summary>Topmost in-game Telemetry Debug Overlay (live SimHub UDP packet).</summary>
    public bool TelemetryDebugOverlay { get; set; }
    public double? TelemetryDebugOverlayLeft { get; set; }
    public double? TelemetryDebugOverlayTop { get; set; }

    /// <summary>Brief top-center HUD when hardware FFB effect-gain binds fire.</summary>
    public bool EffectChangesOverlay { get; set; } = true;

    /// <summary>Ask GitHub at launch whether a newer published release exists.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// When true, Start whitelists the emulator and hides all other controllers/wheels
    /// (virtual G920 stays visible). Off = leave the user's HidHide config alone.
    /// </summary>
    public bool AutoApplyHidHideConfigOnStart { get; set; }

    /// <summary>
    /// When true (and auto-apply runs), Start snapshots HidHide first and Stop restores it.
    /// </summary>
    public bool UnloadHidHideConfigWhenStopped { get; set; }

    /// <summary>
    /// UI unit for telemetry speed tuning/display: <c>mph</c> (default) or <c>kmh</c>.
    /// SimHub packets always use km/h regardless.
    /// </summary>
    public string TelemetrySpeedUnit { get; set; } = "mph";

    /// <summary>Release tag the user chose Later for (e.g. v0.2.6). A newer tag notifies again.</summary>
    public string? DismissedUpdateTag { get; set; }

    /// <summary>Send synthesized telemetry to SimHub (UDP External Sim) for games with no native feed.</summary>
    public bool TelemetryEnabled { get; set; }

    public string TelemetryHost { get; set; } = SimHubPacket.DefaultHost;
    public int TelemetryPort { get; set; } = SimHubPacket.DefaultPort;
    public int TelemetrySendHz { get; set; } = SimHubPacket.DefaultSendHz;

    /// <summary>Simulated speed floor when moving (km/h). Stopped still reports 0.</summary>
    public float TelemetrySpeedMinKmh { get; set; } = TelemetryTuning.DefaultSpeedMinKmh;

    /// <summary>Simulated speed ceiling (km/h).</summary>
    public float TelemetrySpeedMaxKmh { get; set; } = TelemetryTuning.DefaultSpeedMaxKmh;

    /// <summary>Idle / minimum simulated RPM.</summary>
    public float TelemetryRpmMin { get; set; } = TelemetryTuning.DefaultRpmMin;

    /// <summary>Engine max / gauge ceiling (SimHub EngineMaxRpm).</summary>
    public float TelemetryRpmMax { get; set; } = TelemetryTuning.DefaultRpmMax;

    /// <summary>Absolute redline / shift point (SimHub EngineShiftRpm). Not a percent.</summary>
    public float TelemetryRpmRedline { get; set; } = TelemetryTuning.DefaultRpmRedline;

    /// <summary>Scale for SurfaceRumble (1 = 100%).</summary>
    public float TelemetrySurfaceRumbleScale { get; set; } = 1f;

    /// <summary>Scale for Impact (1 = 100%).</summary>
    public float TelemetryImpactScale { get; set; } = 1f;

    /// <summary>Scale for RoadLoad (1 = 100%).</summary>
    public float TelemetryRoadLoadScale { get; set; } = 1f;

    /// <summary>Scale for ShakeIt Engine vibrations force (1 = 100%). RPM is always sent.</summary>
    public float TelemetryEngineVibrationScale { get; set; } = 1f;

    /// <summary>Hard-cut rev-limiter strength when pinned at gear top (0 = off, 1 = 100%).</summary>
    public float TelemetryRpmBounceAmount { get; set; } = TelemetryTuning.DefaultRpmBounceAmount;

    /// <summary>Hard-cut rev-limiter cycle rate (Hz).</summary>
    public float TelemetryRpmBounceHz { get; set; } = TelemetryTuning.DefaultRpmBounceHz;

    /// <summary>Simulated full-throttle accel (km/h per second).</summary>
    public float TelemetryAccelKmhPerSec { get; set; } = TelemetryTuning.DefaultAccelKmhPerSec;

    /// <summary>Simulated full-brake decel (km/h per second).</summary>
    public float TelemetryBrakeKmhPerSec { get; set; } = TelemetryTuning.DefaultBrakeKmhPerSec;

    /// <summary>Simulated coast / engine-brake (km/h per second).</summary>
    public float TelemetryCoastKmhPerSec { get; set; } = TelemetryTuning.DefaultCoastKmhPerSec;

    /// <summary>High-speed aero drag scale (1 = 100%).</summary>
    public float TelemetryAeroDragScale { get; set; } = TelemetryTuning.DefaultAeroDragScale;

    /// <summary>Gear pull strength on throttle accel (1 = 100%).</summary>
    public float TelemetryGearPullScale { get; set; } = TelemetryTuning.DefaultGearPullScale;

    /// <summary>How fast speed tapers toward a lower gear's max when overspeeding (km/h per second).</summary>
    public float TelemetryGearSettleKmhPerSec { get; set; } = TelemetryTuning.DefaultGearSettleKmhPerSec;

    /// <summary>FFB impact / heavy CF speed dump (1 = 100%).</summary>
    public float TelemetryCrashDumpScale { get; set; } = TelemetryTuning.DefaultCrashDumpScale;

    /// <summary>Handbrake hold speed dump (km/h per second). Button bind lives on the input profile.</summary>
    public float TelemetryHandbrakeKmhPerSec { get; set; } = TelemetryTuning.DefaultHandbrakeKmhPerSec;

    /// <summary>NOS / turbo hold boost (km/h per second). Button bind lives on the input profile.</summary>
    public float TelemetryNosBoostKmhPerSec { get; set; } = TelemetryTuning.DefaultNosBoostKmhPerSec;

    public float TelemetryGear1MaxKmh { get; set; } = TelemetryTuning.DefaultGear1MaxKmh;
    public float TelemetryGear2MaxKmh { get; set; } = TelemetryTuning.DefaultGear2MaxKmh;
    public float TelemetryGear3MaxKmh { get; set; } = TelemetryTuning.DefaultGear3MaxKmh;
    public float TelemetryGear4MaxKmh { get; set; } = TelemetryTuning.DefaultGear4MaxKmh;
    public float TelemetryGear5MaxKmh { get; set; } = TelemetryTuning.DefaultGear5MaxKmh;
    public float TelemetryGear6MaxKmh { get; set; } = TelemetryTuning.DefaultGear6MaxKmh;

    /// <summary>Absolute gearbox ratios (Blocklayer). 0 = derive from max speeds.</summary>
    public float TelemetryGear1Ratio { get; set; }
    public float TelemetryGear2Ratio { get; set; }
    public float TelemetryGear3Ratio { get; set; }
    public float TelemetryGear4Ratio { get; set; }
    public float TelemetryGear5Ratio { get; set; }
    public float TelemetryGear6Ratio { get; set; }

    /// <summary>Differential / final-drive ratio (Blocklayer Diff Ratio).</summary>
    public float TelemetryDiffRatio { get; set; } = TelemetryTuning.DefaultDiffRatio;

    /// <summary>Tire diameter in inches (Blocklayer).</summary>
    public float TelemetryTireDiameterInches { get; set; } = TelemetryTuning.DefaultTireDiameterInches;

    public TelemetryTuning ToTelemetryTuning()
    {
        var tuning = new TelemetryTuning
        {
            SpeedMinKmh = TelemetrySpeedMinKmh,
            SpeedMaxKmh = TelemetrySpeedMaxKmh,
            RpmMin = TelemetryRpmMin,
            RpmMax = TelemetryRpmMax,
            RpmRedline = TelemetryRpmRedline,
            SurfaceRumbleScale = TelemetrySurfaceRumbleScale,
            ImpactScale = TelemetryImpactScale,
            RoadLoadScale = TelemetryRoadLoadScale,
            EngineVibrationScale = TelemetryEngineVibrationScale,
            RpmBounceAmount = TelemetryRpmBounceAmount,
            RpmBounceHz = TelemetryRpmBounceHz,
            AccelKmhPerSec = TelemetryAccelKmhPerSec,
            BrakeKmhPerSec = TelemetryBrakeKmhPerSec,
            CoastKmhPerSec = TelemetryCoastKmhPerSec,
            AeroDragScale = TelemetryAeroDragScale,
            GearPullScale = TelemetryGearPullScale,
            GearSettleKmhPerSec = TelemetryGearSettleKmhPerSec,
            CrashDumpScale = TelemetryCrashDumpScale,
            HandbrakeKmhPerSec = TelemetryHandbrakeKmhPerSec,
            NosBoostKmhPerSec = TelemetryNosBoostKmhPerSec,
            Gear1Ratio = TelemetryGear1Ratio,
            Gear2Ratio = TelemetryGear2Ratio,
            Gear3Ratio = TelemetryGear3Ratio,
            Gear4Ratio = TelemetryGear4Ratio,
            Gear5Ratio = TelemetryGear5Ratio,
            Gear6Ratio = TelemetryGear6Ratio,
            Gear1MaxKmh = TelemetryGear1MaxKmh,
            Gear2MaxKmh = TelemetryGear2MaxKmh,
            Gear3MaxKmh = TelemetryGear3MaxKmh,
            Gear4MaxKmh = TelemetryGear4MaxKmh,
            Gear5MaxKmh = TelemetryGear5MaxKmh,
            Gear6MaxKmh = TelemetryGear6MaxKmh,
            DiffRatio = TelemetryDiffRatio,
            TireDiameterInches = TelemetryTireDiameterInches,
        };
        tuning.Clamp();
        return tuning;
    }

    public void ApplyTelemetryTuning(TelemetryTuning tuning)
    {
        tuning ??= TelemetryTuning.CreateDefault();
        tuning.Clamp();
        TelemetrySpeedMinKmh = tuning.SpeedMinKmh;
        TelemetrySpeedMaxKmh = tuning.SpeedMaxKmh;
        TelemetryRpmMin = tuning.RpmMin;
        TelemetryRpmMax = tuning.RpmMax;
        TelemetryRpmRedline = tuning.RpmRedline;
        TelemetrySurfaceRumbleScale = tuning.SurfaceRumbleScale;
        TelemetryImpactScale = tuning.ImpactScale;
        TelemetryRoadLoadScale = tuning.RoadLoadScale;
        TelemetryEngineVibrationScale = tuning.EngineVibrationScale;
        TelemetryRpmBounceAmount = tuning.RpmBounceAmount;
        TelemetryRpmBounceHz = tuning.RpmBounceHz;
        TelemetryAccelKmhPerSec = tuning.AccelKmhPerSec;
        TelemetryBrakeKmhPerSec = tuning.BrakeKmhPerSec;
        TelemetryCoastKmhPerSec = tuning.CoastKmhPerSec;
        TelemetryAeroDragScale = tuning.AeroDragScale;
        TelemetryGearPullScale = tuning.GearPullScale;
        TelemetryGearSettleKmhPerSec = tuning.GearSettleKmhPerSec;
        TelemetryCrashDumpScale = tuning.CrashDumpScale;
        TelemetryHandbrakeKmhPerSec = tuning.HandbrakeKmhPerSec;
        TelemetryNosBoostKmhPerSec = tuning.NosBoostKmhPerSec;
        TelemetryGear1Ratio = tuning.Gear1Ratio;
        TelemetryGear2Ratio = tuning.Gear2Ratio;
        TelemetryGear3Ratio = tuning.Gear3Ratio;
        TelemetryGear4Ratio = tuning.Gear4Ratio;
        TelemetryGear5Ratio = tuning.Gear5Ratio;
        TelemetryGear6Ratio = tuning.Gear6Ratio;
        TelemetryGear1MaxKmh = tuning.Gear1MaxKmh;
        TelemetryGear2MaxKmh = tuning.Gear2MaxKmh;
        TelemetryGear3MaxKmh = tuning.Gear3MaxKmh;
        TelemetryGear4MaxKmh = tuning.Gear4MaxKmh;
        TelemetryGear5MaxKmh = tuning.Gear5MaxKmh;
        TelemetryGear6MaxKmh = tuning.Gear6MaxKmh;
        TelemetryDiffRatio = tuning.DiffRatio;
        TelemetryTireDiameterInches = tuning.TireDiameterInches;
    }

    public void NormalizeTelemetryTuning()
    {
        ApplyTelemetryTuning(ToTelemetryTuning());
        TelemetrySendHz = SimHubPacket.ClampSendHz(TelemetrySendHz);
        if (string.IsNullOrWhiteSpace(LastTelemetryProfileName))
            LastTelemetryProfileName = TelemetryProfile.DefaultProfileName;
    }
}

public sealed class TelemetrySettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = SimHubPacket.DefaultHost;
    public int Port { get; set; } = SimHubPacket.DefaultPort;
    public int SendHz { get; set; } = SimHubPacket.DefaultSendHz;
    public TelemetryTuning Tuning { get; set; } = TelemetryTuning.CreateDefault();
}
