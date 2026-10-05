namespace G920Emulator.VirtualHid;

/// <summary>
/// Minimal HID++ 2.0 Feature Access emulator for G920 force feedback (feature 0x8123).
/// Games/Logitech OEMForceFeedback talk Root + this feature over reports 0x11/0x12.
/// </summary>
public sealed class HidppFfbEmulator
{
    public const byte ReportLong = 0x11;
    public const byte ReportVeryLong = 0x12;
    public const int LongLength = 20;
    public const int VeryLongLength = 64;

    private const ushort FeatureRoot = 0x0000;
    private const ushort FeatureFeatureSet = 0x0001;
    private const ushort FeatureFfb = 0x8123;
    private const byte IndexRoot = 0;
    private const byte IndexFeatureSet = 1;
    private const byte IndexFfb = 2;

    private const byte FnGetInfo = 0x00;
    private const byte FnResetAll = 0x01;
    private const byte FnDownloadEffect = 0x02;
    private const byte FnSetEffectState = 0x03;
    private const byte FnDestroyEffect = 0x04;
    private const byte FnGetAperture = 0x05;
    private const byte FnSetAperture = 0x06;
    private const byte FnGetGlobalGains = 0x07;
    private const byte FnSetGlobalGains = 0x08;

    private const byte EffectConstant = 0x00;
    private const byte EffectSpring = 0x06;
    private const byte EffectDamper = 0x07;
    private const byte EffectFriction = 0x08;
    private const byte EffectInertia = 0x09;

    private const byte EffectStateStop = 0x01;
    private const byte EffectStatePlay = 0x02;

    private const int SlotCount = 8; // includes 1 reserved
    private const int PlayableSlots = SlotCount - 1;

    private readonly object _gate = new();
    private readonly EffectSlot[] _slots = new EffectSlot[PlayableSlots];
    private readonly Queue<byte[]> _pending11 = new();
    private readonly Queue<byte[]> _pending12 = new();

    private ushort _aperture = 900;
    private ushort _globalGain = 0xFFFF;
    private float _currentTorque;
    private float _steering; // -1..1
    private int _writeCount;
    private int _downloadCount;
    private int _playCount;
    private string _lastFn = "";

    // Emulator per-type gains (0..2), mapped from DI-style profile gains.
    private float _gainConstant = 1f;
    private float _gainSpring = 1f;
    private float _gainDamper = 1f;
    private float _gainFriction = 1f;
    private float _gainInertia = 1f;
    private float _gainPeriodic = 1f;

    public float CurrentTorque
    {
        get { lock (_gate) return _currentTorque; }
    }

    public HidppFfbStats GetStats()
    {
        lock (_gate)
        {
            return new HidppFfbStats(
                _writeCount,
                _downloadCount,
                _playCount,
                _currentTorque,
                _lastFn,
                _slots.Count(s => s.InUse),
                _slots.Count(s => s.InUse && s.Playing));
        }
    }

    /// <summary>
    /// Applies profile per-type gains (0..2). HID++ types are mapped to the closest DI groups.
    /// </summary>
    public void SetEffectGains(float constant, float spring, float damper, float friction, float inertia, float periodic)
    {
        lock (_gate)
        {
            _gainConstant = ClampGain(constant);
            _gainSpring = ClampGain(spring);
            _gainDamper = ClampGain(damper);
            _gainFriction = ClampGain(friction);
            _gainInertia = ClampGain(inertia);
            _gainPeriodic = ClampGain(periodic);
            RecomputeTorqueUnlocked();
        }
    }

    public void SetSteering(float steeringCentered)
    {
        lock (_gate)
        {
            _steering = Math.Clamp(steeringCentered, -1f, 1f);
            RecomputeTorqueUnlocked();
        }
    }

    public float ComputeTorque(float steeringCentered)
    {
        lock (_gate)
        {
            _steering = Math.Clamp(steeringCentered, -1f, 1f);
            RecomputeTorqueUnlocked();
            return _currentTorque;
        }
    }

    public bool TryHandleWrite(byte reportId, ReadOnlySpan<byte> data, out float torque, out byte[]? immediateResponse)
    {
        torque = 0f;
        immediateResponse = null;
        if (reportId is not (ReportLong or ReportVeryLong))
            return false;

        var payload = data;
        if (payload.Length > 0 && payload[0] == reportId)
            payload = payload[1..];

        if (payload.Length < 3)
            return false;

        var deviceIndex = payload[0];
        var featureIndex = payload[1];
        var funcSw = payload[2];
        var function = (byte)(funcSw >> 4);
        var paramsSpan = payload.Length > 3 ? payload[3..] : ReadOnlySpan<byte>.Empty;

        byte[]? responseParams;
        var responseReportId = reportId;

        lock (_gate)
        {
            _writeCount++;

            if (featureIndex == IndexRoot)
            {
                _lastFn = $"Root.fn{function}";
                responseParams = HandleRoot(function, paramsSpan);
            }
            else if (featureIndex == IndexFeatureSet)
            {
                _lastFn = $"FeatureSet.fn{function}";
                responseParams = HandleFeatureSet(function, paramsSpan);
            }
            else if (featureIndex == IndexFfb)
            {
                _lastFn = $"FFB.fn{function}";
                responseParams = HandleFfb(function, paramsSpan, out responseReportId);
                RecomputeTorqueUnlocked();
            }
            else
            {
                _lastFn = $"Err.feat{featureIndex}";
                // Unknown feature — soft-success empty response keeps some hosts happier than 0xFF errors.
                responseParams = [];
            }

            var response = BuildResponse(responseReportId, deviceIndex, featureIndex, funcSw, responseParams ?? []);
            EnqueueResponse(response);
            immediateResponse = response;
            torque = _currentTorque;
        }

        return true;
    }

    public bool TryTakePendingRead(byte reportId, Span<byte> destination)
    {
        lock (_gate)
        {
            var queue = reportId == ReportVeryLong ? _pending12 : _pending11;
            if (queue.Count == 0)
            {
                destination.Clear();
                if (destination.Length > 0)
                    destination[0] = reportId;
                return false;
            }

            var response = queue.Dequeue();
            destination.Clear();
            var copy = Math.Min(response.Length, destination.Length);
            response.AsSpan(0, copy).CopyTo(destination);
            if (destination.Length > 0)
                destination[0] = reportId;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            for (var i = 0; i < _slots.Length; i++)
                _slots[i] = default;
            _aperture = 900;
            _globalGain = 0xFFFF;
            _currentTorque = 0;
            _steering = 0;
            _writeCount = 0;
            _downloadCount = 0;
            _playCount = 0;
            _lastFn = "";
            _pending11.Clear();
            _pending12.Clear();
        }
    }

    private byte[] HandleRoot(byte function, ReadOnlySpan<byte> parameters)
    {
        if (function == 0)
        {
            ushort featureId = 0;
            if (parameters.Length >= 2)
                featureId = (ushort)((parameters[0] << 8) | parameters[1]);

            return featureId switch
            {
                FeatureRoot => [IndexRoot, 0x00, 0x02],
                FeatureFeatureSet => [IndexFeatureSet, 0x00, 0x01],
                FeatureFfb => [IndexFfb, 0x00, 0x01],
                _ => [0x00, 0x00, 0x00],
            };
        }

        // getProtocolVersion / ping
        var ping = parameters.Length >= 3 ? parameters[2] : (byte)0;
        return [0x04, 0x02, ping];
    }

    private byte[] HandleFeatureSet(byte function, ReadOnlySpan<byte> parameters)
    {
        // 0x0001 Feature Set: getCount / getFeatureId
        if (function == 0)
            return [3]; // root + featureSet + ffb (count of features)

        if (function == 1)
        {
            var index = parameters.Length > 0 ? parameters[0] : (byte)0;
            return index switch
            {
                0 => [(byte)(FeatureRoot >> 8), (byte)(FeatureRoot & 0xFF), 0x00, 0x02],
                1 => [(byte)(FeatureFeatureSet >> 8), (byte)(FeatureFeatureSet & 0xFF), 0x00, 0x01],
                2 => [(byte)(FeatureFfb >> 8), (byte)(FeatureFfb & 0xFF), 0x00, 0x01],
                _ => [0x00, 0x00, 0x00, 0x00],
            };
        }

        return [];
    }

    private byte[] HandleFfb(byte function, ReadOnlySpan<byte> parameters, out byte responseReportId)
    {
        responseReportId = ReportLong;

        switch (function)
        {
            case FnGetInfo:
                return [SlotCount, 0x00, 0x01]; // slots + reserved/version-ish

            case FnResetAll:
                for (var i = 0; i < _slots.Length; i++)
                    _slots[i] = default;
                return [];

            case FnDownloadEffect:
            {
                responseReportId = parameters.Length > (LongLength - 4) ? ReportVeryLong : ReportLong;
                _downloadCount++;
                var slot = AllocateOrUpdateSlot(parameters);
                return [slot];
            }

            case FnSetEffectState:
            {
                var slot = parameters.Length > 0 ? parameters[0] : (byte)0;
                var state = parameters.Length > 1 ? parameters[1] : EffectStateStop;
                if (slot >= 1 && slot <= PlayableSlots)
                {
                    ref var effect = ref _slots[slot - 1];
                    effect.Playing = state == EffectStatePlay;
                    if (effect.Playing) _playCount++;
                }
                else if (slot == 0)
                {
                    // Some hosts send slot 0 meaning "last / all constant"
                    for (var i = 0; i < _slots.Length; i++)
                    {
                        if (!_slots[i].InUse) continue;
                        _slots[i].Playing = state == EffectStatePlay;
                        if (_slots[i].Playing) _playCount++;
                    }
                }
                return [];
            }

            case FnDestroyEffect:
            {
                var slot = parameters.Length > 0 ? parameters[0] : (byte)0;
                if (slot >= 1 && slot <= PlayableSlots)
                    _slots[slot - 1] = default;
                return [];
            }

            case FnGetAperture:
                return [(byte)(_aperture >> 8), (byte)(_aperture & 0xFF)];

            case FnSetAperture:
                if (parameters.Length >= 2)
                {
                    _aperture = (ushort)((parameters[0] << 8) | parameters[1]);
                    if (_aperture < 180) _aperture = 180;
                    if (_aperture > 900) _aperture = 900;
                }
                return [];

            case FnGetGlobalGains:
                return [(byte)(_globalGain >> 8), (byte)(_globalGain & 0xFF), 0x00, 0x00];

            case FnSetGlobalGains:
                if (parameters.Length >= 2)
                    _globalGain = (ushort)((parameters[0] << 8) | parameters[1]);
                return [];

            default:
                return [];
        }
    }

    private byte AllocateOrUpdateSlot(ReadOnlySpan<byte> parameters)
    {
        byte requested = parameters.Length > 0 ? parameters[0] : (byte)0;
        byte slot;
        if (requested >= 1 && requested <= PlayableSlots)
        {
            slot = requested;
        }
        else
        {
            slot = 0;
            for (byte i = 0; i < PlayableSlots; i++)
            {
                if (!_slots[i].InUse)
                {
                    slot = (byte)(i + 1);
                    break;
                }
            }
            if (slot == 0)
                slot = 1;
        }

        var type = parameters.Length > 1 ? (byte)(parameters[1] & 0x7F) : EffectConstant;
        var autostart = parameters.Length > 1 && (parameters[1] & 0x80) != 0;

        if (requested == 0)
        {
            // Prefer updating an existing slot of the same type (live FFB updates).
            for (byte i = 0; i < PlayableSlots; i++)
            {
                if (_slots[i].InUse && _slots[i].Type == type)
                {
                    slot = (byte)(i + 1);
                    break;
                }
            }
        }

        ref var effect = ref _slots[slot - 1];
        var wasPlaying = effect.InUse && effect.Playing;
        effect.InUse = true;
        effect.Type = type;

        if (type == EffectConstant)
        {
            effect.Force = parameters.Length >= 8
                ? (short)((parameters[6] << 8) | parameters[7])
                : (short)0;
            // Racing titles often download constant forces and never send PLAY, or
            // update magnitude on an already-playing effect. Keep/force playing.
            effect.Playing = autostart || wasPlaying || effect.Force != 0;
        }
        else if (type is EffectSpring or EffectDamper or EffectFriction or EffectInertia)
        {
            // Condition params (see Linux hidpp_ff_upload_effect).
            effect.Force = 0;
            if (parameters.Length >= 16)
            {
                effect.LeftCoeff = (short)((parameters[8] << 8) | parameters[9]);
                effect.RightCoeff = (short)((parameters[14] << 8) | parameters[15]);
                effect.Center = parameters.Length >= 14
                    ? (short)((parameters[12] << 8) | parameters[13])
                    : (short)0;
            }
            // Condition effects are active once downloaded (games rarely send a separate PLAY).
            effect.Playing = true;
        }
        else
        {
            // Periodic / ramp: use magnitude-ish field as a weak constant contribution.
            effect.Force = parameters.Length >= 8
                ? (short)((parameters[6] << 8) | parameters[7])
                : (short)0;
            effect.Playing = autostart || wasPlaying || effect.Force != 0;
        }

        if (effect.Playing) _playCount++;
        return slot;
    }

    private void RecomputeTorqueUnlocked()
    {
        float sum = 0;

        for (var i = 0; i < _slots.Length; i++)
        {
            ref readonly var s = ref _slots[i];
            if (!s.InUse || !s.Playing)
                continue;

            if (s.Type == EffectConstant)
            {
                sum += (s.Force / 32767f) * _gainConstant;
                continue;
            }

            if (s.Type is EffectSpring or EffectDamper or EffectFriction or EffectInertia)
            {
                // Map condition coeffs (~±32767) into a restoring torque vs steering.
                var center = s.Center / 32767f;
                var error = _steering - center;
                var coeff = error >= 0 ? s.RightCoeff : s.LeftCoeff;
                // Damper/friction approx: oppose position (no velocity available here).
                var scale = s.Type == EffectSpring ? 1.0f : 0.35f;
                var typeGain = s.Type switch
                {
                    EffectSpring => _gainSpring,
                    EffectDamper => _gainDamper,
                    EffectFriction => _gainFriction,
                    _ => _gainInertia,
                };
                sum += -((coeff / 32767f) * error * scale) * typeGain;
            }
            else
            {
                sum += (s.Force / 32767f) * 0.35f * _gainPeriodic;
            }
        }

        var gain = _globalGain <= 0 ? 1f : _globalGain / 65535f;
        _currentTorque = Math.Clamp(sum * gain, -1f, 1f);
    }

    private void EnqueueResponse(byte[] response)
    {
        if (response[0] == ReportVeryLong)
        {
            _pending12.Clear();
            _pending12.Enqueue(response);
        }
        else
        {
            _pending11.Clear();
            _pending11.Enqueue(response);
        }
    }

    private static float ClampGain(float v) => Math.Clamp(v, 0f, 2f);

    private static byte[] BuildResponse(byte reportId, byte deviceIndex, byte featureIndex, byte funcSw, ReadOnlySpan<byte> parameters)
    {
        var length = reportId == ReportVeryLong ? VeryLongLength : LongLength;
        var response = new byte[length];
        response[0] = reportId;
        response[1] = deviceIndex;
        response[2] = featureIndex;
        response[3] = funcSw;
        var copy = Math.Min(parameters.Length, length - 4);
        if (copy > 0)
            parameters[..copy].CopyTo(response.AsSpan(4));
        return response;
    }

    private struct EffectSlot
    {
        public bool InUse;
        public bool Playing;
        public byte Type;
        public short Force;
        public short LeftCoeff;
        public short RightCoeff;
        public short Center;
    }
}

public readonly record struct HidppFfbStats(
    int WriteCount,
    int DownloadCount,
    int PlayCount,
    float CurrentTorque,
    string LastFunction,
    int SlotsInUse,
    int SlotsPlaying);
