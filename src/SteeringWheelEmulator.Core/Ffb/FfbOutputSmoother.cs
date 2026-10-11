namespace SteeringWheelEmulator.Core.Ffb;

/// <summary>
/// Applies optional user feel adjustments after the game's effects are mixed.
/// Defaults leave the mix untouched.
/// </summary>
public sealed class FfbOutputSmoother
{
    /// <summary>Mute window after FFB first arms when boot ease-in is on.</summary>
    public const int BootMuteMs = 5000;

    public bool BootEaseIn { get; set; }
    public float ArmThreshold { get; set; } = 0.04f;
    public float SmoothingMs { get; set; }
    /// <summary>Interpolate blend window (ms) on the mixed force. 0 = off.</summary>
    public float ReconstructionMs { get; set; }
    /// <summary>Gap fill ms when the mix drops between updates. 0 = blend only.</summary>
    public float IdleGapHoldMs { get; set; }
    public float SoftClipStart { get; set; } = 1f;

    public float Deadband { get; set; }
    public float MaxSlewPerSecond { get; set; }
    public float MaxSpikeStep { get; set; }
    public int MagnitudeEpsilon { get; set; }

    private float _output;
    private float _reconOutput;
    private float _shapedTorque;
    private bool _reconInit;
    /// <summary>Last significant sample — held across mix gaps when Gap fill is on.</summary>
    private float _lastIdlePulse;
    /// <summary>Ms since that pulse dropped near zero.</summary>
    private float _idleGapMs;
    private long _softStartEndTick;
    private long _lastTick;
    private long _lastReconTick;
    private long _lastShapeTick;
    private bool _armed;
    private bool _fadeComplete;

    public void Reset()
    {
        _output = 0f;
        _reconOutput = 0f;
        _shapedTorque = 0f;
        _reconInit = false;
        _lastIdlePulse = 0f;
        _idleGapMs = 0f;
        _softStartEndTick = 0;
        _lastTick = 0;
        _lastReconTick = 0;
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
        ReconstructionMs = (float)feel.ReconstructionMs;
        IdleGapHoldMs = (float)feel.IdleGapHoldMs;
        SoftClipStart = (float)feel.PeakSoftStart;
        Deadband = (float)feel.Deadband;
        MaxSlewPerSecond = (float)feel.MaxSlewPerSecond;
        MaxSpikeStep = (float)feel.MaxSpikeStep;
        MagnitudeEpsilon = (int)Math.Round(feel.MagnitudeEpsilon);
    }

    public float Process(float target, uint downloadCount, uint typesSeen, bool playing) =>
        Process(target, downloadCount, typesSeen, playing, ReadOnlySpan<int>.Empty, 0f, 0f);

    /// <summary>
    /// Apply mix adjustments (Interpolate / Gap fill, shaping, smoothing).
    /// Use <see cref="ProcessAt"/> for offline replay with a clock.
    /// </summary>
    public float Process(
        float target,
        uint downloadCount,
        uint typesSeen,
        bool playing,
        ReadOnlySpan<int> typeTorqueDi,
        float rimVelAbs = 0f,
        float rimPos = 0f) =>
        ProcessAt(target, downloadCount, typesSeen, playing, typeTorqueDi, rimVelAbs, rimPos,
            Environment.TickCount64);

    /// <summary>Same as <see cref="Process"/> with an explicit millisecond clock (replay / tests).</summary>
    public float ProcessAt(
        float target,
        uint downloadCount,
        uint typesSeen,
        bool playing,
        ReadOnlySpan<int> typeTorqueDi,
        float rimVelAbs,
        float rimPos,
        long now)
    {
        _ = downloadCount;
        _ = typesSeen;
        _ = playing;

        target = Math.Clamp(target, -1f, 1f);
        target = SoftClip(target);

        if (!_armed)
        {
            if (!BootEaseIn)
            {
                _armed = true;
                _fadeComplete = true;
                _lastTick = now;
                _lastReconTick = now;
                target = ApplyReconstruction(target, typeTorqueDi, now, rimVelAbs, rimPos);
                _output = target;
                return ApplySmoothing(ShapeGameTorque(target, now), now);
            }

            if (Math.Abs(target) < ArmThreshold)
            {
                _output = 0f;
                _reconOutput = 0f;
                _shapedTorque = 0f;
                _reconInit = false;
                _lastTick = now;
                return 0f;
            }

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
                _output = 0f;
                _lastTick = now;
                return 0f;
            }
        }

        target = ApplyReconstruction(target, typeTorqueDi, now, rimVelAbs, rimPos);
        return ApplySmoothing(ShapeGameTorque(target, now), now);
    }

    private float ApplyReconstruction(
        float target,
        ReadOnlySpan<int> typeDi,
        long now,
        float rimVelAbs,
        float rimPos)
    {
        var baseTau = Math.Max(0f, ReconstructionMs);
        if (baseTau <= 0.5f)
        {
            _reconOutput = target;
            _reconInit = false;
            _lastIdlePulse = 0f;
            _idleGapMs = 0f;
            _lastReconTick = now;
            return target;
        }

        // Mix adjustments: blend + optional Gap fill (not idle-gated).
        _ = typeDi;
        _ = rimVelAbs;
        _ = rimPos;
        return ApplyInterpolate(target, now, baseTau);
    }

    /// <summary>Blend the mixed force; optionally hold the last sample across update gaps.</summary>
    private float ApplyInterpolate(float target, long now, float blendMs)
    {
        var dtMs = _lastReconTick == 0 ? 2f : Math.Clamp(now - _lastReconTick, 1, 50);
        _lastReconTick = now;

        const float pulseFloor = 0.018f;
        var filterTarget = target;
        var gapHold = Math.Max(0f, IdleGapHoldMs);

        if (Math.Abs(target) >= pulseFloor)
        {
            _lastIdlePulse = target;
            _idleGapMs = 0f;
            filterTarget = target;
        }
        else if (Math.Abs(_lastIdlePulse) >= pulseFloor && gapHold > 0.5f)
        {
            _idleGapMs += dtMs;
            if (_idleGapMs < gapHold)
            {
                // Hold last sample; light linear fade only in the last 25% of the hold.
                var fadeStart = gapHold * 0.75f;
                if (_idleGapMs <= fadeStart)
                    filterTarget = _lastIdlePulse;
                else
                {
                    var t = (_idleGapMs - fadeStart) / Math.Max(1f, gapHold - fadeStart);
                    filterTarget = _lastIdlePulse * (1f - t);
                }
            }
            else
            {
                _lastIdlePulse = 0f;
                _idleGapMs = 0f;
                filterTarget = target;
            }
        }

        return BlendToward(filterTarget, dtMs, blendMs, reverseTauMul: 3f, releaseTauMul: 2f);
    }

    private float BlendToward(
        float filterTarget,
        float dtMs,
        float tau,
        float reverseTauMul,
        float releaseTauMul)
    {
        if (!_reconInit)
        {
            _reconOutput = filterTarget;
            _reconInit = true;
            return _reconOutput;
        }

        var outSign = Math.Sign(_reconOutput);
        var tgtSign = Math.Sign(filterTarget);
        var reversing = outSign != 0 && tgtSign != 0 && outSign != tgtSign;
        var releasing = Math.Abs(filterTarget) < Math.Abs(_reconOutput) - 0.002f
            || (Math.Abs(filterTarget) < 0.002f && Math.Abs(_reconOutput) > 0.02f);

        float useTau;
        if (reversing)
            useTau = Math.Max(1f, tau * reverseTauMul);
        else if (releasing)
            useTau = Math.Max(1f, tau * releaseTauMul);
        else
            useTau = Math.Max(1f, tau);

        var alpha = 1f - MathF.Exp(-dtMs / useTau);
        var next = _reconOutput + (filterTarget - _reconOutput) * alpha;
        if (reversing && outSign != 0 && Math.Sign(next) != outSign)
            next = 0f;

        _reconOutput = next;
        return _reconOutput;
    }

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

        if (MaxSpikeStep > 0.001f)
        {
            var maxPerSec = Math.Clamp(MaxSpikeStep, 0.01f, 1f) * 100f;
            var spike = Math.Clamp(maxPerSec * dtSec, 1e-4f, 1f);
            if (Math.Abs(delta) > spike)
                delta = Math.Sign(delta) * spike;
        }

        if (MaxSlewPerSecond > 0.5f)
        {
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
