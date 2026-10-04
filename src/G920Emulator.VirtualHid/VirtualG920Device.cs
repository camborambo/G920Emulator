using System.Runtime.InteropServices;
using System.Text;
using G920Emulator.Core.Bridge;
using G920Emulator.Core.Mapping;

namespace G920Emulator.VirtualHid;

/// <summary>
/// Virtual Logitech G920 via WinUHid. Falls back to preview mode (no OS device) when the driver is missing.
/// </summary>
public sealed class VirtualG920Device : IVirtualG920Device
{
    // Include REV so COL01 is HID\VID_046D&PID_C262&REV_9601&Col01 — that does NOT match
    // G HUB logi_joy_hid.inf (exact HID\…&Col01). The Logitech filter as function driver
    // swallows HID++ FFB and never forwards WriteReport to VHF (writes stay 0).
    // DirectInput still keys OEMForceFeedback off VID/PID from the HID descriptor.
    private static readonly string HardwareIdsMultiSz =
        $"HID\\VID_046D&PID_C262&REV_{G920HidDescriptor.VersionNumber:X4}\0\0";

    private readonly object _gate = new();
    private IntPtr _device = IntPtr.Zero;
    private GCHandle _descriptorHandle;
    private GCHandle _callbackHandle;
    private GCHandle _hardwareIdsHandle;
    private GCHandle _instanceIdHandle;
    private WinUHidNative.EventCallback? _callback;
    private Func<byte[]>? _reportProvider;
    private Action<FfbCommand>? _onFfb;
    private readonly HidppFfbEmulator _hidppFfb = new();
    private byte[] _lastReport = G920ReportBuilder.Build(new());
    private byte[] _vendor11 = new byte[20]; // report id + 19 payload
    private byte[] _vendor12 = new byte[64]; // report id + 63 payload
    private bool _previewMode;
    private bool _running;
    private bool _disposed;
    private int _hostWriteCount;
    private byte _lastHostReportId;
    private string _lastHostWriteHex = "";
    private string _hostPathHint = "";

    public bool IsRunning => _running;
    public bool IsDriverAvailable { get; private set; }
    public bool IsPreviewMode => _previewMode;
    public string? LastError { get; private set; }

    public float SampleFfbTorque(float steeringCentered) =>
        _hidppFfb.ComputeTorque(steeringCentered);

    public VirtualFfbIngressStats GetFfbIngressStats()
    {
        var s = _hidppFfb.GetStats();
        return new VirtualFfbIngressStats(
            s.WriteCount, s.DownloadCount, s.PlayCount, s.CurrentTorque,
            s.LastFunction, s.SlotsInUse, s.SlotsPlaying,
            _hostWriteCount, _lastHostReportId, _lastHostWriteHex, _hostPathHint);
    }

    public VirtualG920Device()
    {
        _vendor11[0] = 0x11;
        _vendor12[0] = 0x12;
        IsDriverAvailable = WinUHidNative.TryProbeDriver(out var msg);
        if (!IsDriverAvailable)
            LastError = msg;
    }

    public bool Start(Func<byte[]> latestReportProvider, Action<FfbCommand>? onFfb = null)
    {
        Stop();
        _reportProvider = latestReportProvider;
        _onFfb = onFfb;
        _hidppFfb.Reset();
        _hostWriteCount = 0;
        _lastHostReportId = 0;
        _lastHostWriteHex = "";
        _hostPathHint = "";
        Array.Clear(_vendor11);
        Array.Clear(_vendor12);
        _vendor11[0] = 0x11;
        _vendor12[0] = 0x12;

        if (!WinUHidNative.TryProbeDriver(out var probeMsg))
        {
            IsDriverAvailable = false;
            _previewMode = true;
            _running = true;
            LastError = probeMsg + " Running in preview mode (no virtual G920 exposed to games).";
            return true;
        }

        IsDriverAvailable = true;
        _previewMode = false;

        try
        {
            // Drop any leftover Col01 still owned by logi_joy_hid_filter from older builds.
            LogiJoyHidBinder.TryRemoveLogitechCol01();

            var descriptor = G920HidDescriptor.Bytes;
            _descriptorHandle = GCHandle.Alloc(descriptor, GCHandleType.Pinned);

            var hwIds = Encoding.Unicode.GetBytes(HardwareIdsMultiSz);
            _hardwareIdsHandle = GCHandle.Alloc(hwIds, GCHandleType.Pinned);

            var instanceId = Encoding.Unicode.GetBytes("G920Emulator\0");
            _instanceIdHandle = GCHandle.Alloc(instanceId, GCHandleType.Pinned);

            var config = new WinUHidNative.DeviceConfig
            {
                // Feature + output reports — HID++ FAP may arrive as WriteReport or SetFeature.
                SupportedEvents =
                    WinUHidNative.EventType.GetFeature |
                    WinUHidNative.EventType.SetFeature |
                    WinUHidNative.EventType.WriteReport |
                    WinUHidNative.EventType.ReadReport,
                VendorID = G920HidDescriptor.VendorId,
                ProductID = G920HidDescriptor.ProductId,
                VersionNumber = G920HidDescriptor.VersionNumber,
                ReportDescriptorLength = (ushort)descriptor.Length,
                ReportDescriptor = _descriptorHandle.AddrOfPinnedObject(),
                ContainerId = G920HidDescriptor.ContainerId,
                InstanceID = _instanceIdHandle.AddrOfPinnedObject(),
                HardwareIDs = _hardwareIdsHandle.AddrOfPinnedObject(),
                ReadReportPeriodUs = 2000, // 500 Hz
            };

            _device = WinUHidNative.WinUHidCreateDevice(ref config);
            if (_device == IntPtr.Zero)
            {
                LastError = $"WinUHidCreateDevice failed ({Marshal.GetLastWin32Error()}). Is the driver installed?";
                FreePins();
                return false;
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
            G920OemRegistration.EnsureRegistered();

            // After PnP settles, confirm we are NOT on Logitech's filter (required for FFB ingress).
            _ = Task.Run(() =>
            {
                Thread.Sleep(900);
                if (LogiJoyHidBinder.IsCol01BoundToLogitech())
                {
                    LogiJoyHidBinder.TryRemoveLogitechCol01();
                    _hostPathHint = "WARN: logi_joy_hid was bound — removed so HID++ can reach WinUHid";
                }
                else
                {
                    _hostPathHint = "Microsoft HID path (FFB writes should reach WinUHid)";
                }
            });

            _running = true;
            LastError = null;
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

    public void SubmitReport(byte[] report)
    {
        lock (_gate)
        {
            _lastReport = report;
            if (!_running || _previewMode || _device == IntPtr.Zero)
                return;
            WinUHidNative.WinUHidSubmitInputReport(_device, report, (uint)report.Length);
        }
    }

    public void Stop()
    {
        _running = false;
        DestroyNative();
        _previewMode = false;
    }

    private void OnEvent(IntPtr ctx, IntPtr device, IntPtr evtPtr)
    {
        if (evtPtr == IntPtr.Zero)
            return;

        var type = (WinUHidNative.EventType)Marshal.ReadInt32(evtPtr, 0);
        var reportId = Marshal.ReadByte(evtPtr, 8);

        if (type is WinUHidNative.EventType.ReadReport or WinUHidNative.EventType.GetFeature)
        {
            if (reportId is 0x11 or 0x12)
            {
                var buffer = reportId == 0x12 ? _vendor12 : _vendor11;
                _hidppFfb.TryTakePendingRead(reportId, buffer);
                WinUHidNative.WinUHidCompleteReadEvent(device, evtPtr, buffer, (uint)buffer.Length);
                return;
            }

            // ReportId 0 (or unknown): any valid input report — use joystick state.
            var report = _reportProvider?.Invoke() ?? _lastReport;
            WinUHidNative.WinUHidCompleteReadEvent(device, evtPtr, report, (uint)report.Length);
            return;
        }

        if (type is WinUHidNative.EventType.WriteReport or WinUHidNative.EventType.SetFeature)
        {
            var dataLength = Marshal.ReadInt32(evtPtr, 9);
            if (dataLength > 0 && dataLength < 256)
            {
                var data = new byte[dataLength];
                Marshal.Copy(IntPtr.Add(evtPtr, 13), data, 0, dataLength);
                NoteHostWrite(reportId, data);
                HandleOutput(device, reportId, data);
            }

            WinUHidNative.WinUHidCompleteWriteEvent(device, evtPtr, true);
        }
    }

    private void NoteHostWrite(byte reportId, byte[] data)
    {
        _hostWriteCount++;
        _lastHostReportId = reportId;
        var n = Math.Min(data.Length, 16);
        _lastHostWriteHex = $"{reportId:X2}: {Convert.ToHexString(data.AsSpan(0, n))}";
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} write#{_hostWriteCount} id={reportId:X2} len={data.Length} {_lastHostWriteHex}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "g920-hidpp-ingress.log"), line);
        }
        catch { /* ignore */ }
    }

    private void HandleOutput(IntPtr device, byte reportId, byte[] data)
    {
        if (data.Length == 0)
            return;

        // Effective report id may be only in the payload when WinUHid sets ReportId=0.
        var effectiveId = reportId;
        if (effectiveId == 0 && data.Length > 0 && data[0] is 0x11 or 0x12)
            effectiveId = data[0];
        else if (effectiveId == 0 && LooksLikeBareHidpp(data))
            effectiveId = data.Length > 19 ? (byte)0x12 : (byte)0x11;

        if (effectiveId is not (0x11 or 0x12))
            return;

        if (!_hidppFfb.TryHandleWrite(effectiveId, data, out var torque, out var response))
            return;

        // Try to push the HID++ reply as an interrupt IN immediately. If WinUHid
        // is in ReadReport-throttled mode this may return NOT_READY; the queued
        // reply is still delivered on the next ReadReport for 0x11/0x12.
        if (response is not null && device != IntPtr.Zero && !_previewMode)
        {
            try { WinUHidNative.WinUHidSubmitInputReport(device, response, (uint)response.Length); }
            catch { /* ignore */ }
        }

        _onFfb?.Invoke(new FfbCommand(torque, Stop: Math.Abs(torque) < 0.0001f));
    }

    /// <summary>
    /// HID++ FAP payload without a leading report-id byte (common when ReportId=0).
    /// </summary>
    private static bool LooksLikeBareHidpp(ReadOnlySpan<byte> data)
    {
        if (data.Length is 19 or 63)
            return true;
        // deviceIndex (often 0xFF wired) + featureIndex + function/sw
        return data.Length >= 3 && data[0] is 0xFF or <= 0x0F;
    }

    private void DestroyNative()
    {
        if (_device != IntPtr.Zero)
        {
            try { WinUHidNative.WinUHidStopDevice(_device); } catch { /* ignore */ }
            try { WinUHidNative.WinUHidDestroyDevice(_device); } catch { /* ignore */ }
            _device = IntPtr.Zero;
        }

        if (_callbackHandle.IsAllocated)
            _callbackHandle.Free();
        FreePins();
        _callback = null;
    }

    private void FreePins()
    {
        if (_descriptorHandle.IsAllocated)
            _descriptorHandle.Free();
        if (_hardwareIdsHandle.IsAllocated)
            _hardwareIdsHandle.Free();
        if (_instanceIdHandle.IsAllocated)
            _instanceIdHandle.Free();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
