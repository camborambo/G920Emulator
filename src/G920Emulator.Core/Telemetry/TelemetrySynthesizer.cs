using G920Emulator.Core.Ffb;
using G920Emulator.Core.Models;

namespace G920Emulator.Core.Telemetry;

/// <summary>
/// Arcade estimates from mapped G920 inputs plus OEM FFB mix.
/// Not real game physics — for titles with no telemetry API.
/// </summary>
public sealed class TelemetrySynthesizer
{
    private const float MenuDecayPerSec = 4f;
    private const float ImpactThreshold = 0.12f;
    private const float ImpactDecayPerSec = 4.5f;
    private const float RumbleSmoothSec = 0.05f;
    private const float GForceSmoothSec = 0.08f;
    private const float KmhToMps = 1f / 3.6f;
    /// <summary>At full AeroDragScale and top speed, extra coast ≈ this many km/h per second.</summary>
    private const float AeroDragKmhPerSecAtVmax = 45f;
    /// <summary>At Impact dump 100% and no throttle, one impact removes this fraction of speed.</summary>
    private const float ImpactSpeedLossAtFull = 0.28f;
    /// <summary>Ignore further impact dumps for this long so FFB chatter cannot stack to ~0.</summary>
    private const float ImpactDumpCooldownSec = 0.4f;
    /// <summary>Continuous scrub from heavy |CF| at Impact dump 100% (km/h per second at |CF|=1).</summary>
    private const float ScrubKmhPerSecAtFullCf = 40f;
    /// <summary>At ~100 km/h full lock ≈ 1.2 g lateral.</summary>
    private const float LateralCurvature = 0.015f;
    private const float MaxSurgeMs2 = 28f;
    private const float MaxSwayMs2 = 22f;
    private const float MaxHeaveMs2 = 18f;

    private TelemetryTuning _tuning = TelemetryTuning.CreateDefault();
    private float _speedKmh;
    private float _rpm;
    private float _impact;
    private float _rumble;
    private float _surgeMs2;
    private float _swayMs2;
    private float _heaveMs2;
    private float _prevSpeedMps;
    private string _lastGear = "N";
    private int _paddleGear = 1;
    private bool _paddleArmed;
    private bool _prevPaddleLeft;
    private bool _prevPaddleRight;
    private float _prevCf;
    private float _prevPeriodic;
    private float _impactDumpCooldown;
    private float _suspensionPhase;
    private float _rpmBouncePhase;
    /// <summary>Keeps ignition/idle alive briefly after pedals/speed go quiet (N only).</summary>
    private float _engineLingerSec;

    public void Configure(TelemetryTuning? tuning)
    {
        _tuning = (tuning ?? TelemetryTuning.CreateDefault()).Clone();
    }

    public TelemetryFrame Update(
        MappedG920State mapped,
        float steering,
        OemFfbSharedMemory.Snapshot? oem,
        bool knownGameRunning,
        double dtSec)
    {
        dtSec = Math.Clamp(dtSec, 0.0005, 0.05);
        var throttle = Math.Clamp(mapped.Throttle, 0f, 1f);
        var brake = Math.Clamp(mapped.Brake, 0f, 1f);
        var clutch = Math.Clamp(mapped.Clutch, 0f, 1f);
        var steer = Math.Clamp(steering, -1f, 1f);
        var speedMax = Math.Max(20f, _tuning.SpeedMaxKmh);
        var rpmMin = Math.Clamp(_tuning.RpmMin, 0f, _tuning.RpmMax);
        var rpmMax = Math.Max(rpmMin + 100f, _tuning.RpmMax);
        var rpmRedline = Math.Clamp(_tuning.RpmRedline <= 0 ? rpmMax : _tuning.RpmRedline, rpmMin + 50f, rpmMax);

        var type = oem?.CombinedTypeTorqueDi() ?? [];
        static float Di(int[] t, int i) => i < t.Length ? Math.Clamp(t[i] / 10000f, -1f, 1f) : 0f;
        static float AbsDi(int[] t, int i) => MathF.Abs(Di(t, i));

        var cf = Di(type, 0);
        // Signed mix for FfbPeriodic (can cancel across wave types).
        var periodic = Math.Clamp(
            Di(type, 2) + Di(type, 3) + Di(type, 4) + Di(type, 5) + Di(type, 6),
            -1f, 1f);
        // Vibration magnitude must sum absolutes — opposite-phase sine/square was wiping rumble to ~0.
        // Include CustomForce (11); many titles put curb/crash shake there, not only Sine/Square.
        var vibMag = Math.Clamp(
            AbsDi(type, 2) + AbsDi(type, 3) + AbsDi(type, 4) + AbsDi(type, 5) + AbsDi(type, 6)
            + AbsDi(type, 11),
            0f, 1f);
        var spring = Di(type, 7);
        var damper = Di(type, 8);
        var friction = AbsDi(type, 10);

        var playing = oem is { } s && (s.Playing || (s.AuxPlaying && !s.IsAuxStale())) && !s.IsStale();
        var session = knownGameRunning || playing;

        var gear = ResolveGear(mapped);
        var prevGear = _lastGear;
        var gearChanged = !string.Equals(gear, prevGear, StringComparison.Ordinal);
        if (gearChanged)
            _lastGear = gear;

        // Neutral = not in gear: no throttle acceleration (speed may only coast/brake down).
        var inGear = gear is not "N";
        var driveGear = inGear ? gear : "N";

        var gearRatio = GearRpmRatio(driveGear);
        var gearPull = 1f + (GearPullFactor(driveGear) - 1f) * _tuning.GearPullScale;
        var clutchFactor = 1f - Math.Clamp(clutch, 0f, 1f) * 0.85f;
        // Each gear has a top speed (from 1st-top % + curve). Neutral keeps the current ceiling for coast.
        var gearCap = inGear ? _tuning.GearTopSpeedKmh(driveGear) : Math.Max(_speedKmh, 1f);

        var load = MathF.Abs(cf) + vibMag;
        var delta = Math.Max(MathF.Abs(cf - _prevCf), MathF.Abs(vibMag - _prevPeriodic));
        var impactHit = session && delta > ImpactThreshold && load > 0.08f;
        if (impactHit)
            _impact = 1f;
        else
            _impact = Math.Max(0f, _impact - ImpactDecayPerSec * (float)dtSec);
        _prevCf = cf;
        _prevPeriodic = vibMag;

        // Keep integrating while pedals are active so Live/SimHub stay testable without the title.
        var driving = throttle > 0.02f || brake > 0.02f || _speedKmh > 0.5f;
        // In gear (H-shifter held) = engine running at idle when stopped. Without this, speed
        // falling below 0.5 km/h ended the session and snapped RPM to 0 mid-taper.
        if (session || driving)
            _engineLingerSec = 4f;
        else if (!inGear)
            _engineLingerSec = Math.Max(0f, _engineLingerSec - (float)dtSec);
        var sessionRunning = session || driving || inGear || _engineLingerSec > 0f;
        var speedScale = speedMax / TelemetryTuning.DefaultSpeedMaxKmh;

        // Speed may still coast down when only "linger/in-gear idle" is keeping the session.
        if (!session && !driving)
        {
            _speedKmh = Math.Max(0f, _speedKmh - (float)(MenuDecayPerSec * 80.0 * dtSec));
        }
        else
        {
            var speedNorm = Math.Clamp(_speedKmh / speedMax, 0f, 1f);
            var accel = _tuning.AccelKmhPerSec * gearPull * clutchFactor;
            var throttle01 = Math.Clamp(throttle, 0f, 1f);
            // Pedal rest noise: ignore tiny brake under gas so it cannot stall the gear pin.
            var brakeEff = throttle01 >= 0.50f && brake < 0.04f ? 0f : brake;
            // Coast: off-throttle only (user rule).
            var coast = 0f;
            if (throttle01 < 0.02f)
            {
                coast = _tuning.CoastKmhPerSec;
                if (!inGear || clutch > 0.5f)
                    coast *= 1.35f;
            }
            // Aero from speed always, but ×(1−throttle)²: full when lifted (RPM falls),
            // ~0 at WOT so a 90–95% pedal can still reach the gear cap / Max RPM.
            var aeroNorm = inGear && gearCap > 1f
                ? Math.Clamp(_speedKmh / gearCap, 0f, 1f)
                : speedNorm;
            var lift = 1f - throttle01;
            var aero = AeroDragKmhPerSecAtVmax * _tuning.AeroDragScale * aeroNorm * aeroNorm
                       * lift * lift;
            // Heavy CF + lifted throttle ≈ scrubbing — fade out as gas comes back on.
            var scrub = 0f;
            var absCf = MathF.Abs(cf);
            if (absCf > 0.28f && throttle01 < 0.45f)
            {
                var scrubAmt = (absCf - 0.28f) / 0.72f * ScrubKmhPerSecAtFullCf * _tuning.CrashDumpScale;
                scrub = scrubAmt * (1f - throttle01 / 0.45f);
            }

            // Soft headroom: full accel until the last `band`, then ease off (√ so we can
            // still finish). Impact/FFB chatter can still invent a hard ceiling below the cap.
            var gap = inGear && gearCap > 1f ? Math.Max(0f, gearCap - _speedKmh) : 0f;
            var band = Math.Max(6f, gearCap * 0.14f);
            var linear = gap <= 0f ? 0f : Math.Clamp(gap / band, 0f, 1f);
            var headroom = linear <= 0f ? 0f : MathF.Sqrt(linear);
            if (inGear && gap > 0f)
            {
                var step = throttle01 * accel * speedScale * headroom * (float)dtSec;
                if (Math.Abs(damper) > 0.02f && throttle01 > 0.05f)
                    step += 4f * speedScale * gearPull * headroom * (float)dtSec;
                _speedKmh += Math.Min(gap, step);
            }
            _speedKmh -= brakeEff * _tuning.BrakeKmhPerSec * speedScale * (float)dtSec;
            _speedKmh -= (coast + aero + scrub) * speedScale * (float)dtSec;
            if (inGear && gearCap > 1f)
                _speedKmh = Math.Min(_speedKmh, gearCap);
        }

        _impactDumpCooldown = Math.Max(0f, _impactDumpCooldown - (float)dtSec);

        // Impact dump slider (0–200%): how hard an FFB spike cuts speed. Throttle softens the cut.
        // Under strong throttle, skip dumps — they invent a false ceiling below the gear cap.
        var nearGearPin = inGear && throttle > 0.70f;
        if (impactHit && _impactDumpCooldown <= 0f && _tuning.CrashDumpScale > 0f && _speedKmh > 1f
            && !nearGearPin)
        {
            var strength = Math.Clamp(_tuning.CrashDumpScale, 0f, 2f);
            var loss = ImpactSpeedLossAtFull * strength * (1f - throttle * 0.85f);
            // Holding gas: never wipe the estimate — floor keep rises with throttle.
            var minKeep = 0.5f + 0.45f * throttle;
            var keep = Math.Clamp(1f - loss, minKeep, 1f);
            _speedKmh *= keep;
            _impactDumpCooldown = ImpactDumpCooldownSec;
        }

        // Downshift / over-rev: taper toward the gear's ceiling — never hard-snap to gearCap.
        if (inGear && _speedKmh > gearCap && _tuning.GearSettleKmhPerSec > 0f)
        {
            var over = _speedKmh - gearCap;
            // Slightly stronger engine-brake feel the farther over the cap we are.
            var overFactor = 1f + Math.Clamp(over / Math.Max(25f, gearCap * 0.3f), 0f, 1.25f);
            var step = _tuning.GearSettleKmhPerSec * speedScale * overFactor * (float)dtSec;
            _speedKmh = Math.Max(gearCap, _speedKmh - step);
        }

        if (_speedKmh < 0.5f) _speedKmh = 0f;
        _speedKmh = Math.Clamp(_speedKmh, 0f, speedMax);

        var moving = _speedKmh > 1f || throttle > 0.04f;
        // RPM follows how far through the current gear's speed band we are (speed / gear cap).
        // Upshift raises the cap → fraction drops → RPM falls. Higher gears accelerate slower
        // (Gear pull), so RPM also builds slower. Throttle must NOT yank RPM to max at low speed.
        var gearSpeedFrac = inGear && gearCap > 1f
            ? Math.Clamp(_speedKmh / gearCap, 0f, 1f)
            : 0f;
        var t = Math.Clamp(throttle, 0f, 1f);
        float rpmFrac;
        if (!inGear)
        {
            // Neutral: free-rev with throttle (full pedal can hit RpmMax).
            rpmFrac = t * t;
        }
        else
        {
            var speedFracForRpm = gearSpeedFrac;
            // Small load pull above the speed-based RPM — more in low gears, never a full bypass.
            var loadPull = t * (0.12f + 0.10f * (1f - gearRatio)) * (1f - speedFracForRpm);
            // Clutch in: weaken road-speed coupling, allow a bit more throttle rev.
            if (clutch > 0.35f)
            {
                var c = (clutch - 0.35f) / 0.65f;
                speedFracForRpm *= 1f - 0.75f * c;
                loadPull = Math.Max(loadPull, t * 0.45f * c);
            }
            // Speed-linked only — do not snap rpmFrac to 1 before the gear pin (that jumped RPM).
            rpmFrac = Math.Clamp(speedFracForRpm + loadPull, 0f, 1f);
        }

        // Idle floor while the session is live (stopped in gear / N / lift). Never send 0 RPM
        // with ignition on — SimHub graphs and Engine vibrations expect a real idle.
        float targetRpm;
        if (!sessionRunning)
            targetRpm = 0f;
        else if (!moving || (gear is "N" && t < 0.02f))
            targetRpm = rpmMin;
        else
            targetRpm = rpmMin + (rpmMax - rpmMin) * rpmFrac;

        if (gearChanged && sessionRunning)
        {
            var prevIdx = GearIndex(prevGear);
            var nextIdx = GearIndex(gear);
            if (nextIdx > prevIdx && prevIdx >= 1)
            {
                // Upshift: drop most of the way to the new (lower) target immediately.
                _rpm = targetRpm + (_rpm - targetRpm) * 0.18f;
            }
            else if (nextIdx >= 1 && nextIdx < prevIdx)
            {
                // Downshift: allow a short over-rev toward the new higher target.
                _rpm = Math.Min(rpmMax, Math.Max(_rpm, targetRpm * 0.92f));
            }
        }

        // Climb slower in taller / weaker-pull gears; fall faster (upshift / lift).
        // Same tau all the way to the pin — no late "catch up to Max" shortcut (that jumped RPM).
        var climbSec = 0.07f + 0.18f * Math.Clamp(2f - gearPull, 0f, 1.6f);
        var fallSec = 0.045f;
        var rpmTau = targetRpm < _rpm - 15f ? fallSec : climbSec;
        var rpmAlpha = 1f - MathF.Exp((float)(-dtSec / Math.Max(0.02f, rpmTau)));
        _rpm += (targetRpm - _rpm) * rpmAlpha;
        if (sessionRunning)
            _rpm = Math.Clamp(_rpm, rpmMin, rpmMax);
        else
            _rpm = Math.Clamp(_rpm, 0f, rpmMax);

        // Rev-limiter flutter when pinned — dip from current RPM only (never retarget to Max).
        if (_tuning.RpmBounceAmount > 0.001f && sessionRunning && inGear && moving)
        {
            var gearPin = Math.Clamp((gearSpeedFrac - 0.82f) / 0.18f, 0f, 1f)
                          * Math.Clamp((t - 0.30f) / 0.45f, 0f, 1f);
            var rpmPin = Math.Clamp((rpmFrac - 0.88f) / 0.12f, 0f, 1f)
                         * Math.Clamp((t - 0.45f) / 0.40f, 0f, 1f);
            var engage = Math.Max(gearPin, rpmPin);
            if (engage > 0.01f)
            {
                var hz = Math.Clamp(_tuning.RpmBounceHz, 2f, 30f);
                _rpmBouncePhase += (float)(dtSec * Math.PI * 2.0 * hz);
                if (_rpmBouncePhase > MathF.PI * 64f)
                    _rpmBouncePhase -= MathF.PI * 64f;
                var wave = MathF.Pow(MathF.Abs(MathF.Sin(_rpmBouncePhase)), 0.55f);
                var depth = (rpmMax - rpmMin) * 0.10f * _tuning.RpmBounceAmount * engage;
                _rpm = Math.Clamp(_rpm - depth * wave, rpmMin, rpmMax);
            }
        }

        if (!sessionRunning)
            _rpm = 0f;

        var rumbleTarget = sessionRunning ? vibMag : 0f;
        var rumbleAlpha = 1f - MathF.Exp((float)(-dtSec / RumbleSmoothSec));
        _rumble += (rumbleTarget - _rumble) * rumbleAlpha;

        // Stay at 0 when stopped; otherwise report the integrated estimate.
        var reportSpeed = !sessionRunning || _speedKmh < 0.5f ? 0f : _speedKmh;

        var speedMps = reportSpeed * KmhToMps;
        float surgeTarget;
        float swayTarget;
        float heaveTarget;
        if (!sessionRunning)
        {
            surgeTarget = swayTarget = heaveTarget = 0f;
        }
        else
        {
            // Vehicle-frame longitudinal accel (m/s²): +throttle / −brake → SimHub LocalSurgeMs2
            // and the Live G-G circle (accel up / brake down).
            var speedDelta = (speedMps - _prevSpeedMps) / (float)dtSec;
            var reverse = gear is "R" ? -1f : 1f;
            surgeTarget = Math.Clamp(speedDelta * reverse, -MaxSurgeMs2, MaxSurgeMs2);

            swayTarget = Math.Clamp(
                -steer * speedMps * speedMps * LateralCurvature,
                -MaxSwayMs2,
                MaxSwayMs2);

            heaveTarget = Math.Clamp(
                _impact * 12f + MathF.Abs(_rumble) * 2.5f,
                -MaxHeaveMs2,
                MaxHeaveMs2);
        }

        _prevSpeedMps = speedMps;
        var gAlpha = 1f - MathF.Exp((float)(-dtSec / GForceSmoothSec));
        _surgeMs2 += (surgeTarget - _surgeMs2) * gAlpha;
        _swayMs2 += (swayTarget - _swayMs2) * gAlpha;
        _heaveMs2 += (heaveTarget - _heaveMs2) * gAlpha;
        if (!sessionRunning)
        {
            if (MathF.Abs(_surgeMs2) < 0.05f) _surgeMs2 = 0f;
            if (MathF.Abs(_swayMs2) < 0.05f) _swayMs2 = 0f;
            if (MathF.Abs(_heaveMs2) < 0.05f) _heaveMs2 = 0f;
        }

        var rumble = Math.Clamp(_rumble * _tuning.SurfaceRumbleScale, 0f, 1f);
        var impact = Math.Clamp(_impact * _tuning.ImpactScale, 0f, 1f);
        // Road load: constant force plus a touch of friction (tire scrub), not spring centering.
        var roadLoad = Math.Clamp((MathF.Abs(cf) + 0.35f * friction) * _tuning.RoadLoadScale, 0f, 1f);
        var rpmSpan = Math.Max(1f, rpmMax - _tuning.RpmMin);
        var rpmNorm = sessionRunning ? Math.Clamp((_rpm - _tuning.RpmMin) / rpmSpan, 0f, 1f) : 0f;
        var engineVibration = Math.Clamp(rpmNorm * _tuning.EngineVibrationScale, 0f, 1f);

        // ShakeIt "Road vibration" / kerbs use standard suspension + tyre-contact fields
        // (custom SurfaceRumble alone is not enough for those built-in effects).
        _suspensionPhase += (float)(dtSec * (18f + rumble * 40f + impact * 25f));
        if (_suspensionPhase > MathF.PI * 32f)
            _suspensionPhase -= MathF.PI * 32f;
        var (suspFl, suspFr, suspRl, suspRr) = BuildSuspensionVelocities(
            rumble, impact, _heaveMs2, reportSpeed, _suspensionPhase, sessionRunning);
        var (tyreFl, tyreFr, tyreRl, tyreRr) = BuildTyreContactSurfaces(rumble, impact, sessionRunning);

        // SimHub: EngineMaxRpm = gauge/limiter scale; EngineShiftRpm = absolute redline RPM
        // (not a %). SimHub may show that as a % of max in Car Settings — we always send RPM.

        // Do NOT set SessionPaused when OEM FFB goes quiet (ACTUATORSOFF / menus). SimHub
        // treats paused as a frozen/zeroed dash — idle RPM and Engine vibrations disappear
        // even though we still send packets. Estimated telemetry has no real pause signal.
        return new TelemetryFrame(
            SessionRunning: sessionRunning,
            SessionPaused: false,
            SpeedKmh: reportSpeed,
            EngineRpm: _rpm,
            EngineMaxRpm: rpmMax,
            EngineShiftRpm: rpmRedline,
            EngineIgnitionOn: sessionRunning,
            EngineStarted: sessionRunning,
            Throttle: throttle,
            Brake: brake,
            Clutch: clutch,
            Gear: gear,
            LocalSurgeMs2: _surgeMs2,
            LocalSwayMs2: _swayMs2,
            LocalHeaveMs2: _heaveMs2,
            SuspensionVelocityFrontLeftMps: suspFl,
            SuspensionVelocityFrontRightMps: suspFr,
            SuspensionVelocityRearLeftMps: suspRl,
            SuspensionVelocityRearRightMps: suspRr,
            TyreContactSurfaceFrontLeft: tyreFl,
            TyreContactSurfaceFrontRight: tyreFr,
            TyreContactSurfaceRearLeft: tyreRl,
            TyreContactSurfaceRearRight: tyreRr,
            Steering: steer,
            FfbConstant: cf,
            FfbSpring: spring,
            FfbDamper: damper,
            FfbPeriodic: periodic,
            SurfaceRumble: rumble,
            Impact: impact,
            RoadLoad: roadLoad,
            EngineVibration: engineVibration,
            GearSpeedFrac: gearSpeedFrac);
    }

    public void Reset()
    {
        _speedKmh = 0;
        _rpm = 0;
        _impact = 0;
        _rumble = 0;
        _surgeMs2 = _swayMs2 = _heaveMs2 = 0;
        _prevSpeedMps = 0;
        _lastGear = "N";
        _paddleGear = 1;
        _paddleArmed = false;
        _prevPaddleLeft = _prevPaddleRight = false;
        _prevCf = _prevPeriodic = 0;
        _impactDumpCooldown = 0;
        _suspensionPhase = 0;
        _rpmBouncePhase = 0;
        _engineLingerSec = 0;
    }

    private static (float Fl, float Fr, float Rl, float Rr) BuildSuspensionVelocities(
        float rumble,
        float impact,
        float heaveMs2,
        float speedKmh,
        float phase,
        bool sessionRunning)
    {
        if (!sessionRunning)
            return (0f, 0f, 0f, 0f);

        var speedFactor = Math.Clamp(speedKmh / 120f, 0.15f, 1.4f);
        // Light asphalt texture from speed so ShakeIt road vib is not silent when FFB rumble is 0.
        var roadTex = speedKmh > 8f ? 0.06f * speedFactor : 0f;
        var amp = (rumble * 0.55f + impact * 0.9f) * speedFactor
                  + MathF.Abs(heaveMs2) * 0.012f
                  + roadTex;
        if (amp < 0.01f)
            return (0f, 0f, 0f, 0f);

        // Stagger corners so ShakeIt road vib sees independent wheel motion.
        var fl = amp * MathF.Sin(phase);
        var fr = amp * MathF.Sin(phase + 1.7f);
        var rl = amp * MathF.Sin(phase + 3.1f);
        var rr = amp * MathF.Sin(phase + 4.6f);
        return (fl, fr, rl, rr);
    }

    private static (ushort Fl, ushort Fr, ushort Rl, ushort Rr) BuildTyreContactSurfaces(
        float rumble,
        float impact,
        bool sessionRunning)
    {
        if (!sessionRunning)
            return (0, 0, 0, 0);

        ushort surface = SimHubPacket.TyreContactPrimary;
        if (impact > 0.45f)
            surface = SimHubPacket.TyreContactGravel;
        else if (rumble > 0.12f || impact > 0.18f)
            surface = SimHubPacket.TyreContactRumbleStrips;

        return (surface, surface, surface, surface);
    }

    private string ResolveGear(MappedG920State mapped)
    {
        if (mapped.GearR) { _paddleArmed = false; return "R"; }
        if (mapped.Gear1) { _paddleArmed = false; return "1"; }
        if (mapped.Gear2) { _paddleArmed = false; return "2"; }
        if (mapped.Gear3) { _paddleArmed = false; return "3"; }
        if (mapped.Gear4) { _paddleArmed = false; return "4"; }
        if (mapped.Gear5) { _paddleArmed = false; return "5"; }
        if (mapped.Gear6) { _paddleArmed = false; return "6"; }

        var left = mapped.PaddleLeft && !_prevPaddleLeft;
        var right = mapped.PaddleRight && !_prevPaddleRight;
        _prevPaddleLeft = mapped.PaddleLeft;
        _prevPaddleRight = mapped.PaddleRight;
        if (right)
        {
            _paddleArmed = true;
            _paddleGear = Math.Min(6, _paddleGear + 1);
        }
        else if (left)
        {
            _paddleArmed = true;
            _paddleGear = Math.Max(1, _paddleGear - 1);
        }

        return _paddleArmed ? _paddleGear.ToString() : "N";
    }

    private static float GearRpmRatio(string gear) => gear switch
    {
        "R" => 0.85f,
        "1" => 0.55f,
        "2" => 0.68f,
        "3" => 0.80f,
        "4" => 0.90f,
        "5" => 0.97f,
        "6" => 1.00f,
        _ => 0.62f,
    };

    private static float GearPullFactor(string gear) => gear switch
    {
        "1" => 1.40f,
        "2" => 1.20f,
        "3" => 1.05f,
        "4" => 0.92f,
        "5" => 0.80f,
        "6" => 0.70f,
        "R" => 0.95f,
        _ => 0.30f,
    };

    private static int GearIndex(string gear) => gear switch
    {
        "R" => 0,
        "1" => 1,
        "2" => 2,
        "3" => 3,
        "4" => 4,
        "5" => 5,
        "6" => 6,
        _ => -1,
    };
}
