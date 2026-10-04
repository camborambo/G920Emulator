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
    void SubmitReport(byte[] report);

    /// <summary>Sample HID++ FFB torque for the current steering position (-1..1).</summary>
    float SampleFfbTorque(float steeringCentered);

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
    private readonly object _gate = new();

    private IVirtualG920Device? _virtualDevice;
    private MappingProfile _profile = MappingProfile.CreateDefault();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private MappedG920State _latest = new();
    private byte[] _latestReport = G920ReportBuilder.Build(new MappedG920State());
    private bool _disposed;
    private Action<MappedG920State>? _onMappedFrame;
    private uint _lastOemFfbSequence;
    private float _oemFfbTorque;
    private bool _oemFfbPlaying;
    private string _oemFfbStatus = "OEM FFB: waiting for g920ffb.dll";
    private string _oemFfbTypesSeen = "";
    private string _oemFfbTypesPlaying = "";
    private uint _oemFfbDownloadCount;

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
        set { lock (_gate) _profile = value; }
    }

    public bool IsRunning => _cts is not null;
    public MappedG920State LatestState { get { lock (_gate) return _latest; } }
    public string? VirtualDeviceError => _virtualDevice?.LastError;
    public bool IsDriverAvailable => _virtualDevice?.IsDriverAvailable ?? false;
    public string? LastFfbStatus { get; private set; }

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
            _ffb.BindInputHub(_inputHub, IntPtr.Zero);
            AttachFfbFromProfileUnlocked();
            device = _virtualDevice ?? throw new InvalidOperationException("Virtual device not attached.");
        }

        // Never hold _gate across WinUHid create/start/repair. Host ReadReport callbacks
        // take that same lock via the report provider — holding it here deadlocks Start.
        if (!device.Start(() =>
            {
                lock (_gate) return (byte[])_latestReport.Clone();
            }, OnFfb))
        {
            throw new InvalidOperationException(device.LastError ?? "Failed to start virtual G920.");
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Factory.StartNew(() => RunLoop(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Stop()
    {
        _cts?.Cancel();
        var loop = _loop;
        _loop = null;
        // Brief wait only — never hang the UI on a stuck loop iteration.
        try { loop?.Wait(300); } catch { /* ignore */ }
        if (loop is { IsCompleted: false })
            _ = loop.ContinueWith(_ => { /* observe */ }, TaskScheduler.Default);

        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;

        // Tear down WinUHid first, outside _gate. Callbacks use _reportProvider which locks
        // _gate — if we held the lock while WinUHidStopDevice waited on those callbacks,
        // Stop would deadlock and the app would freeze.
        IVirtualG920Device? virtualDevice;
        lock (_gate)
        {
            virtualDevice = _virtualDevice;
        }

        try { virtualDevice?.Stop(); } catch { /* ignore */ }

        lock (_gate)
        {
            _ffb.Stop();
            _ffb.Detach();
        }

        OemFfbSharedMemory.Close();
    }

    /// <summary>Re-attach FFB output to the profile's selected physical device.</summary>
    public bool TryAttachFfb(out string status)
    {
        lock (_gate)
        {
            AttachFfbFromProfileUnlocked();
            status = LastFfbStatus ?? "";
            return _ffb.IsReady;
        }
    }

    private void AttachFfbFromProfileUnlocked()
    {
        _ffb.ApplyFromProfile(_profile);

        // Auto-pick first FFB-capable device when the profile has none selected.
        if (string.IsNullOrWhiteSpace(_profile.FfbSourceDeviceId))
        {
            var ffbDevice = _inputHub.RefreshDevices().FirstOrDefault(d => d.SupportsForceFeedback);
            if (ffbDevice is not null)
                _profile.FfbSourceDeviceId = ffbDevice.Id;
        }

        if (string.IsNullOrWhiteSpace(_profile.FfbSourceDeviceId))
        {
            LastFfbStatus = "FFB: no force-feedback device selected.";
            return;
        }

        if (_ffb.TryAttach(_profile.FfbSourceDeviceId, out var error))
            LastFfbStatus = "FFB: attached to physical device.";
        else
            LastFfbStatus = string.IsNullOrWhiteSpace(error) ? "FFB: attach failed." : $"FFB: {error}";
    }

    private void OnFfb(FfbCommand cmd)
    {
        var torque = cmd.Stop ? 0f : cmd.Torque;
        _ffb.NoteIncoming(torque);
        // UpdateTorque no-ops while the in-app FFB test override is active.
        _ffb.UpdateTorque(torque);
    }

    private void RunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var devices = _inputHub.Poll();
                MappedG920State mapped;
                MappingProfile profile;
                Action<MappedG920State>? mappedCallback;
                lock (_gate)
                {
                    profile = _profile;
                    mapped = _mapper.Map(profile, devices);
                    _latest = mapped;
                    _latestReport = G920ReportBuilder.Build(mapped);
                    mappedCallback = _onMappedFrame;
                }

                _virtualDevice?.SubmitReport(_latestReport);

                // Condition effects (auto-center spring) must use the physical rim angle.
                // Virtual G920 steer (DualSense) is the wrong feedback loop for the base.
                if (!_ffb.TryGetPhysicalSteering(out var ffbSteer))
                    ffbSteer = mapped.Steering;
                OemFfbSharedMemory.WriteSteering(ffbSteer);

                // Prefer OEM EffectDriver shared memory (exact DI effect mix from the game).
                // Fall back to HID++ emulator sampling when OEM driver isn't publishing.
                var usedOem = false;
                if (OemFfbSharedMemory.TryRead(out var oemSnap, out var oemErr))
                {
                    lock (_gate)
                    {
                        _oemFfbTorque = oemSnap.Torque;
                        _oemFfbPlaying = oemSnap.Playing;
                        _lastOemFfbSequence = oemSnap.Sequence;
                        _oemFfbDownloadCount = oemSnap.DownloadCount;
                        _oemFfbTypesSeen = OemFfbSharedMemory.FormatTypeMask(oemSnap.TypesSeen);
                        _oemFfbTypesPlaying = OemFfbSharedMemory.FormatTypeMask(oemSnap.TypesPlaying);
                        _oemFfbStatus = oemSnap.Playing
                            ? $"OEM FFB: playing seq={oemSnap.Sequence} dl={oemSnap.DownloadCount}"
                            : $"OEM FFB: idle seq={oemSnap.Sequence} dl={oemSnap.DownloadCount}";
                    }

                    if (_ffb.IsReady)
                    {
                        _ffb.NoteIncoming(oemSnap.Torque);
                        _ffb.UpdateTorque(oemSnap.Torque);
                        usedOem = true;
                    }
                }
                else
                {
                    lock (_gate) _oemFfbStatus = $"OEM FFB: {oemErr ?? "not connected"}";
                }

                if (!usedOem && _virtualDevice is not null && _ffb.IsReady)
                {
                    var torque = _virtualDevice.SampleFfbTorque(mapped.Steering);
                    _ffb.NoteIncoming(torque);
                    _ffb.UpdateTorque(torque);
                }

                try { mappedCallback?.Invoke(mapped); }
                catch { /* add-on errors must not stop the bridge */ }
            }
            catch
            {
                // Keep the loop alive across transient device errors.
            }

            Thread.Sleep(2); // ~500 Hz
        }
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
