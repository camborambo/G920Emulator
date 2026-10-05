using System.Diagnostics;
using G920Emulator.Core.Ffb;
using G920Emulator.Core.Input;
using G920Emulator.Core.Mapping;
using G920Emulator.Core.Models;

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
    private MappedG920State _latest = new();
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
    private uint _oemFfbDownloadCount;
    private float _ffbRimSteer;
    private float _lastRim;
    private long _lastRimTick;
    private float _rimVelocity;
    private bool _forcedCenterActive;
    private long _lastInputRefreshTick;
    private int _submitFailStreak;
    private int _recoverCount;
    private string _linkStatus = "";
    /// <summary>Last live axes from the exclusive FFB base (InputHub does not Poll it).</summary>
    private Dictionary<string, float>? _lastFfbAxes01;
    /// <summary>Queued physical CF target — applied on a side thread so Simucube DI
    /// SetParameters cannot stall virtual G920 axis submits.</summary>
    private float _ffbQueuedTorque;
    private int _ffbQueuedVersion;
    private const int InputTargetPeriodMs = 2;

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
            lock (_gate) _profile = value;
            _ffb.ApplyFromProfile(value);
            ApplyEffectGains(value);
        }
    }

    public bool IsRunning => _cts is not null;
    public MappedG920State LatestState { get { lock (_gate) return _latest; } }
    public string? VirtualDeviceError => _virtualDevice?.LastError;
    public bool IsDriverAvailable => _virtualDevice?.IsDriverAvailable ?? false;
    public string? LastFfbStatus { get; private set; }

    /// <summary>Input / virtual-device health while the bridge runs (empty when OK).</summary>
    public string LinkStatus { get { lock (_gate) return _linkStatus; } }

    public void AttachVirtualDevice(IVirtualG920Device device)
    {
        lock (_gate)
        {
            _virtualDevice?.Dispose();
            _virtualDevice = device;
        }
    }

    public IReadOnlyList<InputDeviceInfo> RefreshDevices() => _inputHub.RefreshDevices();

    public void BindFfbWindow(IntPtr hwnd) => _ffb.BindInputHub(_inputHub, hwnd);

    public FfbDiagnostics GetFfbDiagnostics()
    {
        var d = _ffb.GetDiagnostics();
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
        lock (_gate)
        {
            d.OemFfbTorque = _oemFfbTorque;
            d.OemFfbSequence = _lastOemFfbSequence;
            d.OemFfbPlaying = _oemFfbPlaying;
            d.OemFfbStatus = _oemFfbStatus;
            d.OemFfbTypesSeen = _oemFfbTypesSeen;
            d.OemFfbTypesPlaying = _oemFfbTypesPlaying;
            d.OemFfbDownloadCount = _oemFfbDownloadCount;
            d.FfbRimSteer = _ffbRimSteer;
            d.HardwareAutoCenter = _ffb.TestAutoCenterActive ? true : false;
        }
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
        // (that deadlock freezes reports — joy.cpl still lists the device, buttons die).
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

        _cts = new CancellationTokenSource();
        _loop = Task.Factory.StartNew(() => RunLoop(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ffbApplyLoop = Task.Factory.StartNew(() => RunFfbApplyLoop(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Stop()
    {
        _cts?.Cancel();
        var loop = _loop;
        var ffbLoop = _ffbApplyLoop;
        _loop = null;
        _ffbApplyLoop = null;

        // Drop test FFB loops first so they are not ApplyTorque'ing during detach.
        try { _ffb.ClearTestOverride(); } catch { /* ignore */ }

        // Brief wait only — never hang Stop on a stuck loop iteration.
        try { loop?.Wait(400); } catch { /* ignore */ }
        try { ffbLoop?.Wait(400); } catch { /* ignore */ }
        if (loop is { IsCompleted: false })
            _ = loop.ContinueWith(_ => { /* observe */ }, TaskScheduler.Default);
        if (ffbLoop is { IsCompleted: false })
            _ = ffbLoop.ContinueWith(_ => { /* observe */ }, TaskScheduler.Default);

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
        // — holding Bridge here deadlocks Stop and freezes the app.
        try { _ffb.Stop(); } catch { /* ignore */ }
        try { _ffb.Detach(); } catch { /* ignore */ }
        _inputHub.PinFfbDevice(null);
        _lastFfbAxes01 = null;

        OemFfbSharedMemory.Close();
        lock (_gate) _linkStatus = "";
    }

    /// <summary>Re-attach FFB output to the profile's selected physical device.</summary>
    public bool TryAttachFfb(out string status)
    {
        // Must not hold _gate across RefreshDevices / DI Exclusive acquire — the bridge
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
            LastFfbStatus = "FFB: no force-feedback device selected.";
            return;
        }

        // Baseline cache before exclusive attach, then pin so Poll never touches this joy.
        _inputHub.CapturePinnedBaseline(profile.FfbSourceDeviceId);
        _inputHub.PinFfbDevice(profile.FfbSourceDeviceId);

        if (_ffb.TryAttach(profile.FfbSourceDeviceId, out var error))
        {
            LastFfbStatus = "FFB: attached to physical device.";
            if (_ffb.TryGetPhysicalAxes01(out var axes))
                _lastFfbAxes01 = axes;
        }
        else
        {
            _inputHub.PinFfbDevice(null);
            _lastFfbAxes01 = null;
            LastFfbStatus = string.IsNullOrWhiteSpace(error) ? "FFB: attach failed." : $"FFB: {error}";
        }
    }

    private void OnFfb(FfbCommand cmd)
    {
        var torque = cmd.Stop ? 0f : cmd.Torque;
        _ffb.NoteIncoming(torque);
        // Queue — never block the WinUHid callback on physical DI SetParameters.
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
                // Re-enumerate + remap product GUIDs so a DualSense/wheel replug mid-race
                // does not leave the mapper pointed at a dead instance GUID.
                if (now - _lastInputRefreshTick >= 1500)
                {
                    _lastInputRefreshTick = now;
                    var attached = _inputHub.RefreshDevices();
                    lock (_gate)
                    {
                        var remapped = Profiles.DeviceBindingResolver.RemapProfile(_profile, attached);
                        if (remapped > 0)
                            _linkStatus = $"Remapped {remapped} binding(s) after device refresh";
                    }
                }

                // Non-FFB devices only — never blocks on the exclusive FFB joystick.
                var devices = _inputHub.Poll();
                devices = OverlayPinnedFfbAxes(devices, _profile.FfbSourceDeviceId);

                MappedG920State mapped;
                MappingProfile profile;
                Action<MappedG920State>? mappedCallback;
                byte[] report;
                lock (_gate)
                {
                    profile = _profile;
                    mapped = _mapper.Map(profile, devices);
                    _latest = mapped;
                    report = G920ReportBuilder.Build(mapped);
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
                // Physical DI apply runs on a side thread — never stall HID submits here.
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
                        _oemFfbStatus = $"OEM FFB: stale (no game publishing) seq={oemSnap.Sequence}";
                    }
                }
                else if (oemErr is null)
                {
                    // Game Torque + optional Aux rumble (Steam/etc.) — every OEM effect on
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
                    lock (_gate) _oemFfbStatus = $"OEM FFB: {oemErr ?? "not connected"}";
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
    /// Merge live FFB-base axes into the poll snapshot. Failures keep the last good axes
    /// so a slow/failed rim read cannot drop pedals/buttons (those come from other devices).
    /// </summary>
    private IReadOnlyDictionary<string, DeviceState> OverlayPinnedFfbAxes(
        IReadOnlyDictionary<string, DeviceState> devices,
        string? ffbDeviceId)
    {
        if (string.IsNullOrWhiteSpace(ffbDeviceId))
            return devices;

        if (_ffb.IsReady && _ffb.TryGetPhysicalAxes01(out var live))
            _lastFfbAxes01 = live;

        var axes = _lastFfbAxes01;
        if (axes is null || axes.Count == 0)
            return devices;

        var copy = new Dictionary<string, DeviceState>(devices, StringComparer.OrdinalIgnoreCase);
        if (copy.TryGetValue(ffbDeviceId, out var existing))
        {
            var merged = new Dictionary<string, float>(existing.Axes, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in axes)
                merged[k] = v;
            copy[ffbDeviceId] = new DeviceState
            {
                DeviceId = existing.DeviceId,
                Axes = merged,
                Buttons = existing.Buttons,
                Hat = existing.Hat,
            };
        }
        else
        {
            copy[ffbDeviceId] = new DeviceState
            {
                DeviceId = ffbDeviceId,
                Axes = new Dictionary<string, float>(axes, StringComparer.OrdinalIgnoreCase),
                Buttons = [],
                Hat = -1,
            };
        }

        return copy;
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
    }
}
