using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace G920Emulator.Core.Ffb;

/// <summary>
/// Bidirectional shared memory with native <c>g920ffb.dll</c>.
/// </summary>
public static class OemFfbSharedMemory
{
    public const string MapName = "Local\\G920Emulator.FfbTorque";
    public const uint Magic = 0x46463947; // 'G9FF'
    public const uint Version = 3;

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

    public readonly record struct Snapshot(
        float Torque,
        uint Sequence,
        bool Playing,
        uint TypesSeen,
        uint TypesPlaying,
        uint DownloadCount,
        uint LastEffectType,
        uint LastFlags);

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
                Sequence: _view.ReadUInt32(Offset.Sequence),
                Playing: _view.ReadUInt32(Offset.Playing) != 0,
                TypesSeen: _view.ReadUInt32(Offset.TypesSeen),
                TypesPlaying: _view.ReadUInt32(Offset.TypesPlaying),
                DownloadCount: _view.ReadUInt32(Offset.DownloadCount),
                LastEffectType: _view.ReadUInt32(Offset.LastEffectType),
                LastFlags: _view.ReadUInt32(Offset.LastFlags));
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

        torque = snap.Torque;
        sequence = snap.Sequence;
        playing = snap.Playing;
        return true;
    }

    public static void WriteSteering(float steeringCentered)
    {
        try
        {
            EnsureOpen();
            if (_view is null) return;

            steeringCentered = Math.Clamp(steeringCentered, -1f, 1f);
            var now = Environment.TickCount64;
            var dtMs = Math.Max(1, now - _lastSteerTick);
            var vel = Math.Clamp((steeringCentered - _lastSteering) / dtMs, -1f, 1f);

            _view.Write(Offset.Steering, steeringCentered);
            _view.Write(Offset.SteeringVel, vel);

            _lastSteering = steeringCentered;
            _lastSteerTick = now;
        }
        catch (FileNotFoundException) { Close(); }
        catch { Close(); }
    }

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
            _view = _mmf.CreateViewAccessor(0, Marshal.SizeOf<G920FfbSharedState>(), MemoryMappedFileAccess.ReadWrite);
            _lastSteerTick = Environment.TickCount64;
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
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct G920FfbSharedState
    {
        public uint Magic;
        public uint Version;
        public uint Sequence;
        public float Torque;
        public uint Playing;
        public ulong TickMs;
        public float Steering;
        public float SteeringVel;
        public uint TypesSeen;
        public uint TypesPlaying;
        public uint DownloadCount;
        public uint LastEffectType;
        public uint LastFlags;
    }
}
