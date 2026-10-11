using System.Diagnostics;

namespace SteeringWheelEmulator.Core.Telemetry;

/// <summary>
/// Optional process check for titles listed in the SimHub definition (no memory access).
/// Session is also considered live when OEM FFB is playing, so other G920 games still work.
/// </summary>
public static class GameProcessProbe
{
    private static long _nextTick;
    private static bool _cached;

    public static bool IsKnownGameRunning()
    {
        var now = Environment.TickCount64;
        if (now < _nextTick)
            return _cached;

        // Process enumeration is expensive; 5s is plenty for session/live detection.
        _nextTick = now + 5000;
        try
        {
            foreach (var name in SimHubPacket.KnownGameProcessNames)
            {
                var list = Process.GetProcessesByName(name);
                if (list.Length > 0)
                {
                    foreach (var p in list)
                        p.Dispose();
                    _cached = true;
                    return true;
                }
            }

            _cached = false;
            return false;
        }
        catch
        {
            _cached = false;
            return false;
        }
    }
}
