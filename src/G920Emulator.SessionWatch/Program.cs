using System.Diagnostics;
using G920Emulator.VirtualHid;

namespace G920Emulator.SessionWatch;

/// <summary>
/// Waits for the parent G920 Emulator process; if it dies while the OEM/SDK session
/// is still marked Active, restores the baseline registry so Forza/other games are safe.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var parentPid = 0;
        string? sessionPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--parent" && i + 1 < args.Length && int.TryParse(args[++i], out var pid))
                parentPid = pid;
            else if (args[i] == "--session" && i + 1 < args.Length)
                sessionPath = args[++i];
        }

        if (parentPid <= 0)
            return 2;

        try
        {
            using var parent = Process.GetProcessById(parentPid);
            parent.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Parent already gone.
        }
        catch
        {
            // Ignore other wait failures; still try restore if Active.
        }

        return OemRegistrationSession.WatchdogRestoreIfActive(sessionPath);
    }
}
