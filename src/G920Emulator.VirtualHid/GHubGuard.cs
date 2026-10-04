using System.Diagnostics;
using Microsoft.Win32;

namespace G920Emulator.VirtualHid;

/// <summary>
/// Keeps OEM + Col01 friendly name correct while the bridge runs.
/// Never restarts PnP devices — that orphans WinUHid VHF children.
/// </summary>
public sealed class GHubGuard : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _lastStatus = "";
    private int _maintainBusy; // 0/1 — avoid overlapping Maintain with Stop

    public string LastStatus => _lastStatus;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), token);
    }

    private static CancellationTokenSource? _appWatchCts;
    private static int _appWatchRestores;

    /// <summary>Times the app-wide watch has repaired the registry since launch.</summary>
    public static int AppWatchRestoreCount => _appWatchRestores;

    public static bool IsAppWatchRunning => _appWatchCts is { IsCancellationRequested: false };

    /// <summary>
    /// App-lifetime registry guard (no pnputil, no device restarts). Runs whether or not the
    /// bridge is started so a G HUB install/uninstall is undone within seconds.
    /// </summary>
    public static void StartAppWatch()
    {
        if (IsAppWatchRunning) return;
        var cts = new CancellationTokenSource();
        _appWatchCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!G920OemRegistration.IsIntact())
                    {
                        G920OemRegistration.EnsureRegistered(installSdk: false);
                        Interlocked.Increment(ref _appWatchRestores);
                    }
                }
                catch
                {
                    // ignore
                }

                try { await Task.Delay(2000, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }, token);
    }

    public static void StopAppWatch()
    {
        var cts = Interlocked.Exchange(ref _appWatchCts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch { /* ignore */ }
        cts.Dispose();
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }

        // Never block the UI for long. Maintain() can be inside pnputil for many seconds;
        // waiting on that freezes the window ("Not Responding").
        var loop = _loop;
        _loop = null;
        if (loop is not null)
        {
            try
            {
                if (!loop.Wait(200))
                {
                    // Detach; let the cancelled task finish in the background.
                    _ = loop.ContinueWith(_ => { /* observe */ }, TaskScheduler.Default);
                }
            }
            catch
            {
                // ignore
            }
        }

        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }

    public void Dispose() => Stop();

    /// <summary>One-shot maintain pass (safe to call often).</summary>
    public static string Maintain(bool allowDeviceRestart, CancellationToken token = default)
    {
        _ = allowDeviceRestart; // restarts are unsafe for WinUHid; ignored
        if (token.IsCancellationRequested)
            return "G HUB guard: cancelled";

        var parts = new List<string>();

        // 1) Registry identity: OEM tree, FFB CLSID, COM server and Logitech SDK pin.
        //    G HUB installs rewrite these and its uninstaller deletes them; independent of Col01.
        if (!G920OemRegistration.IsIntact())
        {
            G920OemRegistration.EnsureRegistered(installSdk: false);
            parts.Add("registry identity restored");
        }

        if (token.IsCancellationRequested) return "G HUB guard: cancelled";

        if (!G920DeviceIdentityFix.IsVirtualCol01Present())
        {
            parts.Add("virtual Col01 NOT present — Stop/Start bridge");
            return "G HUB guard: " + string.Join("; ", parts);
        }

        if (token.IsCancellationRequested) return "G HUB guard: cancelled";

        // 2) Strip Logi filter if it rebound (HEAD recipe: Col01 Logitech-bound / legacy only).
        if (!token.IsCancellationRequested && LogiJoyHidBinder.IsCol01BoundToLogitech())
        {
            if (token.IsCancellationRequested) return "G HUB guard: cancelled";
            LogiJoyHidBinder.TryRemoveLogitechCol01();
            parts.Add("removed logi_joy binding");
        }

        if (token.IsCancellationRequested) return "G HUB guard: cancelled";

        // 3) Friendly name only — never pnputil restart.
        if (G920DeviceIdentityFix.NeedsFriendlyNameFix())
            parts.Add(G920DeviceIdentityFix.Apply(restartDevice: false));
        else
            G920DeviceIdentityFix.EnsureFriendlyNameOnly();

        if (token.IsCancellationRequested) return "G HUB guard: cancelled";

        if (IsGHubProcessRunning())
            parts.Add("G HUB running — guarding");

        return parts.Count == 0 ? "G HUB guard: OK (Col01 present)" : "G HUB guard: " + string.Join("; ", parts);
    }

    private async Task RunAsync(CancellationToken token)
    {
        var schedule = new[]
        {
            1000, 2000, 3000, 5000, 8000, 12000, 20000, 30000, 45000, 60000,
        };

        foreach (var delay in schedule)
        {
            if (token.IsCancellationRequested) return;
            await RunMaintainOnce(token).ConfigureAwait(false);
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        while (!token.IsCancellationRequested)
        {
            await RunMaintainOnce(token).ConfigureAwait(false);
            try { await Task.Delay(15_000, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private Task RunMaintainOnce(CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.CompletedTask;
        if (Interlocked.CompareExchange(ref _maintainBusy, 1, 0) != 0)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                if (token.IsCancellationRequested) return;
                _lastStatus = Maintain(allowDeviceRestart: false, token);
            }
            catch (Exception ex)
            {
                _lastStatus = "G HUB guard: " + ex.Message;
            }
            finally
            {
                Interlocked.Exchange(ref _maintainBusy, 0);
            }
        });
    }

    private static bool IsGHubProcessRunning()
    {
        try
        {
            foreach (var name in new[] { "lghub", "lghub_agent", "lghub_updater", "LogitechGHub" })
            {
                if (Process.GetProcessesByName(name).Length > 0)
                    return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

}
