namespace G920Emulator.Core.Ffb;

/// <summary>Legacy profile field only — Idle mode dropdown removed; INT path is always used when Interpolate &gt; 0.</summary>
public enum IdleSmoothMode
{
    Ema = 0,
    Interpolate = 1,
}

/// <summary>
/// User-tunable output shaping applied after the game's DI effects are mixed.
/// Defaults leave the mix untouched (exact game feedback).
/// </summary>
public sealed class FfbOutputFeel
{
    /// <summary>
    /// Low-pass time constant in ms (0 = off). Higher = softer / less "raw" on DD bases.
    /// Useful range ~0-40.
    /// </summary>
    public double SmoothingMs { get; set; }

    /// <summary>
    /// Interpolate blend window in ms (0 = off). Softens sparse updates on the mixed force.
    /// </summary>
    public double ReconstructionMs { get; set; }

    /// <summary>
    /// Gap fill: max ms to hold the last sample when Interpolate &gt; 0.
    /// 0 = off / blend only. Range 0–200.
    /// </summary>
    public double IdleGapHoldMs { get; set; }

    /// <summary>Ignored legacy JSON field (dropdown removed).</summary>
    public IdleSmoothMode IdleSmoothMode { get; set; } = IdleSmoothMode.Interpolate;

    /// <summary>
    /// Device pace: physical update period in ms (0 = every update).
    /// Typical useful range 2–5 when enabled.
    /// </summary>
    public double CfPacePeriodMs { get; set; }

    /// <summary>
    /// Soft-knee starts at this |torque| (0.50-1.00). 1.00 = off (no peak compression).
    /// UI shows inverted strength: 0 = off, 100% = knee at 0.50 (strongest).
    /// </summary>
    public double PeakSoftStart { get; set; } = 1.0;

    /// <summary>
    /// When true, mute game torque for a short window when FFB first comes online
    /// so the base does not kick/shake. Off by default.
    /// </summary>
    public bool BootEaseIn { get; set; }

    /// <summary>
    /// Legacy fade duration (ms). On load, any value &gt; 0 migrates to <see cref="BootEaseIn"/> = true.
    /// </summary>
    public double SoftStartMs { get; set; }

    /// <summary>
    /// Game-path torque deadband (0 = off). Tiny values (~0.004) kill chatter without
    /// muting Unbound's sparse CF spikes.
    /// </summary>
    public double Deadband { get; set; }

    /// <summary>
    /// Max |torque| change per second (0 = unlimited / off).
    /// Zero targets ramp down 2.5× faster for pause / no-signal safety.
    /// </summary>
    public double MaxSlewPerSecond { get; set; }

    /// <summary>
    /// Cap on |Δtorque| per second toward the target (0 = off; 0.05-1.00 maps to ~5-100 /s).
    /// Lower non-zero = softer chase. Independent of game/display Hz.
    /// Legacy profiles stored 1.0 as off — <see cref="Clamp"/> migrates that to 0.
    /// </summary>
    public double MaxSpikeStep { get; set; }

    /// <summary>
    /// Skip physical DI updates when |Δmagnitude| is below this (0 = off). Fork used 12
    /// (of 0..10000) to reduce Fanatec grind from ±1 chatter.
    /// </summary>
    public double MagnitudeEpsilon { get; set; }

    /// <summary>
    /// Add an emulator centering spring from the physical rim angle, for games that
    /// send no centering force. Off by default (Raw = exact game mix).
    /// </summary>
    public bool ForceCenterSpring { get; set; }

    /// <summary>Max centering torque (0.05-1.00) of the forced spring.</summary>
    public double CenterSpringStrength { get; set; } = 0.3;

    /// <summary>
    /// Rim offset (fraction of full rotation from center, 0.05-0.50) where the forced
    /// spring reaches full strength. Lower = stiffer near center.
    /// </summary>
    public double CenterSpringRange { get; set; } = 0.25;

    /// <summary>Centering deadzone around center (0-0.05 of full rotation).</summary>
    public double CenterSpringDeadzone { get; set; }

    /// <summary>
    /// Extra Constant Force flip on top of the driver's DI→app polarity fix.
    /// Independent of Invert FFB. Off by default - leave off unless a specific
    /// game/base still feels mirrored after the built-in conversion.
    /// </summary>
    public bool InvertConstantForce { get; set; }

    /// <summary>
    /// Scale applied to rim velocity before damper/inertia condition eval (1.0 = off).
    /// Range matches effect gains (25%–200%). Desktop classic / NFS Unbound used 2.0.
    /// </summary>
    public double DamperVelocityScale { get; set; } = 1.0;

    /// <summary>
    /// Scale applied to damper deadband before eval (1.0 = off). Desktop classic used ~0.33.
    /// </summary>
    public double DamperDeadbandScale { get; set; } = 1.0;

    /// <summary>
    /// Spring coefficient multiplier (1.0 = off). Scales DI spring coefficients
    /// without changing saturation — stronger near center sooner.
    /// </summary>
    public double SpringCoefficientScale { get; set; } = 1.0;

    /// <summary>
    /// Friction coefficient multiplier (1.0 = off). Higher can feel
    /// gritty/backlashing on some bases; lower weakens friction.
    /// </summary>
    public double FrictionCoefficientScale { get; set; } = 1.0;

    public void Clamp()
    {
        SmoothingMs = Math.Clamp(SmoothingMs, 0, 40);
        ReconstructionMs = Math.Clamp(ReconstructionMs, 0, 100);
        IdleGapHoldMs = Math.Clamp(IdleGapHoldMs, 0, 200);
        CfPacePeriodMs = Math.Clamp(CfPacePeriodMs, 0, 34);
        PeakSoftStart = Math.Clamp(PeakSoftStart, 0.5, 1.0);
        SoftStartMs = Math.Clamp(SoftStartMs, 0, 2000);
        if (SoftStartMs > 0.001)
        {
            BootEaseIn = true;
            SoftStartMs = 0;
        }
        Deadband = Math.Clamp(Deadband, 0, 0.05);
        MaxSlewPerSecond = Math.Clamp(MaxSlewPerSecond, 0, 200);
        MaxSpikeStep = Math.Clamp(MaxSpikeStep, 0, 1.0);
        // Legacy: 100% meant off (slider could not reach 0). Treat as off.
        if (MaxSpikeStep >= 0.999)
            MaxSpikeStep = 0;
        MagnitudeEpsilon = Math.Clamp(MagnitudeEpsilon, 0, 64);
        CenterSpringStrength = Math.Clamp(CenterSpringStrength, 0.05, 1.0);
        CenterSpringRange = Math.Clamp(CenterSpringRange, 0.05, 0.5);
        CenterSpringDeadzone = Math.Clamp(CenterSpringDeadzone, 0, 0.05);
        DamperVelocityScale = Math.Clamp(DamperVelocityScale, 0.25, 2.0);
        DamperDeadbandScale = Math.Clamp(DamperDeadbandScale, 0.1, 1.0);
        SpringCoefficientScale = Math.Clamp(SpringCoefficientScale, 0, 2.0);
        FrictionCoefficientScale = Math.Clamp(FrictionCoefficientScale, 0, 2.0);
    }

    /// <summary>
    /// Forced centering torque (app convention, + = right) for a rim position
    /// (-1..1 = full lock) and rim velocity (full-rotations per second).
    /// </summary>
    public float ComputeCenterSpring(float rim, float rimVelocity)
    {
        if (!ForceCenterSpring)
            return 0f;
        var strength = (float)Math.Clamp(CenterSpringStrength, 0.05, 1.0);
        var range = (float)Math.Clamp(CenterSpringRange, 0.05, 0.5);
        var dead = (float)Math.Clamp(CenterSpringDeadzone, 0, 0.05);

        var offset = Math.Abs(rim) - dead;
        if (offset <= 0f)
            offset = 0f;
        var spring = -Math.Sign(rim) * strength * Math.Min(1f, offset / range);
        // Light damping so direct-drive bases don't oscillate around center.
        var damping = -rimVelocity * strength * 0.08f;
        return Math.Clamp(spring + damping, -strength, strength);
    }

    /// <summary>True when any ShapeGameTorque-style control is active.</summary>
    public bool HasTorqueShaping =>
        Deadband > 0.0005 ||
        MaxSlewPerSecond > 0.5 ||
        MaxSpikeStep > 0.001 ||
        MagnitudeEpsilon > 0.5;

    public static FfbOutputFeel CreateDefault() => new();
}
