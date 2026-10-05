namespace G920Emulator.Core.Ffb;

/// <summary>
/// User-tunable output shaping applied after the game's DI effects are mixed.
/// Defaults leave the mix untouched (exact game feedback).
/// </summary>
public sealed class FfbOutputFeel
{
    /// <summary>
    /// Low-pass time constant in ms (0 = off). Higher = softer / less "raw" on DD bases.
    /// Useful range ~0–40.
    /// </summary>
    public double SmoothingMs { get; set; }

    /// <summary>
    /// Soft-knee starts at this |torque| (0.50–1.00). 1.00 = off (no peak compression).
    /// </summary>
    public double PeakSoftStart { get; set; } = 1.0;

    /// <summary>
    /// One-shot ease-in when FFB first appears (ms). 0 = off (default).
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
    /// Cap on a single-frame step toward the target (0.05–1.00). 1.00 = allow full steps (off).
    /// </summary>
    public double MaxSpikeStep { get; set; } = 1.0;

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

    /// <summary>Max centering torque (0.05–1.00) of the forced spring.</summary>
    public double CenterSpringStrength { get; set; } = 0.3;

    /// <summary>
    /// Rim offset (fraction of full rotation from center, 0.05–0.50) where the forced
    /// spring reaches full strength. Lower = stiffer near center.
    /// </summary>
    public double CenterSpringRange { get; set; } = 0.25;

    /// <summary>Centering deadzone around center (0–0.05 of full rotation).</summary>
    public double CenterSpringDeadzone { get; set; }

    /// <summary>
    /// Negate Constant Force only (Desktop classic mix). Independent of Invert FFB.
    /// Off by default (Raw = game CF sign).
    /// </summary>
    public bool InvertConstantForce { get; set; }

    /// <summary>
    /// Scale applied to rim velocity before damper/inertia condition eval (1.0 = off).
    /// Desktop classic used 2.0.
    /// </summary>
    public double DamperVelocityScale { get; set; } = 1.0;

    /// <summary>
    /// Scale applied to damper deadband before eval (1.0 = off). Desktop classic used ~0.33.
    /// </summary>
    public double DamperDeadbandScale { get; set; } = 1.0;

    public void Clamp()
    {
        SmoothingMs = Math.Clamp(SmoothingMs, 0, 40);
        PeakSoftStart = Math.Clamp(PeakSoftStart, 0.5, 1.0);
        SoftStartMs = Math.Clamp(SoftStartMs, 0, 2000);
        Deadband = Math.Clamp(Deadband, 0, 0.05);
        MaxSlewPerSecond = Math.Clamp(MaxSlewPerSecond, 0, 200);
        MaxSpikeStep = Math.Clamp(MaxSpikeStep, 0.05, 1.0);
        MagnitudeEpsilon = Math.Clamp(MagnitudeEpsilon, 0, 64);
        CenterSpringStrength = Math.Clamp(CenterSpringStrength, 0.05, 1.0);
        CenterSpringRange = Math.Clamp(CenterSpringRange, 0.05, 0.5);
        CenterSpringDeadzone = Math.Clamp(CenterSpringDeadzone, 0, 0.05);
        DamperVelocityScale = Math.Clamp(DamperVelocityScale, 0.25, 4.0);
        DamperDeadbandScale = Math.Clamp(DamperDeadbandScale, 0.1, 1.0);
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
        MaxSpikeStep < 0.999 ||
        MagnitudeEpsilon > 0.5;

    public static FfbOutputFeel CreateDefault() => new();
}
