using System.Diagnostics;
using System.Runtime.InteropServices;
using G920Emulator.Core.Input;
using G920Emulator.Core.Models;
using SharpDX;
using SharpDX.DirectInput;

namespace G920Emulator.Core.Ffb;

/// <summary>
/// Applies force-feedback torque to a physical DirectInput FFB device.
/// Uses the same Joystick handle owned by <see cref="InputHub"/> so Fanatec / Simucube / Moza
/// bases are not blocked by a second exclusive acquire.
/// </summary>
public sealed class FfbBridge : IDisposable
{
    // DIJOFS_X — data-format offset for the X axis.
    private const int DiJofsX = 0;
    // Infinite duration (DIEFFECT.dwDuration = -1 / 0xFFFFFFFF).
    private const int InfiniteDuration = -1;

    private readonly object _gate = new();
    private readonly DirectInput _fallbackDi = new();
    private InputHub? _hub;
    private IntPtr _hwnd;
    private string? _deviceId;
    private string? _deviceName;
    private Joystick? _joystick;
    private Joystick? _ownedFallback;
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
    private float _lastIncomingTorque;
    private DateTime? _lastIncomingUtc;
    private DateTime? _lastApplyUtc;
    private string? _lastError;
    private string _status = "FFB: not attached.";
    private bool _testOverride;
    private int _incomingCount;
    private int _applyCount;
    private bool _disposed;

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

    public double Gain { get; set; } = 1.0;
    public bool Invert { get; set; }

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

    public bool TryAttach(string? deviceId, out string error) =>
        TryAttach(deviceId, _hwnd, out error);

    public bool TryAttach(string? deviceId, IntPtr hwnd, out string error)
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
                if (_hub is not null && _hub.TryGetJoystick(deviceId, out var hubJoy, out var hubName))
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

            try { joy.Unacquire(); } catch { /* ignore */ }

            string coop;
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

            try { joy.Properties.AutoCenter = false; } catch { /* some devices reject */ }
            try { joy.Properties.ForceFeedbackGain = 10000; } catch { /* optional */ }
            joy.Acquire();

            // Soft reset so True Drive / Fanatec drop stale effects from other clients.
            try { joy.SendForceFeedbackCommand(ForceFeedbackCommand.Reset); } catch { /* optional */ }
            try { joy.SendForceFeedbackCommand(ForceFeedbackCommand.SetActuatorsOn); } catch { /* optional */ }

            _constantForceGuid = ResolveConstantForceGuid(joy);

            var objectId = ResolveFfAxisObjectId(joy, out var axisName);
            var created = TryCreateConstantEffect(joy, objectId, axisName, out var effect, out var axisInfo, out var createError);
            if (!created || effect is null)
            {
                error = createError ?? "Could not create constant-force effect.";
                lock (_gate) { _status = "FFB: effect create failed."; _lastError = error; }
                DetachEffectsOnly();
                return false;
            }

            // Simucube True Drive only calculates torque for effects that are created AND playing.
            try { effect.Start(EffectPlayFlags.NoDownload); } catch { /* some drivers ignore */ }
            try { effect.Start(); } catch { /* already playing */ }

            if (!TrySetMagnitude(effect, 0, out var setError))
            {
                error = $"Effect created but updates fail: {setError}";
                lock (_gate) { _status = "FFB: effect update failed."; _lastError = error; }
                try { effect.Dispose(); } catch { /* ignore */ }
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
    /// Physical FFB axis position as -1..1 (center 0). Required for game spring/damper
    /// auto-center to track the real rim — not the virtual G920 / DualSense steer.
    /// </summary>
    public bool TryGetPhysicalSteering(out float steeringCentered)
    {
        steeringCentered = 0f;
        Joystick? joy;
        lock (_gate) joy = _joystick;
        if (joy is null) return false;

        try
        {
            joy.Poll();
            var state = joy.GetCurrentState();
            var x = state.X;
            if (x < 0) x = 0;
            if (x > 65535) x = 65535;
            steeringCentered = (x / 65535f) * 2f - 1f;
            return true;
        }
        catch
        {
            try
            {
                joy.Acquire();
                joy.Poll();
                var state = joy.GetCurrentState();
                var x = state.X;
                if (x < 0) x = 0;
                if (x > 65535) x = 65535;
                steeringCentered = (x / 65535f) * 2f - 1f;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <param name="torque">-1 .. 1, positive = right</param>
    public void UpdateTorque(float torque) => ApplyTorque(torque, fromTest: false);

    public void ApplyTestTorque(float torque)
    {
        lock (_gate) _testOverride = true;
        ApplyTorque(torque, fromTest: true);
    }

    public void ClearTestOverride()
    {
        lock (_gate) _testOverride = false;
        ApplyTorque(0, fromTest: true);
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
        lock (_gate)
        {
            if (!fromTest && _testOverride)
                return;
            effect = _constantEffect;
            joy = _joystick;
            if (effect is null)
                return;
        }

        // App convention: positive torque = right. DirectInput X on Fanatec/Simucube/Moza
        // bases is opposite that sense, so we negate when building DI magnitude.
        torque = Math.Clamp(torque, -1f, 1f);
        if (Invert) torque = -torque;
        torque = (float)(torque * Math.Clamp(Gain, 0, 2));

        var magnitude = (int)Math.Clamp(Math.Round(-torque * 10000), -10000, 10000);

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
            }
            else
            {
                lock (_gate)
                {
                    _lastError = string.IsNullOrEmpty(recreateError)
                        ? $"Apply failed: {setError}"
                        : $"Apply failed: {setError} | recreate: {recreateError}";
                }
                return;
            }
        }

        lock (_gate)
        {
            _lastCommandTorque = torque;
            _lastMagnitude = magnitude;
            _lastApplyUtc = DateTime.UtcNow;
            _applyCount++;
            _lastError = null;
        }
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
            effect.SetParameters(p,
                EffectParameterFlags.Direction |
                EffectParameterFlags.TypeSpecificParameters |
                EffectParameterFlags.Start);
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
                IncomingUpdateCount = _incomingCount,
                ApplyCount = _applyCount,
            };
        }
    }

    public void Detach()
    {
        DetachEffectsOnly();
        lock (_gate)
        {
            _deviceId = null;
            _deviceName = null;
            _status = "FFB: not attached.";
            _testOverride = false;
            _vendor = WheelVendor.Generic;
        }
    }

    private void DetachEffectsOnly()
    {
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
            id = _deviceId;
            hub = _hub;
            hwnd = _hwnd;
        }

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
            hub?.RestoreNonExclusive(id, hwnd);
        }
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

        throw new InvalidOperationException("Could not find an FFB axis on the selected device.");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
