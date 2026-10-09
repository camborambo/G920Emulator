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
    // v7: per-type mix torque; isolated map name from v6.
    public const string MapName = "Local\\G920Emulator.FfbTorque.v7";
    public const uint Magic = 0x46463947; // 'G9FF'
    public const uint Version = 7;
    public const int TypeGainCount = 16;

    // Same names as g920ffb-effects.log EffectName().
    public static readonly string[] TypeNames =
    [
        "ConstantForce", "RampForce", "Square", "Sine", "Triangle", "SawtoothUp", "SawtoothDown",
        "Spring", "Damper", "Inertia", "Friction", "CustomForce",
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
        ulong AuxTickMs,
        int[] TypeTorque,
        int[] AuxTypeTorque,
        uint GamePid,
        uint AuxPid)
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

        /// <summary>Fill combined game+aux DI torque into <paramref name="dest"/> (no alloc).</summary>
        public void FillCombinedTypeTorqueDi(Span<int> dest)
        {
            var n = Math.Min(TypeGainCount, dest.Length);
            var auxLive = AuxPlaying && !IsAuxStale();
            for (var i = 0; i < n; i++)
            {
                var g = TypeTorque is { Length: > 0 } && i < TypeTorque.Length ? TypeTorque[i] : 0;
                var a = auxLive && AuxTypeTorque is { Length: > 0 } && i < AuxTypeTorque.Length
                    ? AuxTypeTorque[i]
                    : 0;
                dest[i] = g + a;
            }
            for (var i = n; i < dest.Length; i++)
                dest[i] = 0;
        }

        public int[] CombinedTypeTorqueDi()
        {
            var result = new int[TypeGainCount];
            FillCombinedTypeTorqueDi(result);
            return result;
        }

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
                AuxTickMs: _view.ReadUInt64(Offset.AuxTickMs),
                TypeTorque: ReadInt32Array(_view, Offset.TypeTorque, TypeGainCount, aux: false),
                AuxTypeTorque: ReadInt32Array(_view, Offset.AuxTypeTorque, TypeGainCount, aux: true),
                GamePid: ReadUInt32If(_view, Offset.GamePid),
                AuxPid: ReadUInt32If(_view, Offset.AuxPid));
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
    /// Writes per DI effect-type gains (UINT16, 10000 = 100%, up to 20000 = 200%).
    /// Applied by <c>g920ffb.dll</c> before effects are mixed.
    /// </summary>
    /// <returns>False when shared memory is not open (game OEM not publishing yet).</returns>
    public static bool WriteTypeGains(ReadOnlySpan<ushort> gains)
    {
        try
        {
            EnsureOpen();
            if (_view is null) return false;

            var count = Math.Min(TypeGainCount, gains.Length);
            for (var i = 0; i < count; i++)
                _view.Write(Offset.TypeGain + i * sizeof(ushort), gains[i]);
            return true;
        }
        catch (FileNotFoundException) { Close(); return false; }
        catch { Close(); return false; }
    }

    public static bool WriteTypeGains(FfbEffectGains gains) =>
        WriteTypeGains(gains.ToSharedMemoryGains());

    /// <summary>Read back TypeGain slots written by the emulator (for Debug verify).</summary>
    public static bool TryReadTypeGains(Span<ushort> dest)
    {
        try
        {
            EnsureOpen();
            if (_view is null) return false;
            var count = Math.Min(TypeGainCount, dest.Length);
            for (var i = 0; i < count; i++)
                dest[i] = _view.ReadUInt16(Offset.TypeGain + i * sizeof(ushort));
            for (var i = count; i < dest.Length; i++)
                dest[i] = 0;
            return true;
        }
        catch (FileNotFoundException) { Close(); return false; }
        catch { Close(); return false; }
    }

    /// <summary>
    /// Writes OEM mix options (CF invert, damper velocity/deadband scales) for <c>g920ffb.dll</c>.
    /// Scales use the same 10000 = 1.0 convention as type gains.
    /// </summary>
    public static bool WriteMixOptions(FfbOutputFeel? feel)
    {
        feel ??= FfbOutputFeel.CreateDefault();
        feel.Clamp();
        try
        {
            EnsureOpen();
            if (_view is null) return false;

            uint flags = feel.InvertConstantForce ? 1u : 0u;
            _view.Write(Offset.MixFlags, flags);
            _view.Write(Offset.DamperVelScale, ToDiScale(feel.DamperVelocityScale));
            _view.Write(Offset.DamperDeadbandScale, ToDiScale(feel.DamperDeadbandScale));
            return true;
        }
        catch (FileNotFoundException) { Close(); return false; }
        catch { Close(); return false; }
    }

    /// <summary>Read back OEM mix options (for Debug verify).</summary>
    public static bool TryReadMixOptions(out uint mixFlags, out ushort damperVelScale, out ushort damperDeadbandScale)
    {
        mixFlags = 0;
        damperVelScale = 0;
        damperDeadbandScale = 0;
        try
        {
            EnsureOpen();
            if (_view is null) return false;
            mixFlags = _view.ReadUInt32(Offset.MixFlags);
            damperVelScale = _view.ReadUInt16(Offset.DamperVelScale);
            damperDeadbandScale = _view.ReadUInt16(Offset.DamperDeadbandScale);
            return true;
        }
        catch (FileNotFoundException) { Close(); return false; }
        catch { Close(); return false; }
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

    public static string TypeName(uint typeId) =>
        typeId < (uint)TypeNames.Length ? TypeNames[typeId] : $"T{typeId}";

    /// <summary>
    /// Live per-type table for FFB debug / FFB Debug Overlay (fixed-width Consolas).
    /// Torque values are DI units scaled to −1..+1.
    /// </summary>
    public static string FormatOemEffects(uint seen, uint playing, ReadOnlySpan<int> torqueDi, uint lastEffectType)
    {
        static float Di(ReadOnlySpan<int> t, int i) =>
            i < t.Length ? t[i] / 10000f : 0f;

        var nl = Environment.NewLine;
        var cf = Di(torqueDi, 0);
        var periodic = Di(torqueDi, 2) + Di(torqueDi, 3) + Di(torqueDi, 4) + Di(torqueDi, 5) + Di(torqueDi, 6);
        var spring = Di(torqueDi, 7);
        var damper = Di(torqueDi, 8);
        var other = Di(torqueDi, 1) + Di(torqueDi, 9) + Di(torqueDi, 10) + Di(torqueDi, 11);
        var last = (seen | playing) == 0 ? "(none)" : TypeName(lastEffectType);
        var sb = new StringBuilder();
        sb.Append("MIX").Append(nl);
        AppendRow(sb, "cf", $"{cf,7:0.00}");
        AppendRow(sb, "periodic", $"{periodic,7:0.00}");
        AppendRow(sb, "spring", $"{spring,7:0.00}");
        AppendRow(sb, "damper", $"{damper,7:0.00}");
        AppendRow(sb, "other", $"{other,7:0.00}");
        AppendRow(sb, "last", last);
        sb.Append(nl);
        sb.Append($"  {"Type",-15} {"State",-8} {"Torque",7}").Append(nl);
        for (var i = 0; i < TypeNames.Length; i++)
        {
            var bit = 1u << i;
            var status = (playing & bit) != 0 ? "playing" : (seen & bit) != 0 ? "seen" : "idle";
            var torque = Di(torqueDi, i);
            sb.Append($"  {TypeNames[i],-15} {status,-8} {torque,7:0.00}").Append(nl);
        }
        return sb.ToString().TrimEnd();
    }

    private static void AppendRow(StringBuilder sb, string key, string value)
    {
        sb.Append("  ").Append(key.PadRight(15)).Append(value).Append(Environment.NewLine);
    }

    public static string FormatOemEffects(in Snapshot snap, bool playing)
    {
        Span<int> combined = stackalloc int[TypeGainCount];
        if (playing)
            snap.FillCombinedTypeTorqueDi(combined);
        else
            combined.Clear();
        return FormatOemEffects(
            snap.TypesSeen | snap.AuxTypesPlaying,
            playing ? snap.CombinedTypesPlaying : 0u,
            combined,
            snap.LastEffectType);
    }

    [ThreadStatic] private static int[]? t_typeTorqueBuf;
    [ThreadStatic] private static int[]? t_auxTypeTorqueBuf;

    private static int[] ReadInt32Array(MemoryMappedViewAccessor view, int offset, int count, bool aux)
    {
        // Reuse per-thread buffers across TryRead calls. Callers must not retain Snapshot
        // arrays across another TryRead on the same thread (telemetry copies what it needs).
        ref var slot = ref aux ? ref t_auxTypeTorqueBuf : ref t_typeTorqueBuf;
        if (slot is null || slot.Length < count)
            slot = new int[count];
        var arr = slot;

        var bytes = count * sizeof(int);
        if (view.Capacity < offset + bytes)
        {
            Array.Clear(arr, 0, count);
            return arr;
        }
        for (var i = 0; i < count; i++)
            arr[i] = view.ReadInt32(offset + i * sizeof(int));
        return arr;
    }

    private static uint ReadUInt32If(MemoryMappedViewAccessor view, int offset) =>
        view.Capacity >= offset + sizeof(uint) ? view.ReadUInt32(offset) : 0u;

    private static void EnsureOpen()
    {
        // Hot path: input + telemetry both touch shared memory. Avoid taking Gate when open.
        if (Volatile.Read(ref _view) is not null)
            return;

        lock (Gate)
        {
            if (_view is not null) return;
            _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
            Volatile.Write(ref _view, _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite));
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
            Volatile.Write(ref _view, null);
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
        public const int TypeTorque = 116; // INT32[16] → ends 180
        public const int AuxTypeTorque = 180; // INT32[16] → ends 244
        public const int GamePid = 244;
        public const int AuxPid = 248;
        public const int Size = 252;
    }
}
