using G920Emulator.Core.Ffb;
using G920Emulator.Core.Telemetry;

namespace G920Emulator.Core.Setup;

/// <summary>
/// Live bridge / FFB state captured at diagnostics export time.
/// </summary>
public sealed class DiagnosticsLiveSnapshot
{
    public bool BridgeRunning { get; init; }
    public string LinkStatus { get; init; } = "";
    public string? VirtualDeviceError { get; init; }
    public bool VirtualPreviewMode { get; init; }
    public bool VirtualCol01Present { get; init; }
    public string HostPathHint { get; init; } = "";

    /// <summary>Interrupt-push WinUHid input (no ReadReport).</summary>
    public bool InterruptPushVirtualInput { get; init; }
    public bool HostStaleAutoRecover { get; init; }
    public int HostStaleRecoverAgeMs { get; init; }
    public int HostStaleRecoverCooldownMs { get; init; }
    public int VirtualRecoverCount { get; init; }
    public long HostInputReadAgeMs { get; init; } = -1;
    public long SubmitNotReadyCount { get; init; }

    /// <summary>g920ffb COM InprocServer32 paths (install vs ProgramData) for oem-registry.txt.</summary>
    public string OemComInprocReport { get; init; } = "";

    public string? ActiveInputProfile { get; init; }
    public string? ActiveFfbProfile { get; init; }
    public string? FfbSourceDeviceId { get; init; }
    public string? FfbSourceDeviceName { get; init; }

    public double MasterGain { get; init; } = 1;
    public bool FfbInvert { get; init; }
    public FfbEffectGains? EffectGains { get; init; }
    public FfbOutputFeel? OutputFeel { get; init; }

    public FfbDiagnostics? Ffb { get; init; }
    public string TelemetryStatus { get; init; } = "";
    public TelemetryFrame? Telemetry { get; init; }
}
