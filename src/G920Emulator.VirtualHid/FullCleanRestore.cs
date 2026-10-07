using G920Emulator.Core.Setup;

namespace G920Emulator.VirtualHid;

/// <summary>
/// Single entry point that undoes every system change this app can leave behind.
/// When adding a new OS/registry/driver mutation elsewhere, add the matching undo step here.
///
/// Covered today:
///   • OEM joystick tree (VID_046D&amp;PID_C262) + OEMForceFeedback CLSID
///   • g920ffb COM registration
///   • Logitech Steering Wheel SDK ServerBinary pin
///   • ProgramData LogitechSDK cache
///   • ProgramData g920ffb.dll cache (COM InprocServer copy)
///   • oem-session.json session state
///   • Stale DirectInput cache / orphan virtual C262 PnP nodes
///   • hidpp_forcefeedback_x64.dll rename (.g920emulator-disabled) from older G HUB repair
///   • WinUHid device node + published driver package
///   • Windows test signing (bcdedit testsigning off — reboot required)
///
/// Intentionally not touched:
///   • HidHide configuration (user-owned; existed before this app)
///   • Secure Boot (UEFI setting; user re-enables it in BIOS)
///   • User profiles / settings under %AppData%\G920Emulator
///   • Deleted Logitech logi_joy_* driver packages (need G HUB reinstall to restore)
/// </summary>
public static class FullCleanRestore
{
    public sealed record Result(bool Success, string Summary, IReadOnlyList<string> Steps);

    /// <summary>
    /// Runs a full clean restore. Caller must stop the bridge first (<see cref="OemRegistrationSession.IsActive"/>).
    /// Elevates via UAC for Program Files / ProgramData / driver-store cleanup.
    /// </summary>
    /// <param name="uninstallWinUHid">When true, also remove Root\WinUHid and the driver package.</param>
    public static Result Run(bool uninstallWinUHid = true)
    {
        if (OemRegistrationSession.IsActive)
        {
            return new Result(
                false,
                "Stop the bridge first, then run Full clean restore.",
                ["Bridge session is still active."]);
        }

        var steps = new List<string>();
        var failed = false;

        try
        {
            var end = OemRegistrationSession.EndSession();
            steps.Add("OEM session: " + end);
        }
        catch (Exception ex)
        {
            failed = true;
            steps.Add("OEM session: " + ex.Message);
        }

        try
        {
            steps.Add("OEM/SDK registry: " +
                      G920OemRegistration.RestoreSystemLogitechRegistration(restoreLogitechOemClsid: false));
        }
        catch (Exception ex)
        {
            failed = true;
            steps.Add("OEM/SDK registry: " + ex.Message);
        }

        try
        {
            steps.Add(G920OemRegistration.RemoveCachedSdkFiles());
        }
        catch (Exception ex)
        {
            failed = true;
            steps.Add("SDK cache: " + ex.Message);
        }

        try
        {
            steps.Add(G920OemRegistration.RemoveCachedG920FfbFiles());
        }
        catch (Exception ex)
        {
            failed = true;
            steps.Add("g920ffb cache: " + ex.Message);
        }

        try
        {
            steps.Add("Device caches: " + GHubConflictRepair.PurgeEmulatorDeviceCaches());
        }
        catch (Exception ex)
        {
            failed = true;
            steps.Add("Device caches: " + ex.Message);
        }

        try
        {
            steps.Add("G HUB repair undo: " + GHubConflictRepair.RestoreDestructiveGHubRepairs());
        }
        catch (Exception ex)
        {
            failed = true;
            steps.Add("G HUB repair undo: " + ex.Message);
        }

        try
        {
            GHubGuard.StopAppWatch();
            steps.Add("G HUB guard: stopped");
        }
        catch (Exception ex)
        {
            steps.Add("G HUB guard: " + ex.Message);
        }

        if (uninstallWinUHid)
        {
            try
            {
                var winuhid = new BundledWinUHidInstaller().Uninstall();
                steps.Add("WinUHid: " + winuhid.Message);
                if (!winuhid.Success)
                    failed = true;
            }
            catch (Exception ex)
            {
                failed = true;
                steps.Add("WinUHid: " + ex.Message);
            }
        }
        else
        {
            steps.Add("WinUHid: skipped (left installed)");
        }

        // Elevated pass: hidpp restore + ProgramData + DI cache + leftover WinUHid packages.
        try
        {
            steps.Add(ElevatedSystemCleanup.RunForzaSafeCleanup());
        }
        catch (Exception ex)
        {
            failed = true;
            steps.Add("Elevated cleanup: " + ex.Message);
        }

        try
        {
            if (OemRegistrationSession.HasOurPinsPresent() || OemRegistrationSession.HasOurOemTreeLeftover())
            {
                steps.Add("Final OEM sweep: " +
                          G920OemRegistration.RestoreSystemLogitechRegistration(restoreLogitechOemClsid: false));
            }
            else
            {
                steps.Add("Final OEM sweep: clean");
            }
        }
        catch (Exception ex)
        {
            failed = true;
            steps.Add("Final OEM sweep: " + ex.Message);
        }

        var summary = failed
            ? "Full clean restore finished with errors. Review the steps below."
            : "Full clean restore complete. OEM/SDK pins, SDK cache, G HUB repair leftovers, WinUHid, and test signing were cleared." +
              " REBOOT if you turned off test signing. For Forza Horizon 6 alone, prefer Uninstall WinUHid" +
              " (OEM/SDK wipe is optional). HidHide and your profiles were left unchanged.";

        return new Result(!failed, summary, steps);
    }
}
