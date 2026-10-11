using System.Diagnostics;
using System.Runtime.InteropServices;
using SteeringWheelEmulator.Core.Input;
using SteeringWheelEmulator.Core.Models;
using SharpDX;
using SharpDX.DirectInput;

namespace SteeringWheelEmulator.Core.Ffb;

/// <summary>
/// Applies force-feedback torque to a physical DirectInput FFB device.
/// Uses the same Joystick handle owned by <see cref="InputHub"/> so Fanatec / Simucube / Moza
/// bases are not blocked by a second exclusive acquire.
/// </summary>
public sealed class FfbBridge : IDisposable
{
    // DIJOFS_X - data-format offset for the X axis.
    private const int DiJofsX = 0;
    // Infinite duration (DIEFFECT.dwDuration = -1 / 0xFFFFFFFF).
    private const int InfiniteDuration = -1;

    /// <summary>
    /// Suggested Device pace period (ms) when enabling Output feel → Device pace.
    /// Default core is unpaced (period 0) for every vendor.
    /// </summary>
    public const int DefaultCfPacePeriodMs = 3;
    /// <summary>Legacy alias — Interpolate default is off (0); use profile slider.</summary>
    public const float IdleSmoothMs = 0f;
    /// <summary>Alias for <see cref="IdleSmoothMs"/>.</summary>
    public const float DefaultReconstructionMs = IdleSmoothMs;
    /// <summary>|Δmagnitude| that snaps reconstruction (crashes only — lower values
    /// re-grained fast idle turns that should stay filtered).</summary>
    private const int CfReconstructionJump = 7500;
    /// <summary>Coast gain when OEM target is unchanged between mixer ticks.</summary>
    private const float CfReconstructionCoast = 0.85f;

    private readonly object _gate = new();
    /// <summary>Serializes all DirectInput joy/effect calls (DI is not thread-safe).</summary>
    private readonly object _diGate = new();
    private readonly DirectInput _fallbackDi = new();
    private InputHub? _hub;
    private IntPtr _hwnd;
    private string? _deviceId;
    private string? _deviceName;
    private Joystick? _joystick;
    private Joystick? _ownedFallback;
    /// <summary>Legacy dual-handle Poll joystick (same GUID as FFB); experimental flag always off.</summary>
    private Joystick? _inputJoystick;
    private readonly object _inputGate = new();
    private Effect? _constantEffect;
    private Guid _constantForceGuid = EffectGuid.ConstantForce;
    private EffectFlags _effectFlags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets;
    private int[] _axes = [DiJofsX];
    private int[] _directions = [0];
    private WheelVendor _vendor = WheelVendor.Generic;
    private string _coopLevel = "";
    private string _axisInfo = "";
    private float _lastCommandTorque;
    private int _lastMagnitude;
    private int _lastAppliedMagnitude = int.MinValue;
    private float _lastIncomingTorque;
    private DateTime? _lastIncomingUtc;
    private DateTime? _lastApplyUtc;
    private string? _lastError;
    private string _status = "FFB: not attached.";
    private bool _testOverride;
    private bool _testAutoCenter;
    private float _testAutoCenterGain = 0.85f;
    private CancellationTokenSource? _testAutoCenterCts;
    private Task? _testAutoCenterTask;
    private int _incomingCount;
    private int _applyCount;
    private int _paceTargetMagnitude;
    private float _paceReconstructedMagnitude;
    private bool _paceReconInit;
    private int _pacePrevTargetMagnitude;
    /// <summary>OEM target velocity (DI magnitude units per ms) for gap fill.</summary>
    private float _paceTargetVelPerMs;
    private long _lastPaceApplyTickMs;
    private int _paceSkipCount;
    private int _paceApplyCount;
    /// <summary>Output reconstruction EMA τ (ms) on paced path. 0 = pace only. Usually 0 (smoother owns Interpolate).</summary>
    private float _reconstructionTauMs;
    /// <summary>Device-pace period (ms). 0 = unpaced release core. From Output feel.</summary>
    private int _cfPacePeriodMs;
    /// <summary>Last Interpolate ms from Output feel (diag only; smoother owns the filter).</summary>
    private float _idleSmoothMs;
    private bool _disposed;
    /// <summary>Latest rim axes from the exclusive FFB joy (0..1). Refreshed under _diGate.</summary>
    private Dictionary<string, float>? _cachedAxes01;
    private bool[]? _cachedButtons;
    private int _cachedHat = -1;
    private long _cachedAxesTick;
    /// <summary>Winning SetParameters flags for this base - skip multi-strategy probes after first success.</summary>
    private EffectParameterFlags? _fastMagnitudeFlags;
    private bool _fastMagnitudeUsesFullParams;
    /// <summary>Legacy experimental: SetParameters outside the DI lock (always off).</summary>
    private bool _experimentalUnlockedSetParameters;
    /// <summary>Legacy experimental: brief wait on rim reads (always off).</summary>
    private bool _experimentalNonBlockingRimReads;
    /// <summary>Legacy experimental dual-handle (always off; Fanatec PC Comp dual-handle is core).</summary>
    private bool _experimentalDualHandleInput;
    /// <summary>Legacy experimental CF pacing on Fanatec (always off).</summary>
    private bool _experimentalCfPacingOnFanatec;

    private enum WheelVendor
    {
        Generic,
        Fanatec,
        Simucube,
        Simagic,
        Moza,
        Thrustmaster,
        Logitech,
        Other,
    }

    public string? ActiveDeviceId
    {
        get { lock (_gate) return _deviceId; }
    }

    public bool IsReady
    {
        get { lock (_gate) return _constantEffect is not null; }
    }

    /// <summary>
    /// True when Device pace is active — apply loop must keep ticking so
    /// the latest OEM target is slipped between queue updates. Opt-in via Output feel
    /// → Device pace (legacy experimental Fanatec override always off).
    /// </summary>
    public bool UsesCfPacing
    {
        get { lock (_gate) return _constantEffect is not null && ShouldPaceCfUnlocked(); }
    }

    /// <summary>
    /// Interpolate / Gap fill apply on every vendor when set (0 = off = unified core).
    /// </summary>
    public bool UsesInterpolate => true;

    /// <summary>Diagnostics: effective CF pacing policy for the attached base.</summary>
    public string CfPacingLabel
    {
        get { lock (_gate) return CfPacingLabelUnlocked(); }
    }

    private string CfPacingLabelUnlocked()
    {
        if (_constantEffect is null)
            return "off (not attached)";
        var period = PacePeriodMsUnlocked();
        if (period <= 0)
            return "off";
        var note = _cfPacePeriodMs <= 0 && _experimentalCfPacingOnFanatec
            ? "debug override"
            : "feel";
        return $"on ({note} · {period}ms)";
    }

    private bool ShouldPaceCfUnlocked() => PacePeriodMsUnlocked() > 0;

    private int PacePeriodMsUnlocked()
    {
        if (_cfPacePeriodMs > 0)
            return _cfPacePeriodMs;
        // Legacy experimental Fanatec pace override (always off at runtime).
        if (_experimentalCfPacingOnFanatec)
            return DefaultCfPacePeriodMs;
        return 0;
    }

    /// <summary>Output feel → Device pace period (ms). 0 = unpaced core.</summary>
    public void SetCfPacePeriodMs(float ms)
    {
        lock (_gate)
            _cfPacePeriodMs = (int)Math.Clamp(Math.Round(ms), 0, 34);
    }

    /// <summary>Output feel → Interpolate ms (for diagnostics; <see cref="FfbOutputSmoother"/> applies it).</summary>
    public void SetInterpolateMs(float ms)
    {
        lock (_gate)
            _idleSmoothMs = Math.Clamp(ms, 0f, 100f);
    }

    public double Gain { get; set; } = 1.0;
    public bool Invert { get; set; }

    /// <summary>
    /// Skip DI updates when |Δmagnitude| is below this (0 = off). From FFB feel / ShapeGameTorque.
    /// </summary>
    public int MagnitudeEpsilon { get; set; }

    public bool TestOverride
    {
        get { lock (_gate) return _testOverride; }
        set { lock (_gate) _testOverride = value; }
    }

    public void BindInputHub(InputHub hub, IntPtr hwnd)
    {
        lock (_gate)
        {
            _hub = hub;
            _hwnd = hwnd != IntPtr.Zero ? hwnd : ResolveHwnd();
        }
    }

    /// <summary>
    /// Legacy experimental knobs (Settings → Debug Test UI removed). Callers pass all false.
    /// </summary>
    public void SetExperimentalInputOptions(
        bool unlockedSetParameters,
        bool nonBlockingRimReads,
        bool dualHandleInput,
        bool cfPacingOnFanatec)
    {
        lock (_gate)
        {
            _experimentalUnlockedSetParameters = unlockedSetParameters;
            _experimentalNonBlockingRimReads = nonBlockingRimReads;
            _experimentalDualHandleInput = dualHandleInput;
            _experimentalCfPacingOnFanatec = cfPacingOnFanatec;
        }
    }

    private bool ExperimentalUnlockedSetParameters
    {
        get { lock (_gate) return _experimentalUnlockedSetParameters; }
    }

    private bool ExperimentalNonBlockingRimReads
    {
        get { lock (_gate) return _experimentalNonBlockingRimReads; }
    }

    private bool ExperimentalDualHandleInput
    {
        get { lock (_gate) return _experimentalDualHandleInput; }
    }

    /// <summary>True when the NonExclusive dual-handle Poll joystick is open.</summary>
    public bool DualHandleInputActive
    {
        get { lock (_gate) return _inputJoystick is not null; }
    }

    public bool TryAttach(string? deviceId, out string error) =>
        TryAttach(deviceId, _hwnd, preferSharedInput: false, out error);

    public bool TryAttach(string? deviceId, bool preferSharedInput, out string error) =>
        TryAttach(deviceId, _hwnd, preferSharedInput, out error);

    public bool TryAttach(string? deviceId, IntPtr hwnd, out string error) =>
        TryAttach(deviceId, hwnd, preferSharedInput: false, out error);

    /// <param name="preferSharedInput">
    /// True when the same joystick is also a binding source (steer/pedals/buttons).
    /// Prefers NonExclusive so GetCurrentState keeps updating under FFB (Fanatec and
    /// Simucube both hit this when pedals live on the FFB base).
    /// </param>
    public bool TryAttach(string? deviceId, IntPtr hwnd, bool preferSharedInput, out string error)
    {
        error = "";
        DetachEffectsOnly();

        if (string.IsNullOrWhiteSpace(deviceId) || !Guid.TryParse(deviceId, out var guid))
        {
            error = "No FFB output device selected.";
            lock (_gate) { _status = "FFB: no device selected."; _lastError = error; }
            return false;
        }

        hwnd = hwnd != IntPtr.Zero ? hwnd : ResolveHwnd();

        try
        {
            Joystick joy;
            string deviceName;
            var usingHub = false;

            lock (_gate)
            {
                _hwnd = hwnd;
                // Dual-handle: never borrow InputHub's joystick. Exclusive FFB must be a
                // standalone acquire so InputHub can keep Polling NonExclusive for bindings.
                // (Fanatec DD2: shared Exclusive + second NE handle still flatlined input.)
                var preferStandalone = ExperimentalDualHandleInput;
                if (!preferStandalone &&
                    _hub is not null &&
                    _hub.TryGetJoystick(deviceId, out var hubJoy, out var hubName))
                {
                    joy = hubJoy;
                    deviceName = hubName;
                    usingHub = true;
                }
                else
                {
                    joy = new Joystick(_fallbackDi, guid);
                    deviceName = joy.Information.InstanceName;
                    _ownedFallback = joy;
                }

                _joystick = joy;
                _vendor = DetectVendor(deviceName, joy.Information.ProductName);
            }

            if (!joy.Capabilities.Flags.HasFlag(DeviceFlags.ForceFeedback))
            {
                if (!usingHub)
                {
                    joy.Dispose();
                    lock (_gate) { _ownedFallback = null; _joystick = null; }
                }
                error = "Selected device does not report force feedback support.";
                lock (_gate) { _status = "FFB: device has no FFB flag."; _lastError = error; }
                return false;
            }

            string coop;
            Effect? effect;
            string axisInfo;
            lock (_diGate)
            {
                try { joy.Unacquire(); } catch { /* ignore */ }

                // preferSharedInput only when Settings experimental fixes + NonExclusive.
                if (preferSharedInput)
                {
                    try
                    {
                        joy.SetCooperativeLevel(hwnd, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                        coop = "Background|NonExclusive";
                    }
                    catch
                    {
                        joy.SetCooperativeLevel(hwnd, CooperativeLevel.Background | CooperativeLevel.Exclusive);
                        coop = "Background|Exclusive (nonexclusive failed)";
                    }
                }
                else
                {
                    try
                    {
                        joy.SetCooperativeLevel(hwnd, CooperativeLevel.Background | CooperativeLevel.Exclusive);
                        coop = "Background|Exclusive";
                    }
                    catch
                    {
                        joy.SetCooperativeLevel(hwnd, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                        coop = "Background|NonExclusive (exclusive failed)";
                    }
                }

                // Normalize steering axis to 0..65535 so spring auto-center sees a real rim angle.
                try
                {
                    foreach (var obj in joy.GetObjects(DeviceObjectTypeFlags.AbsoluteAxis))
                    {
                        var n = obj.Name ?? "";
                        if (n.Contains("X", StringComparison.OrdinalIgnoreCase) ||
                            n.Contains("Wheel", StringComparison.OrdinalIgnoreCase) ||
                            n.Contains("Steer", StringComparison.OrdinalIgnoreCase))
                        {
                            joy.GetObjectPropertiesById(obj.ObjectId).Range = new InputRange(0, 65535);
                            break;
                        }
                    }
                }
                catch { /* range optional */ }

                // Hardware auto-center stays OFF during gameplay so it cannot fight the
                // OEM DI mix (CF/Spring/Damper/periodic). FFB debug Center can enable it
                // briefly for a manual return-to-center test.
                try { joy.Properties.AutoCenter = false; } catch { /* some devices reject */ }
                try { joy.Properties.ForceFeedbackGain = 10000; } catch { /* optional */ }
                joy.Acquire();

                // Soft reset so True Drive / Fanatec drop stale effects from other clients.
                try { joy.SendForceFeedbackCommand(ForceFeedbackCommand.Reset); } catch { /* optional */ }
                try { joy.SendForceFeedbackCommand(ForceFeedbackCommand.SetActuatorsOn); } catch { /* optional */ }

                _constantForceGuid = ResolveConstantForceGuid(joy);

                var objectId = ResolveFfAxisObjectId(joy, out var axisName);
                if (objectId < 0)
                {
                    error = "Could not find an FFB axis on the selected device.";
                    lock (_gate) { _status = "FFB: no FFB axis."; _lastError = error; }
                    // Fall through to DetachEffectsOnly outside this lock.
                    effect = null;
                    axisInfo = "";
                }
                else
                {
                    var created = TryCreateConstantEffect(joy, objectId, axisName, out effect, out axisInfo, out var createError);
                    if (!created || effect is null)
                    {
                        error = createError ?? "Could not create constant-force effect.";
                        lock (_gate) { _status = "FFB: effect create failed."; _lastError = error; }
                        effect = null;
                    }
                    else
                    {
                        // Simucube True Drive only calculates torque for effects that are created AND playing.
                        try { effect.Start(EffectPlayFlags.NoDownload); } catch { /* some drivers ignore */ }
                        try { effect.Start(); } catch { /* already playing */ }

                        if (!TrySetMagnitude(effect, 0, out var setError))
                        {
                            error = $"Effect created but updates fail: {setError}";
                            lock (_gate) { _status = "FFB: effect update failed."; _lastError = error; }
                            try { effect.Dispose(); } catch { /* ignore */ }
                            effect = null;
                        }
                    }
                }
            }

            if (effect is null)
            {
                DetachEffectsOnly();
                return false;
            }

            lock (_gate)
            {
                _constantEffect = effect;
                _deviceId = deviceId;
                _deviceName = deviceName;
                _coopLevel = usingHub ? $"{coop} (shared input handle)" : $"{coop} (standalone)";
                _axisInfo = $"{VendorLabel(_vendor)} · {axisInfo}";
                _lastError = null;
                _status = $"FFB: attached to {deviceName}";
                _lastCommandTorque = 0;
                _lastMagnitude = 0;
                _lastAppliedMagnitude = int.MinValue;
                _lastApplyUtc = null;
                _paceTargetMagnitude = 0;
                _paceReconstructedMagnitude = 0;
                _paceReconInit = false;
                _pacePrevTargetMagnitude = 0;
                _paceTargetVelPerMs = 0;
                _lastPaceApplyTickMs = 0;
                _paceSkipCount = 0;
                _paceApplyCount = 0;
            }

            // Dual-handle: InputHub (unpinned) is the NonExclusive Poll path — no second
            // overlay joystick. Label so diagnostics show the corrected architecture.
            if (ExperimentalDualHandleInput && !usingHub)
            {
                lock (_gate)
                {
                    _coopLevel += " + InputHub NonExclusive poll";
                    _status = $"FFB: attached to {deviceName} (dual-handle: standalone Exclusive FFB)";
                }
            }

            return true;
        }
        catch (SharpDXException ex)
        {
            DetachEffectsOnly();
            error = $"Failed to attach FFB: {ex.Message}. Close wheel software (True Drive / Fanatec / Simagic / Moza Pit House) if it holds exclusive FFB, or try Admin.";
            lock (_gate) { _status = "FFB: attach failed."; _lastError = error; }
            return false;
        }
        catch (Exception ex)
        {
            DetachEffectsOnly();
            error = ex.Message;
            lock (_gate) { _status = "FFB: attach failed."; _lastError = error; }
            return false;
        }
    }

    /// <summary>
    /// Open a second Joystick on the same instance GUID with NonExclusive acquire.
    /// Used only for Poll; Exclusive FFB stays on <see cref="_joystick"/>.
    /// </summary>
    private bool TryOpenDualHandleInput(Guid guid, IntPtr hwnd, out string note)
    {
        note = "";
        CloseDualHandleInput();

        try
        {
            var inputJoy = new Joystick(_fallbackDi, guid);
            try
            {
                inputJoy.SetCooperativeLevel(hwnd, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
            }
            catch
            {
                try { inputJoy.Dispose(); } catch { /* ignore */ }
                note = "dual-handle open failed (coop)";
                return false;
            }

            try { inputJoy.Acquire(); }
            catch
            {
                try { inputJoy.Dispose(); } catch { /* ignore */ }
                note = "dual-handle open failed (acquire)";
                return false;
            }

            // Prime one sample so overlay has data immediately.
            try
            {
                inputJoy.Poll();
                var state = inputJoy.GetCurrentState();
                CacheInputFromState(state);
            }
            catch { /* first Poll optional */ }

            lock (_gate)
                _inputJoystick = inputJoy;
            note = "dual-handle input";
            return true;
        }
        catch (Exception ex)
        {
            note = "dual-handle open failed: " + ex.Message;
            return false;
        }
    }

    private void CloseDualHandleInput()
    {
        Joystick? joy;
        lock (_gate)
        {
            joy = _inputJoystick;
            _inputJoystick = null;
        }

        if (joy is null) return;
        lock (_inputGate)
        {
            try { joy.Unacquire(); } catch { /* ignore */ }
            try { joy.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Live axes/buttons/hat from the Dual-handle NonExclusive Poll joystick.
    /// Does not touch <see cref="_diGate"/> so Exclusive SetParameters cannot stall input.
    /// </summary>
    public bool TryGetDualHandleInput(
        out Dictionary<string, float> axes,
        out bool[] buttons,
        out int hat)
    {
        axes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        buttons = [];
        hat = -1;

        Joystick? joy;
        lock (_gate) joy = _inputJoystick;
        if (joy is null) return false;

        if (!Monitor.TryEnter(_inputGate, 0))
        {
            lock (_gate)
            {
                if (_cachedAxes01 is null || _cachedAxes01.Count == 0)
                    return false;
                CopyCachedInputUnlocked(axes, out buttons, out hat);
                return true;
            }
        }

        try
        {
            try
            {
                joy.Poll();
                var state = joy.GetCurrentState();
                CacheInputFromState(state);
            }
            catch
            {
                try
                {
                    joy.Acquire();
                    joy.Poll();
                    var state = joy.GetCurrentState();
                    CacheInputFromState(state);
                }
                catch
                {
                    lock (_gate)
                    {
                        if (_cachedAxes01 is null || _cachedAxes01.Count == 0)
                            return false;
                        CopyCachedInputUnlocked(axes, out buttons, out hat);
                        return true;
                    }
                }
            }

            lock (_gate)
                CopyCachedInputUnlocked(axes, out buttons, out hat);
            return true;
        }
        finally
        {
            Monitor.Exit(_inputGate);
        }
    }

    /// <summary>
    /// Overlay source: dual-handle when active, otherwise Exclusive FFB handle (release path).
    /// </summary>
    public bool TryGetOverlayInput(
        out Dictionary<string, float> axes,
        out bool[] buttons,
        out int hat)
    {
        if (DualHandleInputActive && TryGetDualHandleInput(out axes, out buttons, out hat))
            return true;
        return TryGetPhysicalInput(out axes, out buttons, out hat);
    }

    /// <summary>
    /// Physical FFB axis position as -1..1 (center 0). Required for game spring/damper
    /// auto-center to track the real rim - not the virtual G920 / DualSense steer.
    /// </summary>
    public bool TryGetPhysicalSteering(out float steeringCentered)
    {
        steeringCentered = 0f;
        // Prefer dual-handle / non-blocking overlay so spring auto-center tracks live rim.
        if (DualHandleInputActive || ExperimentalNonBlockingRimReads)
        {
            if (!TryGetOverlayInput(out var axes, out _, out _))
                return false;
            if (!axes.TryGetValue("X", out var x01))
                return false;
            steeringCentered = Math.Clamp(x01 * 2f - 1f, -1f, 1f);
            return true;
        }

        if (!TryReadPhysicalJoystickState(out var state))
            return false;
        steeringCentered = NormalizeAxisToCentered(state.X);
        return true;
    }

    /// <summary>Age of the exclusive-FFB input cache in ms, or -1 if never sampled.</summary>
    public long PhysicalInputCacheAgeMs
    {
        get
        {
            lock (_gate)
            {
                if (_cachedAxesTick == 0 || _cachedAxes01 is null || _cachedAxes01.Count == 0)
                    return -1;
                return Math.Max(0, Environment.TickCount64 - _cachedAxesTick);
            }
        }
    }

    /// <summary>Cooperative level string from the last successful attach (for health logs).</summary>
    public string CooperativeLevelLabel
    {
        get { lock (_gate) return string.IsNullOrEmpty(_coopLevel) ? "-" : _coopLevel; }
    }

    /// <summary>
    /// Live axis values (0..1) from the exclusive FFB joystick - same scale as
    /// <see cref="Input.InputHub"/> DeviceState axes.
    /// Never blocks behind a slow DI SetParameters: returns a fresh cache when the
    /// FFB apply thread holds <c>_diGate</c>.
    /// </summary>
    public bool TryGetPhysicalAxes01(out Dictionary<string, float> axes) =>
        TryGetPhysicalInput(out axes, out _, out _);

    /// <summary>
    /// Live axes + buttons + hat from the exclusive FFB joystick. Required when the
    /// FFB base is also a binding source: InputHub skips Poll on the pinned device, so
    /// the bridge must overlay buttons from this shared exclusive handle (axes alone
    /// left Fanatec wheel buttons frozen after Start).
    /// </summary>
    public bool TryGetPhysicalInput(
        out Dictionary<string, float> axes,
        out bool[] buttons,
        out int hat)
    {
        axes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        buttons = [];
        hat = -1;

        // Hot path: serve sub-frame cache so input submit is not serialized with FFB USB.
        long cacheAge;
        lock (_gate)
        {
            cacheAge = _cachedAxesTick == 0
                ? long.MaxValue
                : Environment.TickCount64 - _cachedAxesTick;
            if (_cachedAxes01 is { Count: > 0 } && cacheAge <= 4)
            {
                CopyCachedInputUnlocked(axes, out buttons, out hat);
                return true;
            }
        }

        // Legacy experimental: brief wait when cache is stale. Release: never wait (TryEnter 0).
        var waitMs = ExperimentalNonBlockingRimReads && cacheAge > 24 ? 8 : 0;
        if (!Monitor.TryEnter(_diGate, waitMs))
        {
            // Apply thread is busy - last sample is better than stalling the bridge.
            lock (_gate)
            {
                if (_cachedAxes01 is null || _cachedAxes01.Count == 0)
                    return false;
                CopyCachedInputUnlocked(axes, out buttons, out hat);
                return true;
            }
        }

        try
        {
            if (!TryReadPhysicalJoystickStateUnlocked(out var state))
            {
                lock (_gate)
                {
                    if (_cachedAxes01 is null || _cachedAxes01.Count == 0)
                        return false;
                    CopyCachedInputUnlocked(axes, out buttons, out hat);
                    return true;
                }
            }

            CacheInputFromState(state);
            lock (_gate)
            {
                CopyCachedInputUnlocked(axes, out buttons, out hat);
            }
            return true;
        }
        finally
        {
            Monitor.Exit(_diGate);
        }
    }

    private bool TryReadPhysicalJoystickState(out JoystickState state)
    {
        state = default!;
        Joystick? joy;
        lock (_gate) joy = _joystick;
        if (joy is null) return false;

        lock (_diGate)
            return TryReadPhysicalJoystickStateUnlocked(out state);
    }

    private void CopyCachedInputUnlocked(
        Dictionary<string, float> axes,
        out bool[] buttons,
        out int hat)
    {
        foreach (var (k, v) in _cachedAxes01!)
            axes[k] = v;
        buttons = _cachedButtons is { Length: > 0 }
            ? (bool[])_cachedButtons.Clone()
            : [];
        hat = _cachedHat;
    }

    private bool TryReadPhysicalJoystickStateUnlocked(out JoystickState state)
    {
        state = default!;
        Joystick? joy;
        lock (_gate) joy = _joystick;
        if (joy is null) return false;

        try
        {
            joy.Poll();
            state = joy.GetCurrentState();
            CacheInputFromState(state);
            return true;
        }
        catch
        {
            try
            {
                joy.Acquire();
                joy.Poll();
                state = joy.GetCurrentState();
                CacheInputFromState(state);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private void CacheInputFromState(JoystickState state)
    {
        var axes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["X"] = NormalizeAxis01(state.X),
            ["Y"] = NormalizeAxis01(state.Y),
            ["Z"] = NormalizeAxis01(state.Z),
            ["Rx"] = NormalizeAxis01(state.RotationX),
            ["Ry"] = NormalizeAxis01(state.RotationY),
            ["Rz"] = NormalizeAxis01(state.RotationZ),
        };
        if (state.Sliders.Length > 0)
            axes["Slider0"] = NormalizeAxis01(state.Sliders[0]);
        if (state.Sliders.Length > 1)
            axes["Slider1"] = NormalizeAxis01(state.Sliders[1]);

        var hat = -1;
        if (state.PointOfViewControllers.Length > 0)
        {
            var pov = state.PointOfViewControllers[0];
            if (pov >= 0)
                hat = pov / 4500; // 0..7 - same as InputHub
        }

        lock (_gate)
        {
            _cachedAxes01 = axes;
            _cachedButtons = (bool[])state.Buttons.Clone();
            _cachedHat = hat;
            _cachedAxesTick = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Optional hardware DIPROP_AUTOCENTER. Used only by FFB debug Center test -
    /// not toggled from the game OEM mix (that would fight Fanatec/Simucube DI effects).
    /// </summary>
    public void SetHardwareAutoCenter(bool enabled)
    {
        Joystick? joy;
        lock (_gate) joy = _joystick;
        if (joy is null) return;

        // Prefer live property change so we do not interrupt the CF effect or the
        // shared InputHub acquire (Unacquire here used to make Poll dispose the joy
        // and freeze virtual G920 buttons).
        lock (_diGate)
        {
            try
            {
                joy.Properties.AutoCenter = enabled;
            }
            catch
            {
                // Unsupported on this base - do not Unacquire; FFB debug Center still has
                // the software spring path.
            }
        }
    }

    private static float NormalizeAxisToCentered(int x)
    {
        if (x < 0)
            return Math.Clamp(x / 32767f, -1f, 1f);
        if (x > 65535) x = 65535;
        return (x / 65535f) * 2f - 1f;
    }

    /// <summary>Match InputHub DeviceState axis scale (0..1).</summary>
    private static float NormalizeAxis01(int value)
    {
        if (value < 0)
            return Math.Clamp((value + 32768) / 65535f, 0f, 1f);
        if (value > 65535) value = 65535;
        return value / 65535f;
    }

    /// <param name="torque">-1 .. 1, positive = right</param>
    public void UpdateTorque(float torque) => ApplyTorque(torque, fromTest: false);

    public bool TestAutoCenterActive
    {
        get { lock (_gate) return _testAutoCenter; }
    }

    public void ApplyTestTorque(float torque)
    {
        StopTestAutoCenterLoop();
        lock (_gate)
        {
            _testOverride = true;
            _testAutoCenter = false;
        }
        try { SetHardwareAutoCenter(false); } catch { /* ignore */ }
        ApplyTorque(torque, fromTest: true);
    }

    /// <summary>
    /// FFB debug Center: software return-to-center spring from physical rim angle,
    /// plus hardware AutoCenter when the driver supports it.
    /// </summary>
    public void StartTestAutoCenter(float gain = 0.85f)
    {
        StopTestAutoCenterLoop();
        lock (_gate)
        {
            _testOverride = true;
            _testAutoCenter = true;
            _testAutoCenterGain = Math.Clamp(gain, 0.05f, 2f);
        }

        try { SetHardwareAutoCenter(true); } catch { /* optional */ }

        var cts = new CancellationTokenSource();
        _testAutoCenterCts = cts;
        _testAutoCenterTask = Task.Run(() => RunTestAutoCenterLoop(cts.Token), cts.Token);

        // Immediate kick so the rim moves before the first loop sleep.
        if (TryGetPhysicalSteering(out var rim))
            ApplyTorque(-rim * _testAutoCenterGain, fromTest: true);
        else
            ApplyTorque(0, fromTest: true);
    }

    public void ClearTestOverride()
    {
        StopTestAutoCenterLoop();
        lock (_gate)
        {
            _testOverride = false;
            _testAutoCenter = false;
        }
        try { SetHardwareAutoCenter(false); } catch { /* ignore */ }
        ApplyTorque(0, fromTest: true);
    }

    private void StopTestAutoCenterLoop()
    {
        var cts = _testAutoCenterCts;
        _testAutoCenterCts = null;
        var task = _testAutoCenterTask;
        _testAutoCenterTask = null;
        if (cts is null && task is null) return;
        try { cts?.Cancel(); } catch { /* ignore */ }
        try { task?.Wait(200); } catch { /* ignore */ }
        try { cts?.Dispose(); } catch { /* ignore */ }
    }

    private void RunTestAutoCenterLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool active;
            float gain;
            lock (_gate)
            {
                active = _testAutoCenter && _testOverride;
                gain = _testAutoCenterGain;
            }
            if (!active) break;

            if (TryGetPhysicalSteering(out var rim))
            {
                // Same sense as DI Spring: force opposite rim offset → return to center.
                ApplyTorque(-rim * gain, fromTest: true);
            }

            try { Thread.Sleep(2); }
            catch { break; }
        }
    }

    public void NoteIncoming(float torque)
    {
        lock (_gate)
        {
            _lastIncomingTorque = torque;
            _lastIncomingUtc = DateTime.UtcNow;
            _incomingCount++;
        }
    }

    private void ApplyTorque(float torque, bool fromTest)
    {
        Effect? effect;
        Joystick? joy;
        var pacing = false;
        lock (_gate)
        {
            if (!fromTest && _testOverride)
                return;
            effect = _constantEffect;
            joy = _joystick;
            if (effect is null)
                return;
            pacing = ShouldPaceCfUnlocked();
        }

        // App convention: positive torque = right. DirectInput X on Fanatec/Simucube/Moza
        // bases is opposite that sense, so we negate when building DI magnitude.
        torque = Math.Clamp(torque, -1f, 1f);
        if (Invert) torque = -torque;
        torque = (float)(torque * Math.Clamp(Gain, 0, 2));

        var magnitude = (int)Math.Clamp(Math.Round(-torque * 10000), -10000, 10000);

        if (pacing)
        {
            ApplyTorquePaced(effect, joy, torque, magnitude, fromTest);
            return;
        }

        // Unpaced release path (default for every vendor).
        // Optional feel: skip tiny DI chatter. Always allow return-to-zero.
        var eps = MagnitudeEpsilon;
        if (!fromTest && eps > 0 &&
            _lastAppliedMagnitude != int.MinValue &&
            Math.Abs(magnitude - _lastAppliedMagnitude) < eps &&
            !(magnitude == 0 && _lastAppliedMagnitude != 0))
        {
            lock (_gate)
            {
                _lastCommandTorque = torque;
                _lastMagnitude = _lastAppliedMagnitude;
            }
            return;
        }

        // Unchanged magnitude: skip the USB round-trip, but refresh periodically so
        // bases that drop idle effects keep it allocated and playing.
        lock (_gate)
        {
            if (magnitude == _lastMagnitude && _lastApplyUtc is { } lastApply &&
                (DateTime.UtcNow - lastApply).TotalMilliseconds < 100)
            {
                return;
            }
        }

        if (!PushMagnitudeToDevice(effect, joy, magnitude, out var pushError))
        {
            lock (_gate) { _lastError = pushError; }
            return;
        }

        lock (_gate)
        {
            _lastCommandTorque = torque;
            _lastMagnitude = magnitude;
            _lastAppliedMagnitude = magnitude;
            _lastApplyUtc = DateTime.UtcNow;
            _applyCount++;
            _lastError = null;
        }
    }

    /// <summary>
    /// Paced CF: slip SetParameters at the feel Device pace period while optionally
    /// reconstructing OEM torque (apply-path EMA usually off — Interpolate owns that).
    /// </summary>
    private void ApplyTorquePaced(Effect effect, Joystick? joy, float torque, int targetMagnitude, bool fromTest)
    {
        var now = Environment.TickCount64;
        int sendMagnitude;
        lock (_gate)
        {
            _lastCommandTorque = torque;
            _paceTargetMagnitude = targetMagnitude;
            var paceMs = Math.Max(1, PacePeriodMsUnlocked());

            var elapsed = _lastPaceApplyTickMs == 0
                ? long.MaxValue
                : now - _lastPaceApplyTickMs;

            // FFB debug Left/Right: snap, no reconstruction lag.
            if (fromTest)
            {
                if (elapsed < paceMs &&
                    _lastAppliedMagnitude != int.MinValue &&
                    Math.Abs(targetMagnitude - _lastAppliedMagnitude) < 30)
                {
                    _paceSkipCount++;
                    return;
                }

                sendMagnitude = targetMagnitude;
                _paceReconstructedMagnitude = targetMagnitude;
                _paceReconInit = true;
                _pacePrevTargetMagnitude = targetMagnitude;
                _paceTargetVelPerMs = 0;
            }
            else
            {
                if (!_paceReconInit)
                {
                    _paceReconstructedMagnitude = targetMagnitude;
                    _pacePrevTargetMagnitude = targetMagnitude;
                    _paceTargetVelPerMs = 0;
                    _paceReconInit = true;
                }

                var jump = Math.Abs(targetMagnitude - _paceReconstructedMagnitude) >= CfReconstructionJump;
                if (jump)
                {
                    _paceReconstructedMagnitude = targetMagnitude;
                    _paceTargetVelPerMs = 0;
                }
                else
                {
                    var dtMs = (float)Math.Clamp(elapsed == long.MaxValue ? paceMs : elapsed, 1, 40);
                    var tau = _reconstructionTauMs;
                    if (tau <= 0.5f)
                    {
                        // Reconstruction off: pace only, snap to OEM target.
                        _paceReconstructedMagnitude = targetMagnitude;
                        _paceTargetVelPerMs = 0;
                    }
                    else
                    {
                        var alpha = 1f - MathF.Exp(-dtMs / tau);

                        // Track OEM target velocity when the mix steps; coast when it holds.
                        var targetDelta = targetMagnitude - _pacePrevTargetMagnitude;
                        if (Math.Abs(targetDelta) >= CfReconstructionJump)
                        {
                            _paceTargetVelPerMs = 0;
                        }
                        else if (targetDelta != 0)
                        {
                            var instantVel = targetDelta / dtMs;
                            _paceTargetVelPerMs += (instantVel - _paceTargetVelPerMs) * Math.Clamp(alpha * 1.4f, 0.05f, 1f);
                        }
                        else
                        {
                            // Decay velocity estimate while target holds (avoid runaway coast).
                            _paceTargetVelPerMs *= MathF.Exp(-dtMs / (tau * 1.25f));
                        }

                        _paceReconstructedMagnitude += (targetMagnitude - _paceReconstructedMagnitude) * alpha;
                        // Fill gaps between OEM ticks (Tuner Reconstruction role).
                        // Applies to the whole mix output (incl. idle spring).
                        _paceReconstructedMagnitude += _paceTargetVelPerMs * dtMs * (CfReconstructionCoast * alpha);
                    }
                }

                _paceReconstructedMagnitude = Math.Clamp(_paceReconstructedMagnitude, -10000f, 10000f);
                sendMagnitude = (int)Math.Clamp(Math.Round(_paceReconstructedMagnitude), -10000, 10000);
                _pacePrevTargetMagnitude = targetMagnitude;

                if (!jump && elapsed < paceMs)
                {
                    _paceSkipCount++;
                    return;
                }

                if (sendMagnitude == _lastMagnitude &&
                    _lastApplyUtc is { } lastApply &&
                    (DateTime.UtcNow - lastApply).TotalMilliseconds < 100)
                {
                    _lastPaceApplyTickMs = now;
                    return;
                }

                var eps = MagnitudeEpsilon;
                if (eps > 0 &&
                    _lastAppliedMagnitude != int.MinValue &&
                    Math.Abs(sendMagnitude - _lastAppliedMagnitude) < eps &&
                    !(sendMagnitude == 0 && _lastAppliedMagnitude != 0))
                {
                    _lastMagnitude = _lastAppliedMagnitude;
                    _lastPaceApplyTickMs = now;
                    return;
                }
            }
        }

        if (!PushMagnitudeToDevice(effect, joy, sendMagnitude, out var pushError))
        {
            lock (_gate) { _lastError = pushError; }
            return;
        }

        lock (_gate)
        {
            _lastMagnitude = sendMagnitude;
            _lastAppliedMagnitude = sendMagnitude;
            _lastPaceApplyTickMs = Environment.TickCount64;
            _lastApplyUtc = DateTime.UtcNow;
            _applyCount++;
            _paceApplyCount++;
            _lastError = null;
        }
    }

    private bool PushMagnitudeToDevice(Effect effect, Joystick? joy, int magnitude, out string? error)
    {
        error = null;
        if (ExperimentalUnlockedSetParameters)
        {
            if (joy is not null && Monitor.TryEnter(_diGate, 5))
            {
                try { TryReadPhysicalJoystickStateUnlocked(out _); }
                finally { Monitor.Exit(_diGate); }
            }

            string? setError = null;
            var setOk = false;
            try { setOk = TrySetMagnitude(effect, magnitude, out setError); }
            catch (Exception ex) { setError = ex.Message; }

            if (!setOk)
            {
                string? recreateError = null;
                var recreatedOk = false;
                if (joy is not null && Monitor.TryEnter(_diGate, 50))
                {
                    try
                    {
                        if (TryRecreateEffect(joy, magnitude, out var recreated, out recreateError))
                        {
                            lock (_gate)
                            {
                                try { _constantEffect?.Dispose(); } catch { /* ignore */ }
                                _constantEffect = recreated;
                            }
                            _fastMagnitudeFlags = null;
                            recreatedOk = true;
                        }
                    }
                    finally { Monitor.Exit(_diGate); }
                }

                if (!recreatedOk)
                {
                    error = string.IsNullOrEmpty(recreateError)
                        ? $"Apply failed: {setError}"
                        : $"Apply failed: {setError} | recreate: {recreateError}";
                    return false;
                }
            }

            if (joy is not null && Monitor.TryEnter(_diGate, 5))
            {
                try { TryReadPhysicalJoystickStateUnlocked(out _); }
                finally { Monitor.Exit(_diGate); }
            }

            return true;
        }

        lock (_diGate)
        {
            if (joy is not null)
                TryReadPhysicalJoystickStateUnlocked(out _);

            if (!TrySetMagnitude(effect, magnitude, out var setError))
            {
                string? recreateError = null;
                if (joy is not null && TryRecreateEffect(joy, magnitude, out var recreated, out recreateError))
                {
                    lock (_gate)
                    {
                        try { _constantEffect?.Dispose(); } catch { /* ignore */ }
                        _constantEffect = recreated;
                    }
                    _fastMagnitudeFlags = null;
                }
                else
                {
                    error = string.IsNullOrEmpty(recreateError)
                        ? $"Apply failed: {setError}"
                        : $"Apply failed: {setError} | recreate: {recreateError}";
                    return false;
                }
            }

            if (joy is not null)
                TryReadPhysicalJoystickStateUnlocked(out _);
        }

        return true;
    }

    /// <summary>
    /// Update constant-force magnitude without GetParameters() (Fanatec E_INVALIDARG).
    /// Also tries the DirectInput FFConst pattern (DIRECTION + TYPESPECIFIC + START)
    /// which Simucube True Drive expects so the effect stays allocated/playing.
    /// </summary>
    private bool TrySetMagnitude(Effect effect, int magnitude, out string error)
    {
        error = "";
        Exception? last = null;

        EffectFlags flags;
        int[] axes;
        int[] dirs;
        lock (_gate)
        {
            flags = _effectFlags;
            axes = _axes;
            dirs = _directions;
        }

        // Hot path: reuse the flags that already worked on this base (avoids 2-3 USB probes).
        if (_fastMagnitudeFlags is { } fastFlags)
        {
            try
            {
                if (_fastMagnitudeUsesFullParams)
                {
                    var p = new EffectParameters
                    {
                        Flags = flags,
                        Axes = axes,
                        Directions = dirs,
                        Parameters = new ConstantForce { Magnitude = magnitude },
                    };
                    effect.SetParameters(p, fastFlags);
                }
                else
                {
                    var p = new EffectParameters
                    {
                        Parameters = new ConstantForce { Magnitude = magnitude },
                    };
                    effect.SetParameters(p, fastFlags);
                }
                return true;
            }
            catch
            {
                _fastMagnitudeFlags = null;
            }
        }

        // 1) FFConst-style: direction + type-specific + start (best for Simucube).
        try
        {
            var p = new EffectParameters
            {
                Flags = flags,
                Axes = axes,
                Directions = dirs,
                Parameters = new ConstantForce { Magnitude = magnitude },
            };
            var updateFlags = EffectParameterFlags.Direction |
                              EffectParameterFlags.TypeSpecificParameters |
                              EffectParameterFlags.Start;
            effect.SetParameters(p, updateFlags);
            _fastMagnitudeFlags = updateFlags;
            _fastMagnitudeUsesFullParams = true;
            return true;
        }
        catch (Exception ex) { last = ex; }

        // 2) Type-specific only / with start / norestart (best for Fanatec).
        foreach (var updateFlags in new[]
        {
            EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start,
            EffectParameterFlags.TypeSpecificParameters,
            EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.NoRestart,
        })
        {
            try
            {
                var p = new EffectParameters
                {
                    Parameters = new ConstantForce { Magnitude = magnitude },
                };
                effect.SetParameters(p, updateFlags);
                _fastMagnitudeFlags = updateFlags;
                _fastMagnitudeUsesFullParams = false;
                return true;
            }
            catch (Exception ex) { last = ex; }
        }

        // 3) Stop → set → start (drivers that disallow live type-specific edits).
        try
        {
            try { effect.Stop(); } catch { /* ignore */ }
            var p = new EffectParameters
            {
                Flags = flags,
                Axes = axes,
                Directions = dirs,
                Parameters = new ConstantForce { Magnitude = magnitude },
            };
            effect.SetParameters(p,
                EffectParameterFlags.Direction | EffectParameterFlags.TypeSpecificParameters);
            effect.Start();
            return true;
        }
        catch (Exception ex) { last = ex; }

        error = last?.Message ?? "unknown SetParameters failure";
        return false;
    }

    private bool TryRecreateEffect(Joystick joy, int magnitude, out Effect? effect, out string? error)
    {
        effect = null;
        error = null;
        try
        {
            Effect? old;
            Guid guid;
            lock (_gate)
            {
                old = _constantEffect;
                guid = _constantForceGuid;
            }
            try { old?.Stop(); } catch { /* ignore */ }
            try { old?.Dispose(); } catch { /* ignore */ }

            var p = BuildEffectParameters(magnitude);
            effect = new Effect(joy, guid, p);
            effect.Start();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            effect = null;
            return false;
        }
    }

    private bool TryCreateConstantEffect(
        Joystick joy,
        int objectId,
        string axisName,
        out Effect? effect,
        out string axisInfo,
        out string? error)
    {
        effect = null;
        axisInfo = "";
        error = null;

        Guid preferredGuid;
        lock (_gate) preferredGuid = _constantForceGuid;

        // Try device-reported GUID first, then the standard ConstantForce GUID.
        var guids = preferredGuid == EffectGuid.ConstantForce
            ? new[] { EffectGuid.ConstantForce }
            : new[] { preferredGuid, EffectGuid.ConstantForce };

        // Full strategy set for Generic / Moza / Simagic / Fanatec / Simucube / etc.
        // Order is the MS FFConst + Fanatec-safe path first, then fallbacks.
        var candidates = new (EffectFlags Flags, int[] Axes, int[] Dirs, string Label)[]
        {
            (EffectFlags.Cartesian | EffectFlags.ObjectOffsets, [DiJofsX], [0],
                $"Cartesian|ObjectOffsets X={DiJofsX} ({axisName})"),
            (EffectFlags.Polar | EffectFlags.ObjectOffsets, [DiJofsX], [0],
                $"Polar|ObjectOffsets X={DiJofsX} ({axisName})"),
            (EffectFlags.Cartesian | EffectFlags.ObjectIds, [objectId], [0],
                $"Cartesian|ObjectIds id=0x{objectId:X8} ({axisName})"),
            (EffectFlags.Polar | EffectFlags.ObjectIds, [objectId], [0],
                $"Polar|ObjectIds id=0x{objectId:X8} ({axisName})"),
            // Some bases expose Y or a second FF actuator; rare but cheap to try.
            (EffectFlags.Cartesian | EffectFlags.ObjectOffsets, [0, 4], [0, 0],
                $"Cartesian|ObjectOffsets X+Y ({axisName})"),
        };

        var errors = new List<string>();
        foreach (var guid in guids)
        {
            foreach (var (flags, axes, dirs, label) in candidates)
            {
                var fullLabel = guid == EffectGuid.ConstantForce ? label : $"{label} [hw-guid]";
                try
                {
                    var p = new EffectParameters
                    {
                        Flags = flags,
                        Duration = InfiniteDuration,
                        SamplePeriod = 0,
                        Gain = 10000,
                        TriggerButton = -1,
                        TriggerRepeatInterval = 0,
                        Axes = axes,
                        Directions = dirs,
                        Envelope = null,
                        Parameters = new ConstantForce { Magnitude = 0 },
                    };

                    var created = new Effect(joy, guid, p);
                    // Download so Simucube / similar stacks show the effect as allocated.
                    try { created.Download(); } catch { /* optional */ }

                    lock (_gate)
                    {
                        _constantForceGuid = guid;
                        _effectFlags = flags;
                        _axes = axes;
                        _directions = dirs;
                    }

                    effect = created;
                    axisInfo = fullLabel;
                    return true;
                }
                catch (Exception ex)
                {
                    errors.Add($"{fullLabel}: {ex.Message}");
                }
            }
        }

        error = string.Join(" | ", errors);
        return false;
    }

    private EffectParameters BuildEffectParameters(int magnitude)
    {
        EffectFlags flags;
        int[] axes;
        int[] dirs;
        lock (_gate)
        {
            flags = _effectFlags;
            axes = _axes;
            dirs = _directions;
        }

        return new EffectParameters
        {
            Flags = flags,
            Duration = InfiniteDuration,
            SamplePeriod = 0,
            Gain = 10000,
            TriggerButton = -1,
            TriggerRepeatInterval = 0,
            Axes = axes,
            Directions = dirs,
            Envelope = null,
            Parameters = new ConstantForce { Magnitude = magnitude },
        };
    }

    public void Stop() => ApplyTorque(0, fromTest: true);

    public void ApplyFromProfile(MappingProfile profile)
    {
        Gain = profile.FfbGain;
        Invert = profile.FfbInvert;
        // Reconstruction LPF is applied in FfbOutputSmoother; keep paced apply without
        // a second EMA/coast (stacked lag hunts with Forza spring at idle).
        SetReconstructionMs(0);
    }

    /// <summary>Reconstruction filter τ in ms (0 = off). Drives paced-output EMA.</summary>
    public void SetReconstructionMs(float ms)
    {
        lock (_gate)
            _reconstructionTauMs = Math.Clamp(ms, 0f, 100f);
    }

    public FfbDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            return new FfbDiagnostics
            {
                IsAttached = _constantEffect is not null,
                DeviceId = _deviceId,
                DeviceName = _deviceName,
                VendorProfile = VendorLabel(_vendor),
                CooperativeLevel = _coopLevel,
                AxisInfo = _axisInfo,
                LastCommandTorque = _lastCommandTorque,
                LastMagnitude = _lastMagnitude,
                LastIncomingTorque = _lastIncomingTorque,
                LastIncomingUtc = _lastIncomingUtc,
                LastApplyUtc = _lastApplyUtc,
                LastError = _lastError,
                Status = _status,
                TestOverrideActive = _testOverride,
                TestAutoCenterActive = _testAutoCenter,
                IncomingUpdateCount = _incomingCount,
                ApplyCount = _applyCount,
                CfPacing = CfPacingLabelUnlocked(),
                CfPacingTargetMagnitude = _paceTargetMagnitude,
                CfPacingSkipCount = _paceSkipCount,
                CfPacingApplyCount = _paceApplyCount,
                IdleSmooth = _idleSmoothMs > 0.5f
                    ? $"on ({_idleSmoothMs:0}ms)"
                    : "off",
            };
        }
    }

    public void Detach()
    {
        StopTestAutoCenterLoop();
        DetachEffectsOnly();
        lock (_gate)
        {
            _deviceId = null;
            _deviceName = null;
            _status = "FFB: not attached.";
            _testOverride = false;
            _testAutoCenter = false;
            _vendor = WheelVendor.Generic;
        }
    }

    private void DetachEffectsOnly()
    {
        CloseDualHandleInput();

        Effect? effect;
        Joystick? owned;
        Joystick? shared;
        string? id;
        InputHub? hub;
        IntPtr hwnd;
        lock (_gate)
        {
            effect = _constantEffect;
            _constantEffect = null;
            owned = _ownedFallback;
            _ownedFallback = null;
            shared = _joystick;
            _joystick = null;
            _cachedAxes01 = null;
            _cachedButtons = null;
            _cachedHat = -1;
            _cachedAxesTick = 0;
            id = _deviceId;
            hub = _hub;
            hwnd = _hwnd;
        }

        lock (_diGate)
        {
            try { effect?.Stop(); } catch { /* ignore */ }
            try { effect?.Unload(); } catch { /* ignore */ }
            effect?.Dispose();

            if (owned is not null)
            {
                try { owned.SendForceFeedbackCommand(ForceFeedbackCommand.SetActuatorsOff); } catch { /* ignore */ }
                try { owned.Unacquire(); } catch { /* ignore */ }
                owned.Dispose();
            }
            else if (shared is not null)
            {
                try { shared.SendForceFeedbackCommand(ForceFeedbackCommand.SetActuatorsOff); } catch { /* ignore */ }
            }
        }

        // Restore input acquire outside _diGate - RestoreNonExclusive takes InputHub's lock.
        if (owned is null && shared is not null)
            hub?.RestoreNonExclusive(id, hwnd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        _fallbackDi.Dispose();
    }

    private static WheelVendor DetectVendor(string? instanceName, string? productName)
    {
        var n = $"{instanceName} {productName}".ToLowerInvariant();
        if (n.Contains("simucube") || n.Contains("granite devices") || n.Contains("granite"))
            return WheelVendor.Simucube;
        if (n.Contains("simagic") || n.Contains("alpha mini") || n.Contains("alpha ultimate") || n.Contains("m10 wheel"))
            return WheelVendor.Simagic;
        if (n.Contains("fanatec") || n.Contains("podium") || n.Contains("csl dd") || n.Contains("gt dd") || n.Contains("clubsport dd"))
            return WheelVendor.Fanatec;
        if (n.Contains("moza"))
            return WheelVendor.Moza;
        if (n.Contains("thrustmaster") || n.Contains("t300") || n.Contains("t-gt") || n.Contains("ts-pc") || n.Contains("ts-xw"))
            return WheelVendor.Thrustmaster;
        if (n.Contains("logitech") || n.Contains("g29") || n.Contains("g920") || n.Contains("g923") || n.Contains("gpro"))
            return WheelVendor.Logitech;
        if (n.Contains("asetek") || n.Contains("vrs ") || n.Contains("cammus") || n.Contains("pxn") ||
            n.Contains("leoxz") || n.Contains("accuforce") || n.Contains("odw ") || n.Contains("openffboard"))
            return WheelVendor.Other;
        return WheelVendor.Generic;
    }

    private static string VendorLabel(WheelVendor v) => v switch
    {
        WheelVendor.Fanatec => "Fanatec",
        WheelVendor.Simucube => "Simucube",
        WheelVendor.Simagic => "Simagic",
        WheelVendor.Moza => "Moza",
        WheelVendor.Thrustmaster => "Thrustmaster",
        WheelVendor.Logitech => "Logitech",
        WheelVendor.Other => "Other",
        _ => "Generic",
    };

    private static Guid ResolveConstantForceGuid(Joystick joy)
    {
        try
        {
            foreach (var info in joy.GetEffects())
            {
                // Prefer a device-reported constant-force GUID when present.
                if (info.Guid == EffectGuid.ConstantForce)
                    return info.Guid;
                var typeName = info.Name ?? "";
                if (typeName.Contains("Constant", StringComparison.OrdinalIgnoreCase))
                    return info.Guid;
            }
        }
        catch
        {
            // Fall back to the standard GUID.
        }

        return EffectGuid.ConstantForce;
    }

    private static IntPtr ResolveHwnd()
    {
        var hwnd = Process.GetCurrentProcess().MainWindowHandle;
        return hwnd != IntPtr.Zero ? hwnd : GetDesktopWindow();
    }

    private static int ResolveFfAxisObjectId(Joystick joy, out string name)
    {
        foreach (var axisName in new[] { "X Axis", "X-Axis", "X" })
        {
            try
            {
                var obj = joy.GetObjectInfoByName(axisName);
                name = axisName;
                return (int)obj.ObjectId;
            }
            catch { /* try next */ }
        }

        var objects = joy.GetObjects(DeviceObjectTypeFlags.ForceFeedbackActuator | DeviceObjectTypeFlags.AbsoluteAxis).ToList();
        if (objects.Count > 0)
        {
            name = objects[0].Name;
            return (int)objects[0].ObjectId;
        }

        name = "";
        return -1;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
