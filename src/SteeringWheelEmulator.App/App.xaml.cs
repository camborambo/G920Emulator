using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SteeringWheelEmulator.Core;
using SteeringWheelEmulator.Core.Setup;

namespace SteeringWheelEmulator.App;

public partial class App : Application
{
    private const string MutexName = @"Global\SteeringWheelEmulator.SingleInstance";
    private const string MutexNameLocal = @"Local\SteeringWheelEmulator.SingleInstance";
    private const string WakeName = @"Global\SteeringWheelEmulator.Wake";
    private const string WakeNameLocal = @"Local\SteeringWheelEmulator.Wake";
    // One-release: detect / wake a still-running older build.
    private const string LegacyMutexName = @"Global\G920Emulator.SingleInstance";
    private const string LegacyMutexNameLocal = @"Local\G920Emulator.SingleInstance";
    private const string LegacyWakeName = @"Global\G920Emulator.Wake";
    private const string LegacyWakeNameLocal = @"Local\G920Emulator.Wake";

    private Mutex? _mutex;
    private EventWaitHandle? _wake;
    private CancellationTokenSource? _wakeCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Explorer launches with CWD = install folder; leave immediately so helpers cannot pin it.
        InstallFolderGuard.LeaveInstallFolder();
        try { AppPaths.MigrateLegacyDataRoots(); } catch { /* best-effort */ }

        if (!TryOwnSingleInstance())
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        // HidHide auto-apply / unload-on-stop are Settings toggles; otherwise use Dependencies / HidHide Client.
        base.OnStartup(e);
        new MainWindow().Show();
        StartWakeListener();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wakeCts?.Cancel();
        try { _wake?.Dispose(); } catch { /* ignore */ }
        _wake = null;
        InstallFolderGuard.LeaveInstallFolder();
        try { DependencyChecker.KillOrphanHidHideHelpers(); }
        catch { /* ignore */ }
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* ignore */ }
            try { _mutex.Dispose(); } catch { /* ignore */ }
            _mutex = null;
        }

        base.OnExit(e);
    }

    private bool TryOwnSingleInstance()
    {
        // Prefer new mutex; treat a still-running pre-rename exe as already owned.
        if (MutexExists(LegacyMutexName) || MutexExists(LegacyMutexNameLocal))
            return false;
        if (TryCreateMutex(MutexName, out _mutex, out var owned))
            return owned;
        return TryCreateMutex(MutexNameLocal, out _mutex, out owned) && owned;
    }

    private static bool MutexExists(string name)
    {
        try
        {
            using var m = Mutex.OpenExisting(name);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateMutex(string name, out Mutex? mutex, out bool owned)
    {
        owned = false;
        mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: true, name, out var created);
            if (created)
            {
                owned = true;
                return true;
            }

            try
            {
                owned = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            if (!owned)
            {
                mutex.Dispose();
                mutex = null;
            }

            return true;
        }
        catch
        {
            try { mutex?.Dispose(); } catch { /* ignore */ }
            mutex = null;
            return false;
        }
    }

    private void SignalExistingInstance()
    {
        foreach (var name in new[] { WakeName, WakeNameLocal, LegacyWakeName, LegacyWakeNameLocal })
        {
            try
            {
                using var wake = EventWaitHandle.OpenExisting(name);
                wake.Set();
                return;
            }
            catch
            {
                // try the other scope / legacy name
            }
        }
    }

    private void StartWakeListener()
    {
        _wake = CreateWakeHandle();
        if (_wake is null)
            return;

        _wakeCts = new CancellationTokenSource();
        var wake = _wake;
        var ct = _wakeCts.Token;
        _ = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!wake.WaitOne(TimeSpan.FromMilliseconds(400)))
                        continue;
                }
                catch
                {
                    break;
                }

                try
                {
                    Dispatcher.BeginInvoke(ActivateMainWindow, DispatcherPriority.Normal);
                }
                catch
                {
                    break;
                }
            }
        }, ct);
    }

    private EventWaitHandle? CreateWakeHandle()
    {
        try { return new EventWaitHandle(false, EventResetMode.AutoReset, WakeName); }
        catch { /* fall back */ }
        try { return new EventWaitHandle(false, EventResetMode.AutoReset, WakeNameLocal); }
        catch { return null; }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is MainWindow window)
            window.BringToForeground();
        else
            MainWindow?.Activate();
    }
}
