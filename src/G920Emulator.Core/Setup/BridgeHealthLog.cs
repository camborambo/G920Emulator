using System.Text;

namespace G920Emulator.Core.Setup;

/// <summary>
/// Debug-only timeline for mid-race virtual G920 death (UI stays up, Col01/submit dies).
/// Written from the managed bridge - never from the game OEM thread.
/// When Debug is off, <see cref="IsEnabled"/> is false and all APIs no-op (no file I/O).
/// </summary>
public static class BridgeHealthLog
{
    public const string LogFileName = "g920emulator-bridge-health.log";
    public static string LogPath => Path.Combine(Path.GetTempPath(), LogFileName);

    private static readonly object Gate = new();
    private static int _enabled; // 0/1 for Volatile
    private static string _lastLine = "";
    private static long _lastHeartbeatTick;

    /// <summary>True only while status-bar Debug session is active.</summary>
    public static bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public static void Start()
    {
        lock (Gate)
        {
            Volatile.Write(ref _enabled, 1);
            _lastLine = "";
            _lastHeartbeatTick = 0;
            try
            {
                if (File.Exists(LogPath))
                    File.Delete(LogPath);
            }
            catch { /* ignore */ }

            Write_NoLock("HEALTH START");
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (Volatile.Read(ref _enabled) == 0) return;
            Write_NoLock("HEALTH STOP");
            Volatile.Write(ref _enabled, 0);
            _lastLine = "";
        }
    }

    /// <summary>Log a state change (deduped). No-op when Debug is off.</summary>
    public static void Note(string message)
    {
        if (Volatile.Read(ref _enabled) == 0 || string.IsNullOrWhiteSpace(message))
            return;
        lock (Gate)
        {
            if (Volatile.Read(ref _enabled) == 0) return;
            if (string.Equals(message, _lastLine, StringComparison.Ordinal))
                return;
            _lastLine = message;
            Write_NoLock(message);
        }
    }

    /// <summary>
    /// Periodic snapshot while Debug is on (default every 5s).
    /// No-op when Debug is off (callers should still guard string build with <see cref="IsEnabled"/>).
    /// </summary>
    public static void Heartbeat(string snapshot, int intervalMs = 5_000)
    {
        if (Volatile.Read(ref _enabled) == 0 || string.IsNullOrWhiteSpace(snapshot))
            return;
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (Volatile.Read(ref _enabled) == 0) return;
            if (_lastHeartbeatTick != 0 && now - _lastHeartbeatTick < intervalMs)
                return;
            _lastHeartbeatTick = now;
            Write_NoLock("HB " + snapshot);
        }
    }

    private static void Write_NoLock(string body)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {body}{Environment.NewLine}";
            File.AppendAllText(LogPath, line, Encoding.ASCII);
        }
        catch
        {
            // ignore - diagnostics must never break the bridge
        }
    }
}
