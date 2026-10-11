using System.Text;

namespace SteeringWheelEmulator.Core.Setup;

/// <summary>
/// Gates verbose diagnostic logging. While active, native <c>emuffb.dll</c> sees
/// a named event (<see cref="NativeLogEventName"/>) and writes OEM effect logs.
/// Export is offered after the user stops a session.
/// </summary>
public sealed class DiagnosticsDebugSession : IDisposable
{
    /// <summary>Must match the OpenEvent name in native emuffb/log.cpp.</summary>
    public const string NativeLogEventName = @"Local\G920Emulator.FfbDebug";

    private static readonly string[] SessionLogNames =
    [
        "emuffb-effects.log",
        "emuffb-effects.log.old",
        "g920-hidpp-ingress.log",
        "steeringwheel-emulator-bridge-health.log",
        "g920emulator-bridge-health.log",
    ];

    private EventWaitHandle? _nativeGate;
    private bool _disposed;

    public bool IsActive { get; private set; }

    /// <summary>True after Stop() following a completed Start() - export is allowed.</summary>
    public bool ExportReady { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsActive)
            return;

        ClearSessionLogs();
        ProcessResourceProbe.StartSparseLog();
        BridgeHealthLog.Start();
        _nativeGate = new EventWaitHandle(true, EventResetMode.ManualReset, NativeLogEventName);
        IsActive = true;
        ExportReady = false;
        AppendManagedMarker("DEBUG SESSION START (emulator)");
    }

    public void Stop()
    {
        if (!IsActive)
            return;

        AppendManagedMarker("DEBUG SESSION STOP (emulator)");
        try { BridgeHealthLog.Stop(); } catch { /* ignore */ }
        try { ProcessResourceProbe.StopSparseLog(); } catch { /* ignore */ }
        try { _nativeGate?.Dispose(); } catch { /* ignore */ }
        _nativeGate = null;
        IsActive = false;
        ExportReady = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsActive)
        {
            try { AppendManagedMarker("DEBUG SESSION STOP (emulator dispose)"); } catch { /* ignore */ }
            try { BridgeHealthLog.Stop(); } catch { /* ignore */ }
            try { ProcessResourceProbe.StopSparseLog(); } catch { /* ignore */ }
        }
        try { _nativeGate?.Dispose(); } catch { /* ignore */ }
        _nativeGate = null;
        IsActive = false;
    }

    private static void ClearSessionLogs()
    {
        var temp = Path.GetTempPath();
        foreach (var name in SessionLogNames)
        {
            try
            {
                var path = Path.Combine(temp, name);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // best-effort - locked files are fine to leave
            }
        }
    }

    private static void AppendManagedMarker(string body)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "emuffb-effects.log");
            var line = $"{DateTime.Now:HH:mm:ss.fff} {body}{Environment.NewLine}";
            File.AppendAllText(path, line, Encoding.ASCII);
        }
        catch
        {
            // ignore
        }
    }
}
