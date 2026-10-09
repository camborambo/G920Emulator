using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using G920Emulator.Core.Ffb;

namespace G920Emulator.Core.Models;

/// <summary>Result of loading a mapping profile, including recoverable corruption details.</summary>
public sealed class ProfileLoadResult
{
    public required MappingProfile Profile { get; init; }

    /// <summary>Raw target names removed because they are not valid <see cref="G920Control"/> values for this build.</summary>
    public IReadOnlyList<string> SkippedTargets { get; init; } = [];

    /// <summary>Set when the file could not be parsed at all and a fallback profile was used.</summary>
    public string? FatalError { get; init; }

    public bool HasIssues => SkippedTargets.Count > 0 || !string.IsNullOrEmpty(FatalError);

    public string FormatUserMessage(string profileLabel)
    {
        var label = string.IsNullOrWhiteSpace(profileLabel) ? Profile.Name : profileLabel.Trim();
        if (!string.IsNullOrEmpty(FatalError))
        {
            return
                $"Profile \"{label}\" could not be read and was not loaded.\n\n" +
                $"{FatalError}\n\n" +
                "A default profile is active. Fix or replace the JSON, or create a new profile and rebind your controls.";
        }

        var targets = string.Join("\n", SkippedTargets.Select(t => "• " + t));
        return
            $"Profile \"{label}\" had invalid or unsupported binding targets for this version of G920 Emulator:\n\n" +
            $"{targets}\n\n" +
            "Those mappings were cleared so the app can start. Rebind the affected controls (for example Handbrake / NOS under Telemetry arcade inputs), then Save the profile.";
    }
}

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
    /// User-created named mappings after the standard G920 layout (Toggle + optional Bind FN).
    /// </summary>
    public List<CustomBinding> CustomBindings { get; set; } = [];

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

    public static MappingProfile Load(string path) => LoadWithDiagnostics(path).Profile;

    /// <summary>
    /// Loads a profile JSON, skipping bindings/custom bindings whose <c>target</c> is not a known
    /// <see cref="G920Control"/> (e.g. saved by a newer app build). Never throws for unknown enums.
    /// </summary>
    public static ProfileLoadResult LoadWithDiagnostics(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            var fallback = CreateDefault();
            fallback.Name = Path.GetFileNameWithoutExtension(path);
            return new ProfileLoadResult
            {
                Profile = fallback,
                FatalError = ex.Message,
            };
        }

        var skipped = StripUnknownControlTargets(ref json);
        try
        {
            var profile = JsonSerializer.Deserialize<MappingProfile>(json, JsonOptions) ?? CreateDefault();
            profile.NormalizeBindings();
            return new ProfileLoadResult
            {
                Profile = profile,
                SkippedTargets = skipped,
            };
        }
        catch (Exception ex)
        {
            var fallback = CreateDefault();
            fallback.Name = Path.GetFileNameWithoutExtension(path);
            return new ProfileLoadResult
            {
                Profile = fallback,
                SkippedTargets = skipped,
                FatalError = ex.Message,
            };
        }
    }

    /// <summary>
    /// Removes binding objects whose camelCase <c>target</c> string is not a known enum member,
    /// so <see cref="JsonStringEnumConverter"/> cannot crash the process.
    /// </summary>
    private static List<string> StripUnknownControlTargets(ref string json)
    {
        var skipped = new List<string>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            return skipped;
        }

        if (root is not JsonObject obj)
            return skipped;

        StripUnknownFromArray(obj["bindings"] as JsonArray, skipped);
        StripUnknownFromArray(obj["customBindings"] as JsonArray, skipped);

        // Preserve camelCase property names for the normal deserialize path.
        json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        return skipped;
    }

    private static void StripUnknownFromArray(JsonArray? array, List<string> skipped)
    {
        if (array is null) return;
        for (var i = array.Count - 1; i >= 0; i--)
        {
            if (array[i] is not JsonObject binding)
                continue;
            var targetNode = binding["target"];
            if (targetNode is null)
                continue;
            var raw = targetNode.GetValueKind() == JsonValueKind.String
                ? targetNode.GetValue<string>()
                : targetNode.ToString();
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (Enum.TryParse<G920Control>(raw, ignoreCase: true, out _))
                continue;
            if (!skipped.Contains(raw, StringComparer.OrdinalIgnoreCase))
                skipped.Add(raw);
            array.RemoveAt(i);
        }
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
        {
            binding.Normalize();
            foreach (var source in binding.Sources)
                source.AxisFromCenter = false;
        }

        CustomBindings ??= [];
        foreach (var custom in CustomBindings)
        {
            custom.Normalize();
            foreach (var source in custom.Sources)
                source.AxisFromCenter = false;
            foreach (var source in custom.FnSources ?? [])
                source.AxisFromCenter = false;
        }
        CustomBindings.RemoveAll(c =>
            string.IsNullOrWhiteSpace(c.Id) ||
            (!c.IsFnModifier && !G920ControlInfo.IsCustomBindingTarget(c.Target)));

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
        a.IsHat == b.IsHat;

    private static SourceRef CloneSource(SourceRef s) => new()
    {
        DeviceId = s.DeviceId,
        ProductId = s.ProductId,
        Axis = s.Axis,
        Button = s.Button,
        IsHat = s.IsHat,
        AxisFromCenter = false,
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

    /// <summary>
    /// Analog axis→axis only: usable throw start (0..1, after Invert). Remaps to rest.
    /// Ignored for axis→button (use <see cref="Deadzone"/> as Activate on Axis).
    /// </summary>
    public double AxisStart { get; set; }

    /// <summary>
    /// Analog axis→axis only: usable throw end (0..1, after Invert). Remaps to full.
    /// Ignored for axis→button.
    /// </summary>
    public double AxisEnd { get; set; } = 1;

    /// <summary>
    /// When true, axis sources on a button target are converted to digital presses.
    /// Set automatically whenever a button binding has an axis source.
    /// </summary>
    public bool UseAxisAsButton { get; set; }

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

        foreach (var source in Sources)
            source.AxisFromCenter = false;

        var hasAxis = Sources.Any(s => s.Axis is not null);
        var buttonTarget = G920ControlInfo.IsButton(Target);
        var centeredAxis = Target == G920Control.Steering;

        // Button targets always convert axes to digital presses (games never see an analog button).
        if (buttonTarget)
        {
            UseAxisAsButton = hasAxis;
            // Activate on Axis threshold lives in Deadzone. AxisStart/End are analog-only.
            if (hasAxis)
            {
                // Migrate brief dual-band experiment: treat former AxisStart as threshold.
                if (Deadzone <= 0.001 && AxisStart > 0.001)
                    Deadzone = AxisStart;
                Deadzone = Deadzone > 0.001 ? Math.Clamp(Deadzone, 0.05, 0.95) : 0.5;
            }
            else
            {
                Deadzone = 0;
            }

            AxisStart = 0;
            AxisEnd = 1;
        }
        else
        {
            UseAxisAsButton = false;
            NormalizeAnalogAxisRange(hasAxis, centeredAxis);
        }
    }

    internal void NormalizeAnalogAxisRange(bool hasAxis, bool centeredAxis)
    {
        if (!hasAxis)
        {
            AxisStart = 0;
            AxisEnd = 1;
            return;
        }

        // Legacy pedal deadzone → AxisStart when range still unset.
        // Steering center-deadzone stays on Deadzone when range is still 0..1.
        var rangeUnset = AxisStart <= 0.0001 && AxisEnd >= 0.9999;
        if (rangeUnset && !centeredAxis && Deadzone > 0.001)
        {
            AxisStart = Math.Clamp(Deadzone, 0, 0.5);
            AxisEnd = 1;
        }

        AxisStart = Math.Clamp(AxisStart, 0, 0.95);
        AxisEnd = Math.Clamp(AxisEnd, 0.05, 1);
        if (AxisEnd < AxisStart + 0.05)
            AxisEnd = Math.Min(1, AxisStart + 0.05);

        if (!centeredAxis)
            Deadzone = AxisStart;
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
        a.IsHat == b.IsHat;
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
    /// Legacy flag (ignored). Axis→button always uses absolute high-side throw.
    /// Cleared on profile normalize; kept only so old JSON still deserializes.
    /// </summary>
    public bool AxisFromCenter { get; set; }
}

/// <summary>
/// Named user binding after the standard G920 layout. Maps <see cref="Sources"/> to a
/// G920 button (hold or Toggle); optional <see cref="FnSources"/> means FN must be held.
/// </summary>
public sealed class CustomBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom";

    /// <summary>Legacy: FN-only binding. Prefer <see cref="FnSources"/> on button bindings.</summary>
    public bool IsFnModifier { get; set; }

    /// <summary>
    /// When true, rising edge latches the G920 target on/off.
    /// When false, hold-to-press (momentary) while the button source is active.
    /// </summary>
    public bool Toggle { get; set; } = true;

    /// <summary>
    /// When true, the button only fires while FN is held.
    /// Set automatically when <see cref="FnSources"/> is non-empty.
    /// </summary>
    public bool RequiresFn { get; set; }

    /// <summary>
    /// When true, axis sources are converted to digital presses (threshold).
    /// Set automatically when button or FN sources include an axis.
    /// </summary>
    public bool UseAxisAsButton { get; set; }

    public G920Control Target { get; set; } = G920Control.ButtonA;

    /// <summary>Button sources (Bind Button) — hold or Toggle per <see cref="Toggle"/>.</summary>
    public List<SourceRef> Sources { get; set; } = [];

    /// <summary>Optional FN hold sources for this combo (Bind FN).</summary>
    public List<SourceRef> FnSources { get; set; } = [];

    public double Deadzone { get; set; }
    public bool Invert { get; set; }
    public double AxisStart { get; set; }
    public double AxisEnd { get; set; } = 1;
    public double FnDeadzone { get; set; }
    public bool FnInvert { get; set; }
    public double FnAxisStart { get; set; }
    public double FnAxisEnd { get; set; } = 1;

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id))
            Id = Guid.NewGuid().ToString("N");
        Name = string.IsNullOrWhiteSpace(Name) ? "Custom" : Name.Trim();
        Sources ??= [];
        FnSources ??= [];
        Sources.RemoveAll(IsEmptySource);
        FnSources.RemoveAll(IsEmptySource);
        foreach (var source in Sources.Concat(FnSources))
            source.AxisFromCenter = false;

        if (IsFnModifier)
        {
            // Legacy FN-only rows: keep Sources as the hold key; no per-binding FnSources.
            RequiresFn = false;
            FnSources = [];
            Target = G920Control.ButtonA;
        }
        else if (!G920ControlInfo.IsCustomBindingTarget(Target))
        {
            Target = G920Control.ButtonA;
        }

        // Bind FN on this combo ⇒ Requires FN held.
        if (!IsFnModifier)
            RequiresFn = FnSources.Count > 0;

        var buttonAxis = Sources.Any(s => s.Axis is not null);
        var fnAxis = FnSources.Any(s => s.Axis is not null);
        UseAxisAsButton = buttonAxis || fnAxis;

        Deadzone = NormalizeActivateThreshold(buttonAxis, Deadzone, AxisStart);
        FnDeadzone = NormalizeActivateThreshold(fnAxis, FnDeadzone, FnAxisStart);
        // AxisStart/End are analog-only; customs are always digital targets.
        AxisStart = 0;
        AxisEnd = 1;
        FnAxisStart = 0;
        FnAxisEnd = 1;
    }

    private static double NormalizeActivateThreshold(bool hasAxis, double deadzone, double legacyStart)
    {
        if (!hasAxis)
            return 0;
        if (deadzone <= 0.001 && legacyStart > 0.001)
            deadzone = legacyStart;
        return deadzone > 0.001 ? Math.Clamp(deadzone, 0.05, 0.95) : 0.5;
    }

    public void ClearSources()
    {
        Sources = [];
    }

    private static bool IsEmptySource(SourceRef s) =>
        string.IsNullOrWhiteSpace(s.DeviceId) &&
        s.Axis is null &&
        s.Button is null &&
        !s.IsHat;
}
