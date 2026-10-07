namespace G920Emulator.Core.Telemetry;

/// <summary>User-tunable ranges/scales for estimated SimHub telemetry.</summary>
public sealed class TelemetryTuning
{
    public const float DefaultSpeedMinKmh = 0f;
    public const float DefaultSpeedMaxKmh = 350f;
    public const float DefaultRpmMin = 800f;
    public const float DefaultRpmMax = 8000f;
    /// <summary>Shift / redline point in absolute RPM (SimHub EngineShiftRpm). Not a percent.</summary>
    public const float DefaultRpmRedline = 7200f;
    public const float AbsoluteSpeedMaxKmh = 400f;
    public const float AbsoluteRpmMax = 12000f;

    public const float DefaultAccelKmhPerSec = 55f;
    public const float DefaultBrakeKmhPerSec = 90f;
    /// <summary>Baseline coast without aero — previous hard-coded 8 felt far too slow.</summary>
    public const float DefaultCoastKmhPerSec = 28f;
    public const float DefaultAeroDragScale = 1f;
    public const float DefaultGearPullScale = 1f;
    /// <summary>
    /// When above a gear's top speed (typical after downshift), bleed toward the cap at this rate.
    /// Previous hard clamp + ~0.2 s bleed felt like an instant snap.
    /// </summary>
    public const float DefaultGearSettleKmhPerSec = 40f;
    /// <summary>How hard FFB impact / heavy CF cuts estimated speed (walls, crashes). 0.5 = mild default.</summary>
    public const float DefaultCrashDumpScale = 0.5f;
    /// <summary>Rev-limiter bounce depth at 100% (fraction of idle–max span dipped each flutter).</summary>
    public const float DefaultRpmBounceAmount = 0.55f;
    /// <summary>How fast the limiter flutters when pinned at gear top / redline.</summary>
    public const float DefaultRpmBounceHz = 12f;

    public const float DefaultGear1MaxKmh = 70f;
    public const float DefaultGear2MaxKmh = 120f;
    public const float DefaultGear3MaxKmh = 175f;
    public const float DefaultGear4MaxKmh = 230f;
    public const float DefaultGear5MaxKmh = 285f;
    public const float DefaultGear6MaxKmh = 350f;

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
    /// Rev-limiter bounce depth when speed/RPM is pinned at the gear top (0 = off, 1 = default depth, 2 = strong).
    /// </summary>
    public float RpmBounceAmount { get; set; } = DefaultRpmBounceAmount;

    /// <summary>Flutter rate (Hz) for the rev-limiter bounce.</summary>
    public float RpmBounceHz { get; set; } = DefaultRpmBounceHz;

    /// <summary>Full-throttle acceleration (km/h per second) before gear pull.</summary>
    public float AccelKmhPerSec { get; set; } = DefaultAccelKmhPerSec;

    /// <summary>Full-brake deceleration (km/h per second).</summary>
    public float BrakeKmhPerSec { get; set; } = DefaultBrakeKmhPerSec;

    /// <summary>Engine-braking / rolling coast (km/h per second) when throttle is off.</summary>
    public float CoastKmhPerSec { get; set; } = DefaultCoastKmhPerSec;

    /// <summary>Extra high-speed drag (0–2). At 1.0 ≈ +45 km/h/s loss near top speed.</summary>
    public float AeroDragScale { get; set; } = DefaultAeroDragScale;

    /// <summary>How strongly gear changes throttle pull (0 = ignore gear, 1 = default ratios, 2 = exaggerated).</summary>
    public float GearPullScale { get; set; } = DefaultGearPullScale;

    /// <summary>
    /// How fast estimated speed tapers down toward a lower gear's max when overspeeding (km/h per second).
    /// 0 = no settle (only coast / brake / aero reduce speed).
    /// </summary>
    public float GearSettleKmhPerSec { get; set; } = DefaultGearSettleKmhPerSec;

    /// <summary>
    /// FFB-based speed kill on impacts / heavy constant force (0–2).
    /// Guess only — games do not tell us we hit a wall.
    /// </summary>
    public float CrashDumpScale { get; set; } = DefaultCrashDumpScale;

    public float Gear1MaxKmh { get; set; } = DefaultGear1MaxKmh;
    public float Gear2MaxKmh { get; set; } = DefaultGear2MaxKmh;
    public float Gear3MaxKmh { get; set; } = DefaultGear3MaxKmh;
    public float Gear4MaxKmh { get; set; } = DefaultGear4MaxKmh;
    public float Gear5MaxKmh { get; set; } = DefaultGear5MaxKmh;
    public float Gear6MaxKmh { get; set; } = DefaultGear6MaxKmh;

    public static TelemetryTuning CreateDefault() => new();

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

        Gear1MaxKmh = ClampGearMax(Gear1MaxKmh, DefaultGear1MaxKmh);
        Gear2MaxKmh = ClampGearMax(Gear2MaxKmh, DefaultGear2MaxKmh);
        Gear3MaxKmh = ClampGearMax(Gear3MaxKmh, DefaultGear3MaxKmh);
        Gear4MaxKmh = ClampGearMax(Gear4MaxKmh, DefaultGear4MaxKmh);
        Gear5MaxKmh = ClampGearMax(Gear5MaxKmh, DefaultGear5MaxKmh);
        Gear6MaxKmh = ClampGearMax(Gear6MaxKmh, DefaultGear6MaxKmh);
    }

    private float ClampGearMax(float value, float fallback) =>
        Math.Clamp(value <= 0 ? fallback : value, 5f, SpeedMaxKmh);

    /// <summary>Top speed for a gear (1–6 / R). N is uncapped (SpeedMax).</summary>
    public float GearTopSpeedKmh(string gear)
    {
        Clamp();
        return gear switch
        {
            "1" => Gear1MaxKmh,
            "2" => Gear2MaxKmh,
            "3" => Gear3MaxKmh,
            "4" => Gear4MaxKmh,
            "5" => Gear5MaxKmh,
            "6" => Gear6MaxKmh,
            "R" => Math.Clamp(Gear1MaxKmh * 1.1f, 5f, SpeedMaxKmh),
            _ => SpeedMaxKmh,
        };
    }

    public string FormatGearTopSpeeds()
    {
        Clamp();
        static string Cap(float kmh) => kmh >= 100 ? $"{kmh:0}" : $"{kmh:0.#}";
        return string.Join(" · ",
            $"1:{Cap(Gear1MaxKmh)}",
            $"2:{Cap(Gear2MaxKmh)}",
            $"3:{Cap(Gear3MaxKmh)}",
            $"4:{Cap(Gear4MaxKmh)}",
            $"5:{Cap(Gear5MaxKmh)}",
            $"6:{Cap(Gear6MaxKmh)}");
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
            Gear1MaxKmh = Gear1MaxKmh,
            Gear2MaxKmh = Gear2MaxKmh,
            Gear3MaxKmh = Gear3MaxKmh,
            Gear4MaxKmh = Gear4MaxKmh,
            Gear5MaxKmh = Gear5MaxKmh,
            Gear6MaxKmh = Gear6MaxKmh,
        };
        copy.Clamp();
        return copy;
    }
}
