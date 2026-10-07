using System.Text.Json;
using System.Text.Json.Serialization;

namespace G920Emulator.Core.Telemetry;

/// <summary>
/// Saveable simulation-tuning preset for SimHub UDP telemetry.
/// Host/port/rate stay in app settings - profiles are the dash/dynamics feel only.
/// </summary>
public sealed class TelemetryProfile
{
    public const string DefaultProfileName = "Default";

    public string Name { get; set; } = DefaultProfileName;
    public TelemetryTuning Tuning { get; set; } = TelemetryTuning.CreateDefault();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static TelemetryProfile CreateDefault() => new()
    {
        Name = DefaultProfileName,
        Tuning = TelemetryTuning.CreateDefault(),
    };

    public static TelemetryProfile FromTuning(string name, TelemetryTuning? tuning) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? DefaultProfileName : name.Trim(),
        Tuning = (tuning ?? TelemetryTuning.CreateDefault()).Clone(),
    };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Name))
            Name = DefaultProfileName;
        Tuning ??= TelemetryTuning.CreateDefault();
        Tuning.Clamp();
    }

    public static TelemetryProfile Load(string path)
    {
        var json = File.ReadAllText(path);
        var profile = JsonSerializer.Deserialize<TelemetryProfile>(json, JsonOptions) ?? CreateDefault();
        profile.Normalize();
        return profile;
    }

    public void Save(string path)
    {
        Normalize();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
