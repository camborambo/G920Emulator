using System.Diagnostics;
using G920Emulator.Core.Ffb;
using G920Emulator.Core.Input;
using G920Emulator.Core.Mapping;
using G920Emulator.Core.Models;
using G920Emulator.Core.Profiles;
using G920Emulator.Core.Telemetry;

namespace G920Emulator.Core.Bridge;

public interface IVirtualG920Device : IDisposable
{
    bool IsRunning { get; }
    bool IsDriverAvailable { get; }
    string? LastError { get; }
    bool Start(Func<byte[]> latestReportProvider, Action<FfbCommand>? onFfb = null);
    void Stop();

    /// <summary>Push the latest joystick report. Returns false if the virtual device rejected it.</summary>
    bool SubmitReport(byte[] report);

    /// <summary>Tear down and recreate the WinUHid device (orphan Col01 / dead submits).</summary>
    bool TryRecover(Func<byte[]> latestReportProvider, Action<FfbCommand>? onFfb = null);

    /// <summary>Sample HID++ FFB torque for the current steering position (-1..1).</summary>
    float SampleFfbTorque(float steeringCentered);

    /// <summary>Apply per-effect gains for the HID++ fallback path (0..2).</summary>
    void SetFfbEffectGains(float constant, float spring, float damper, float friction, float inertia, float periodic);

    /// <summary>Diagnostic counters for game→virtual HID++ FFB ingress.</summary>
    VirtualFfbIngressStats GetFfbIngressStats();
}

public readonly record struct VirtualFfbIngressStats(
    int WriteCount,
    int DownloadCount,
    int PlayCount,
    float CurrentTorque,
    string LastFunction,
    int SlotsInUse,
    int SlotsPlaying,
    int HostWriteCount = 0,
    byte LastHostReportId = 0,
    string LastHostWriteHex = "",
    string HostPathHint = "");

public readonly record struct FfbCommand(float Torque, bool Stop);

/// <summary>Owns the polling loop that maps physical inputs to the virtual G920.</summary>
public sealed class BridgeService : IDisposable
{
    private readonly InputHub _inputHub = new();
    private readonly MapperEngine _mapper = new();
    private readonly FfbBridge _ffb = new();
    private readonly FfbOutputSmoother _ffbSmoother = new();
    private readonly object _gate = new();

    private IVirtualG920Device? _virtualDevice;
    private MappingProfile _profile = MappingProfile.CreateDefault();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _ffbApplyLoop;
    private Task? _telemetryLoop;
    private MappedG920State _latest = new();
    private IReadOnlyDictionary<string, DeviceState> _latestDevices =
        new Dictionary<string, DeviceState>(StringComparer.OrdinalIgnoreCase);
    private byte[] _latestReport = G920ReportBuilder.Build(new MappedG920State());
    /// <summary>Immutable snapshot for WinUHid ReadReport callbacks (no Bridge lock).</summary>
    private byte[] _callbackReport = G920ReportBuilder.Build(new MappedG920State());
    private bool _disposed;
    private Action<MappedG920State>? _onMappedFrame;
    private uint _lastOemFfbSequence;
    private float _oemFfbTorque;
    private bool _oemFfbPlaying;
    private string _oemFfbStatus = "OEM FFB: waiting for g920ffb.dll";
    private string _oemFfbTypesSeen = "";
    private string _oemFfbTypesPlaying = "";
    private string _oemFfbEffectsDetail = OemFfbSharedMemory.FormatOemEffects(0, 0, [], 0);
    private uint _oemFfbDownloadCount;
    private uint _oemTypesSeenMask;
    private uint _oemTypesPlayingMask;
    private uint _oemLastEffectType;
    private int[] _oemTypeTorqueDi = new int[OemFfbSharedMemory.TypeGainCount];
    private float _ffbRimSteer;
    private float _lastRim;
    private long _lastRimTick;
    private float _rimVelocity;
    private bool _forcedCenterActive;
    private long _lastInputRefreshTick;
    private int _inputRefreshInFlight;
    private int _submitFailStreak;
    private int _recoverCount;
    private string _linkStatus = "";
    /// <summary>Last live axes from the exclusive FFB base (InputHub does not Poll it).</summary>
    private Dictionary<string, float>? _lastFfbAxes01;
    private bool[]? _lastFfbButtons;
    private int _lastFfbHat = -1;
    /// <summary>Queued physical CF target - applied on a side thread so Simucube DI
    /// SetParameters cannot stall virtual G920 axis submits.</summary>
    private float _ffbQueuedTorque;
    private int _ffbQueuedVersion;
    private float _emittedSteer;
    private bool _emittedSteerValid;
    private HashSet<string>? _pollDeviceIds;
    private MappingProfile? _pollDeviceIdsForProfile;
    /// <summary>When true, <see cref="GetPollDeviceIds"/> returns null (poll every game control).</summary>
    private bool _pollAllDevices;
    private const int InputTargetPeriodMs = 2;
    /// <summary>Background DI rescan interval. Hot-path RefreshDevices was hitching steering.</summary>
    private const int InputRefreshIntervalMs = 8000;
    private readonly TelemetrySynthesizer _telemetrySynth = new();
    private readonly SimHubUdpSender _telemetryUdp = new();
    private readonly byte[] _telemetryPacket = SimHubPacket.CreateBuffer();
    private readonly ulong _telemetryEmitterId = SimHubRegistrationId();
    private TelemetrySettings _telemetrySettings = new();
    private TelemetryFrame _latestTelemetry;
    private ulong _telemetrySessionId;
    private ulong _telemetryPackets;
    private double _telemetrySessionTime;
    private long _lastTelemetrySendTick;
    private string _telemetryStatus = "Telemetry: off";
    /// <summary>0/1 - input thread skips telemetry queue when off (no synth/UDP/process scan).</summary>
    private int _telemetryEnabledFlag;
    /// <summary>Latest sample for the telemetry side thread (never blocks HID/FFB).</summary>
    private MappedG920State? _telemetryQueuedMapped;
    private float _telemetryQueuedSteer;
    private int _telemetryQueuedHandbrake;
    private int _telemetryQueuedNos;
    private int _telemetryQueuedVersion;

    public InputHub InputHub => _inputHub;
    public FfbBridge Ffb => _ffb;

    /// <summary>Optional per-frame callback after mapping.</summary>
    public Action<MappedG920State>? OnMappedFrame
    {
        get { lock (_gate) return _onMappedFrame; }
        set { lock (_gate) _onMappedFrame = value; }
    }
    public MappingProfile Profile
    {
        get { lock (_gate) return _profile; }
        set
        {
            lock (_gate)
            {
                _profile = value;
                // Bind-on-the-fly mutates the same profile instance - always rebuild the
                // poll filter so newly bound device IDs are included next frame.
                _pollDeviceIds = null;
                _pollDeviceIdsForProfile = null;
                _pollAllDevices = false;
            }
            _ffb.ApplyFromProfile(value);
            ApplyEffectGains(value);
        }
    }

    /// <summary>Clears custom Toggle latches (call after load / custom binding edits).</summary>
    public void ResetCustomBindingState()
    {
        _mapper.ResetCustomState();
        lock (_gate)
        {
            // Customs may have added new device IDs - force poll filter rebuild.
            _pollDeviceIds = null;
            _pollDeviceIdsForProfile = null;
            _pollAllDevices = false;
        }
    }

    public bool IsRunning => _cts is not null;
    public MappedG920State LatestState { get { lock (_gate) return _latest; } }
    public IReadOnlyDictionary<string, DeviceState> LatestDevices { get { lock (_gate) return _latestDevices; } }
    public string? VirtualDeviceError => _virtualDevice?.LastError;
    public bool IsDriverAvailable => _virtualDevice?.IsDriverAvailable ?? false;
    public string? LastFfbStatus { get; private set; }

    /// <summary>Input / virtual-device health while the bridge runs (empty when OK).</summary>
    public string LinkStatus { get { lock (_gate) return _linkStatus; } }

    public TelemetryFrame LatestTelemetry { get { lock (_gate) return _latestTelemetry; } }
    public string TelemetryStatus { get { lock (_gate) return _telemetryStatus; } }
    public double TelemetryPacketsPerSecond => _telemetryUdp.PacketsPerSecond;

    public void ConfigureTelemetry(TelemetrySettings settings)
    {
        settings ??= new TelemetrySettings();
        var hz = SimHubPacket.ClampSendHz(settings.SendHz);
        var tuning = (settings.Tuning ?? TelemetryTuning.CreateDefault()).Clone();
        var host = string.IsNullOrWhiteSpace(settings.Host) ? SimHubPacket.DefaultHost : settings.Host.Trim();
        var port = settings.Port is < 1 or > 65535 ? SimHubPacket.DefaultPort : settings.Port;
        lock (_gate)
        {
            _telemetrySettings = new TelemetrySettings
            {
                Enabled = settings.Enabled,
                Host = host,
                Port = port,
                SendHz = hz,
                Tuning = tuning,
            };
            _telemetrySynth.Configure(tuning);
            if (!_telemetrySettings.Enabled)
                _telemetryStatus = "Telemetry: off";
        }

        Volatile.Write(ref _telemetryEnabledFlag, settings.Enabled ? 1 : 0);

        // UDP / DNS only when enabled - avoid host resolve cost while telemetry is off.
        if (settings.Enabled)
        {
            _telemetryUdp.Configure(host, port);
            EngineVibrationScaleBridge.Publish(tuning.EngineVibrationScale);
        }
        else
        {
            EngineVibrationScaleBridge.Publish(1f);
        }
    }

    public void AttachVirtualDevice(IVirtualG920Device device)
    {
        lock (_gate)
        {
            _virtualDevice?.Dispose();
            _virtualDevice = device;
        }
    }

    public IReadOnlyList<InputDeviceInfo> RefreshDevices() => _inputHub.RefreshDevices();

    /// <summary>
    /// Poll every attached device for bind-listen / UI, overlaying the pinned FFB base
    /// so Fanatec (etc.) buttons stay live while exclusive FFB owns that joystick.
    /// </summary>
    public IReadOnlyDictionary<string, DeviceState> PollForUi()
    {
        string? ffbId;
        lock (_gate) ffbId = _profile.FfbSourceDeviceId;
        if (_ffb.IsReady && !string.IsNullOrWhiteSpace(_ffb.ActiveDeviceId))
            ffbId = _ffb.ActiveDeviceId;

        var devices = _inputHub.Poll();
        return OverlayPinnedFfbAxes(devices, ffbId);
    }

    /// <summary>
    /// Map with the same <see cref="MapperEngine"/> used by the bridge loop so custom
    /// Toggle latches persist across UI preview frames (and into Start).
    /// When the bridge is running, returns <see cref="LatestState"/> — do not Map here or
    /// rising edges are double-counted and Toggle latches desync from the virtual HID report.
    /// </summary>
    public MappedG920State MapForUi()
    {
        if (IsRunning)
        {
            lock (_gate) return _latest;
        }

        var devices = PollForUi();
        lock (_gate)
        {
            // Bridge may have started while we polled.
            if (_cts is not null)
                return _latest;

            var mapped = _mapper.Map(_profile, devices);
            _latest = mapped;
            _latestDevices = devices;
            // Keep callback snapshot in sync so a subsequent Start already has the latched report.
            var report = G920ReportBuilder.Build(mapped);
            _latestReport = report;
            Volatile.Write(ref _callbackReport, report);
            return mapped;
        }
    }

    public void BindFfbWindow(IntPtr hwnd) => _ffb.BindInputHub(_inputHub, hwnd);

    public FfbDiagnostics GetFfbDiagnostics()
    {
        var d = _ffb.GetDiagnostics();
        lock (_gate)
        {
            d.OemFfbTorque = _oemFfbTorque;
            d.OemFfbSequence = _lastOemFfbSequence;
            d.OemFfbPlaying = _oemFfbPlaying;
            d.OemFfbStatus = _oemFfbStatus;
            d.OemFfbTypesSeen = _oemFfbTypesSeen;
            d.OemFfbTypesPlaying = _oemFfbTypesPlaying;
            d.OemFfbEffectsDetail = _oemFfbEffectsDetail;
            d.OemFfbDownloadCount = _oemFfbDownloadCount;
            d.OemTypesSeenMask = _oemTypesSeenMask;
            d.OemTypesPlayingMask = _oemTypesPlayingMask;
            d.OemLastEffectType = _oemLastEffectType;
            d.OemTypeTorqueDi = _oemTypeTorqueDi;
            d.FfbRimSteer = _ffbRimSteer;
            d.HardwareAutoCenter = _ffb.TestAutoCenterActive ? true : false;
        }

        var ingress = _virtualDevice?.GetFfbIngressStats();
        if (ingress is null)
            return d;

        d.HidppWriteCount = ingress.Value.WriteCount;
        d.HidppDownloadCount = ingress.Value.DownloadCount;
        d.HidppPlayCount = ingress.Value.PlayCount;
        d.HidppSlotsInUse = ingress.Value.SlotsInUse;
        d.HidppSlotsPlaying = ingress.Value.SlotsPlaying;
        d.HidppLastFunction = ingress.Value.LastFunction;
        d.HidppCurrentTorque = ingress.Value.CurrentTorque;
        d.HostWriteCount = ingress.Value.HostWriteCount;
        d.LastHostReportId = ingress.Value.LastHostReportId;
        d.LastHostWriteHex = ingress.Value.LastHostWriteHex;
        d.HostPathHint = ingress.Value.HostPathHint;
        return d;
    }

    public void Start()
    {
        if (_cts is not null)
            return;

        IVirtualG920Device device;
        lock (_gate)
        {
            // Ensure game FFB is not blocked by a leftover test override.
            _ffb.ClearTestOverride();
            _ffbSmoother.Reset();
            _ffb.BindInputHub(_inputHub, IntPtr.Zero);
            device = _virtualDevice ?? throw new InvalidOperationException("Virtual device not attached.");
        }

        // Attach outside _gate (see TryAttachFfb) so Start cannot deadlock the loop.
        AttachFfbFromProfile();

        // Lock-free report provider: WinUHid ReadReport must never wait on Bridge._gate
        // (that deadlock freezes reports - joy.cpl still lists the device, buttons die).
        if (!device.Start(() =>
            {
                var snap = Volatile.Read(ref _callbackReport);
                return (byte[])snap.Clone();
            }, OnFfb))
        {
            throw new InvalidOperationException(device.LastError ?? "Failed to start virtual G920.");
        }

        ApplyEffectGains(_profile);
        _submitFailStreak = 0;
        _lastInputRefreshTick = 0;
        lock (_gate) _linkStatus = "";

        _telemetrySynth.Reset();
        _telemetryUdp.ResetStats();
        _telemetrySessionId = SimHubRegistrationId();
        _telemetryPackets = 0;
        _telemetrySessionTime = 0;
        _lastTelemetrySendTick = 0;
        Volatile.Write(ref _telemetryQueuedVersion, 0);
        Volatile.Write(ref _telemetryQueuedMapped, null);

        _cts = new CancellationTokenSource();
        _loop = Task.Factory.StartNew(() => RunLoop(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ffbApplyLoop = Task.Factory.StartNew(() => RunFfbApplyLoop(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _telemetryLoop = Task.Factory.StartNew(() => RunTelemetryLoop(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Stop()
    {
        _cts?.Cancel();
        var loop = _loop;
        var ffbLoop = _ffbApplyLoop;
        var telemetryLoop = _telemetryLoop;
        _loop = null;
        _ffbApplyLoop = null;
        _telemetryLoop = null;

        // Drop test FFB loops first so they are not ApplyTorque'ing during detach.
        try { _ffb.ClearTestOverride(); } catch { /* ignore */ }

        // Brief wait only - never hang Stop on a stuck loop iteration.
        try { loop?.Wait(400); } catch { /* ignore */ }
        try { ffbLoop?.Wait(400); } catch { /* ignore */ }
        try { telemetryLoop?.Wait(400); } catch { /* ignore */ }
        if (loop is { IsCompleted: false })
            _ = loop.ContinueWith(_ => { /* observe */ }, TaskScheduler.Default);
        if (ffbLoop is { IsCompleted: false })
            _ = ffbLoop.ContinueWith(_ => { /* observe */ }, TaskScheduler.Default);
        if (telemetryLoop is { IsCompleted: false })
            _ = telemetryLoop.ContinueWith(_ => { /* observe */ }, TaskScheduler.Default);

        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;

        // Tear down WinUHid outside _gate. Callbacks must not wait on Bridge._gate while
        // WinUHidStopDevice waits on those callbacks.
        IVirtualG920Device? virtualDevice;
        lock (_gate)
        {
            virtualDevice = _virtualDevice;
        }

        try { virtualDevice?.Stop(); } catch { /* ignore */ }

        // CRITICAL: do not hold Bridge._gate across FFB Detach. Detach → RestoreNonExclusive
        // needs InputHub._gate; the loop (if still winding down) takes InputHub then Bridge
        // - holding Bridge here deadlocks Stop and freezes the app.
        // Cap DI detach - Unacquire/Reset can hang on some bases while the rim is moving.
        var ffbTeardown = Task.Run(() =>
        {
        try { _ffb.Stop(); } catch { /* ignore */ }
        try { _ffb.Detach(); } catch { /* ignore */ }
        });
        try { ffbTeardown.Wait(1500); } catch { /* ignore */ }
        _inputHub.PinFfbDevice(null);
        _lastFfbAxes01 = null;
        _lastFfbButtons = null;
        _lastFfbHat = -1;
        _emittedSteerValid = false;
        _pollDeviceIds = null;
        _pollDeviceIdsForProfile = null;

        OemFfbSharedMemory.Close();
        lock (_gate) _linkStatus = "";
    }

    /// <summary>Re-attach FFB output to the profile's selected physical device.</summary>
    public bool TryAttachFfb(out string status)
    {
        // Must not hold _gate across RefreshDevices / DI Exclusive acquire - the bridge
        // loop takes InputHub then _gate; holding _gate here deadlocks and freezes
        // virtual G920 buttons until the process is restarted.
        AttachFfbFromProfile();
        status = LastFfbStatus ?? "";
        return _ffb.IsReady;
    }

    private void AttachFfbFromProfile()
    {
        MappingProfile profile;
        lock (_gate) profile = _profile;
        _ffb.ApplyFromProfile(profile);

        var attached = _inputHub.RefreshDevices();
        int remapped;
        lock (_gate)
        {
            remapped = Profiles.DeviceBindingResolver.RemapProfile(_profile, attached);
            // Auto-pick first FFB-capable device when the profile has none selected.
            if (string.IsNullOrWhiteSpace(_profile.FfbSourceDeviceId))
            {
                var ffbDevice = attached.FirstOrDefault(d => d.SupportsForceFeedback);
                if (ffbDevice is not null)
                {
                    _profile.FfbSourceDeviceId = ffbDevice.Id;
                    _profile.FfbSourceProductId = ffbDevice.ProductId;
                }
            }

            profile = _profile;
            if (remapped > 0)
                _linkStatus = $"Remapped {remapped} binding(s) after FFB attach";
        }

        if (string.IsNullOrWhiteSpace(profile.FfbSourceDeviceId))
        {
            _inputHub.PinFfbDevice(null);
            _lastFfbAxes01 = null;
            _lastFfbButtons = null;
            _lastFfbHat = -1;
            LastFfbStatus = "FFB: no force-feedback device selected.";
            return;
        }

        // Baseline cache before exclusive attach, then pin so Poll never touches this joy.
        _inputHub.CapturePinnedBaseline(profile.FfbSourceDeviceId);
        _inputHub.PinFfbDevice(profile.FfbSourceDeviceId);

        if (_ffb.TryAttach(profile.FfbSourceDeviceId, out var error))
        {
            LastFfbStatus = "FFB: attached to physical device.";
            if (_ffb.TryGetPhysicalInput(out var axes, out var buttons, out var hat))
            {
                _lastFfbAxes01 = axes;
                _lastFfbButtons = buttons;
                _lastFfbHat = hat;
            }
        }
        else
        {
            _inputHub.PinFfbDevice(null);
            _lastFfbAxes01 = null;
            _lastFfbButtons = null;
            _lastFfbHat = -1;
            LastFfbStatus = string.IsNullOrWhiteSpace(error) ? "FFB: attach failed." : $"FFB: {error}";
        }
    }

    private void OnFfb(FfbCommand cmd)
    {
        var torque = cmd.Stop ? 0f : cmd.Torque;
        _ffb.NoteIncoming(torque);
        // Queue - never block the WinUHid callback on physical DI SetParameters.
        QueuePhysicalFfbTorque(torque);
    }

    private void QueuePhysicalFfbTorque(float torque)
    {
        Volatile.Write(ref _ffbQueuedTorque, torque);
        Interlocked.Increment(ref _ffbQueuedVersion);
    }

    private void TryRecoverVirtualDevice()
    {
        IVirtualG920Device? device;
        lock (_gate) device = _virtualDevice;
        if (device is null)
            return;

        lock (_gate)
            _linkStatus = "Recovering virtual G920 (submit/Col01 lost)…";

        var ok = device.TryRecover(
            () =>
            {
                var snap = Volatile.Read(ref _callbackReport);
                return (byte[])snap.Clone();
            },
            OnFfb);

        _recoverCount++;
        lock (_gate)
        {
            _linkStatus = ok
                ? $"Virtual G920 recovered (#{_recoverCount})"
                : $"Virtual G920 recover failed (#{_recoverCount}): {device.LastError}";
        }

        if (ok)
            ApplyEffectGains(_profile);
    }

    private void RunLoop(CancellationToken token)
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; }
        catch { /* best-effort */ }

        var sw = Stopwatch.StartNew();
        while (!token.IsCancellationRequested)
        {
            sw.Restart();
            try
            {
                var now = Environment.TickCount64;
                // Re-enumerate off the hot path - GetDevices under the loop caused visible
                // steering pauses on multi-device Simucube rigs (~every 1.5s before).
                if (now - _lastInputRefreshTick >= InputRefreshIntervalMs &&
                    Interlocked.CompareExchange(ref _inputRefreshInFlight, 1, 0) == 0)
                {
                    _lastInputRefreshTick = now;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var attached = _inputHub.RefreshDevices();
                            lock (_gate)
                            {
                                var remapped = Profiles.DeviceBindingResolver.RemapProfile(_profile, attached);
                                if (remapped > 0)
                                    _linkStatus = $"Remapped {remapped} binding(s) after device refresh";
                                _pollDeviceIds = null; // rebuild filter after remap
                            }
                        }
                        catch { /* keep looping */ }
                        finally
                        {
                            Interlocked.Exchange(ref _inputRefreshInFlight, 0);
                        }
                    });
                }

                MappingProfile profile;
                lock (_gate) profile = _profile;
                var pollIds = GetPollDeviceIds(profile);

                // Bound devices (+ pinned FFB cache). Null filter = poll all (used when
                // custom bindings are present so Toggle/FN sources are never dropped).
                var devices = _inputHub.Poll(pollIds);
                devices = OverlayPinnedFfbAxes(devices, profile.FfbSourceDeviceId);

                MappedG920State mapped;
                Action<MappedG920State>? mappedCallback;
                byte[] report;
                lock (_gate)
                {
                    profile = _profile;
                    mapped = _mapper.Map(profile, devices);
                    // Soften rim catch-up after Simucube SetParameters held the DI lock
                    // (freeze → jump felt like a steering hitch).
                    mapped.Steering = SoftCatchUpSteer(mapped.Steering);
                    _latest = mapped;
                    _latestDevices = devices;
                    report = G920ReportBuilder.Build(mapped);
                    // Clone so WinUHid / ReadReport never share a buffer with the next frame.
                    report = (byte[])report.Clone();
                    _latestReport = report;
                    mappedCallback = _onMappedFrame;
                }

                // Publish for ReadReport callbacks before Submit (interrupt IN path).
                Volatile.Write(ref _callbackReport, report);

                var submitted = _virtualDevice?.SubmitReport(report) ?? true;
                if (!submitted)
                {
                    _submitFailStreak++;
                    // Recreating the device mid-game drops the game's DI handle (and its FFB
                    // effects), so only recover after ~1 s of hard failures at 500 Hz.
                    if (_submitFailStreak >= 500)
                    {
                        TryRecoverVirtualDevice();
                        _submitFailStreak = 0;
                    }
                    else if (_submitFailStreak % 25 == 1)
                    {
                        var err = _virtualDevice?.LastError;
                        lock (_gate)
                            _linkStatus = string.IsNullOrWhiteSpace(err)
                                ? $"Virtual submit failing ({_submitFailStreak})"
                                : $"Virtual submit failing ({_submitFailStreak}): {err}";
                    }
                }
                else if (_submitFailStreak > 0)
                {
                    _submitFailStreak = 0;
                    lock (_gate) _linkStatus = "";
                }

                // Arcade auto-center (DI Spring) must use the physical rim angle.
                // Prefer the already-polled FFB device state, then exclusive FFB handle,
                // and only fall back to virtual/DualSense steer last.
                float ffbSteer;
                if (!TrySteerFromDevice(devices, profile.FfbSourceDeviceId, out ffbSteer) &&
                    !_ffb.TryGetPhysicalSteering(out ffbSteer))
                {
                    ffbSteer = mapped.Steering;
                }
                lock (_gate) _ffbRimSteer = ffbSteer;
                OemFfbSharedMemory.WriteSteering(ffbSteer);

                // Optional emulator centering spring for games that send none. Added after
                // smoothing so it never lags; pre-flipped so Invert FFB can't make it push away.
                var centerTorque = ComputeForcedCenter(profile.FfbOutputFeel, ffbSteer, now);
                var wroteTorque = false;

                // Prefer OEM EffectDriver shared memory (exact DI effect mix from the game).
                // Fall back to HID++ emulator sampling when OEM driver isn't publishing.
                // Physical DI apply runs on a side thread - never stall HID submits here.
                var usedOem = false;
                var oemStale = false;
                if (OemFfbSharedMemory.TryRead(out var oemSnap, out var oemErr) && oemSnap.IsStale())
                {
                    // The game's driver thread stopped publishing (exit/crash): never keep
                    // applying its last torque.
                    oemStale = true;
                    lock (_gate)
                    {
                        _oemFfbTorque = 0;
                        _oemFfbPlaying = false;
                        _oemFfbTypesPlaying = OemFfbSharedMemory.FormatTypeMask(0);
                        _oemTypesPlayingMask = 0;
                        _oemTypesSeenMask = oemSnap.TypesSeen | oemSnap.AuxTypesPlaying;
                        _oemLastEffectType = oemSnap.LastEffectType;
                        _oemTypeTorqueDi = oemSnap.CombinedTypeTorqueDi();
                        _oemFfbEffectsDetail = OemFfbSharedMemory.FormatOemEffects(oemSnap, playing: false);
                        _oemFfbStatus = $"OEM FFB: stale (no game publishing) seq={oemSnap.Sequence}";
                    }
                }
                else if (oemErr is null)
                {
                    // Keep gains / mix options fresh once the DLL has created the map.
                    OemFfbSharedMemory.WriteTypeGains(profile.FfbEffectGains);
                    OemFfbSharedMemory.WriteMixOptions(profile.FfbOutputFeel);

                    // Game Torque + optional Aux rumble (Steam/etc.) - every OEM effect on
                    // the virtual G920 reaches the selected base regardless of host PC.
                    var combined = oemSnap.CombinedTorque;
                    var combinedPlaying = oemSnap.Playing || (oemSnap.AuxPlaying && !oemSnap.IsAuxStale());
                    var combinedTypes = oemSnap.CombinedTypesPlaying;
                    lock (_gate)
                    {
                        _oemFfbTorque = combined;
                        _oemFfbPlaying = combinedPlaying;
                        _lastOemFfbSequence = oemSnap.Sequence;
                        _oemFfbDownloadCount = oemSnap.DownloadCount;
                        _oemFfbTypesSeen = OemFfbSharedMemory.FormatTypeMask(oemSnap.TypesSeen | oemSnap.AuxTypesPlaying);
                        _oemFfbTypesPlaying = OemFfbSharedMemory.FormatTypeMask(combinedTypes);
                        _oemTypesSeenMask = oemSnap.TypesSeen | oemSnap.AuxTypesPlaying;
                        _oemTypesPlayingMask = combinedTypes;
                        _oemLastEffectType = oemSnap.LastEffectType;
                        _oemTypeTorqueDi = oemSnap.CombinedTypeTorqueDi();
                        _oemFfbEffectsDetail = OemFfbSharedMemory.FormatOemEffects(oemSnap, combinedPlaying);
                        _oemFfbStatus = combinedPlaying
                            ? $"OEM FFB: playing seq={oemSnap.Sequence} dl={oemSnap.DownloadCount} rim={ffbSteer:+0.00;-0.00;0.00}"
                            : $"OEM FFB: idle seq={oemSnap.Sequence} dl={oemSnap.DownloadCount} rim={ffbSteer:+0.00;-0.00;0.00}";
                    }

                    if (_ffb.IsReady)
                    {
                        ApplyOutputFeel(profile.FfbOutputFeel);
                        var smoothed = _ffbSmoother.Process(
                            combined,
                            oemSnap.DownloadCount,
                            oemSnap.TypesSeen | oemSnap.AuxTypesPlaying,
                            combinedPlaying);
                        _ffb.NoteIncoming(combined);
                        QueuePhysicalFfbTorque(smoothed + centerTorque);
                        wroteTorque = true;
                        usedOem = true;
                    }
                }
                else
                {
                    lock (_gate)
                    {
                        _oemFfbStatus = $"OEM FFB: {oemErr ?? "not connected"}";
                        _oemTypesPlayingMask = 0;
                        _oemFfbEffectsDetail = OemFfbSharedMemory.FormatOemEffects(0, 0, [], 0);
                    }
                }

                if (!usedOem && _virtualDevice is not null && _ffb.IsReady)
                {
                    var torque = _virtualDevice.SampleFfbTorque(ffbSteer);
                    ApplyOutputFeel(profile.FfbOutputFeel);
                    var smoothed = _ffbSmoother.Process(torque, 0, 0, Math.Abs(torque) > 0.01f);
                    _ffb.NoteIncoming(torque);
                    QueuePhysicalFfbTorque(smoothed + centerTorque);
                    wroteTorque = true;
                }
                else if (oemStale && _ffb.IsReady)
                {
                    _ffb.NoteIncoming(0f);
                    QueuePhysicalFfbTorque(centerTorque);
                    wroteTorque = true;
                }

                // No game source this tick: still drive the forced spring, and release it
                // to zero once when the user turns it off.
                if (!wroteTorque && _ffb.IsReady && (centerTorque != 0f || _forcedCenterActive))
                    QueuePhysicalFfbTorque(centerTorque);
                _forcedCenterActive = centerTorque != 0f;

                try { mappedCallback?.Invoke(mapped); }
                catch { /* add-on errors must not stop the bridge */ }

                // Telemetry synth/UDP/process probe run on a side thread - never stall HID here.
                QueueTelemetrySample(profile, devices, mapped, ffbSteer);
            }
            catch
            {
                // Keep the loop alive across transient device errors.
            }

            // Pace to ~500 Hz without adding Sleep on top of slow work (that felt like
            // micro-stutter on axis movement when Simucube SetParameters ran inline).
            var elapsedMs = sw.Elapsed.TotalMilliseconds;
            var remain = InputTargetPeriodMs - elapsedMs;
            if (remain >= 1.0)
                Thread.Sleep((int)remain);
            else if (remain > 0.05)
                Thread.SpinWait(50);
        }
    }

    /// <summary>
    /// Applies queued torque to the physical base. Kept off the input thread so a slow
    /// DI effect update cannot delay virtual G920 reports.
    /// </summary>
    private void RunFfbApplyLoop(CancellationToken token)
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; }
        catch { /* best-effort */ }

        var lastVersion = 0;
        var sw = Stopwatch.StartNew();
        while (!token.IsCancellationRequested)
        {
            sw.Restart();
            var version = Volatile.Read(ref _ffbQueuedVersion);
            if (version != lastVersion)
            {
                var torque = Volatile.Read(ref _ffbQueuedTorque);
                lastVersion = version;
                try { _ffb.UpdateTorque(torque); }
                catch { /* keep applying */ }
            }

            var remain = InputTargetPeriodMs - sw.Elapsed.TotalMilliseconds;
            if (remain >= 1.0)
                Thread.Sleep((int)remain);
            else if (remain > 0.05)
                Thread.SpinWait(50);
            else if (version == lastVersion)
                Thread.Sleep(1); // idle: don't burn a core when torque is unchanged
        }
    }

    private float ComputeForcedCenter(FfbOutputFeel? feel, float rim, long nowTick)
    {
        var dt = (nowTick - _lastRimTick) / 1000f;
        if (_lastRimTick == 0 || dt <= 0f || dt > 0.25f)
        {
            _rimVelocity = 0f;
        }
        else
        {
            var instant = (rim - _lastRim) / dt;
            _rimVelocity += (instant - _rimVelocity) * 0.2f;
        }
        _lastRim = rim;
        _lastRimTick = nowTick;

        if (feel is null || !feel.ForceCenterSpring)
            return 0f;
        var torque = feel.ComputeCenterSpring(rim, _rimVelocity);
        return _ffb.Invert ? -torque : torque;
    }

    /// <summary>
    /// Device IDs to USB-poll, or <c>null</c> to poll every attached game control.
    /// </summary>
    private HashSet<string>? GetPollDeviceIds(MappingProfile profile)
    {
        if (ReferenceEquals(_pollDeviceIdsForProfile, profile) && (_pollAllDevices || _pollDeviceIds is not null))
            return _pollAllDevices ? null : _pollDeviceIds;

        profile.CustomBindings ??= [];
        // Custom Toggle/FN must never be filtered out of the USB poll. Polling all
        // attached game controls when customs exist keeps joy.cpl / games in sync with
        // the in-app live Buttons line.
        if (profile.CustomBindings.Count > 0)
        {
            _pollAllDevices = true;
            _pollDeviceIds = null;
            _pollDeviceIdsForProfile = profile;
            return null;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(profile.FfbSourceDeviceId))
            ids.Add(profile.FfbSourceDeviceId);
        foreach (var binding in profile.Bindings)
        {
            foreach (var src in binding.Sources)
            {
                if (!string.IsNullOrWhiteSpace(src.DeviceId))
                    ids.Add(src.DeviceId);
            }
        }

        _pollAllDevices = false;
        _pollDeviceIds = ids;
        _pollDeviceIdsForProfile = profile;
        return ids;
    }

    /// <summary>
    /// Limit per-frame steering jumps after a Simucube DI apply stalls rim sampling.
    /// At 500 Hz, 0.08/frame still allows very fast turns but softens USB catch-up spikes.
    /// </summary>
    private float SoftCatchUpSteer(float target)
    {
        target = Math.Clamp(target, -1f, 1f);
        if (!_emittedSteerValid)
        {
            _emittedSteer = target;
            _emittedSteerValid = true;
            return target;
        }

        var delta = target - _emittedSteer;
        const float maxStep = 0.08f;
        if (Math.Abs(delta) <= maxStep)
        {
            _emittedSteer = target;
            return target;
        }

        _emittedSteer += Math.Sign(delta) * maxStep;
        return _emittedSteer;
    }

    /// <summary>
    /// Merge live FFB-base axes/buttons/hat into the poll snapshot. InputHub skips Poll on
    /// the pinned exclusive FFB joystick (avoids blocking the loop); without this overlay,
    /// Fanatec (and similar) wheel buttons stay frozen while steering still works.
    /// Failures keep the last good sample so a slow rim read cannot drop pedals/buttons
    /// that come from other devices.
    /// </summary>
    private IReadOnlyDictionary<string, DeviceState> OverlayPinnedFfbAxes(
        IReadOnlyDictionary<string, DeviceState> devices,
        string? ffbDeviceId)
    {
        if (string.IsNullOrWhiteSpace(ffbDeviceId))
            return devices;

        if (_ffb.IsReady && _ffb.TryGetPhysicalInput(out var liveAxes, out var liveButtons, out var liveHat))
        {
            _lastFfbAxes01 = liveAxes;
            if (liveButtons.Length > 0)
                _lastFfbButtons = liveButtons;
            _lastFfbHat = liveHat;
        }

        var axes = _lastFfbAxes01;
        if (axes is null || axes.Count == 0)
            return devices;

        var copy = new Dictionary<string, DeviceState>(devices, StringComparer.OrdinalIgnoreCase);
        if (copy.TryGetValue(ffbDeviceId, out var existing))
        {
            var merged = new Dictionary<string, float>(existing.Axes, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in axes)
                merged[k] = v;
            var buttons = MergePinnedButtons(existing.Buttons, _lastFfbButtons);
            copy[ffbDeviceId] = new DeviceState
            {
                DeviceId = existing.DeviceId,
                Axes = merged,
                Buttons = buttons,
                Hat = _lastFfbButtons is not null || _lastFfbHat >= 0 ? _lastFfbHat : existing.Hat,
            };
        }
        else
        {
            copy[ffbDeviceId] = new DeviceState
            {
                DeviceId = ffbDeviceId,
                Axes = new Dictionary<string, float>(axes, StringComparer.OrdinalIgnoreCase),
                Buttons = _lastFfbButtons is { Length: > 0 }
                    ? (bool[])_lastFfbButtons.Clone()
                    : [],
                Hat = _lastFfbHat,
            };
        }

        return copy;
    }

    private static bool[] MergePinnedButtons(bool[] existing, bool[]? live)
    {
        if (live is null || live.Length == 0)
            return existing;
        if (existing.Length == 0)
            return (bool[])live.Clone();

        // Prefer live length when larger (Fanatec wheel packs many DI buttons).
        var n = Math.Max(existing.Length, live.Length);
        var merged = new bool[n];
        for (var i = 0; i < n; i++)
            merged[i] = i < live.Length ? live[i] : existing[i];
        return merged;
    }

    private static bool TrySteerFromDevice(
        IReadOnlyDictionary<string, DeviceState> devices,
        string? deviceId,
        out float steeringCentered)
    {
        steeringCentered = 0f;
        if (string.IsNullOrWhiteSpace(deviceId) ||
            !devices.TryGetValue(deviceId, out var state))
            return false;

        // Prefer X (wheel). Some bases expose rotation on Rx.
        foreach (var key in new[] { "X", "Rx", "Y" })
        {
            if (!state.Axes.TryGetValue(key, out var axis01))
                continue;
            steeringCentered = Math.Clamp(axis01 * 2f - 1f, -1f, 1f);
            return true;
        }

        return false;
    }

    private void ApplyEffectGains(MappingProfile profile)
    {
        OemFfbSharedMemory.WriteTypeGains(profile.FfbEffectGains);
        ApplyEffectGainsUnlocked(profile);
    }

    private void ApplyOutputFeel(FfbOutputFeel? feel)
    {
        _ffbSmoother.ApplyFeel(feel);
        _ffb.MagnitudeEpsilon = _ffbSmoother.MagnitudeEpsilon;
        OemFfbSharedMemory.WriteMixOptions(feel);
    }

    private void ApplyEffectGainsUnlocked(MappingProfile profile)
    {
        var g = profile.FfbEffectGains ?? FfbEffectGains.CreateDefault();
        g.Clamp();
        ApplyOutputFeel(profile.FfbOutputFeel);
        IVirtualG920Device? device;
        lock (_gate) device = _virtualDevice;
        device?.SetFfbEffectGains(
            (float)g.ConstantForce,
            (float)g.SpringForce,
            (float)g.DamperForce,
            (float)g.FrictionForce,
            (float)g.InertiaForce,
            (float)g.Periodic);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _virtualDevice?.Dispose();
        _ffb.Dispose();
        _inputHub.Dispose();
        _telemetryUdp.Dispose();
    }

    /// <summary>
    /// Input-thread enqueue only. When telemetry is off this is a single flag check.
    /// </summary>
    private void QueueTelemetrySample(
        MappingProfile profile,
        IReadOnlyDictionary<string, DeviceState> devices,
        MappedG920State mapped,
        float steering)
    {
        if (Volatile.Read(ref _telemetryEnabledFlag) == 0)
            return;

        var handbrakeHeld = MapperEngine.IsPressed(profile, devices, G920Control.TelemetryHandbrake);
        var nosHeld = MapperEngine.IsPressed(profile, devices, G920Control.TelemetryNos);
        Volatile.Write(ref _telemetryQueuedMapped, mapped);
        Volatile.Write(ref _telemetryQueuedSteer, steering);
        Volatile.Write(ref _telemetryQueuedHandbrake, handbrakeHeld ? 1 : 0);
        Volatile.Write(ref _telemetryQueuedNos, nosHeld ? 1 : 0);
        Interlocked.Increment(ref _telemetryQueuedVersion);
    }

    /// <summary>
    /// Synth + UDP + game-process probe. Kept off the input thread so SimHub I/O and
    /// Process.GetProcessesByName cannot delay virtual G920 submits (Simucube hitch).
    /// </summary>
    private void RunTelemetryLoop(CancellationToken token)
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.Normal; }
        catch { /* best-effort */ }

        var lastVersion = 0;
        var sw = Stopwatch.StartNew();
        while (!token.IsCancellationRequested)
        {
            sw.Restart();

            TelemetrySettings settings;
            lock (_gate) settings = _telemetrySettings;

            if (!settings.Enabled || Volatile.Read(ref _telemetryEnabledFlag) == 0)
            {
                lock (_gate) _telemetryStatus = "Telemetry: off";
                Thread.Sleep(50);
                continue;
            }

            var version = Volatile.Read(ref _telemetryQueuedVersion);
            var periodMs = 1000.0 / Math.Max(1, settings.SendHz);

            if (version == lastVersion)
            {
                var idleRemain = periodMs - sw.Elapsed.TotalMilliseconds;
                if (idleRemain >= 1.0)
                    Thread.Sleep((int)Math.Min(idleRemain, 20));
                else
                    Thread.Sleep(1);
                continue;
            }

            var mapped = Volatile.Read(ref _telemetryQueuedMapped);
            var steering = Volatile.Read(ref _telemetryQueuedSteer);
            var handbrakeHeld = Volatile.Read(ref _telemetryQueuedHandbrake) != 0;
            var nosHeld = Volatile.Read(ref _telemetryQueuedNos) != 0;

            if (mapped is null)
            {
                lastVersion = version;
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            var intervalTicks = Stopwatch.Frequency / Math.Max(1, settings.SendHz);
            if (_lastTelemetrySendTick != 0 && now - _lastTelemetrySendTick < intervalTicks)
            {
                // Keep lastVersion so the latest queued sample is still pending.
                var waitTicks = _lastTelemetrySendTick + intervalTicks - now;
                var waitMs = waitTicks * 1000.0 / Stopwatch.Frequency;
                if (waitMs >= 1.0)
                    Thread.Sleep((int)Math.Clamp(waitMs, 1, periodMs));
                continue;
            }

            double dtSec = periodMs / 1000.0;
            if (_lastTelemetrySendTick != 0)
                dtSec = (now - _lastTelemetrySendTick) / (double)Stopwatch.Frequency;
            _lastTelemetrySendTick = now;
            lastVersion = version;

            try
            {
                OemFfbSharedMemory.Snapshot? oem = null;
                if (OemFfbSharedMemory.TryRead(out var snap, out _) && !snap.IsStale())
                    oem = snap;

                var knownGame = GameProcessProbe.IsKnownGameRunning();
                var frame = _telemetrySynth.Update(
                    mapped, steering, oem, knownGame, dtSec, handbrakeHeld, nosHeld);

                _telemetrySessionTime += dtSec;
                _telemetryPackets++;
                EngineVibrationScaleBridge.Publish(settings.Tuning.EngineVibrationScale);
                SimHubPacket.Write(
                    _telemetryPacket, frame, _telemetryEmitterId, _telemetrySessionId,
                    _telemetryPackets, _telemetrySessionTime);
                var sent = _telemetryUdp.TrySend(_telemetryPacket);
                lock (_gate)
                {
                    _latestTelemetry = frame;
                    var err = _telemetryUdp.LastError;
                    _telemetryStatus = !sent && !string.IsNullOrWhiteSpace(err)
                        ? $"Telemetry: {err}"
                        : $"Telemetry: {_telemetryUdp.PacketsPerSecond:0} pkt/s → {settings.Host}:{settings.Port}";
                }
            }
            catch
            {
                // Keep the telemetry loop alive across transient socket/process errors.
            }

            var remain = periodMs - sw.Elapsed.TotalMilliseconds;
            if (remain >= 1.0)
                Thread.Sleep((int)remain);
            else if (remain > 0.05)
                Thread.SpinWait(20);
        }
    }

    private static ulong SimHubRegistrationId()
    {
        var g = Guid.NewGuid().ToByteArray();
        return BitConverter.ToUInt64(g, 0);
    }
}
