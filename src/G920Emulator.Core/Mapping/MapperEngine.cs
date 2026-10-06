using G920Emulator.Core.Models;

namespace G920Emulator.Core.Mapping;

public sealed class MapperEngine
{
    public MappedG920State Map(MappingProfile profile, IReadOnlyDictionary<string, DeviceState> devices)
    {
        var result = new MappedG920State
        {
            Steering = ReadAxis(profile, devices, G920Control.Steering, centered: true),
            Throttle = ReadAxis(profile, devices, G920Control.Throttle, centered: false),
            Brake = ReadAxis(profile, devices, G920Control.Brake, centered: false),
            Clutch = ReadAxis(profile, devices, G920Control.Clutch, centered: false),
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
            PaddleLeft = ReadButton(profile, devices, G920Control.PaddleLeft),
            PaddleRight = ReadButton(profile, devices, G920Control.PaddleRight),
            Gear1 = ReadButton(profile, devices, G920Control.Gear1),
            Gear2 = ReadButton(profile, devices, G920Control.Gear2),
            Gear3 = ReadButton(profile, devices, G920Control.Gear3),
            Gear4 = ReadButton(profile, devices, G920Control.Gear4),
            Gear5 = ReadButton(profile, devices, G920Control.Gear5),
            Gear6 = ReadButton(profile, devices, G920Control.Gear6),
            GearR = ReadButton(profile, devices, G920Control.GearR),
            GearReverseOutputButton = Math.Clamp(
                profile.GearReverseOutputButton <= 0 ? 19 : profile.GearReverseOutputButton, 1, 19),
        };

        ApplyExclusiveGears(result);
        return result;
    }

    /// <summary>True when a bound button (or axis-as-button) for <paramref name="target"/> is pressed.</summary>
    public static bool IsPressed(
        MappingProfile profile,
        IReadOnlyDictionary<string, DeviceState> devices,
        G920Control target) =>
        ReadButton(profile, devices, target);

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

        // Combine multiple axes: pedals use max; steering uses the strongest deflection.
        float combined = centered ? 0f : 0f;
        var any = false;
        foreach (var source in sources)
        {
            if (!TryGetDevice(source, devices, out var device))
                continue;
            if (string.IsNullOrEmpty(source.Axis) || !device.Axes.TryGetValue(source.Axis, out var raw))
                continue;

            float value;
            if (centered)
            {
                value = (raw * 2f) - 1f;
                if (binding.Invert)
                    value = -value;
            }
            else
            {
                value = binding.Invert ? 1f - raw : raw;
            }

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

        combined = ApplyDeadzone(combined, (float)binding.Deadzone, centered);
        combined = (float)(combined * binding.Scale);
        return centered ? Math.Clamp(combined, -1f, 1f) : Math.Clamp(combined, 0f, 1f);
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

    private static bool ReadButton(MappingProfile profile, IReadOnlyDictionary<string, DeviceState> devices, G920Control target)
    {
        var binding = profile.Bindings.FirstOrDefault(b => b.Target == target);
        if (binding is null)
            return false;

        var pressed = false;
        var any = false;
        // Deadzone doubles as axis→button activation threshold (default 50%).
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
                if (AxisAsButtonPressed(raw, source.AxisFromCenter, binding.Invert, threshold))
                    pressed = true;
            }
        }

        if (!any)
            return false;
        // Invert for pure button sources is applied per-axis above; for button-only
        // bindings with Invert, flip the OR result when no axis sources contributed.
        var hasAxis = binding.EffectiveSources.Any(s => !string.IsNullOrEmpty(s.Axis));
        if (!hasAxis && binding.Invert)
            return !pressed;
        return pressed;
    }

    /// <summary>
    /// Convert a 0..1 axis into a digital press. Invert flips the high/low sense
    /// (rest-high pedals). AxisFromCenter uses deflection from 0.5.
    /// </summary>
    private static bool AxisAsButtonPressed(float raw, bool fromCenter, bool invert, double threshold)
    {
        raw = Math.Clamp(raw, 0f, 1f);
        float amount;
        if (fromCenter)
        {
            amount = Math.Abs(raw - 0.5f) * 2f;
            // Invert is uncommon for centered; still allow flipping the compare.
            return invert ? amount <= (float)threshold : amount >= (float)threshold;
        }

        var value = invert ? 1f - raw : raw;
        return value >= (float)threshold;
    }

    private static int ReadHat(MappingProfile profile, IReadOnlyDictionary<string, DeviceState> devices)
    {
        // Prefer a physical POV/hat when it is deflected.
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

        // Pads without a hat: synthesize 8-way DI hat from cardinal button (or axis→button) bindings.
        var up = ReadButton(profile, devices, G920Control.HatUp);
        var down = ReadButton(profile, devices, G920Control.HatDown);
        var left = ReadButton(profile, devices, G920Control.HatLeft);
        var right = ReadButton(profile, devices, G920Control.HatRight);

        if (up && down) { up = false; down = false; }
        if (left && right) { left = false; right = false; }
        if (!up && !down && !left && !right)
            return -1;

        // DI hat: 0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW
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

    /// <summary>
    /// Resolve a bound source to a live device state. Prefers instance GUID; if that
    /// device is gone, fall back to any device whose id was already remapped onto the
    /// source (ProductId remaps happen in <c>DeviceBindingResolver</c> before Map).
    /// </summary>
    private static bool TryGetDevice(
        SourceRef source,
        IReadOnlyDictionary<string, DeviceState> devices,
        out DeviceState device)
    {
        if (!string.IsNullOrWhiteSpace(source.DeviceId) &&
            devices.TryGetValue(source.DeviceId, out device!))
            return true;

        // Last resort: single attached device with matching state.DeviceId already updated.
        device = null!;
        return false;
    }
}
