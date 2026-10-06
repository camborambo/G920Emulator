using System.Text.Json;
using System.Text.Json.Serialization;

namespace G920Emulator.Core.Ffb;

/// <summary>
/// Saveable force-feedback preset. Defaults are raw game mix (no feel shaping).
/// Kept separate from input mapping profiles so the same feel can be reused across games.
/// </summary>
public sealed class FfbProfile
{
    public string Name { get; set; } = "Raw";
    public double FfbGain { get; set; } = 1.0;
    public bool FfbInvert { get; set; }
    public FfbEffectGains EffectGains { get; set; } = FfbEffectGains.CreateDefault();
    public FfbOutputFeel OutputFeel { get; set; } = FfbOutputFeel.CreateDefault();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Exact game feedback — all feel options off, gains at 100%.</summary>
    public static FfbProfile CreateRaw() => new()
    {
        Name = "Raw",
        FfbGain = 1.0,
        FfbInvert = false,
        EffectGains = FfbEffectGains.CreateDefault(),
        OutputFeel = FfbOutputFeel.CreateDefault(),
    };

    public const string NfsUnboundHeatProfileName = "Need For Speed Unbound / Heat";

    /// <summary>
    /// Desktop-era known-good Unbound/Heat mix: weakened spring, boosted CF/damper,
    /// damper path scales, light torque shaping. CF polarity is corrected in
    /// <c>g920ffb</c> (DI→app); Invert Constant Force stays off.
    /// </summary>
    public static FfbProfile CreateNfsUnboundHeat() => new()
    {
        Name = NfsUnboundHeatProfileName,
        FfbGain = 1.0,
        FfbInvert = false,
        EffectGains = new FfbEffectGains
        {
            ConstantForce = 2.0,
            SpringForce = 0.4,
            DamperForce = 1.5,
        },
        OutputFeel = new FfbOutputFeel
        {
            InvertConstantForce = false,
            DamperVelocityScale = 2.0,
            DamperDeadbandScale = 1.0 / 3.0,
            Deadband = 0.004,
            MaxSlewPerSecond = 40,
            MaxSpikeStep = 1.0,
            MagnitudeEpsilon = 12,
        },
    };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Name))
            Name = "Raw";
        FfbGain = Math.Clamp(FfbGain, 0, 2);
        EffectGains ??= FfbEffectGains.CreateDefault();
        EffectGains.Clamp();
        OutputFeel ??= FfbOutputFeel.CreateDefault();
        OutputFeel.Clamp();
    }

    public static FfbProfile Load(string path)
    {
        var json = File.ReadAllText(path);
        var profile = JsonSerializer.Deserialize<FfbProfile>(json, JsonOptions) ?? CreateRaw();
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

    /// <summary>Copy FFB fields from a legacy mapping profile that stored them inline.</summary>
    public static FfbProfile FromMappingInline(
        string name,
        double gain,
        bool invert,
        FfbEffectGains? gains,
        FfbOutputFeel? feel) => new()
    {
        Name = name,
        FfbGain = gain,
        FfbInvert = invert,
        EffectGains = gains ?? FfbEffectGains.CreateDefault(),
        OutputFeel = feel ?? FfbOutputFeel.CreateDefault(),
    };
}
