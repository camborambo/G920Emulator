using SteeringWheelEmulator.Core.Models;

namespace SteeringWheelEmulator.Core.Mapping;

public sealed class MapperEngine
{
    private readonly Dictionary<string, bool> _customToggleLatched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _customPrevEffective = new(StringComparer.Ordinal);

    /// <summary>Clears FN/custom toggle latches (call when the profile is replaced or customs change).</summary>
    public void ResetCustomState()
    {
        _customToggleLatched.Clear();
        _customPrevEffective.Clear();
    }

    public MappedG920State Map(MappingProfile profile, IReadOnlyDictionary<string, DeviceState> devices)
    {
        UpdateCustomToggles(profile, devices);

        var catalog = EmulatedDeviceProfiles.Get(profile.EmulatedDevice).Catalog;
        var defaultReverse = catalog.DefaultGearReverseOutputButton;
        var maxReverse = catalog.MaxGearReverseOutputButton;

        var result = new MappedG920State
        {
            Steering = ReadAxis(profile, devices, G920Control.Steering, centered: true),
            Throttle = ReadAxis(profile, devices, G920Control.Throttle, centered: false),
            Brake = ReadAxis(profile, devices, G920Control.Brake, centered: false),
            Clutch = ReadAxis(profile, devices, G920Control.Clutch, centered: false),
            Handbrake = ReadAxis(profile, devices, G920Control.Handbrake, centered: false),
            Hat = ReadHat(profile, devices),
            ButtonA = ReadButton(profile, devices, G920Control.ButtonA),
            ButtonB = ReadButton(profile, devices, G920Control.ButtonB),
            ButtonX = ReadButton(profile, devices, G920Control.ButtonX),
            ButtonY = ReadButton(profile, devices, G920Control.ButtonY),
            ButtonLb = ReadButton(profile, devices, G920Control.ButtonLb),
            ButtonRb = ReadButton(profile, devices, G920Control.ButtonRb),
            ButtonView = ReadButton(profile, devices, G920Control.ButtonView),
            ButtonMenu = ReadButton(profile, devices, G920Control.ButtonMenu),
            ButtonLs = ReadButton(profile, devices, G920Control.ButtonLs),
            ButtonRs = ReadButton(profile, devices, G920Control.ButtonRs),
            RightTrigger = ReadButton(profile, devices, G920Control.RightTrigger),
            LeftTrigger = ReadButton(profile, devices, G920Control.LeftTrigger),
            Button21 = ReadButton(profile, devices, G920Control.Button21),
            Button23 = ReadButton(profile, devices, G920Control.Button23),
            Button24 = ReadButton(profile, devices, G920Control.Button24),
            Button25 = ReadButton(profile, devices, G920Control.Button25),
            Button26 = ReadButton(profile, devices, G920Control.Button26),
            Button27 = ReadButton(profile, devices, G920Control.Button27),
            Button28 = ReadButton(profile, devices, G920Control.Button28),
            Button29 = ReadButton(profile, devices, G920Control.Button29),
            Button30 = ReadButton(profile, devices, G920Control.Button30),
            Button31 = ReadButton(profile, devices, G920Control.Button31),
            Button32 = ReadButton(profile, devices, G920Control.Button32),
            PaddleLeft = ReadButton(profile, devices, G920Control.PaddleLeft),
            PaddleRight = ReadButton(profile, devices, G920Control.PaddleRight),
            Gear1 = ReadButton(profile, devices, G920Control.Gear1),
            Gear2 = ReadButton(profile, devices, G920Control.Gear2),
            Gear3 = ReadButton(profile, devices, G920Control.Gear3),
            Gear4 = ReadButton(profile, devices, G920Control.Gear4),
            Gear5 = ReadButton(profile, devices, G920Control.Gear5),
            Gear6 = ReadButton(profile, devices, G920Control.Gear6),
            Gear7 = ReadButton(profile, devices, G920Control.Gear7),
            GearR = ReadButton(profile, devices, G920Control.GearR),
            GearReverseOutputButton = Math.Clamp(
                profile.GearReverseOutputButton <= 0 ? defaultReverse : profile.GearReverseOutputButton,
                1,
                maxReverse),
        };

        ApplyExclusiveGears(result);
        return result;
    }

    /// <summary>True when a bound button (or axis-as-button) for <paramref name="target"/> is pressed.</summary>
    public static bool IsPressed(
        MappingProfile profile,
        IReadOnlyDictionary<string, DeviceState> devices,
        G920Control target) =>
        ReadStandardButton(profile, devices, target);

    private void UpdateCustomToggles(MappingProfile profile, IReadOnlyDictionary<string, DeviceState> devices)
    {
        profile.CustomBindings ??= [];
        var liveIds = new HashSet<string>(StringComparer.Ordinal);

        // Legacy global FN-only bindings (IsFnModifier).
        var globalFnHeld = false;
        foreach (var custom in profile.CustomBindings)
        {
            if (!custom.IsFnModifier) continue;
            if (SourcesPressed(custom.Sources, custom.Invert, custom.Deadzone, devices, out _))
                globalFnHeld = true;
        }

        foreach (var custom in profile.CustomBindings)
        {
            if (custom.IsFnModifier) continue;
            if (string.IsNullOrWhiteSpace(custom.Id)) continue;
            liveIds.Add(custom.Id);

            var raw = SourcesPressed(
                custom.Sources, custom.Invert, custom.Deadzone, devices, out var buttonSeen);
            bool effective;
            bool comboSeen;
            if (custom.FnSources is { Count: > 0 })
            {
                var fnHeld = SourcesPressed(
                    custom.FnSources, custom.FnInvert, custom.FnDeadzone, devices, out var fnSeen);
                // Need both sides present; if FN device drops mid-hold, don't invent edges.
                comboSeen = buttonSeen && fnSeen;
                effective = comboSeen && fnHeld && raw;
            }
            else if (custom.RequiresFn)
            {
                // Legacy: RequiresFn without per-binding FnSources → any global FN.
                comboSeen = buttonSeen;
                effective = comboSeen && globalFnHeld && raw;
            }
            else
            {
                comboSeen = buttonSeen;
                effective = comboSeen && raw;
            }

            _customPrevEffective.TryGetValue(custom.Id, out var prev);
            if (custom.Toggle)
            {
                // Only edge-detect when sources are visible. A one-frame device dropout
                // would otherwise look like release→press and flip the latch off.
                if (comboSeen && effective && !prev)
                {
                    _customToggleLatched.TryGetValue(custom.Id, out var latched);
                    _customToggleLatched[custom.Id] = !latched;
                }

                if (comboSeen)
                    _customPrevEffective[custom.Id] = effective;
            }
            else
            {
                // Hold / momentary: active only while the combo is pressed.
                _customToggleLatched[custom.Id] = effective;
                if (comboSeen)
                    _customPrevEffective[custom.Id] = effective;
            }
        }

        foreach (var key in _customToggleLatched.Keys.Where(k => !liveIds.Contains(k)).ToList())
            _customToggleLatched.Remove(key);
        foreach (var key in _customPrevEffective.Keys.Where(k => !liveIds.Contains(k)).ToList())
            _customPrevEffective.Remove(key);
    }

    private bool AnyCustomToggle(MappingProfile profile, G920Control target)
    {
        profile.CustomBindings ??= [];
        foreach (var custom in profile.CustomBindings)
        {
            if (custom.IsFnModifier) continue;
            if (custom.Target != target) continue;
            if (_customToggleLatched.TryGetValue(custom.Id, out var on) && on)
                return true;
        }

        return false;
    }

    private static bool SourcesPressed(
        IReadOnlyList<SourceRef>? sources,
        bool invert,
        double activateThreshold,
        IReadOnlyDictionary<string, DeviceState> devices,
        out bool deviceSeen)
    {
        deviceSeen = false;
        if (sources is null || sources.Count == 0)
            return false;

        var pressed = false;
        var any = false;
        var hasAxis = false;
        var threshold = activateThreshold > 0.001 ? Math.Clamp(activateThreshold, 0.05, 0.95) : 0.5;

        foreach (var source in sources)
        {
            if (!TryGetDevice(source, devices, out var device))
                continue;

            if (source.IsHat)
            {
                any = true;
                deviceSeen = true;
                if (device.Hat >= 0)
                    pressed = true;
                continue;
            }

            if (source.Button is int button)
            {
                if (button < 0 || button >= device.Buttons.Length)
                    continue;
                any = true;
                deviceSeen = true;
                if (device.Buttons[button])
                    pressed = true;
                continue;
            }

            if (!string.IsNullOrEmpty(source.Axis) &&
                device.Axes.TryGetValue(source.Axis, out var raw))
            {
                any = true;
                deviceSeen = true;
                hasAxis = true;
                if (AxisAsButtonPressed(raw, invert, threshold))
                    pressed = true;
            }
        }

        if (!any)
            return false;
        if (!hasAxis && invert)
            return !pressed;
        return pressed;
    }

    private static void ApplyExclusiveGears(MappedG920State state)
    {
        var gears = new (Func<MappedG920State, bool> get, Action<MappedG920State, bool> set)[]
        {
            (s => s.GearR, (s, v) => s.GearR = v),
            (s => s.Gear1, (s, v) => s.Gear1 = v),
            (s => s.Gear2, (s, v) => s.Gear2 = v),
            (s => s.Gear3, (s, v) => s.Gear3 = v),
            (s => s.Gear4, (s, v) => s.Gear4 = v),
            (s => s.Gear5, (s, v) => s.Gear5 = v),
            (s => s.Gear6, (s, v) => s.Gear6 = v),
            (s => s.Gear7, (s, v) => s.Gear7 = v),
        };

        var active = -1;
        for (var i = 0; i < gears.Length; i++)
        {
            if (gears[i].get(state))
            {
                active = i;
                break;
            }
        }

        if (active < 0)
            return;

        for (var i = 0; i < gears.Length; i++)
            gears[i].set(state, i == active);
    }

    private static float ReadAxis(MappingProfile profile, IReadOnlyDictionary<string, DeviceState> devices, G920Control target, bool centered)
    {
        var binding = profile.Bindings.FirstOrDefault(b => b.Target == target);
        if (binding is null)
            return 0f;

        var sources = binding.EffectiveSources.Where(s => !string.IsNullOrEmpty(s.Axis)).ToList();
        if (sources.Count == 0)
            return 0f;

        float combined = 0f;
        var any = false;
        foreach (var source in sources)
        {
            if (!TryGetDevice(source, devices, out var device))
                continue;
            if (string.IsNullOrEmpty(source.Axis) || !device.Axes.TryGetValue(source.Axis, out var raw))
                continue;

            var value01 = binding.Invert ? 1f - raw : raw;
            var remapped = ApplyAxisRange(value01, binding.AxisStart, binding.AxisEnd);
            var value = centered ? (remapped * 2f) - 1f : remapped;

            if (!any)
            {
                combined = value;
                any = true;
            }
            else if (centered)
            {
                if (Math.Abs(value) > Math.Abs(combined))
                    combined = value;
            }
            else
            {
                combined = Math.Max(combined, value);
            }
        }

        if (!any)
            return 0f;

        // Legacy steering: center deadzone when usable range is still full throw.
        if (centered &&
            binding.Deadzone > 0.001 &&
            binding.AxisStart <= 0.0001 &&
            binding.AxisEnd >= 0.9999)
        {
            combined = ApplyDeadzone(combined, (float)binding.Deadzone, centered: true);
        }

        combined = (float)(combined * binding.Scale);
        return centered ? Math.Clamp(combined, -1f, 1f) : Math.Clamp(combined, 0f, 1f);
    }

    /// <summary>Remap axis value in [start,end] → [0,1]; below start → 0, above end → 1.</summary>
    internal static float ApplyAxisRange(float value01, double start, double end)
    {
        value01 = Math.Clamp(value01, 0f, 1f);
        var s = (float)Math.Clamp(start, 0, 0.95);
        var e = (float)Math.Clamp(end, 0.05, 1);
        if (e < s + 0.05f)
            e = Math.Min(1f, s + 0.05f);

        if (value01 <= s) return 0f;
        if (value01 >= e) return 1f;
        return (value01 - s) / (e - s);
    }

    private static float ApplyDeadzone(float value, float deadzone, bool centered)
    {
        deadzone = Math.Clamp(deadzone, 0f, 0.95f);
        if (deadzone <= 0f)
            return value;

        if (centered)
        {
            var magnitude = Math.Abs(value);
            if (magnitude <= deadzone)
                return 0f;
            var sign = Math.Sign(value);
            return sign * ((magnitude - deadzone) / (1f - deadzone));
        }

        if (value <= deadzone)
            return 0f;
        return (value - deadzone) / (1f - deadzone);
    }

    private bool ReadButton(MappingProfile profile, IReadOnlyDictionary<string, DeviceState> devices, G920Control target) =>
        ReadStandardButton(profile, devices, target) || AnyCustomToggle(profile, target);

    private static bool ReadStandardButton(MappingProfile profile, IReadOnlyDictionary<string, DeviceState> devices, G920Control target)
    {
        var binding = profile.Bindings.FirstOrDefault(b => b.Target == target);
        if (binding is null)
            return false;

        var pressed = false;
        var any = false;
        var threshold = binding.Deadzone > 0.001 ? Math.Clamp(binding.Deadzone, 0.05, 0.95) : 0.5;

        foreach (var source in binding.EffectiveSources)
        {
            if (!TryGetDevice(source, devices, out var device))
                continue;

            if (source.Button is int button)
            {
                if (button < 0 || button >= device.Buttons.Length)
                    continue;
                any = true;
                if (device.Buttons[button])
                    pressed = true;
                continue;
            }

            if (!string.IsNullOrEmpty(source.Axis) &&
                device.Axes.TryGetValue(source.Axis, out var raw))
            {
                any = true;
                if (AxisAsButtonPressed(raw, binding.Invert, threshold))
                    pressed = true;
            }
        }

        if (!any)
            return false;
        var hasAxis = binding.EffectiveSources.Any(s => !string.IsNullOrEmpty(s.Axis));
        if (!hasAxis && binding.Invert)
            return !pressed;
        return pressed;
    }

    /// <summary>Absolute axis→button: pressed when value (after Invert) is at or above Activate on Axis.</summary>
    private static bool AxisAsButtonPressed(float raw, bool invert, double threshold)
    {
        raw = Math.Clamp(raw, 0f, 1f);
        var value = invert ? 1f - raw : raw;
        return value >= (float)threshold;
    }

    private int ReadHat(MappingProfile profile, IReadOnlyDictionary<string, DeviceState> devices)
    {
        var binding = profile.Bindings.FirstOrDefault(b => b.Target == G920Control.Hat);
        if (binding is not null)
        {
            foreach (var source in binding.EffectiveSources)
            {
                if (!source.IsHat)
                    continue;
                if (!TryGetDevice(source, devices, out var device))
                    continue;
                if (device.Hat >= 0)
                    return device.Hat;
            }
        }

        // Custom bindings targeting Hat: when toggled on, treat as centered-N (0) so games see a hat press.
        if (AnyCustomToggle(profile, G920Control.Hat))
            return 0;

        var up = ReadButton(profile, devices, G920Control.HatUp);
        var down = ReadButton(profile, devices, G920Control.HatDown);
        var left = ReadButton(profile, devices, G920Control.HatLeft);
        var right = ReadButton(profile, devices, G920Control.HatRight);

        if (up && down) { up = false; down = false; }
        if (left && right) { left = false; right = false; }
        if (!up && !down && !left && !right)
            return -1;

        if (up && right) return 1;
        if (down && right) return 3;
        if (down && left) return 5;
        if (up && left) return 7;
        if (up) return 0;
        if (right) return 2;
        if (down) return 4;
        if (left) return 6;
        return -1;
    }

    private static bool TryGetDevice(
        SourceRef source,
        IReadOnlyDictionary<string, DeviceState> devices,
        out DeviceState device)
    {
        if (!string.IsNullOrWhiteSpace(source.DeviceId) &&
            devices.TryGetValue(source.DeviceId, out device!))
            return true;

        device = null!;
        return false;
    }
}
