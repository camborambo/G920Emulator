using System.Text.Json;
using System.Text.Json.Serialization;
using G920Emulator.Core.Ffb;

namespace G920Emulator.Core.Models;

public enum ShifterMode
{
    ExclusiveHPattern,
    Passthrough,
}

public sealed class MappingProfile
{
    public string Name { get; set; } = "My Rig → G920";
    public string? FfbSourceDeviceId { get; set; }

    /// <summary>
    /// Stable DirectInput ProductGuid for the FFB device. Used to reattach when
    /// <see cref="FfbSourceDeviceId"/> (instance GUID) changes after an update/replug.
    /// </summary>
    public string? FfbSourceProductId { get; set; }

    public ShifterMode ShifterMode { get; set; } = ShifterMode.ExclusiveHPattern;
    /// <summary>Linked force-feedback profile name (see <c>ffb-profiles\</c>). Default "Raw".</summary>
    public string? FfbProfileName { get; set; } = "Raw";

    /// <summary>Working copy of FFB settings (loaded from / saved to <see cref="FfbProfileName"/>).</summary>
    public double FfbGain { get; set; } = 1.0;
    public bool FfbInvert { get; set; }

    /// <summary>Per DirectInput effect-type gains (Constant, Spring, Damper, …).</summary>
    public FfbEffectGains FfbEffectGains { get; set; } = FfbEffectGains.CreateDefault();

    /// <summary>Optional output feel (smoothing / peaks). Defaults leave game mix unchanged.</summary>
    public FfbOutputFeel FfbOutputFeel { get; set; } = FfbOutputFeel.CreateDefault();

    public void ApplyFfbProfile(FfbProfile ffb)
    {
        ffb.Normalize();
        FfbProfileName = ffb.Name;
        FfbGain = ffb.FfbGain;
        FfbInvert = ffb.FfbInvert;
        FfbEffectGains = ffb.EffectGains;
        FfbOutputFeel = ffb.OutputFeel;
    }

    /// <summary>
    /// Virtual G920 DI button asserted for Gear R (1-19).
    /// Default 19 = official G920 + Driving Force Shifter (LGS). Use 12 for NFS Unbound.
    /// </summary>
    public int GearReverseOutputButton { get; set; } = 19;

    public List<Binding> Bindings { get; set; } = [];

    /// <summary>
    /// Per-slider snap values for FFB "Set as Default" binds (key = <see cref="G920ControlInfo.FfbSliderBind.Id"/>).
    /// </summary>
    public Dictionary<string, double> FfbBindDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MappingProfile CreateDefault() => new()
    {
        Name = "Default",
        FfbProfileName = "Raw",
        Bindings = G920ControlInfo.UiOrder.Select(t => new Binding { Target = t }).ToList(),
    };

    public static MappingProfile Load(string path)
    {
        var json = File.ReadAllText(path);
        var profile = JsonSerializer.Deserialize<MappingProfile>(json, JsonOptions) ?? CreateDefault();
        profile.NormalizeBindings();
        return profile;
    }

    public void Save(string path)
    {
        NormalizeBindings();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public Binding GetOrCreate(G920Control target)
    {
        var existing = Bindings.FirstOrDefault(b => b.Target == target);
        if (existing is not null)
        {
            existing.Normalize();
            return existing;
        }

        var created = new Binding { Target = target };
        Bindings.Add(created);
        return created;
    }

    public void NormalizeBindings()
    {
        GearReverseOutputButton = Math.Clamp(GearReverseOutputButton <= 0 ? 19 : GearReverseOutputButton, 1, 19);
        FfbGain = Math.Clamp(FfbGain, 0, 2);
        FfbEffectGains ??= FfbEffectGains.CreateDefault();
        FfbEffectGains.Clamp();
        FfbOutputFeel ??= FfbOutputFeel.CreateDefault();
        FfbOutputFeel.Clamp();
        foreach (var binding in Bindings)
            binding.Normalize();

        // Legacy separate paddle rows → LB/RB (same DI bits on a real G920).
        MergeSources(G920Control.PaddleLeft, G920Control.ButtonLb);
        MergeSources(G920Control.PaddleRight, G920Control.ButtonRb);
        Bindings.RemoveAll(b => b.Target is G920Control.PaddleLeft or G920Control.PaddleRight);

        // Ensure UI targets exist once (CreateDefault / older profiles).
        foreach (var target in G920ControlInfo.UiOrder)
            GetOrCreate(target);
        foreach (var target in G920ControlInfo.FfbNudgeControls)
            GetOrCreate(target);
        foreach (var target in G920ControlInfo.TelemetryArcadeControls)
            GetOrCreate(target);

        FfbBindDefaults ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        // Re-key with ordinal-ignore comparer after JSON deserialize.
        if (!ReferenceEquals(FfbBindDefaults.Comparer, StringComparer.OrdinalIgnoreCase))
            FfbBindDefaults = new Dictionary<string, double>(FfbBindDefaults, StringComparer.OrdinalIgnoreCase);
    }

    public double GetFfbBindDefault(string sliderId, double fallback)
    {
        FfbBindDefaults ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        return FfbBindDefaults.TryGetValue(sliderId, out var v) ? v : fallback;
    }

    public void SetFfbBindDefault(string sliderId, double value)
    {
        FfbBindDefaults ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        FfbBindDefaults[sliderId] = value;
    }

    private void MergeSources(G920Control from, G920Control into)
    {
        var src = Bindings.FirstOrDefault(b => b.Target == from);
        if (src is null) return;
        src.Normalize();
        if (src.Sources.Count == 0) return;

        var dest = GetOrCreate(into);
        foreach (var s in src.Sources)
        {
            if (!dest.Sources.Any(d => SameSourceRef(d, s)))
                dest.Sources.Add(CloneSource(s));
        }
    }

    private static bool SameSourceRef(SourceRef a, SourceRef b) =>
        string.Equals(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Axis, b.Axis, StringComparison.OrdinalIgnoreCase) &&
        a.Button == b.Button &&
        a.IsHat == b.IsHat &&
        a.AxisFromCenter == b.AxisFromCenter;

    private static SourceRef CloneSource(SourceRef s) => new()
    {
        DeviceId = s.DeviceId,
        ProductId = s.ProductId,
        Axis = s.Axis,
        Button = s.Button,
        IsHat = s.IsHat,
        AxisFromCenter = s.AxisFromCenter,
    };
}

public sealed class Binding
{
    public G920Control Target { get; set; }

    /// <summary>One or more physical inputs that drive this G920 control.</summary>
    public List<SourceRef> Sources { get; set; } = [];

    /// <summary>Legacy single-source field; migrated into <see cref="Sources"/> on load.</summary>
    public SourceRef? Source { get; set; }

    public double Deadzone { get; set; }
    public bool Invert { get; set; }
    public double Scale { get; set; } = 1.0;

    [JsonIgnore]
    public IReadOnlyList<SourceRef> EffectiveSources
    {
        get
        {
            Normalize();
            return Sources;
        }
    }

    public void Normalize()
    {
        Sources ??= [];
        if (Source is not null)
        {
            if (!Sources.Any(s => SameSource(s, Source)))
                Sources.Insert(0, Source);
            Source = null;
        }

        // Drop empty stubs.
        Sources.RemoveAll(s =>
            string.IsNullOrWhiteSpace(s.DeviceId) &&
            s.Axis is null &&
            s.Button is null &&
            !s.IsHat);
    }

    public void ClearSources()
    {
        Sources = [];
        Source = null;
    }

    private static bool SameSource(SourceRef a, SourceRef b) =>
        string.Equals(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Axis, b.Axis, StringComparison.OrdinalIgnoreCase) &&
        a.Button == b.Button &&
        a.IsHat == b.IsHat &&
        a.AxisFromCenter == b.AxisFromCenter;
}

public sealed class SourceRef
{
    /// <summary>DirectInput instance GUID (can change across replugs / Windows resets).</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>DirectInput product GUID - stable identity used to remap <see cref="DeviceId"/>.</summary>
    public string? ProductId { get; set; }

    public string? Axis { get; set; }
    public int? Button { get; set; }
    public bool IsHat { get; set; }

    /// <summary>
    /// When an axis drives a button: measure deflection from center (0.5) instead of
    /// absolute high-side throw. Used for sticks / centered analog shifters.
    /// </summary>
    public bool AxisFromCenter { get; set; }
}
