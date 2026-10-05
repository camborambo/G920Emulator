namespace G920Emulator.Core.Ffb;

public sealed class FfbDiagnostics
{
    public bool IsAttached { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public string VendorProfile { get; init; } = "Generic";
    public string CooperativeLevel { get; init; } = "";
    public string AxisInfo { get; init; } = "";
    public float LastCommandTorque { get; init; }
    public int LastMagnitude { get; init; }
    public float LastIncomingTorque { get; init; }
    public DateTime? LastIncomingUtc { get; init; }
    public DateTime? LastApplyUtc { get; init; }
    public string? LastError { get; init; }
    public string Status { get; init; } = "";
    public bool TestOverrideActive { get; init; }
    public bool TestAutoCenterActive { get; init; }
    public int IncomingUpdateCount { get; init; }
    public int ApplyCount { get; init; }

    // Populated by BridgeService from the virtual G920 HID++ emulator.
    public int HidppWriteCount { get; set; }
    public int HidppDownloadCount { get; set; }
    public int HidppPlayCount { get; set; }
    public int HidppSlotsInUse { get; set; }
    public int HidppSlotsPlaying { get; set; }
    public string HidppLastFunction { get; set; } = "";
    public float HidppCurrentTorque { get; set; }

    /// <summary>All host WriteReport/SetFeature events (not just parsed HID++).</summary>
    public int HostWriteCount { get; set; }
    public byte LastHostReportId { get; set; }
    public string LastHostWriteHex { get; set; } = "";
    public string HostPathHint { get; set; } = "";

    /// <summary>Torque from our OEM EffectDriver shared memory (g920ffb.dll).</summary>
    public float OemFfbTorque { get; set; }
    public uint OemFfbSequence { get; set; }
    public bool OemFfbPlaying { get; set; }
    public string OemFfbStatus { get; set; } = "";
    public string OemFfbTypesSeen { get; set; } = "";
    public string OemFfbTypesPlaying { get; set; } = "";
    public uint OemFfbDownloadCount { get; set; }

    /// <summary>Physical FFB rim angle fed into spring/damper (-1..1).</summary>
    public float FfbRimSteer { get; set; }

    /// <summary>Hardware DIPROP_AUTOCENTER currently requested on the FFB base.</summary>
    public bool? HardwareAutoCenter { get; set; }
}
