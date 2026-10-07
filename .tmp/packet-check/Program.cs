using System.Buffers.Binary;
using G920Emulator.Core.Telemetry;

var frame = new TelemetryFrame(
    SessionRunning: true,
    SessionPaused: false,
    SpeedKmh: 160.9f,
    EngineRpm: 5500f,
    EngineMaxRpm: 8000f,
    EngineShiftRpm: 7360f,
    EngineIgnitionOn: true,
    EngineStarted: true,
    Throttle: 0.75f,
    Brake: 0.1f,
    Clutch: 0f,
    Gear: "3",
    LocalSurgeMs2: 5f,
    LocalSwayMs2: -2f,
    LocalHeaveMs2: 1f,
    SuspensionVelocityFrontLeftMps: 0.2f,
    SuspensionVelocityFrontRightMps: -0.15f,
    SuspensionVelocityRearLeftMps: 0.1f,
    SuspensionVelocityRearRightMps: -0.12f,
    TyreContactSurfaceFrontLeft: SimHubPacket.TyreContactRumbleStrips,
    TyreContactSurfaceFrontRight: SimHubPacket.TyreContactRumbleStrips,
    TyreContactSurfaceRearLeft: SimHubPacket.TyreContactPrimary,
    TyreContactSurfaceRearRight: SimHubPacket.TyreContactPrimary,
    Steering: 0.25f,
    FfbConstant: 0.4f,
    FfbSpring: 0.1f,
    FfbDamper: 0.05f,
    FfbPeriodic: 0.6f,
    SurfaceRumble: 0.55f,
    Impact: 0.8f,
    RoadLoad: 0.35f,
    EngineVibration: 0.6f);

var buf = SimHubPacket.CreateBuffer();
SimHubPacket.Write(buf, frame, 1, 2, 3, 12.5);

float F32(int o) => BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(o, 4));
ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(o, 2));
byte U8(int o) => buf[o];

Console.WriteLine($"len={buf.Length} expect={SimHubPacket.ExpectedPacketLength}");
Console.WriteLine($"sigTele={BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4))} expect={SimHubPacket.TelemetrySignature}");
Console.WriteLine($"SpeedKmh@55={F32(55)}");
Console.WriteLine($"EngineRpm@59={F32(59)}");
Console.WriteLine($"EngineMaxRpm@63={F32(63)}");
Console.WriteLine($"EngineShiftRpm@67={F32(67)}");
Console.WriteLine($"Ignition@71={U8(71)} Started@72={U8(72)}");
Console.WriteLine($"SurfaceRumble@149={F32(149)}");
Console.WriteLine($"TyreFL@121={U16(121)}");
var gear = System.Text.Encoding.UTF8.GetString(buf, 85, 8).TrimEnd('\0');
Console.WriteLine($"Gear@85='{gear}'");

bool ok =
    buf.Length == 161 &&
    Math.Abs(F32(59) - 5500f) < 0.01f &&
    Math.Abs(F32(67) - 7360f) < 0.01f &&
    U16(121) == SimHubPacket.TyreContactRumbleStrips &&
    gear.StartsWith('3');
Console.WriteLine(ok ? "PACKET OK" : "PACKET FAIL");
Environment.Exit(ok ? 0 : 1);
