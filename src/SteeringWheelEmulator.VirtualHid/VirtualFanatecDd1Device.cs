using System.Runtime.InteropServices;
using System.Text;
using SteeringWheelEmulator.Core.Bridge;
using SteeringWheelEmulator.Core.Mapping;
using SteeringWheelEmulator.Core.Models;

namespace SteeringWheelEmulator.VirtualHid;

/// <summary>
/// Virtual Fanatec DD1 (PC or PC Comp) via WinUHid. No G HUB filter path — Fanatec identities
/// do not use Logitech Col01 recovery. Physical Fanatec USB devices remain separate for bind/FFB.
/// </summary>
public sealed class VirtualFanatecDd1Device : IVirtualG920Device
{
    private readonly EmulatedDeviceKind _kind;
    private readonly IEmulatedDeviceProfile _profile;
    private readonly object _gate = new();
    private IntPtr _device = IntPtr.Zero;
    private GCHandle _descriptorHandle;
    private GCHandle _callbackHandle;
    private GCHandle _hardwareIdsHandle;
    private GCHandle _instanceIdHandle;
    private WinUHidNative.EventCallback? _callback;
    private Func<byte[]>? _reportProvider;
    private Action<FfbCommand>? _onFfb;
    private byte[] _lastReport;
    private bool _previewMode;
    private bool _running;
    private bool _disposed;
    private Task? _nativeTeardown;
    private int _hostWriteCount;
    private byte _lastHostReportId;
    private string _lastHostWriteHex = "";
    private string _hostPathHint = "";
    private long _hostInputReadTick;
    private long _submitNotReadyCount;

    /// <summary>Last host output report (LED / telemetry) — Phase 4 follow-on.</summary>
    public byte[]? LastLedOutputReport { get; private set; }

    public int LedOutputWriteCount { get; private set; }

    public VirtualFanatecDd1Device(EmulatedDeviceKind kind)
    {
        if (kind != EmulatedDeviceKind.FanatecDd1PcComp)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Expected Fanatec DD1 PC Comp.");
        _kind = kind;
        _profile = EmulatedDeviceProfiles.Get(kind);
        _lastReport = _profile.BuildIdleReport();
        IsDriverAvailable = WinUHidNative.TryProbeDriver(out var msg);
        if (!IsDriverAvailable)
            LastError = msg;
    }

    public bool IsRunning => _running;
    public bool IsDriverAvailable { get; private set; }
    public bool IsPreviewMode => _previewMode;
    public string? LastError { get; private set; }

    public long HostInputReadAgeMs
    {
        get
        {
            var tick = Interlocked.Read(ref _hostInputReadTick);
            if (tick == 0) return -1;
            return Math.Max(0, Environment.TickCount64 - tick);
        }
    }

    public long SubmitNotReadyCount => Interlocked.Read(ref _submitNotReadyCount);

    public float SampleFfbTorque(float steeringCentered) => 0f;

    public void SetFfbEffectGains(float constant, float spring, float damper, float friction, float inertia, float periodic)
    {
        // OEM SHM path preferred; no HID++ emulator on Fanatec identity.
    }

    public VirtualFfbIngressStats GetFfbIngressStats() =>
        new(0, 0, 0, 0f, LedOutputWriteCount > 0 ? "FanatecOutput" : "none", 0, 0,
            _hostWriteCount, _lastHostReportId, _lastHostWriteHex, _hostPathHint);

    public bool Start(Func<byte[]> latestReportProvider, Action<FfbCommand>? onFfb = null)
    {
        Stop();
        try { _nativeTeardown?.Wait(8000); } catch { /* ignore */ }
        _nativeTeardown = null;
        _reportProvider = latestReportProvider;
        _onFfb = onFfb;
        Interlocked.Exchange(ref _hostInputReadTick, 0);
        Interlocked.Exchange(ref _submitNotReadyCount, 0);
        _hostWriteCount = 0;
        _lastHostReportId = 0;
        _lastHostWriteHex = "";
        _hostPathHint = "";
        LedOutputWriteCount = 0;
        LastLedOutputReport = null;

        if (!WinUHidNative.TryProbeDriver(out var probeMsg))
        {
            IsDriverAvailable = false;
            _previewMode = true;
            _running = true;
            LastError = probeMsg + $" Running in preview mode (no virtual {_profile.DisplayName} exposed).";
            return true;
        }

        IsDriverAvailable = true;
        _previewMode = false;

        try
        {
            _ = GHubConflictRepair.RemoveDisconnectedVirtualNodes();

            var descriptor = FanatecDd1HidDescriptor.Bytes;
            _descriptorHandle = GCHandle.Alloc(descriptor, GCHandleType.Pinned);

            // REV_E001 keeps Hardware IDs distinct from a physical base on the same PID.
            var rev = FanatecDd1HidDescriptor.VersionNumber;
            var hwId =
                $"HID\\VID_{_profile.VendorId:X4}&PID_{_profile.ProductId:X4}&REV_{rev:X4}\0\0";
            var hwIds = Encoding.Unicode.GetBytes(hwId);
            _hardwareIdsHandle = GCHandle.Alloc(hwIds, GCHandleType.Pinned);

            // Stable short InstanceID (PID-based) — survives enum renames (Xbox → PC Comp).
            var instanceId = Encoding.Unicode.GetBytes(
                $"G920Emulator.Fanatec{_profile.ProductId:X4}\0");
            _instanceIdHandle = GCHandle.Alloc(instanceId, GCHandleType.Pinned);

            var config = new WinUHidNative.DeviceConfig
            {
                SupportedEvents =
                    WinUHidNative.EventType.GetFeature |
                    WinUHidNative.EventType.SetFeature |
                    WinUHidNative.EventType.WriteReport,
                VendorID = _profile.VendorId,
                ProductID = _profile.ProductId,
                VersionNumber = _profile.VersionNumber,
                ReportDescriptorLength = (ushort)descriptor.Length,
                ReportDescriptor = _descriptorHandle.AddrOfPinnedObject(),
                ContainerId = _profile.ContainerId,
                InstanceID = _instanceIdHandle.AddrOfPinnedObject(),
                HardwareIDs = _hardwareIdsHandle.AddrOfPinnedObject(),
                ReadReportPeriodUs = 0,
            };

            _device = WinUHidNative.WinUHidCreateDevice(ref config);
            if (_device == IntPtr.Zero)
            {
                var firstErr = Marshal.GetLastWin32Error();
                _ = GHubConflictRepair.RemoveDisconnectedVirtualNodes();
                Thread.Sleep(500);
                _device = WinUHidNative.WinUHidCreateDevice(ref config);
                if (_device == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    var interpretHint = err == 552 || firstErr == 552
                        ? " HID report descriptor was rejected by VHF (ERROR_COULD_NOT_INTERPRET)."
                        : "";
                    LastError =
                        $"WinUHidCreateDevice failed for {_profile.DisplayName} " +
                        $"(err={err}, first={firstErr}).{interpretHint} " +
                        "Stop any game still holding the old virtual wheel, then Start again. " +
                        "If it keeps failing, Device Manager → remove disconnected " +
                        $"HID\\VID_{_profile.VendorId:X4}&PID_{_profile.ProductId:X4} under WinUHid.";
                    FreePins();
                    return false;
                }
            }

            _callback = OnEvent;
            _callbackHandle = GCHandle.Alloc(_callback);
            if (!WinUHidNative.WinUHidStartDevice(_device, _callback, IntPtr.Zero))
            {
                LastError = $"WinUHidStartDevice failed ({Marshal.GetLastWin32Error()}).";
                DestroyNative();
                return false;
            }

            var initial = latestReportProvider();
            WinUHidNative.WinUHidSubmitInputReport(_device, initial, (uint)initial.Length);
            _lastReport = initial;

            FanatecOemRegistration.EnsureRegistered(_kind);
            // Same idea as G920DeviceIdentityFix: without JOY HardwareID + real product name,
            // Windows/Forza often never list the WinUHid Fanatec as a controller. Also strip
            // Fanatec driver filters that bind to VID_0EB7 (FanatecService on Simucube PCs).
            FanatecDeviceIdentityFix.Apply();

            var present = false;
            for (var i = 0; i < 20; i++)
            {
                if (FanatecDeviceIdentityFix.IsVirtualPresent())
                {
                    present = true;
                    break;
                }
                Thread.Sleep(100);
            }

            // Re-apply after PnP children settle (filters can reappear briefly).
            _ = Task.Run(() =>
            {
                Thread.Sleep(900);
                try { FanatecDeviceIdentityFix.Apply(); } catch { /* best-effort */ }
                try { _ = WinUHidPowerPolicy.EnsurePowerManagementOff(); } catch { /* best-effort */ }
            });

            try { _ = WinUHidPowerPolicy.EnsurePowerManagementOff(); }
            catch { /* best-effort */ }

            _hostPathHint = present
                ? $"Virtual {_profile.DisplayName} · HID present · interrupt-push input"
                : $"WARNING: virtual {_profile.DisplayName} HID not present — games will not see a wheel";
            _running = true;
            LastError = present
                ? null
                : $"WinUHid started but virtual Fanatec HID is not Present. " +
                  "Forza will not see a wheel. Stop bridge, close Fanatec Control Panel if open, then Start again.";
            return true;
        }
        catch (DllNotFoundException)
        {
            LastError = "WinUHid.dll not found. See docs/driver-install.md.";
            DestroyNative();
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DestroyNative();
            return false;
        }
    }

    public void Stop()
    {
        if (!_running && _device == IntPtr.Zero)
            return;
        _running = false;
        _previewMode = false;
        DestroyNative();
    }

    public bool SubmitReport(byte[] report)
    {
        if (!_running || _previewMode || _device == IntPtr.Zero)
            return _running;

        lock (_gate)
        {
            _lastReport = report;
            if (WinUHidNative.WinUHidSubmitInputReport(_device, report, (uint)report.Length))
            {
                Interlocked.Exchange(ref _hostInputReadTick, Environment.TickCount64);
                return true;
            }

            // Match VirtualG920Device: ERROR_NOT_READY is interrupt-push busy, not submit death.
            var err = Marshal.GetLastWin32Error();
            if (err == 21)
            {
                Interlocked.Increment(ref _submitNotReadyCount);
                return true;
            }

            LastError = $"WinUHidSubmitInputReport failed ({err}).";
            return false;
        }
    }

    public bool TryRecover(Func<byte[]> latestReportProvider, Action<FfbCommand>? onFfb = null) =>
        Start(latestReportProvider, onFfb);

    private void OnEvent(IntPtr ctx, IntPtr device, IntPtr evtPtr)
    {
        if (evtPtr == IntPtr.Zero)
            return;

        try
        {
            var type = (WinUHidNative.EventType)Marshal.ReadInt32(evtPtr, 0);
            var reportId = Marshal.ReadByte(evtPtr, 8);

            if (type is WinUHidNative.EventType.ReadReport or WinUHidNative.EventType.GetFeature)
            {
                var report = !_running
                    ? (_lastReport.Length > 0 ? _lastReport : _profile.BuildIdleReport())
                    : (_reportProvider?.Invoke() ?? _lastReport);
                if (_running)
                    Interlocked.Exchange(ref _hostInputReadTick, Environment.TickCount64);
                WinUHidNative.WinUHidCompleteReadEvent(device, evtPtr, report, (uint)report.Length);
                return;
            }

            if (type is WinUHidNative.EventType.WriteReport or WinUHidNative.EventType.SetFeature)
            {
                if (_running)
                {
                    var dataLength = Marshal.ReadInt32(evtPtr, 9);
                    if (dataLength > 0 && dataLength < 256)
                    {
                        var data = new byte[dataLength];
                        Marshal.Copy(IntPtr.Add(evtPtr, 13), data, 0, dataLength);
                        _hostWriteCount++;
                        _lastHostReportId = reportId;
                        _lastHostWriteHex =
                            $"{reportId:X2}: {Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 16)))}";

                        // Phase 4: capture LED / dash output reports from the game.
                        if (reportId == 0x02 || (data.Length > 0 && data[0] == 0x02))
                        {
                            LastLedOutputReport = data;
                            LedOutputWriteCount++;
                            _hostPathHint = $"LED/output writes={LedOutputWriteCount}";
                        }

                        // Host HID output may carry LED/FFB-style writes; OEM SHM is preferred
                        // when the game uses EffectDriver.
                        _ = _onFfb;
                    }
                }

                WinUHidNative.WinUHidCompleteWriteEvent(device, evtPtr, true);
            }
        }
        catch
        {
            // never throw from native callback
        }
    }

    private void DestroyNative()
    {
        var device = _device;
        _device = IntPtr.Zero;
        if (device != IntPtr.Zero)
        {
            var teardown = Task.Run(() =>
            {
                try { WinUHidNative.WinUHidStopDevice(device); } catch { /* ignore */ }
                try { WinUHidNative.WinUHidDestroyDevice(device); } catch { /* ignore */ }
            });
            _nativeTeardown = teardown;
            // Match VirtualG920Device: never block Stop/Exit forever on WinUHidStopDevice.
            try
            {
                if (!teardown.Wait(1500))
                    _ = teardown.ContinueWith(_ => { /* observe */ }, TaskScheduler.Default);
            }
            catch { /* ignore */ }
        }

        FreePins();
    }

    private void FreePins()
    {
        if (_callbackHandle.IsAllocated) _callbackHandle.Free();
        if (_descriptorHandle.IsAllocated) _descriptorHandle.Free();
        if (_hardwareIdsHandle.IsAllocated) _hardwareIdsHandle.Free();
        if (_instanceIdHandle.IsAllocated) _instanceIdHandle.Free();
        _callback = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
