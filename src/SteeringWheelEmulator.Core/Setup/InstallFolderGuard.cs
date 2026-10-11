using System.Diagnostics;

namespace SteeringWheelEmulator.Core.Setup;

/// <summary>
/// Keeps the install folder deletable after exit. Explorer-launched EXEs inherit a CWD of
/// the app directory; helper tools (pnputil / cmd / HidHideCLI) then pin that folder open
/// even after G920Emulator.exe has exited.
/// </summary>
public static class InstallFolderGuard
{
    /// <summary>Safe directory for child processes (never the install folder).</summary>
    public static string SafeWorkingDirectory { get; } = Path.GetTempPath();

    /// <summary>
    /// Move this process off the install folder as CWD. Call once at startup and again
    /// before spawning teardown helpers / Environment.Exit.
    /// </summary>
    public static void LeaveInstallFolder()
    {
        try
        {
            var target = SafeWorkingDirectory;
            if (!string.Equals(Directory.GetCurrentDirectory(), target, StringComparison.OrdinalIgnoreCase))
                Directory.SetCurrentDirectory(target);
        }
        catch
        {
            // ignore - best effort
        }
    }

    public static void ApplySafeWorkingDirectory(ProcessStartInfo psi)
    {
        try { psi.WorkingDirectory = SafeWorkingDirectory; }
        catch { /* ignore */ }
    }
}
