namespace G920Emulator.Core.Telemetry;

/// <summary>User-tunable ranges/scales for simulated SimHub telemetry.</summary>
public sealed class TelemetryTuning
{
    public const float DefaultSpeedMinKmh = 0f;
    public const float DefaultSpeedMaxKmh = 350f;
    public const float DefaultRpmMin = 800f;
    public const float DefaultRpmMax = 8000f;
    /// <summary>
    /// Blocklayer "Shift At" RPM - gear top speeds and in-gear RPM peaks use this
    /// (SimHub EngineShiftRpm). Not a percent.
    /// </summary>
    public const float DefaultRpmRedline = 6000f;
    /// <summary>
    /// Ceiling for Blocklayer theoretical gear tops (tall overdrive + high Max RPM can exceed 400 km/h).
    /// UI MPH slider max follows this (~404 MPH at 650).
    /// </summary>
    public const float AbsoluteSpeedMaxKmh = 650f;
    public const float AbsoluteRpmMax = 12000f;

    public const float DefaultAccelKmhPerSec = 55f;
    public const float DefaultBrakeKmhPerSec = 90f;
    /// <summary>Baseline coast without aero - previous hard-coded 8 felt far too slow.</summary>
    public const float DefaultCoastKmhPerSec = 28f;
    public const float DefaultAeroDragScale = 1f;
    public const float DefaultGearPullScale = 1f;
    /// <summary>
    /// When above a gear's top speed (typical after downshift), bleed toward the cap at this rate.
    /// Previous hard clamp + ~0.2 s bleed felt like an instant snap.
    /// </summary>
    public const float DefaultGearSettleKmhPerSec = 40f;
    /// <summary>How hard FFB impact / heavy CF cuts simulated speed (walls, crashes). 0.5 = mild default.</summary>
    public const float DefaultCrashDumpScale = 0.5f;
    /// <summary>Arcade handbrake hold dump (km/h per second) at 100% slider.</summary>
    public const float DefaultHandbrakeKmhPerSec = 80f;
    /// <summary>Arcade NOS / turbo hold boost (km/h per second) at 100% slider.</summary>
    public const float DefaultNosBoostKmhPerSec = 40f;
    /// <summary>Rev-limiter hard-cut strength at 100% (~180 RPM hysteresis band).</summary>
    public const float DefaultRpmBounceAmount = 0.55f;
    /// <summary>Target hard-cut cycle rate (Hz) when pinned at redline / gear top.</summary>
    public const float DefaultRpmBounceHz = 12f;

    /// <summary>Blocklayer Diff Ratio (axle / final drive). Higher = shorter overall gearing.</summary>
    public const float DefaultDiffRatio = 3.42f;
    /// <summary>Blocklayer tire diameter (inches).</summary>
    public const float DefaultTireDiameterInches = 27f;
    /// <summary>Blocklayer MPH constant: MPH = (tire × RPM) / (336 × gear × diff).</summary>
    public const float BlocklayerMphConstant = 336f;
    public const float KmhPerMph = 1.609344f;
    public const float MphPerKmh = 1f / KmhPerMph;

    /// <summary>Absolute gearbox ratios (Blocklayer defaults - 6th is overdrive, not 1.0).</summary>
    public const float DefaultGear1Ratio = 2.97f;
    public const float DefaultGear2Ratio = 2.07f;
    public const float DefaultGear3Ratio = 1.43f;
    public const float DefaultGear4Ratio = 1.00f;
    public const float DefaultGear5Ratio = 0.84f;
    public const float DefaultGear6Ratio = 0.56f;
    public const float MinGearRatio = 0.30f;
    public const float MaxGearRatio = 8.00f;

    // Legacy fallbacks if max speeds exist without ratios.
    public const float DefaultGear1MaxKmh = 70f;
    public const float DefaultGear2MaxKmh = 120f;
    public const float DefaultGear3MaxKmh = 175f;
    public const float DefaultGear4MaxKmh = 230f;
    public const float DefaultGear5MaxKmh = 285f;
    public const float DefaultGear6MaxKmh = 350f;

    // Compat aliases for UI that still references relative clamps.
    public const float MinRelativeGearRatio = MinGearRatio;
    public const float MaxRelativeGearRatio = MaxGearRatio;

    public float SpeedMinKmh { get; set; } = DefaultSpeedMinKmh;
    public float SpeedMaxKmh { get; set; } = DefaultSpeedMaxKmh;
    public float RpmMin { get; set; } = DefaultRpmMin;
    public float RpmMax { get; set; } = DefaultRpmMax;

    /// <summary>
    /// Absolute redline / shift RPM sent as SimHub <c>EngineShiftRpm</c>.
    /// Clamped between idle and <see cref="RpmMax"/> (gauge/limiter = EngineMaxRpm).
    /// </summary>
    public float RpmRedline { get; set; } = DefaultRpmRedline;
    public float SurfaceRumbleScale { get; set; } = 1f;
    public float ImpactScale { get; set; } = 1f;
    public float RoadLoadScale { get; set; } = 1f;

    /// <summary>
    /// Scale for ShakeIt Engine vibrations force (1 = 100%). RPM is always sent;
    /// this multiplies the vibration intensity the SimHub RPM plugin applies.
    /// </summary>
    public float EngineVibrationScale { get; set; } = 1f;

    /// <summary>
    /// Hard-cut rev-limiter strength when pinned at redline / gear top
    /// (0 = off, 1 ≈ 180 RPM hysteresis, 2 ≈ 360 RPM).
    /// </summary>
    public float RpmBounceAmount { get; set; } = DefaultRpmBounceAmount;

    /// <summary>Hard-cut on/off cycle rate (Hz) while the rev-limiter is engaged.</summary>
    public float RpmBounceHz { get; set; } = DefaultRpmBounceHz;

    /// <summary>Full-throttle acceleration (km/h per second) before gear pull.</summary>
    public float AccelKmhPerSec { get; set; } = DefaultAccelKmhPerSec;

    /// <summary>Full-brake deceleration (km/h per second).</summary>
    public float BrakeKmhPerSec { get; set; } = DefaultBrakeKmhPerSec;

    /// <summary>Engine-braking / rolling coast (km/h per second) when throttle is off.</summary>
    public float CoastKmhPerSec { get; set; } = DefaultCoastKmhPerSec;

    /// <summary>Extra high-speed drag (0-2). At 1.0 ≈ +45 km/h/s loss near top speed.</summary>
    public float AeroDragScale { get; set; } = DefaultAeroDragScale;

    /// <summary>How strongly gear changes throttle pull (0 = ignore gear, 1 = default ratios, 2 = exaggerated).</summary>
    public float GearPullScale { get; set; } = DefaultGearPullScale;

    /// <summary>
    /// How fast simulated speed tapers down toward a lower gear's max when overspeeding (km/h per second).
    /// 0 = no settle (only coast / brake / aero reduce speed).
    /// </summary>
    public float GearSettleKmhPerSec { get; set; } = DefaultGearSettleKmhPerSec;

    /// <summary>
    /// FFB-based speed kill on impacts / heavy constant force (0-2).
    /// Guess only - games do not tell us we hit a wall.
    /// </summary>
    public float CrashDumpScale { get; set; } = DefaultCrashDumpScale;

    /// <summary>Speed dump while handbrake input binding is held (km/h per second).</summary>
    public float HandbrakeKmhPerSec { get; set; } = DefaultHandbrakeKmhPerSec;

    /// <summary>Extra accel while NOS input binding is held (km/h per second); still capped by gear max.</summary>
    public float NosBoostKmhPerSec { get; set; } = DefaultNosBoostKmhPerSec;

    /// <summary>Absolute gearbox ratio for each gear (Blocklayer). 0 = derive from max speed / defaults.</summary>
    public float Gear1Ratio { get; set; }
    public float Gear2Ratio { get; set; }
    public float Gear3Ratio { get; set; }
    public float Gear4Ratio { get; set; }
    public float Gear5Ratio { get; set; }
    public float Gear6Ratio { get; set; }

    /// <summary>Cached max speeds at Max RPM (derived from ratios; kept for settings/UI).</summary>
    public float Gear1MaxKmh { get; set; }
    public float Gear2MaxKmh { get; set; }
    public float Gear3MaxKmh { get; set; }
    public float Gear4MaxKmh { get; set; }
    public float Gear5MaxKmh { get; set; }
    public float Gear6MaxKmh { get; set; }

    /// <summary>Differential / final-drive ratio (Blocklayer Diff Ratio).</summary>
    public float DiffRatio { get; set; } = DefaultDiffRatio;

    /// <summary>Tire diameter in inches (Blocklayer).</summary>
    public float TireDiameterInches { get; set; } = DefaultTireDiameterInches;

    public static TelemetryTuning CreateDefault()
    {
        var t = new TelemetryTuning
        {
            Gear1Ratio = DefaultGear1Ratio,
            Gear2Ratio = DefaultGear2Ratio,
            Gear3Ratio = DefaultGear3Ratio,
            Gear4Ratio = DefaultGear4Ratio,
            Gear5Ratio = DefaultGear5Ratio,
            Gear6Ratio = DefaultGear6Ratio,
            DiffRatio = DefaultDiffRatio,
            TireDiameterInches = DefaultTireDiameterInches,
        };
        t.RecalculateGearMaxSpeedsFromRatios();
        t.Clamp();
        return t;
    }

    public void Clamp()
    {
        SpeedMaxKmh = Math.Clamp(SpeedMaxKmh <= 0 ? DefaultSpeedMaxKmh : SpeedMaxKmh, 20f, AbsoluteSpeedMaxKmh);
        SpeedMinKmh = 0f; // UI is max-only; kept for settings compatibility.
        RpmMax = Math.Clamp(RpmMax <= 0 ? DefaultRpmMax : RpmMax, 1000f, AbsoluteRpmMax);
        RpmMin = Math.Clamp(RpmMin, 0f, RpmMax);
        // Missing/legacy profiles: treat redline as max (previous single-thumb behavior).
        if (RpmRedline <= 0)
            RpmRedline = RpmMax;
        RpmRedline = Math.Clamp(RpmRedline, Math.Max(RpmMin, 100f), RpmMax);
        SurfaceRumbleScale = Math.Clamp(SurfaceRumbleScale, 0f, 2f);
        ImpactScale = Math.Clamp(ImpactScale, 0f, 2f);
        RoadLoadScale = Math.Clamp(RoadLoadScale, 0f, 2f);
        EngineVibrationScale = Math.Clamp(EngineVibrationScale, 0f, 2f);
        RpmBounceAmount = Math.Clamp(RpmBounceAmount, 0f, 2f);
        RpmBounceHz = Math.Clamp(RpmBounceHz <= 0 ? DefaultRpmBounceHz : RpmBounceHz, 2f, 30f);
        AccelKmhPerSec = Math.Clamp(AccelKmhPerSec <= 0 ? DefaultAccelKmhPerSec : AccelKmhPerSec, 5f, 200f);
        BrakeKmhPerSec = Math.Clamp(BrakeKmhPerSec <= 0 ? DefaultBrakeKmhPerSec : BrakeKmhPerSec, 10f, 300f);
        CoastKmhPerSec = Math.Clamp(CoastKmhPerSec < 0 ? DefaultCoastKmhPerSec : CoastKmhPerSec, 0f, 120f);
        AeroDragScale = Math.Clamp(AeroDragScale, 0f, 2f);
        GearPullScale = Math.Clamp(GearPullScale, 0f, 2f);
        GearSettleKmhPerSec = Math.Clamp(
            GearSettleKmhPerSec < 0 ? DefaultGearSettleKmhPerSec : GearSettleKmhPerSec, 0f, 200f);
        CrashDumpScale = Math.Clamp(CrashDumpScale, 0f, 2f);
        HandbrakeKmhPerSec = Math.Clamp(
            HandbrakeKmhPerSec < 0 ? DefaultHandbrakeKmhPerSec : HandbrakeKmhPerSec, 0f, 200f);
        NosBoostKmhPerSec = Math.Clamp(
            NosBoostKmhPerSec < 0 ? DefaultNosBoostKmhPerSec : NosBoostKmhPerSec, 0f, 150f);
        DiffRatio = Math.Clamp(DiffRatio <= 0 ? DefaultDiffRatio : DiffRatio, 1.5f, 6.5f);
        TireDiameterInches = Math.Clamp(
            TireDiameterInches <= 0 ? DefaultTireDiameterInches : TireDiameterInches, 18f, 40f);

        EnsureGearRatios();
        RecalculateGearMaxSpeedsFromRatios();
        SyncSpeedMaxFromTopGear();
    }

    /// <summary>
    /// RPM used for Blocklayer gear tops / chart peaks - the shift/redline point
    /// (matches blocklayer.com "Shift At"), not gauge Max RPM.
    /// </summary>
    public float ChartRpm => Math.Max(100f, RpmRedline > 0 ? RpmRedline : RpmMax);

    /// <summary>
    /// Blocklayer: MPH = (tire × RPM) / (336 × gearRatio × diff).
    /// Max road speed in a gear is that formula at <see cref="ChartRpm"/> (shift/redline).
    /// </summary>
    public float MaxSpeedKmhForGearRatio(float gearRatio)
    {
        var g = Math.Clamp(gearRatio <= 0 ? 1f : gearRatio, MinGearRatio, MaxGearRatio);
        var diff = Math.Max(0.5f, DiffRatio);
        var tire = Math.Max(10f, TireDiameterInches);
        var rpm = ChartRpm;
        var mph = (tire * rpm) / (BlocklayerMphConstant * g * diff);
        return Math.Clamp(mph * KmhPerMph, 5f, AbsoluteSpeedMaxKmh);
    }

    /// <summary>Inverse: gearbox ratio from a gear's max speed at shift/redline RPM.</summary>
    public float GearRatioForMaxSpeedKmh(float maxSpeedKmh)
    {
        var mph = Math.Max(1f, maxSpeedKmh * MphPerKmh);
        var diff = Math.Max(0.5f, DiffRatio);
        var tire = Math.Max(10f, TireDiameterInches);
        var rpm = ChartRpm;
        var ratio = (tire * rpm) / (BlocklayerMphConstant * diff * mph);
        return Math.Clamp(ratio, MinGearRatio, MaxGearRatio);
    }

    public void RecalculateGearMaxSpeedsFromRatios()
    {
        Gear1MaxKmh = MaxSpeedKmhForGearRatio(Gear1Ratio);
        Gear2MaxKmh = MaxSpeedKmhForGearRatio(Gear2Ratio);
        Gear3MaxKmh = MaxSpeedKmhForGearRatio(Gear3Ratio);
        Gear4MaxKmh = MaxSpeedKmhForGearRatio(Gear4Ratio);
        Gear5MaxKmh = MaxSpeedKmhForGearRatio(Gear5Ratio);
        Gear6MaxKmh = MaxSpeedKmhForGearRatio(Gear6Ratio);
    }

    private void EnsureGearRatios()
    {
        // Legacy: only max speeds - derive ratios. Else fall back to Blocklayer defaults.
        Gear1Ratio = ResolveRatio(Gear1Ratio, Gear1MaxKmh, DefaultGear1Ratio);
        Gear2Ratio = ResolveRatio(Gear2Ratio, Gear2MaxKmh, DefaultGear2Ratio);
        Gear3Ratio = ResolveRatio(Gear3Ratio, Gear3MaxKmh, DefaultGear3Ratio);
        Gear4Ratio = ResolveRatio(Gear4Ratio, Gear4MaxKmh, DefaultGear4Ratio);
        Gear5Ratio = ResolveRatio(Gear5Ratio, Gear5MaxKmh, DefaultGear5Ratio);
        Gear6Ratio = ResolveRatio(Gear6Ratio, Gear6MaxKmh, DefaultGear6Ratio);
    }

    private float ResolveRatio(float ratio, float maxKmh, float fallback)
    {
        if (ratio > 0)
            return ClampGearRatio(ratio, fallback);
        if (maxKmh > 0)
            return GearRatioForMaxSpeedKmh(maxKmh);
        return fallback;
    }

    private static float ClampGearRatio(float value, float fallback) =>
        Math.Clamp(value <= 0 ? fallback : value, MinGearRatio, MaxGearRatio);

    /// <summary>Tallest gear max speed (often 6th overdrive).</summary>
    public float TopGearMaxKmh
    {
        get
        {
            var tallest = Gear1MaxKmh;
            if (Gear2MaxKmh > tallest) tallest = Gear2MaxKmh;
            if (Gear3MaxKmh > tallest) tallest = Gear3MaxKmh;
            if (Gear4MaxKmh > tallest) tallest = Gear4MaxKmh;
            if (Gear5MaxKmh > tallest) tallest = Gear5MaxKmh;
            if (Gear6MaxKmh > tallest) tallest = Gear6MaxKmh;
            return Math.Max(5f, tallest);
        }
    }

    /// <summary>Top speed for a gear (1-6 / R). N is uncapped (SpeedMax).</summary>
    public float GearTopSpeedKmh(string gear)
    {
        return gear switch
        {
            "1" => Gear1MaxKmh,
            "2" => Gear2MaxKmh,
            "3" => Gear3MaxKmh,
            "4" => Gear4MaxKmh,
            "5" => Gear5MaxKmh,
            "6" => Gear6MaxKmh,
            "R" => Math.Clamp(Gear1MaxKmh * 1.1f, 5f, AbsoluteSpeedMaxKmh),
            _ => SpeedMaxKmh,
        };
    }

    /// <summary>Absolute Blocklayer gearbox ratio for a gear.</summary>
    public float AbsoluteGearRatio(string gear) => gear switch
    {
        "1" => Gear1Ratio,
        "2" => Gear2Ratio,
        "3" => Gear3Ratio,
        "4" => Gear4Ratio,
        "5" => Gear5Ratio,
        "6" => Gear6Ratio,
        "R" => Gear1Ratio * 1.05f,
        _ => 1f,
    };

    public float AbsoluteGearRatio(int gear) =>
        AbsoluteGearRatio(gear is >= 1 and <= 6 ? gear.ToString() : "N");

    /// <summary>Alias used by older UI helpers - absolute gearbox ratio (not forced 1.0 on 6th).</summary>
    public float RelativeGearRatio(string gear) => AbsoluteGearRatio(gear);

    public float RelativeGearRatio(int gear) => AbsoluteGearRatio(gear);

    public void SetGearRatio(int gear, float ratio)
    {
        var r = ClampGearRatio(ratio, 1f);
        switch (gear)
        {
            case 1: Gear1Ratio = r; break;
            case 2: Gear2Ratio = r; break;
            case 3: Gear3Ratio = r; break;
            case 4: Gear4Ratio = r; break;
            case 5: Gear5Ratio = r; break;
            case 6: Gear6Ratio = r; break;
        }
        RecalculateGearMaxSpeedsFromRatios();
        SyncSpeedMaxFromTopGear();
    }

    /// <summary>Edit max speed → recompute that gear's absolute ratio (Blocklayer inverse).</summary>
    public void SetGearMaxKmh(int gear, float maxKmh)
    {
        var value = Math.Clamp(maxKmh <= 0 ? 5f : maxKmh, 5f, AbsoluteSpeedMaxKmh);
        var ratio = GearRatioForMaxSpeedKmh(value);
        SetGearRatio(gear, ratio);
    }

    /// <summary>Throttle pull from absolute gear ratio × diff.</summary>
    public float GearPullFactor(string gear)
    {
        var r = AbsoluteGearRatio(gear);
        var fromGear = Math.Clamp(0.45f + 0.28f * r, 0.45f, 1.75f);
        var fromDiff = Math.Clamp(DiffRatio / DefaultDiffRatio, 0.7f, 1.45f);
        return Math.Clamp(fromGear * fromDiff, 0.45f, 1.85f);
    }

    public float GearRpmCoupling(string gear) =>
        Math.Clamp(1f / Math.Max(0.35f, AbsoluteGearRatio(gear)), 0.15f, 1f);

    public float RpmAtSpeedKmh(string gear, float speedKmh)
    {
        var cap = GearTopSpeedKmh(gear);
        var chart = ChartRpm;
        if (cap < 1f || speedKmh <= 0f)
            return 0f;
        return Math.Clamp(chart * (speedKmh / cap), 0f, Math.Max(chart, RpmMax));
    }

    public float SpeedAtRpmKmh(string gear, float rpm)
    {
        var cap = GearTopSpeedKmh(gear);
        var chart = ChartRpm;
        if (chart < 1f || rpm <= 0f)
            return 0f;
        return Math.Clamp(cap * (rpm / chart), 0f, cap);
    }

    /// <summary>Redline / Diff / Tire changed - recompute max speeds; gearbox ratios stay fixed.</summary>
    public void RefreshMaxSpeedsKeepingRatios()
    {
        EnsureGearRatios();
        RecalculateGearMaxSpeedsFromRatios();
        SyncSpeedMaxFromTopGear();
    }

    public void ScaleGearMaxesForDiffChange(float previousDiff, float newDiff)
    {
        DiffRatio = newDiff;
        RefreshMaxSpeedsKeepingRatios();
    }

    public void ScaleGearMaxesForRpmChange(float previousRpmMax, float newRpmMax)
    {
        RpmMax = newRpmMax;
        if (RpmRedline > RpmMax)
            RpmRedline = RpmMax;
        RefreshMaxSpeedsKeepingRatios();
    }

    public void SyncSpeedMaxFromTopGear()
    {
        SpeedMaxKmh = Math.Clamp(Math.Max(TopGearMaxKmh, 20f), 20f, AbsoluteSpeedMaxKmh);
    }

    public string FormatGearTopSpeeds()
    {
        static string Cap(float kmh) => kmh >= 100 ? $"{kmh:0}" : $"{kmh:0.#}";
        string Pair(int g, float kmh, float ratio) => $"{g}:{Cap(kmh)}@{ratio:0.##}";
        return string.Join(" · ",
            Pair(1, Gear1MaxKmh, Gear1Ratio),
            Pair(2, Gear2MaxKmh, Gear2Ratio),
            Pair(3, Gear3MaxKmh, Gear3Ratio),
            Pair(4, Gear4MaxKmh, Gear4Ratio),
            Pair(5, Gear5MaxKmh, Gear5Ratio),
            Pair(6, Gear6MaxKmh, Gear6Ratio));
    }

    public TelemetryTuning Clone()
    {
        var copy = new TelemetryTuning
        {
            SpeedMinKmh = SpeedMinKmh,
            SpeedMaxKmh = SpeedMaxKmh,
            RpmMin = RpmMin,
            RpmMax = RpmMax,
            RpmRedline = RpmRedline,
            SurfaceRumbleScale = SurfaceRumbleScale,
            ImpactScale = ImpactScale,
            RoadLoadScale = RoadLoadScale,
            EngineVibrationScale = EngineVibrationScale,
            RpmBounceAmount = RpmBounceAmount,
            RpmBounceHz = RpmBounceHz,
            AccelKmhPerSec = AccelKmhPerSec,
            BrakeKmhPerSec = BrakeKmhPerSec,
            CoastKmhPerSec = CoastKmhPerSec,
            AeroDragScale = AeroDragScale,
            GearPullScale = GearPullScale,
            GearSettleKmhPerSec = GearSettleKmhPerSec,
            CrashDumpScale = CrashDumpScale,
            HandbrakeKmhPerSec = HandbrakeKmhPerSec,
            NosBoostKmhPerSec = NosBoostKmhPerSec,
            Gear1Ratio = Gear1Ratio,
            Gear2Ratio = Gear2Ratio,
            Gear3Ratio = Gear3Ratio,
            Gear4Ratio = Gear4Ratio,
            Gear5Ratio = Gear5Ratio,
            Gear6Ratio = Gear6Ratio,
            Gear1MaxKmh = Gear1MaxKmh,
            Gear2MaxKmh = Gear2MaxKmh,
            Gear3MaxKmh = Gear3MaxKmh,
            Gear4MaxKmh = Gear4MaxKmh,
            Gear5MaxKmh = Gear5MaxKmh,
            Gear6MaxKmh = Gear6MaxKmh,
            DiffRatio = DiffRatio,
            TireDiameterInches = TireDiameterInches,
        };
        copy.Clamp();
        return copy;
    }
}
