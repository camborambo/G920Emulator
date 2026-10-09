namespace G920Emulator.Core.Ffb;

/// <summary>
/// Applies optional user feel settings after the OEM DI mix.
/// Defaults (smoothing 0, peak soft 1, boot ease-in off, shaping off) pass game torque through unchanged.
/// </summary>
public sealed class FfbOutputSmoother
{
    /// <summary>Mute window after FFB first arms when boot ease-in is on.</summary>
    public const int BootMuteMs = 5000;

    public bool BootEaseIn { get; set; }
    public float ArmThreshold { get; set; } = 0.04f;
    public float SmoothingMs { get; set; }
    public float SoftClipStart { get; set; } = 1f;

    public float Deadband { get; set; }
    public float MaxSlewPerSecond { get; set; }
    public float MaxSpikeStep { get; set; }
    public int MagnitudeEpsilon { get; set; }

    private float _output;
    private float _shapedTorque;
    private long _softStartEndTick;
    private long _lastTick;
    private long _lastShapeTick;
    private bool _armed;
    private bool _fadeComplete;

    public void Reset()
    {
        _output = 0f;
        _shapedTorque = 0f;
        _softStartEndTick = 0;
        _lastTick = 0;
        _lastShapeTick = 0;
        _armed = false;
        _fadeComplete = false;
    }

    /// <summary>Copy user feel settings from the active profile.</summary>
    public void ApplyFeel(FfbOutputFeel? feel)
    {
        feel ??= FfbOutputFeel.CreateDefault();
        feel.Clamp();
        BootEaseIn = feel.BootEaseIn;
        SmoothingMs = (float)feel.SmoothingMs;
        SoftClipStart = (float)feel.PeakSoftStart;
        Deadband = (float)feel.Deadband;
        MaxSlewPerSecond = (float)feel.MaxSlewPerSecond;
        MaxSpikeStep = (float)feel.MaxSpikeStep;
        MagnitudeEpsilon = (int)Math.Round(feel.MagnitudeEpsilon);
    }

    public float Process(float target, uint downloadCount, uint typesSeen, bool playing)
    {
        _ = downloadCount;
        _ = typesSeen;
        _ = playing;

        target = Math.Clamp(target, -1f, 1f);
        target = SoftClip(target);
        var now = Environment.TickCount64;

        if (!_armed)
        {
            // Boot ease-in off: arm immediately and pass through.
            if (!BootEaseIn)
            {
                _armed = true;
                _fadeComplete = true;
                _lastTick = now;
                _output = target;
                return ApplySmoothing(ShapeGameTorque(target, now), now);
            }

            if (Math.Abs(target) < ArmThreshold)
            {
                _output = 0f;
                _shapedTorque = 0f;
                _lastTick = now;
                return 0f;
            }

            // First meaningful force: mute for BootMuteMs so the base does not kick.
            _armed = true;
            _softStartEndTick = now + BootMuteMs;
            _output = 0f;
            _lastTick = now;
            return 0f;
        }

        if (!_fadeComplete)
        {
            if (now >= _softStartEndTick)
            {
                _fadeComplete = true;
            }
            else
            {
                // Hold at zero for the whole boot window (no effects felt).
                _output = 0f;
                _lastTick = now;
                return 0f;
            }
        }

        return ApplySmoothing(ShapeGameTorque(target, now), now);
    }

    /// <summary>
    /// Optional deadband + spike cap + slew (Desktop fork ShapeGameTorque).
    /// All-zero / full-step defaults are pass-through.
    /// </summary>
    private float ShapeGameTorque(float target, long now)
    {
        var shaping =
            Deadband > 0.0005f ||
            MaxSlewPerSecond > 0.5f ||
            MaxSpikeStep > 0.001f;

        if (!shaping)
        {
            _shapedTorque = target;
            _lastShapeTick = now;
            return target;
        }

        if (Deadband > 0.0005f && Math.Abs(target) < Deadband)
            target = 0f;

        var dtSec = _lastShapeTick == 0 ? 0.002f : Math.Clamp((now - _lastShapeTick) / 1000f, 0.001f, 0.05f);
        _lastShapeTick = now;

        var delta = target - _shapedTorque;

        // Spike cap: max |Δtorque| per second (same idea as Slew). 0 = off;
        // 0.05 → 5/s (strong), 0.50 → 50/s. Older per-call clamp staircased at ~500 Hz.
        if (MaxSpikeStep > 0.001f)
        {
            var maxPerSec = Math.Clamp(MaxSpikeStep, 0.01f, 1f) * 100f;
            var spike = Math.Clamp(maxPerSec * dtSec, 1e-4f, 1f);
            if (Math.Abs(delta) > spike)
                delta = Math.Sign(delta) * spike;
        }

        if (MaxSlewPerSecond > 0.5f)
        {
            // Pull to zero faster than normal slew so pause/no-signal doesn't leave residual force.
            var slew = target == 0f ? MaxSlewPerSecond * 2.5f : MaxSlewPerSecond;
            var maxStep = slew * dtSec;
            if (Math.Abs(delta) > maxStep)
                delta = Math.Sign(delta) * maxStep;
        }

        _shapedTorque += delta;
        if (target == 0f && Deadband > 0.0005f && Math.Abs(_shapedTorque) < Deadband)
            _shapedTorque = 0f;
        return _shapedTorque;
    }

    private float ApplySmoothing(float target, long now)
    {
        var dtMs = _lastTick == 0 ? 2f : Math.Clamp(now - _lastTick, 1, 50);
        _lastTick = now;

        var tau = Math.Max(0f, SmoothingMs);
        if (tau <= 0.5f)
        {
            _output = target;
            return _output;
        }

        var alpha = 1f - MathF.Exp(-dtMs / tau);
        _output += (target - _output) * alpha;
        return _output;
    }

    private float SoftClip(float x)
    {
        var start = Math.Clamp(SoftClipStart, 0.5f, 1f);
        if (start >= 0.999f)
            return x;

        var sign = x < 0 ? -1f : 1f;
        var a = Math.Abs(x);
        if (a <= start)
            return x;

        var t = (a - start) / (1f - start);
        var compressed = start + (1f - start) * (t * (2f - t));
        return sign * compressed;
    }
}
