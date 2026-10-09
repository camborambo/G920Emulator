using System.Buffers.Binary;
using System.Text;

namespace G920Emulator.Core.Telemetry;

/// <summary>
/// Locked SimHub External Sim packet for <c>simhub/G920Telemetry.simdef</c>
/// (UniqueId <c>8c4e2b1a-9f70-4d3e-b6a1-2d5c8e0f1734</c>).
/// Signatures and length were computed by SimHub 9.11+ from that definition.
/// Reorder/add fields only together with a new .simdef.
/// </summary>
public static class SimHubPacket
{
    public const string DefinitionUniqueId = "8c4e2b1a-9f70-4d3e-b6a1-2d5c8e0f1734";
    public const string DefinitionFileName = "G920Telemetry.simdef";
    public const string SimHubGameName = "G920 Emulator (simulated)";
    public const uint GameSignature = 2863604573u;
    public const uint TelemetrySignature = 1228639197u;
    public const ushort LayoutMajorVersion = 1;
    public const ushort LayoutMinorVersion = 0;
    public const int ExpectedPacketLength = 161;
    public const int HeaderSize = 55;
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 20778;
    /// <summary>
    /// SimHub's External Sim loop is 60 Hz. Faster packets are ignored.
    /// Slider allows 1–60 so testers can trade smoothness vs load.
    /// </summary>
    public const int DefaultSendHz = 60;
    public const int MinSendHz = 1;
    public const int MaxSendHz = 60;

    public static int ClampSendHz(int hz) =>
        hz <= 0 ? DefaultSendHz : Math.Clamp(hz, MinSendHz, MaxSendHz);
    public const float MaxSpeedKmh = 350f;
    /// <summary>Upper bound for tuning UI / docs - packet fields use the live TelemetryTuning values.</summary>
    public const float MaxEngineRpm = TelemetryTuning.AbsoluteRpmMax;

    /// <summary>SimHub TyreContactSurface.Primary</summary>
    public const ushort TyreContactPrimary = 1;
    /// <summary>SimHub TyreContactSurface.RumbleStrips</summary>
    public const ushort TyreContactRumbleStrips = 2;
    /// <summary>SimHub TyreContactSurface.Gravel</summary>
    public const ushort TyreContactGravel = 8;

    /// <summary>SimHub auto-detect plus session hint. Other G920 titles still work via OEM FFB.</summary>
    public static readonly string[] KnownGameProcessNames =
    [
        "NeedForSpeedHeat",
        "NeedForSpeedUnbound",
    ];

    public static byte[] CreateBuffer() => new byte[ExpectedPacketLength];

    public static void Write(Span<byte> dest, in TelemetryFrame frame, ulong emitterId, ulong sessionId, ulong packets, double sessionTimeSec)
    {
        if (dest.Length < ExpectedPacketLength)
            throw new ArgumentException("packet buffer too small", nameof(dest));

        dest.Clear();
        var o = 0;
        WriteU32(dest, ref o, GameSignature);
        WriteU32(dest, ref o, TelemetrySignature);
        WriteU16(dest, ref o, LayoutMajorVersion);
        WriteU16(dest, ref o, LayoutMinorVersion);
        WriteU64(dest, ref o, emitterId);
        dest[o++] = 0; // PacketId
        WriteU64(dest, ref o, packets);
        dest[o++] = (byte)(frame.SessionRunning ? 1 : 0);
        dest[o++] = (byte)(frame.SessionPaused ? 1 : 0);
        WriteU64(dest, ref o, sessionId);
        dest[o++] = 0; // IsReplay
        dest[o++] = (byte)(frame.SessionRunning ? 1 : 0); // IsUserInControl
        dest[o++] = 0; // IsAIInControl
        dest[o++] = 0; // IsSpectator
        WriteF64(dest, ref o, sessionTimeSec);
        WriteU32(dest, ref o, 0); // PhysicsDiscontinuityCounter
        if (o != HeaderSize)
            throw new InvalidOperationException($"header size {o} != {HeaderSize}");

        WriteF32(dest, ref o, frame.SpeedKmh);
        WriteF32(dest, ref o, frame.EngineRpm);
        WriteF32(dest, ref o, frame.EngineMaxRpm);
        WriteF32(dest, ref o, frame.EngineShiftRpm);
        dest[o++] = (byte)(frame.EngineIgnitionOn ? 1 : 0);
        dest[o++] = (byte)(frame.EngineStarted ? 1 : 0);
        WriteF32(dest, ref o, frame.Throttle);
        WriteF32(dest, ref o, frame.Brake);
        WriteF32(dest, ref o, frame.Clutch);
        WriteUtf8Z(dest.Slice(o, 8), frame.Gear);
        o += 8;
        WriteF32(dest, ref o, frame.LocalSurgeMs2);
        WriteF32(dest, ref o, frame.LocalSwayMs2);
        WriteF32(dest, ref o, frame.LocalHeaveMs2);
        WriteF32(dest, ref o, frame.SuspensionVelocityFrontLeftMps);
        WriteF32(dest, ref o, frame.SuspensionVelocityFrontRightMps);
        WriteF32(dest, ref o, frame.SuspensionVelocityRearLeftMps);
        WriteF32(dest, ref o, frame.SuspensionVelocityRearRightMps);
        WriteU16(dest, ref o, frame.TyreContactSurfaceFrontLeft);
        WriteU16(dest, ref o, frame.TyreContactSurfaceFrontRight);
        WriteU16(dest, ref o, frame.TyreContactSurfaceRearLeft);
        WriteU16(dest, ref o, frame.TyreContactSurfaceRearRight);
        WriteF32(dest, ref o, frame.Steering);
        WriteF32(dest, ref o, frame.FfbConstant);
        WriteF32(dest, ref o, frame.FfbSpring);
        WriteF32(dest, ref o, frame.FfbDamper);
        WriteF32(dest, ref o, frame.FfbPeriodic);
        WriteF32(dest, ref o, frame.SurfaceRumble);
        WriteF32(dest, ref o, frame.Impact);
        WriteF32(dest, ref o, frame.RoadLoad);
        if (o != ExpectedPacketLength)
            throw new InvalidOperationException($"packet size {o} != {ExpectedPacketLength}");
    }

    private static void WriteU16(Span<byte> dest, ref int o, ushort v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(o, 2), v);
        o += 2;
    }

    private static void WriteU32(Span<byte> dest, ref int o, uint v)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(o, 4), v);
        o += 4;
    }

    private static void WriteU64(Span<byte> dest, ref int o, ulong v)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(o, 8), v);
        o += 8;
    }

    private static void WriteF32(Span<byte> dest, ref int o, float v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(o, 4), v);
        o += 4;
    }

    private static void WriteF64(Span<byte> dest, ref int o, double v)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(dest.Slice(o, 8), v);
        o += 8;
    }

    private static void WriteUtf8Z(Span<byte> dest, string value)
    {
        dest.Clear();
        if (string.IsNullOrEmpty(value))
            return;
        // Gear is almost always a single ASCII char (N/R/1-6) - skip UTF-8 encoder.
        if (value.Length == 1 && value[0] < 0x80 && dest.Length >= 2)
        {
            dest[0] = (byte)value[0];
            dest[1] = 0;
            return;
        }
        var n = Encoding.UTF8.GetBytes(value.AsSpan(), dest[..^1]);
        dest[n] = 0;
    }
}

public readonly record struct TelemetryFrame(
    bool SessionRunning,
    bool SessionPaused,
    float SpeedKmh,
    float EngineRpm,
    float EngineMaxRpm,
    float EngineShiftRpm,
    bool EngineIgnitionOn,
    bool EngineStarted,
    float Throttle,
    float Brake,
    float Clutch,
    string Gear,
    float LocalSurgeMs2,
    float LocalSwayMs2,
    float LocalHeaveMs2,
    float SuspensionVelocityFrontLeftMps,
    float SuspensionVelocityFrontRightMps,
    float SuspensionVelocityRearLeftMps,
    float SuspensionVelocityRearRightMps,
    ushort TyreContactSurfaceFrontLeft,
    ushort TyreContactSurfaceFrontRight,
    ushort TyreContactSurfaceRearLeft,
    ushort TyreContactSurfaceRearRight,
    float Steering,
    float FfbConstant,
    float FfbSpring,
    float FfbDamper,
    float FfbPeriodic,
    float SurfaceRumble,
    float Impact,
    float RoadLoad,
    /// <summary>Live / ShakeIt engine-vib intensity 0..1 (RPM load × Engine scale). Not a UDP field.</summary>
    float EngineVibration,
    /// <summary>speed / current gear cap (0..1). Debug / Live only - not a UDP field.</summary>
    float GearSpeedFrac,
    /// <summary>Arcade handbrake button held. Live only - not a UDP field.</summary>
    bool HandbrakeHeld,
    /// <summary>Arcade NOS / turbo button held. Live only - not a UDP field.</summary>
    bool NosHeld);
