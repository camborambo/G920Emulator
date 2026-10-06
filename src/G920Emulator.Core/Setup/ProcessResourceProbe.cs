using System.Diagnostics;
using System.Text;
using G920Emulator.Core.Ffb;

namespace G920Emulator.Core.Setup;

/// <summary>
/// Light CPU/RAM snapshots of the emulator and the OEM host (game / Steam).
/// Game GPU/CPU being high is expected; we measure whether <em>we</em> are heavy.
/// Never runs on the game OEM thread and never writes <c>g920ffb-effects.log</c>.
/// </summary>
public static class ProcessResourceProbe
{
    public const string LogFileName = "g920emulator-perf.log";
    public static string LogPath => Path.Combine(Path.GetTempPath(), LogFileName);

    private const int IntervalMs = 10_000;

    private static readonly object Gate = new();
    private static Timer? _timer;
    private static readonly Dictionary<int, (TimeSpan Cpu, long Stamp)> CpuMarks = new();

    public static void StartSparseLog()
    {
        lock (Gate)
        {
            DisposeTimer_NoLock();
            CpuMarks.Clear();
            try
            {
                if (File.Exists(LogPath))
                    File.Delete(LogPath);
            }
            catch { /* ignore */ }

            WriteLine_NoLock("PERF START");
            _timer = new Timer(static _ => Tick(), null, IntervalMs, IntervalMs);
        }
    }

    public static void StopSparseLog()
    {
        lock (Gate)
        {
            DisposeTimer_NoLock();
            WriteLine_NoLock("PERF STOP");
            CpuMarks.Clear();
        }
    }

    public static void AppendTo(StringBuilder sb)
    {
        sb.AppendLine("Process load (snapshot, no extra wait; GPU not sampled)");
        try
        {
            lock (Gate)
                sb.AppendLine(FormatBlock());
        }
        catch (Exception ex)
        {
            sb.AppendLine("  failed: " + ex.Message);
        }
    }

    private static void Tick()
    {
        try
        {
            lock (Gate)
            {
                if (_timer is null)
                    return;
                WriteLine_NoLock(null);
            }
        }
        catch { /* ignore */ }
    }

    private static void DisposeTimer_NoLock()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private static void WriteLine_NoLock(string? marker)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {FormatLine()}";
            if (!string.IsNullOrEmpty(marker))
                line += " " + marker;
            File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.ASCII);
        }
        catch { /* ignore */ }
    }

    private static string FormatLine()
    {
        var emu = SnapshotSelf();
        TryOemPids(out var gamePid, out var auxPid);
        var game = SnapshotPid("game", gamePid, emu.Pid);
        var aux = SnapshotPid("aux", auxPid, emu.Pid, skipIf: gamePid);
        var hint = Hint(emu.IntervalCpu1);
        return $"emu {emu.Line} | {game} | {aux} | {hint}";
    }

    private static string FormatBlock()
    {
        var emu = SnapshotSelf();
        TryOemPids(out var gamePid, out var auxPid);
        var sb = new StringBuilder();
        sb.AppendLine($"  emulator: {emu.Line}");
        sb.AppendLine($"    uptime {emu.Uptime}  gc={emu.GcMb:0.0}MB  gc0/1/2={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}  cores={Environment.ProcessorCount}");
        sb.AppendLine("  " + SnapshotPid("game (OEM Torque host)", gamePid, emu.Pid));
        sb.AppendLine("  " + SnapshotPid("aux (Steam/overlay)", auxPid, emu.Pid, skipIf: gamePid));
        sb.AppendLine("  " + Hint(emu.IntervalCpu1));
        sb.AppendLine("  Racing games peg CPU/GPU by design. Compare emulator cpu1 here — not the game’s.");
        sb.AppendLine("  GPU is not sampled (counters are heavy and game GPU high is normal).");
        return sb.ToString().TrimEnd();
    }

    private static string Hint(double? emuCpu1)
    {
        if (emuCpu1 is null)
            return "hint=emulator interval CPU n/a until the next snapshot";
        if (emuCpu1 >= 80)
            return "hint=emulator hot (~1+ core) — can steal time from the game";
        if (emuCpu1 >= 25)
            return "hint=emulator moderate; watch if the game stutters in time with these spikes";
        return "hint=emulator light; high game CPU/GPU is expected and not our load";
    }

    private static void TryOemPids(out int gamePid, out int auxPid)
    {
        gamePid = 0;
        auxPid = 0;
        try
        {
            if (!OemFfbSharedMemory.TryRead(out var snap, out _))
                return;
            gamePid = (int)snap.GamePid;
            auxPid = (int)snap.AuxPid;
        }
        catch { /* ignore */ }
    }

    private readonly record struct SelfSnap(
        int Pid,
        string Line,
        double? IntervalCpu1,
        double GcMb,
        string Uptime);

    private static SelfSnap SnapshotSelf()
    {
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var interval = TakePidCpu1(proc.Id, proc);
        var life = LifetimeCpu1(proc);
        var ws = proc.WorkingSet64 / (1024.0 * 1024.0);
        var priv = proc.PrivateMemorySize64 / (1024.0 * 1024.0);
        var gcMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        var cpu = interval is double c ? $"{c:0.0}%" : "n/a";
        var line =
            $"{proc.ProcessName} pid={proc.Id} cpu1={cpu} cpu1-life={life:0.0}% " +
            $"ws={ws:0.0}MB priv={priv:0.0}MB threads={proc.Threads.Count} handles={proc.HandleCount}";
        var uptime = DateTime.Now - proc.StartTime;
        return new SelfSnap(proc.Id, line, interval, gcMb, uptime.ToString(@"hh\:mm\:ss"));
    }

    private static string SnapshotPid(string role, int pid, int emuPid, int skipIf = 0)
    {
        if (pid <= 0)
            return $"{role}=none (OEM host not publishing yet)";
        if (pid == emuPid || pid == skipIf)
            return $"{role}=same";
        try
        {
            var proc = Process.GetProcessById(pid);
            proc.Refresh();
            var interval = TakePidCpu1(pid, proc);
            var ws = proc.WorkingSet64 / (1024.0 * 1024.0);
            var cpu = interval is double c ? $"{c:0.0}%" : "n/a";
            var name = string.IsNullOrWhiteSpace(proc.ProcessName) ? "?" : proc.ProcessName;
            return $"{role}={name} pid={pid} cpu1={cpu} ws={ws:0.0}MB";
        }
        catch (ArgumentException)
        {
            return $"{role}=pid={pid} (exited)";
        }
        catch (Exception ex)
        {
            return $"{role}=pid={pid} ({ex.GetType().Name})";
        }
    }

    private static double? TakePidCpu1(int pid, Process proc)
    {
        TimeSpan cpu;
        try
        {
            cpu = proc.TotalProcessorTime;
        }
        catch
        {
            return null;
        }

        var stamp = Stopwatch.GetTimestamp();
        double? pct = null;
        if (CpuMarks.TryGetValue(pid, out var prev))
        {
            var wall = (stamp - prev.Stamp) / (double)Stopwatch.Frequency;
            if (wall > 0.05)
                pct = 100.0 * (cpu - prev.Cpu).TotalSeconds / wall;
        }

        CpuMarks[pid] = (cpu, stamp);
        return pct;
    }

    private static double LifetimeCpu1(Process proc)
    {
        try
        {
            var wall = (DateTime.Now - proc.StartTime).TotalSeconds;
            if (wall < 0.05)
                return 0;
            return 100.0 * proc.TotalProcessorTime.TotalSeconds / wall;
        }
        catch
        {
            return 0;
        }
    }
}
