using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace G920Emulator.App;

public partial class App : Application
{
    private const string MutexName = @"Global\G920Emulator.SingleInstance";
    private const string MutexNameLocal = @"Local\G920Emulator.SingleInstance";
    private const string WakeName = @"Global\G920Emulator.Wake";
    private const string WakeNameLocal = @"Local\G920Emulator.Wake";

    private Mutex? _mutex;
    private EventWaitHandle? _wake;
    private CancellationTokenSource? _wakeCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryOwnSingleInstance())
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        // HidHide is never auto-configured at launch — users set it up in Dependencies
        // or HidHide Client so existing whitelist / hide lists are left alone.
        base.OnStartup(e);
        new MainWindow().Show();
        StartWakeListener();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wakeCts?.Cancel();
        try { _wake?.Dispose(); } catch { /* ignore */ }
        _wake = null;
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
        if (TryCreateMutex(MutexName, out _mutex, out var owned))
            return owned;
        return TryCreateMutex(MutexNameLocal, out _mutex, out owned) && owned;
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
        foreach (var name in new[] { WakeName, WakeNameLocal })
        {
            try
            {
                using var wake = EventWaitHandle.OpenExisting(name);
                wake.Set();
                return;
            }
            catch
            {
                // try the other scope
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
