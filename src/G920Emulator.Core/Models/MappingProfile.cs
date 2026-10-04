using System.Text.Json;
using System.Text.Json.Serialization;

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
    public ShifterMode ShifterMode { get; set; } = ShifterMode.ExclusiveHPattern;
    public double FfbGain { get; set; } = 1.0;
    public bool FfbInvert { get; set; }

    /// <summary>
    /// Virtual G920 DI button asserted for Gear R (1–19).
    /// Default 19 = official G920 + Driving Force Shifter (LGS). Use 12 for NFS Unbound.
    /// </summary>
    public int GearReverseOutputButton { get; set; } = 19;

    public List<Binding> Bindings { get; set; } = [];

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
        foreach (var binding in Bindings)
            binding.Normalize();

        // Legacy separate paddle rows → LB/RB (same DI bits on a real G920; L/R swapped).
        MergeSources(G920Control.PaddleRight, G920Control.ButtonLb);
        MergeSources(G920Control.PaddleLeft, G920Control.ButtonRb);
        Bindings.RemoveAll(b => b.Target is G920Control.PaddleLeft or G920Control.PaddleRight);

        // Ensure UI targets exist once (CreateDefault / older profiles).
        foreach (var target in G920ControlInfo.UiOrder)
            GetOrCreate(target);
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
    public string DeviceId { get; set; } = "";
    public string? Axis { get; set; }
    public int? Button { get; set; }
    public bool IsHat { get; set; }

    /// <summary>
    /// When an axis drives a button: measure deflection from center (0.5) instead of
    /// absolute high-side throw. Used for sticks / centered analog shifters.
    /// </summary>
    public bool AxisFromCenter { get; set; }
}
