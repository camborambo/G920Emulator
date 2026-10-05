using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace G920Emulator.Core.Ffb;

/// <summary>
/// Bidirectional shared memory with native <c>g920ffb.dll</c>.
/// Game OEM mix owns <see cref="Snapshot.Torque"/>; Steam/overlay may layer
/// <see cref="Snapshot.AuxTorque"/> so every effect on the virtual G920 reaches the base.
/// </summary>
public static class OemFfbSharedMemory
{
    // v6: AuxTorque layer; isolated map name from v5.
    public const string MapName = "Local\\G920Emulator.FfbTorque.v6";
    public const uint Magic = 0x46463947; // 'G9FF'
    public const uint Version = 6;
    public const int TypeGainCount = 16;

    private static readonly string[] TypeNames =
    [
        "Constant", "Ramp", "Square", "Sine", "Triangle", "SawUp", "SawDown",
        "Spring", "Damper", "Inertia", "Friction", "Custom",
    ];

    private static MemoryMappedFile? _mmf;
    private static MemoryMappedViewAccessor? _view;
    private static readonly object Gate = new();
    private static float _lastSteering;
    private static long _lastSteerTick;
    private static float _steeringVel;

    /// <summary>Rim travel (centered units, -1..1 span = 2) per second that maps to full DI velocity.</summary>
    private const double FullScaleVelocity = 3.0;
    private const double VelocitySmoothingSec = 0.015;

    public readonly record struct Snapshot(
        float Torque,
        float AuxTorque,
        uint Sequence,
        bool Playing,
        bool AuxPlaying,
        uint TypesSeen,
        uint TypesPlaying,
        uint AuxTypesPlaying,
        uint DownloadCount,
        uint LastEffectType,
        uint LastFlags,
        ulong TickMs,
        ulong AuxTickMs)
    {
        /// <summary>Combined torque for the physical base (game + aux rumble layer).</summary>
        public float CombinedTorque
        {
            get
            {
                var t = Torque;
                if (AuxPlaying && !IsAuxStale())
                    t += AuxTorque;
                return Math.Clamp(t, -1f, 1f);
            }
        }

        /// <summary>Types from game and fresh aux layer.</summary>
        public uint CombinedTypesPlaying =>
            TypesPlaying | (AuxPlaying && !IsAuxStale() ? AuxTypesPlaying : 0u);

        /// <summary>The driver thread stopped publishing (game exited or crashed).</summary>
        public bool IsStale(long maxAgeMs = 250) =>
            Environment.TickCount64 - (long)TickMs > maxAgeMs && IsAuxStale(maxAgeMs);

        public bool IsAuxStale(long maxAgeMs = 250) =>
            AuxTickMs == 0 || Environment.TickCount64 - (long)AuxTickMs > maxAgeMs;
    }

    public static bool TryRead(out Snapshot snap, out string? error)
    {
        snap = default;
        error = null;
        try
        {
            EnsureOpen();
            if (_view is null)
            {
                error = "shared memory not open";
                return false;
            }

            var magic = _view.ReadUInt32(Offset.Magic);
            if (magic != Magic)
            {
                error = $"bad magic 0x{magic:X8}";
                return false;
            }

            snap = new Snapshot(
                Torque: _view.ReadSingle(Offset.Torque),
                AuxTorque: _view.ReadSingle(Offset.AuxTorque),
                Sequence: _view.ReadUInt32(Offset.Sequence),
                Playing: _view.ReadUInt32(Offset.Playing) != 0,
                AuxPlaying: _view.ReadUInt32(Offset.AuxPlaying) != 0,
                TypesSeen: _view.ReadUInt32(Offset.TypesSeen),
                TypesPlaying: _view.ReadUInt32(Offset.TypesPlaying),
                AuxTypesPlaying: _view.ReadUInt32(Offset.AuxTypesPlaying),
                DownloadCount: _view.ReadUInt32(Offset.DownloadCount),
                LastEffectType: _view.ReadUInt32(Offset.LastEffectType),
                LastFlags: _view.ReadUInt32(Offset.LastFlags),
                TickMs: _view.ReadUInt64(Offset.TickMs),
                AuxTickMs: _view.ReadUInt64(Offset.AuxTickMs));
            return true;
        }
        catch (FileNotFoundException)
        {
            error = "g920ffb not publishing yet";
            Close();
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Close();
            return false;
        }
    }

    /// <summary>Legacy helper used by older call sites.</summary>
    public static bool TryRead(out float torque, out uint sequence, out bool playing, out string? error)
    {
        if (!TryRead(out var snap, out error))
        {
            torque = 0;
            sequence = 0;
            playing = false;
            return false;
        }

        torque = snap.CombinedTorque;
        sequence = snap.Sequence;
        playing = snap.Playing || (snap.AuxPlaying && !snap.IsAuxStale());
        return true;
    }

    public static void WriteSteering(float steeringCentered)
    {
        try
        {
            EnsureOpen();
            if (_view is null) return;

            steeringCentered = Math.Clamp(steeringCentered, -1f, 1f);
            var now = Stopwatch.GetTimestamp();
            var dtSec = (now - _lastSteerTick) / (double)Stopwatch.Frequency;

            if (dtSec > 0.1)
            {
                _steeringVel = 0f;
            }
            else if (dtSec >= 0.0005)
            {
                // DI velocity metric: ±1 (±10000) = FullScaleVelocity rim units per second.
                var raw = Math.Clamp((float)((steeringCentered - _lastSteering) / dtSec / FullScaleVelocity), -1f, 1f);
                var alpha = (float)(1.0 - Math.Exp(-dtSec / VelocitySmoothingSec));
                _steeringVel += (raw - _steeringVel) * alpha;
            }
            else
            {
                _view.Write(Offset.Steering, steeringCentered);
                return;
            }

            _view.Write(Offset.Steering, steeringCentered);
            _view.Write(Offset.SteeringVel, _steeringVel);

            _lastSteering = steeringCentered;
            _lastSteerTick = now;
        }
        catch (FileNotFoundException) { Close(); }
        catch { Close(); }
    }

    /// <summary>
    /// Writes per DI effect-type gains (UINT16, 10000 = 100%, up to 40000 = 400%).
    /// Applied by <c>g920ffb.dll</c> before effects are mixed.
    /// </summary>
    public static void WriteTypeGains(ReadOnlySpan<ushort> gains)
    {
        try
        {
            EnsureOpen();
            if (_view is null) return;

            var count = Math.Min(TypeGainCount, gains.Length);
            for (var i = 0; i < count; i++)
                _view.Write(Offset.TypeGain + i * sizeof(ushort), gains[i]);
        }
        catch (FileNotFoundException) { Close(); }
        catch { Close(); }
    }

    public static void WriteTypeGains(FfbEffectGains gains) =>
        WriteTypeGains(gains.ToSharedMemoryGains());

    /// <summary>
    /// Writes OEM mix options (CF invert, damper velocity/deadband scales) for <c>g920ffb.dll</c>.
    /// Scales use the same 10000 = 1.0 convention as type gains.
    /// </summary>
    public static void WriteMixOptions(FfbOutputFeel? feel)
    {
        feel ??= FfbOutputFeel.CreateDefault();
        feel.Clamp();
        try
        {
            EnsureOpen();
            if (_view is null) return;

            uint flags = feel.InvertConstantForce ? 1u : 0u;
            _view.Write(Offset.MixFlags, flags);
            _view.Write(Offset.DamperVelScale, ToDiScale(feel.DamperVelocityScale));
            _view.Write(Offset.DamperDeadbandScale, ToDiScale(feel.DamperDeadbandScale));
        }
        catch (FileNotFoundException) { Close(); }
        catch { Close(); }
    }

    private static ushort ToDiScale(double scale) =>
        (ushort)Math.Clamp((int)Math.Round(scale * 10000), 0, 40000);

    public static string FormatTypeMask(uint mask)
    {
        if (mask == 0) return "(none)";
        var sb = new StringBuilder();
        for (var i = 0; i < TypeNames.Length; i++)
        {
            if ((mask & (1u << i)) == 0) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(TypeNames[i]);
        }
        // Unknown high bits
        for (var i = TypeNames.Length; i < 32; i++)
        {
            if ((mask & (1u << i)) == 0) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append($"T{i}");
        }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }

    private static void EnsureOpen()
    {
        lock (Gate)
        {
            if (_view is not null) return;
            _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, Offset.Size, MemoryMappedFileAccess.ReadWrite);
            _lastSteerTick = Stopwatch.GetTimestamp();
            _steeringVel = 0f;
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _view?.Dispose();
            _mmf?.Dispose();
            _view = null;
            _mmf = null;
        }
    }

    private static class Offset
    {
        public const int Magic = 0;
        public const int Version = 4;
        public const int Sequence = 8;
        public const int Torque = 12;
        public const int Playing = 16;
        public const int TickMs = 20;
        public const int Steering = 28;
        public const int SteeringVel = 32;
        public const int TypesSeen = 36;
        public const int TypesPlaying = 40;
        public const int DownloadCount = 44;
        public const int LastEffectType = 48;
        public const int LastFlags = 52;
        public const int TypeGain = 56; // UINT16[16] → ends 88
        public const int AuxTorque = 88;
        public const int AuxPlaying = 92;
        public const int AuxTypesPlaying = 96;
        public const int AuxTickMs = 100;
        public const int MixFlags = 108;
        public const int DamperVelScale = 112;
        public const int DamperDeadbandScale = 114;
        public const int Size = 116;
    }
}
