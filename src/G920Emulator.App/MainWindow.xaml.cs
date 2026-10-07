using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MahApps.Metro.IconPacks;
using Microsoft.Win32;
using G920Emulator.Core;
using G920Emulator.Core.Bridge;
using G920Emulator.Core.Mapping;
using G920Emulator.Core.Models;
using G920Emulator.Core.Profiles;
using G920Emulator.Core.Ffb;
using G920Emulator.Core.Setup;
using G920Emulator.Core.Telemetry;
using G920Emulator.VirtualHid;

namespace G920Emulator.App;

public partial class MainWindow : Window
{
    private readonly BridgeService _bridge = new();
    private readonly VirtualG920Device _virtual = new();
    private readonly ProfileStore _profiles = new();
    private readonly DiagnosticsDebugSession _debugSession = new();
    private readonly ObservableCollection<DeviceRow> _devices = [];
    private readonly ObservableCollection<BindingRow> _bindings = [];
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _ffbProfilePushTimer;
    private MappingProfile _profile = MappingProfile.CreateDefault();
    private bool _suppressProfileCombo;
    private bool _suppressFfbProfileCombo;
    private bool _suppressTelemetryProfileCombo;
    private bool _bindingDialogOpen;
    private bool _ffbTestSliderSilent;
    private bool _effectGainSliderSilent;
    private readonly bool[] _ffbNudgePressed = new bool[G920ControlInfo.FfbNudgeControls.Length];
    private readonly long[] _ffbNudgeLastFire = new long[G920ControlInfo.FfbNudgeControls.Length];
    private readonly long[] _ffbNudgeHoldStart = new long[G920ControlInfo.FfbNudgeControls.Length];
    private TextBox? _ffbValueEditBox;
    private TextBlock? _ffbValueEditLabel;
    private TextBox? _telemetryValueEditBox;
    private TextBlock? _telemetryValueEditLabel;
    /// <summary>True = UI shows/adjusts mph; false = km/h. SimHub always gets km/h.</summary>
    private bool _telemetryUseMph = true;
    private string? _shownLinkStatus;
    private CancellationTokenSource? _ffbPulseCts;
    private long _lastFfbDiagUiTick;
    private bool _refreshDevicesBusy;
    /// <summary>True while Start/Stop/exit tears down DI — UI must not Poll InputHub.</summary>
    private volatile bool _bridgeBusy;
    private int _livePreviewPollInFlight;
    private bool _minimizeToTray;
    private WindowState _restoreWindowState = WindowState.Normal;
    private TrayIcon? _trayIcon;
    private DebugOverlayWindow? _debugOverlay;
    private TelemetryDebugOverlayWindow? _telemetryDebugOverlay;
    private EffectChangesOverlayWindow? _effectChangesOverlay;
    private bool _effectChangesOverlayEnabled = true;
    private readonly CancellationTokenSource _updateCheckCts = new();
    private GitHubReleaseInfo? _pendingRelease;
    private bool _telemetryUiBusy;

    public MainWindow()
    {
        // Create before InitializeComponent — effect/feel sliders fire ValueChanged while XAML loads.
        _ffbProfilePushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _ffbProfilePushTimer.Tick += (_, _) =>
        {
            _ffbProfilePushTimer.Stop();
            _bridge.Profile = _profile;
        };

        InitializeComponent();
        DarkTitleBar.Apply(this);
        Title = "G920 Emulator " + AppVersion.Display;
        if (AppVersionText is not null)
            AppVersionText.Text = "v" + AppVersion.Display;
        DeviceList.ItemsSource = _devices;
        BindingList.ItemsSource = _bindings;
        FfbDeviceCombo.ItemsSource = _devices;

        _bridge.AttachVirtualDevice(_virtual);
        _profile = _profiles.LoadLastOrDefault();
        LoadProfileIntoUi(_profile);
        RefreshSavedProfilesCombo(_profile.Name);
        RefreshFfbProfilesCombo(_profile.FfbProfileName);
        ApplyAppSettings(_profiles.LoadSettings());

        // 16 ms so live axis meters track the ~500 Hz bridge without looking lagged.
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _uiTimer.Tick += (_, _) => RefreshLiveUi();
        _uiTimer.Start();

        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            DarkTitleBar.TryApply(hwnd);
            _bridge.BindFfbWindow(hwnd);
            try { OemRegistrationSession.RecoverIfDirty(); } catch { /* ignore */ }
            // Crash/kill mid-session: restore HidHide off the UI thread (never block Loaded).
            if (!_bridge.IsRunning && DependencyChecker.HasPendingHidHideSnapshot)
                _ = RestorePendingHidHideOnLaunchAsync();
            _ = RefreshDevicesAsync(restoreHidden: false);
            UpdateDependencyUi();
            RefreshDebugSessionUi();
            _ = CheckForGitHubUpdateAsync(force: false);
        };
        Activated += (_, _) =>
        {
            // Re-bind HWND after alt-tab without doing DI work on the UI thread.
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                _bridge.BindFfbWindow(hwnd);
        };
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
    }

    private static readonly Brush OkChipBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0x4A));
    private static readonly Brush BadChipBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0x1F, 0x2A));
    private static readonly Brush SoftChipBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0x5A, 0x1F));
    private static readonly Brush WarnBannerBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x22, 0x18));
    private static readonly Brush OkBannerBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x29));
    private static readonly Brush WarnBorderBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x6A, 0x3A));
    private static readonly Brush OkBorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x40));

    private bool _exitTeardownStarted;
    /// <summary>Set only after HidHide restore + teardown finish — then Close may proceed.</summary>
    private bool _exitAllowed;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitAllowed)
        {
            e.Cancel = false;
            return;
        }

        // Block Alt+F4 / X / Exit until HidHide restore (if any) finishes.
        e.Cancel = true;
        if (_exitTeardownStarted)
            return;

        _exitTeardownStarted = true;
        _bridgeBusy = true;
        try { IsEnabled = false; } catch { /* ignore */ }
        try { SetBridgeControls(running: _bridge.IsRunning, busy: true); } catch { /* ignore */ }

        DisposeTrayIcon();
        CloseDebugOverlay(saveEnabled: false);
        CloseTelemetryDebugOverlay(saveEnabled: false);
        CloseEffectChangesOverlay();
        try { _updateCheckCts.Cancel(); } catch { /* ignore */ }
        _uiTimer.Stop();
        _ffbProfilePushTimer.Stop();
        _ffbPulseCts?.Cancel();
        try { _bridge.Ffb.ClearTestOverride(); } catch { /* ignore */ }
        try
        {
            SyncProfileFromUi();
            var closeName = CurrentInputProfileName();
            if (!string.IsNullOrWhiteSpace(closeName))
                _profiles.Save(_profile, closeName);
        }
        catch { /* ignore autosave failures on close */ }

        var needsHidHideRestore = DependencyChecker.HasPendingHidHideSnapshot;
        StatusText.Text = needsHidHideRestore
            ? "Restoring HidHide (brief)… then exit."
            : "Shutting down…";

        try
        {
            VirtualG920Device.LogHostIngress = false;
            _debugSession.Dispose();
        }
        catch { /* ignore */ }

        _ = FinishExitAsync(needsHidHideRestore);
    }

    private async Task RestorePendingHidHideOnLaunchAsync()
    {
        try
        {
            StatusText.Text = "Restoring HidHide from the last session…";
            var (ok, msg) = await Task.Run(() =>
                    DependencyChecker.EndHidHideSession(timeoutMs: DependencyChecker.HidHideRestoreStopTimeoutMs))
                .ConfigureAwait(true);
            StatusText.Text = ok
                ? "Restored HidHide from the last interrupted session."
                : msg;
            try { UpdateDependencyUi(); } catch { /* ignore */ }
        }
        catch { /* ignore */ }
    }

    private async Task FinishExitAsync(bool restoreHidHide)
    {
        try
        {
            InstallFolderGuard.LeaveInstallFolder();
            // Do not spawn deferred pnputil cleanup during exit — orphans pin the install folder.
            VirtualG920Device.SuppressDeferredDeviceCleanup = true;

            await Task.Run(() =>
            {
                try { GHubGuard.StopAppWatch(); } catch { /* ignore */ }
            }).ConfigureAwait(true);

            await Task.Run(() =>
            {
                try { OemRegistrationSession.EndSession(); } catch { /* ignore */ }
                try { OemRegistrationSession.KillLegacySessionWatchProcesses(); } catch { /* ignore */ }
            }).ConfigureAwait(true);

            if (restoreHidHide || DependencyChecker.HasPendingHidHideSnapshot)
            {
                StatusText.Text = "Restoring HidHide (brief)… then exit.";
                var (ok, msg) = await Task.Run(() =>
                        DependencyChecker.EndHidHideSession(timeoutMs: DependencyChecker.HidHideRestoreExitTimeoutMs))
                    .ConfigureAwait(true);
                StatusText.Text = ok
                    ? "HidHide restored. Finishing shutdown…"
                    : msg + " Finishing shutdown…";
            }
            else
            {
                StatusText.Text = "Shutting down…";
            }

            try { DependencyChecker.KillOrphanHidHideHelpers(); } catch { /* ignore */ }

            await Task.Run(() =>
            {
                try { _bridge.Dispose(); } catch { /* ignore */ }
                try { _virtual.Dispose(); } catch { /* ignore */ }
            }).ConfigureAwait(true);

            try { DependencyChecker.KillOrphanHidHideHelpers(); } catch { /* ignore */ }
        }
        catch
        {
            try { DependencyChecker.KillOrphanHidHideHelpers(); } catch { /* ignore */ }
        }
        finally
        {
            InstallFolderGuard.LeaveInstallFolder();
            try { DependencyChecker.KillOrphanHidHideHelpers(); } catch { /* ignore */ }
            _exitAllowed = true;
            // Hard exit so abandoned native teardown cannot keep the EXE locking the folder.
            Environment.Exit(0);
        }
    }

    private DependencyReport ProbeDependencies() =>
        DependencyChecker.CheckAll(() =>
        {
            var ok = WinUHidNative.TryProbeDriver(out var msg);
            return (ok, msg);
        });

    private void MainTab_Checked(object sender, RoutedEventArgs e)
    {
        if (InputPanel is null || FfbPanel is null || TelemetryPanel is null) return;
        InputPanel.Visibility = InputTabRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FfbPanel.Visibility = FfbTabRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TelemetryPanel.Visibility = TelemetryTabRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDependencyUi()
    {
        var report = ProbeDependencies();
        var ts = report.TestSigning;
        var win = report.WinUHid;
        var hide = report.HidHide;
        var winReady = win?.IsInstalled == true;

        if (ts is not null)
        {
            TestSigningChip.Background = ts.StatusLabel.Equals("Off (OK)", StringComparison.OrdinalIgnoreCase) ? OkChipBrush
                : ts.StatusLabel.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
                    ? (winReady ? SoftChipBrush : OkChipBrush)
                : ts.StatusLabel.Equals("Reboot required", StringComparison.OrdinalIgnoreCase) ? SoftChipBrush
                : BadChipBrush;
            TestSigningChipText.Text = ts.StatusLabel.Equals("Off (OK)", StringComparison.OrdinalIgnoreCase)
                ? "Test signing · Off (OK)"
                : ts.StatusLabel.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
                    ? (winReady ? "Test signing · On (disable for Forza)" : "Test signing · On")
                : ts.StatusLabel.Equals("Reboot required", StringComparison.OrdinalIgnoreCase)
                    ? "Test signing · Reboot required"
                    : "Test signing · Off (for install)";
        }

        if (win is not null)
        {
            WinUHidChip.Background = winReady ? OkChipBrush
                : win.StatusLabel.Equals("Reboot required", StringComparison.OrdinalIgnoreCase) ? SoftChipBrush
                : BadChipBrush;
            WinUHidChipText.Text = winReady
                ? "WinUHid · Installed"
                : $"WinUHid · {win.StatusLabel} (required)";
        }

        if (hide is not null)
        {
            HidHideChip.Background = hide.IsInstalled ? OkChipBrush : BadChipBrush;
            HidHideChipText.Text = hide.IsInstalled ? "HidHide · Installed" : "HidHide · Missing (required)";
        }

        DependenciesBanner.Background = report.ReadyForGames ? OkBannerBrush : WarnBannerBrush;
        DependenciesBanner.BorderBrush = report.ReadyForGames ? OkBorderBrush : WarnBorderBrush;
        DependenciesBanner.Visibility = report.ReadyForGames ? Visibility.Collapsed : Visibility.Visible;
        DependenciesActionButton.Content = "Fix";
        DependenciesActionButton.ToolTip = "Fix missing dependencies…";

        if (report.ReadyForGames)
        {
            if (!_bridgeBusy && !_exitTeardownStarted && StatusText.Text.StartsWith("Dependencies", StringComparison.Ordinal))
                StatusText.Text = "Dependencies OK. Map controls, then Start bridge.";
        }
        else
        {
            var missing = report.MissingRequiredNames;
            StatusText.Text = $"{string.Join(" + ", missing)} missing — open Dependencies…";
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        OpenSettingsMenu(fromTray: false);

    private void OpenSettingsMenu(bool fromTray)
    {
        if (SettingsMenu is null) return;
        ApplyAppSettings(_profiles.LoadSettings());
        var restore = fromTray ? Visibility.Visible : Visibility.Collapsed;
        if (RestoreMenuItem is not null)
            RestoreMenuItem.Visibility = restore;
        if (RestoreMenuSeparator is not null)
            RestoreMenuSeparator.Visibility = restore;

        SettingsMenu.IsOpen = false;
        if (fromTray)
        {
            SettingsMenu.PlacementTarget = this;
            SettingsMenu.Placement = PlacementMode.MousePoint;
            SettingsMenu.HorizontalOffset = 0;
            SettingsMenu.VerticalOffset = 0;
        }
        else
        {
            SettingsMenu.PlacementTarget = SettingsButton;
            SettingsMenu.Placement = PlacementMode.Bottom;
            SettingsMenu.HorizontalOffset = 0;
            SettingsMenu.VerticalOffset = 0;
        }

        SettingsMenu.IsOpen = true;
    }

    private void RestoreFromTrayMenu_Click(object sender, RoutedEventArgs e) =>
        RestoreFromTray(keepMinimized: false);

    private void ExitApp_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyAppSettings(AppSettings settings)
    {
        _minimizeToTray = settings.MinimizeToSystemTray;
        if (MinimizeToTrayMenuItem is not null)
            MinimizeToTrayMenuItem.IsChecked = _minimizeToTray;
        if (DebugOverlayMenuItem is not null)
            DebugOverlayMenuItem.IsChecked = settings.DebugOverlay;
        if (TelemetryDebugOverlayMenuItem is not null)
            TelemetryDebugOverlayMenuItem.IsChecked = settings.TelemetryDebugOverlay;
        _effectChangesOverlayEnabled = settings.EffectChangesOverlay;
        if (EffectChangesOverlayMenuItem is not null)
            EffectChangesOverlayMenuItem.IsChecked = _effectChangesOverlayEnabled;
        if (CheckForUpdatesMenuItem is not null)
            CheckForUpdatesMenuItem.IsChecked = settings.CheckForUpdates;
        if (AutoApplyHidHideMenuItem is not null)
            AutoApplyHidHideMenuItem.IsChecked = settings.AutoApplyHidHideConfigOnStart;
        if (UnloadHidHideMenuItem is not null)
            UnloadHidHideMenuItem.IsChecked = settings.UnloadHidHideConfigWhenStopped;
        _telemetryUseMph = !string.Equals(settings.TelemetrySpeedUnit, "kmh", StringComparison.OrdinalIgnoreCase);
        if (TelemetryUnitKmhMenuItem is not null)
            TelemetryUnitKmhMenuItem.IsChecked = !_telemetryUseMph;
        ApplyTelemetrySettingsToUi(settings);
        RefreshTelemetryProfilesCombo(settings.LastTelemetryProfileName);
        PushTelemetryToBridge(settings);
        if (!_effectChangesOverlayEnabled)
            CloseEffectChangesOverlay();
        ApplyDebugOverlay(settings.DebugOverlay);
        ApplyTelemetryDebugOverlay(settings.TelemetryDebugOverlay);
    }

    private void TelemetryUnitKmhMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // WPF toggles IsChecked before Click — checked means metric UI.
        var useKmh = TelemetryUnitKmhMenuItem?.IsChecked == true;
        _telemetryUseMph = !useKmh;
        UpdateAppSettings(s => s.TelemetrySpeedUnit = useKmh ? "kmh" : "mph");
        StatusText.Text = useKmh
            ? "Telemetry unit: km/h / km/h/s."
            : "Telemetry unit: MPH / MPH/s (default).";
    }

    private void AutoApplyHidHideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var enabled = AutoApplyHidHideMenuItem?.IsChecked == true;
        UpdateAppSettings(s => s.AutoApplyHidHideConfigOnStart = enabled);
        StatusText.Text = enabled
            ? "Apply HidHide on Start — Start will whitelist this app and hide Gaming-list devices."
            : "Apply HidHide on Start off — Start will not change HidHide (manual).";
    }

    private void UnloadHidHideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var enabled = UnloadHidHideMenuItem?.IsChecked == true;
        UpdateAppSettings(s => s.UnloadHidHideConfigWhenStopped = enabled);
        StatusText.Text = enabled
            ? "Restore my HidHide on Stop — Start can save your setup and put it back on Stop."
            : "Restore my HidHide on Stop off — Stop leaves HidHide as Start left it.";
    }

    private void CheckForUpdatesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var enabled = CheckForUpdatesMenuItem?.IsChecked == true;
        UpdateAppSettings(s => s.CheckForUpdates = enabled);
        if (!enabled)
        {
            HideUpdateBanner();
            StatusText.Text = "GitHub update check off.";
            return;
        }

        StatusText.Text = "Checking GitHub for a newer release…";
        _ = CheckForGitHubUpdateAsync(force: true);
    }

    private async Task CheckForGitHubUpdateAsync(bool force)
    {
        var settings = _profiles.LoadSettings();
        if (!force && !settings.CheckForUpdates)
            return;

        GitHubReleaseInfo? latest;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_updateCheckCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            latest = await GitHubUpdateChecker.TryGetLatestAsync(AppVersion.Display, cts.Token).ConfigureAwait(true);
        }
        catch
        {
            latest = null;
        }

        if (!IsLoaded || _exitTeardownStarted)
            return;

        if (latest is null)
        {
            if (force)
                StatusText.Text = "Could not reach GitHub for updates.";
            return;
        }

        if (!latest.IsNewer)
        {
            HideUpdateBanner();
            if (force)
                StatusText.Text = $"You're on the latest release ({AppVersion.Display}).";
            return;
        }

        if (!force && string.Equals(settings.DismissedUpdateTag, latest.Tag, StringComparison.OrdinalIgnoreCase))
            return;

        _pendingRelease = latest;
        if (UpdateBannerText is not null)
            UpdateBannerText.Text = $"Update {latest.VersionLabel} is available — you have {AppVersion.Display}.";
        if (UpdateBanner is not null)
            UpdateBanner.Visibility = Visibility.Visible;
        if (force)
            StatusText.Text = $"Update {latest.VersionLabel} is on GitHub.";
    }

    private bool _updateBusy;

    private void UpdateLater_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy)
            return;
        var tag = _pendingRelease?.Tag;
        if (!string.IsNullOrWhiteSpace(tag))
            UpdateAppSettings(s => s.DismissedUpdateTag = tag);
        HideUpdateBanner();
    }

    private async void UpdateApply_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy)
            return;
        var release = _pendingRelease;
        if (release is null)
            return;

        if (string.IsNullOrWhiteSpace(release.ZipUrl))
        {
            OpenReleasePage(release.HtmlUrl);
            StatusText.Text = "This GitHub release has no zip asset — opened the release page.";
            return;
        }

        _updateBusy = true;
        SetUpdateButtonsEnabled(false);
        var progress = new Progress<double>(p =>
        {
            if (UpdateBannerText is not null)
                UpdateBannerText.Text = $"Downloading {release.VersionLabel}… {(int)(p * 100)}%";
        });

        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (!Directory.Exists(folder))
                folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Directory.CreateDirectory(folder);
            var zipPath = Path.Combine(folder, $"G920Emulator-{release.VersionLabel}-win-x64.zip");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_updateCheckCts.Token);
            cts.CancelAfter(TimeSpan.FromMinutes(10));
            await GitHubUpdateChecker.DownloadAsync(release.ZipUrl, zipPath, progress, cts.Token).ConfigureAwait(true);

            if (UpdateBannerText is not null)
                UpdateBannerText.Text = $"Downloaded {release.VersionLabel} — unzip over your G920 Emulator folder.";
            StatusText.Text = "Saved " + zipPath;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + zipPath + "\"",
                    UseShellExecute = true,
                });
            }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            if (UpdateBannerText is not null)
                UpdateBannerText.Text = $"Update {release.VersionLabel} is available — you have {AppVersion.Display}.";
            StatusText.Text = "Download failed: " + ex.Message;
            try { OpenReleasePage(release.HtmlUrl); } catch { /* ignore */ }
        }
        finally
        {
            _updateBusy = false;
            SetUpdateButtonsEnabled(true);
        }
    }

    private void SetUpdateButtonsEnabled(bool enabled)
    {
        if (UpdateApplyButton is not null)
            UpdateApplyButton.IsEnabled = enabled;
        if (UpdateLaterButton is not null)
            UpdateLaterButton.IsEnabled = enabled;
    }

    private static void OpenReleasePage(string? url)
    {
        url = string.IsNullOrWhiteSpace(url) ? GitHubUpdateChecker.ReleasesPageUrl : url;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void HideUpdateBanner()
    {
        _pendingRelease = null;
        if (UpdateBanner is not null)
            UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void EffectChangesOverlayMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var enabled = EffectChangesOverlayMenuItem?.IsChecked == true;
        UpdateAppSettings(s => s.EffectChangesOverlay = enabled);
        StatusText.Text = enabled
            ? "Effect Changes Overlay on — bind buttons flash the category, name, and value while you drive."
            : "Effect Changes Overlay off.";
    }

    private void ShowEffectChangeToast(string category, string effectName, string value)
    {
        if (!_effectChangesOverlayEnabled)
            return;
        try
        {
            _effectChangesOverlay ??= new EffectChangesOverlayWindow();
            _effectChangesOverlay.ShowChange(category, effectName, value);
        }
        catch { /* ignore HUD failures */ }
    }

    private void CloseEffectChangesOverlay()
    {
        var window = _effectChangesOverlay;
        _effectChangesOverlay = null;
        if (window is null)
            return;
        try
        {
            window.Dismiss();
            window.Close();
        }
        catch { /* ignore */ }
    }

    private void DebugOverlayMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var enabled = DebugOverlayMenuItem?.IsChecked == true;
        UpdateAppSettings(s => s.DebugOverlay = enabled);
        StatusText.Text = enabled
            ? "FFB Debug Overlay on — live inputs and FFB stay on top of the game."
            : "FFB Debug Overlay off.";
    }

    private void TelemetryDebugOverlayMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var enabled = TelemetryDebugOverlayMenuItem?.IsChecked == true;
        UpdateAppSettings(s => s.TelemetryDebugOverlay = enabled);
        StatusText.Text = enabled
            ? "Telemetry Debug Overlay on — live SimHub packet stays on top of the game."
            : "Telemetry Debug Overlay off.";
    }

    private void ApplyDebugOverlay(bool enabled)
    {
        if (enabled)
            ShowDebugOverlay();
        else
            CloseDebugOverlay(saveEnabled: false);
    }

    private void ApplyTelemetryDebugOverlay(bool enabled)
    {
        if (enabled)
            ShowTelemetryDebugOverlay();
        else
            CloseTelemetryDebugOverlay(saveEnabled: false);
    }

    private void ShowDebugOverlay()
    {
        if (_debugOverlay is { IsLoaded: true })
        {
            _debugOverlay.Topmost = true;
            _debugOverlay.Show();
            _lastFfbDiagUiTick = 0;
            RefreshFfbDiagnostics();
            return;
        }

        var settings = _profiles.LoadSettings();
        _debugOverlay = new DebugOverlayWindow();
        _debugOverlay.ClosedByUser += DebugOverlay_ClosedByUser;
        if (settings.DebugOverlayLeft is double left && settings.DebugOverlayTop is double top)
        {
            _debugOverlay.WindowStartupLocation = WindowStartupLocation.Manual;
            _debugOverlay.Left = left;
            _debugOverlay.Top = top;
        }
        else
        {
            _debugOverlay.WindowStartupLocation = WindowStartupLocation.Manual;
            _debugOverlay.Left = SystemParameters.WorkArea.Right - 456;
            _debugOverlay.Top = SystemParameters.WorkArea.Top + 16;
        }

        _debugOverlay.Show();
        _lastFfbDiagUiTick = 0;
        RefreshFfbDiagnostics();
        ApplyLive(_bridge.LatestState);
    }

    private void ShowTelemetryDebugOverlay()
    {
        if (_telemetryDebugOverlay is { IsLoaded: true })
        {
            _telemetryDebugOverlay.Topmost = true;
            _telemetryDebugOverlay.Show();
            ApplyTelemetryLive();
            return;
        }

        var settings = _profiles.LoadSettings();
        _telemetryDebugOverlay = new TelemetryDebugOverlayWindow();
        _telemetryDebugOverlay.ClosedByUser += TelemetryDebugOverlay_ClosedByUser;
        if (settings.TelemetryDebugOverlayLeft is double left && settings.TelemetryDebugOverlayTop is double top)
        {
            _telemetryDebugOverlay.WindowStartupLocation = WindowStartupLocation.Manual;
            _telemetryDebugOverlay.Left = left;
            _telemetryDebugOverlay.Top = top;
        }
        else
        {
            _telemetryDebugOverlay.WindowStartupLocation = WindowStartupLocation.Manual;
            _telemetryDebugOverlay.Left = SystemParameters.WorkArea.Left + 16;
            _telemetryDebugOverlay.Top = SystemParameters.WorkArea.Top + 16;
        }

        _telemetryDebugOverlay.Show();
        ApplyTelemetryLive();
    }

    private void CloseDebugOverlay(bool saveEnabled)
    {
        if (_debugOverlay is null) return;
        var window = _debugOverlay;
        _debugOverlay = null;
        window.ClosedByUser -= DebugOverlay_ClosedByUser;
        PersistOverlayBounds(window);
        try
        {
            if (window.IsVisible)
                window.Close();
        }
        catch { /* ignore */ }

        if (saveEnabled)
            UpdateAppSettings(s => s.DebugOverlay = false);
    }

    private void CloseTelemetryDebugOverlay(bool saveEnabled)
    {
        if (_telemetryDebugOverlay is null) return;
        var window = _telemetryDebugOverlay;
        _telemetryDebugOverlay = null;
        window.ClosedByUser -= TelemetryDebugOverlay_ClosedByUser;
        PersistTelemetryOverlayBounds(window);
        try
        {
            if (window.IsVisible)
                window.Close();
        }
        catch { /* ignore */ }

        if (saveEnabled)
            UpdateAppSettings(s => s.TelemetryDebugOverlay = false);
    }

    private void DebugOverlay_ClosedByUser()
    {
        if (_debugOverlay is not null)
            PersistOverlayBounds(_debugOverlay);
        _debugOverlay = null;
        UpdateAppSettings(s => s.DebugOverlay = false);
        if (StatusText is not null)
            StatusText.Text = "FFB Debug Overlay off.";
    }

    private void TelemetryDebugOverlay_ClosedByUser()
    {
        if (_telemetryDebugOverlay is not null)
            PersistTelemetryOverlayBounds(_telemetryDebugOverlay);
        _telemetryDebugOverlay = null;
        UpdateAppSettings(s => s.TelemetryDebugOverlay = false);
        if (StatusText is not null)
            StatusText.Text = "Telemetry Debug Overlay off.";
    }

    private void PersistOverlayBounds(DebugOverlayWindow window)
    {
        try
        {
            var settings = _profiles.LoadSettings();
            settings.DebugOverlayLeft = window.Left;
            settings.DebugOverlayTop = window.Top;
            _profiles.SaveSettings(settings);
        }
        catch { /* ignore */ }
    }

    private void PersistTelemetryOverlayBounds(TelemetryDebugOverlayWindow window)
    {
        try
        {
            var settings = _profiles.LoadSettings();
            settings.TelemetryDebugOverlayLeft = window.Left;
            settings.TelemetryDebugOverlayTop = window.Top;
            _profiles.SaveSettings(settings);
        }
        catch { /* ignore */ }
    }

    private AppSettings UpdateAppSettings(Action<AppSettings> mutate)
    {
        var settings = _profiles.LoadSettings();
        mutate(settings);
        _profiles.SaveSettings(settings);
        ApplyAppSettings(settings);
        return settings;
    }

    private void ApplyTelemetrySettingsToUi(AppSettings settings)
    {
        _telemetryUiBusy = true;
        try
        {
            settings.NormalizeTelemetryTuning();
            if (TelemetryEnabledCheck is not null)
                TelemetryEnabledCheck.IsChecked = settings.TelemetryEnabled;
            if (TelemetryHostBox is not null)
                TelemetryHostBox.Text = string.IsNullOrWhiteSpace(settings.TelemetryHost)
                    ? SimHubPacket.DefaultHost
                    : settings.TelemetryHost;
            if (TelemetryPortBox is not null)
                TelemetryPortBox.Text = (settings.TelemetryPort is < 1 or > 65535
                    ? SimHubPacket.DefaultPort
                    : settings.TelemetryPort).ToString();
            if (TelemetryHzSlider is not null)
                TelemetryHzSlider.Value = SimHubPacket.ClampSendHz(settings.TelemetrySendHz);
            if (TelemetryHzValueText is not null)
                TelemetryHzValueText.Text = $"{(int)(TelemetryHzSlider?.Value ?? SimHubPacket.DefaultSendHz)} Hz";

            // Tuning controls use the selected display unit; settings + SimHub stay km/h.
            ConfigureTelemetrySpeedControlRanges();
            if (TelemetrySpeedMaxSlider is not null)
                TelemetrySpeedMaxSlider.Value = FromKmh(settings.TelemetrySpeedMaxKmh);
            if (TelemetryRpmRange is not null)
            {
                TelemetryRpmRange.LowerValue = settings.TelemetryRpmMin;
                TelemetryRpmRange.UpperValue = settings.TelemetryRpmMax;
            }
            SyncRpmRedlineSliderRange(
                settings.TelemetryRpmMin,
                settings.TelemetryRpmMax,
                settings.TelemetryRpmRedline);
            if (TelemetryAccelSlider is not null)
                TelemetryAccelSlider.Value = FromKmh(settings.TelemetryAccelKmhPerSec);
            if (TelemetryBrakeDynSlider is not null)
                TelemetryBrakeDynSlider.Value = FromKmh(settings.TelemetryBrakeKmhPerSec);
            if (TelemetryCoastSlider is not null)
                TelemetryCoastSlider.Value = FromKmh(settings.TelemetryCoastKmhPerSec);
            if (TelemetryAeroDragSlider is not null)
                TelemetryAeroDragSlider.Value = settings.TelemetryAeroDragScale;
            if (TelemetryGearPullSlider is not null)
                TelemetryGearPullSlider.Value = settings.TelemetryGearPullScale;
            if (TelemetryGearSettleSlider is not null)
                TelemetryGearSettleSlider.Value = FromKmh(settings.TelemetryGearSettleKmhPerSec);
            SyncGearMaxSliderRanges(FromKmh(settings.TelemetrySpeedMaxKmh));
            if (TelemetryGear1MaxSlider is not null)
                TelemetryGear1MaxSlider.Value = FromKmh(settings.TelemetryGear1MaxKmh);
            if (TelemetryGear2MaxSlider is not null)
                TelemetryGear2MaxSlider.Value = FromKmh(settings.TelemetryGear2MaxKmh);
            if (TelemetryGear3MaxSlider is not null)
                TelemetryGear3MaxSlider.Value = FromKmh(settings.TelemetryGear3MaxKmh);
            if (TelemetryGear4MaxSlider is not null)
                TelemetryGear4MaxSlider.Value = FromKmh(settings.TelemetryGear4MaxKmh);
            if (TelemetryGear5MaxSlider is not null)
                TelemetryGear5MaxSlider.Value = FromKmh(settings.TelemetryGear5MaxKmh);
            if (TelemetryGear6MaxSlider is not null)
                TelemetryGear6MaxSlider.Value = FromKmh(settings.TelemetryGear6MaxKmh);
            if (TelemetryCrashDumpSlider is not null)
                TelemetryCrashDumpSlider.Value = settings.TelemetryCrashDumpScale;
            if (TelemetryRpmBounceAmountSlider is not null)
                TelemetryRpmBounceAmountSlider.Value = settings.TelemetryRpmBounceAmount;
            if (TelemetryRpmBounceHzSlider is not null)
                TelemetryRpmBounceHzSlider.Value = settings.TelemetryRpmBounceHz;
            if (TelemetryEngineVibrationScaleSlider is not null)
                TelemetryEngineVibrationScaleSlider.Value = settings.TelemetryEngineVibrationScale;
            if (TelemetryRumbleScaleSlider is not null)
                TelemetryRumbleScaleSlider.Value = settings.TelemetrySurfaceRumbleScale;
            if (TelemetryImpactScaleSlider is not null)
                TelemetryImpactScaleSlider.Value = settings.TelemetryImpactScale;
            if (TelemetryRoadLoadScaleSlider is not null)
                TelemetryRoadLoadScaleSlider.Value = settings.TelemetryRoadLoadScale;
            RefreshTelemetryTuningLabels();
            ApplyTelemetryLiveMeterRanges(settings);
        }
        finally
        {
            _telemetryUiBusy = false;
        }
    }

    private void PushTelemetryToBridge(AppSettings? settings = null)
    {
        settings ??= _profiles.LoadSettings();
        _bridge.ConfigureTelemetry(new TelemetrySettings
        {
            Enabled = settings.TelemetryEnabled,
            Host = settings.TelemetryHost,
            Port = settings.TelemetryPort,
            SendHz = settings.TelemetrySendHz,
            Tuning = settings.ToTelemetryTuning(),
        });
    }

    private void TelemetrySettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_telemetryUiBusy || !IsLoaded) return;
        SaveTelemetrySettingsFromUi();
    }

    private void TelemetryHzSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_telemetryUiBusy || !IsLoaded) return;
        if (TelemetryHzValueText is not null)
            TelemetryHzValueText.Text = $"{(int)e.NewValue} Hz";
        SaveTelemetrySettingsFromUi();
    }

    private void TelemetryTuningSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_telemetryUiBusy || !IsLoaded) return;
        if (ReferenceEquals(sender, TelemetrySpeedMaxSlider) && TelemetrySpeedMaxSlider is not null)
            SyncGearMaxSliderRanges(TelemetrySpeedMaxSlider.Value);
        RefreshTelemetryTuningLabels();
        SaveTelemetrySettingsFromUi();
    }

    private void TelemetryGearMaxSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_telemetryUiBusy || !IsLoaded) return;
        RefreshTelemetryTuningLabels();
        SaveTelemetrySettingsFromUi();
    }

    private void TelemetryRangeThumb_Changed(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_telemetryUiBusy || !IsLoaded) return;
        if (TelemetryRpmRange is not null)
        {
            SyncRpmRedlineSliderRange(
                TelemetryRpmRange.LowerValue,
                TelemetryRpmRange.UpperValue,
                TelemetryRpmRedlineSlider?.Value ?? TelemetryRpmRange.UpperValue);
        }
        RefreshTelemetryTuningLabels();
    }

    private void TelemetryRange_Changed(object? sender, EventArgs e)
    {
        if (_telemetryUiBusy || !IsLoaded) return;
        if (TelemetryRpmRange is not null)
        {
            SyncRpmRedlineSliderRange(
                TelemetryRpmRange.LowerValue,
                TelemetryRpmRange.UpperValue,
                TelemetryRpmRedlineSlider?.Value ?? TelemetryRpmRange.UpperValue);
        }
        RefreshTelemetryTuningLabels();
        SaveTelemetrySettingsFromUi();
    }

    private void SyncRpmRedlineSliderRange(double idleRpm, double maxRpm, double redlineRpm)
    {
        if (TelemetryRpmRedlineSlider is null)
            return;

        var min = Math.Max(100, idleRpm);
        var max = Math.Max(min + 50, maxRpm);
        TelemetryRpmRedlineSlider.Minimum = min;
        TelemetryRpmRedlineSlider.Maximum = max;
        var value = redlineRpm <= 0 ? max : redlineRpm;
        TelemetryRpmRedlineSlider.Value = Math.Clamp(value, min, max);
    }

    private void ConfigureTelemetrySpeedControlRanges()
    {
        if (TelemetrySpeedMaxSlider is not null)
        {
            TelemetrySpeedMaxSlider.Minimum = FromKmh(20);
            TelemetrySpeedMaxSlider.Maximum = AbsoluteSpeedMaxUi;
            TelemetrySpeedMaxSlider.TickFrequency = _telemetryUseMph ? 1 : 5;
        }

        if (TelemetryAccelSlider is not null)
        {
            TelemetryAccelSlider.Minimum = FromKmh(5);
            TelemetryAccelSlider.Maximum = FromKmh(200);
        }

        if (TelemetryBrakeDynSlider is not null)
        {
            TelemetryBrakeDynSlider.Minimum = FromKmh(10);
            TelemetryBrakeDynSlider.Maximum = FromKmh(300);
        }

        if (TelemetryCoastSlider is not null)
        {
            TelemetryCoastSlider.Minimum = 0;
            TelemetryCoastSlider.Maximum = FromKmh(120);
        }

        if (TelemetryGearSettleSlider is not null)
        {
            TelemetryGearSettleSlider.Minimum = 0;
            TelemetryGearSettleSlider.Maximum = FromKmh(200);
        }

        if (TelemetrySpeedMaxTitle is not null)
            TelemetrySpeedMaxTitle.Text = _telemetryUseMph ? "Max speed (MPH)" : "Max speed (km/h)";
        if (TelemetryDynamicsTitle is not null)
            TelemetryDynamicsTitle.Text = _telemetryUseMph ? "Speed dynamics (MPH/s)" : "Speed dynamics (km/h/s)";
        if (TelemetryGearMaxTitle is not null)
            TelemetryGearMaxTitle.Text = _telemetryUseMph ? "Gear caps (MPH)" : "Gear caps (km/h)";
    }

    private void SyncGearMaxSliderRanges(double speedMaxUi)
    {
        var max = Math.Clamp(speedMaxUi, FromKmh(20), AbsoluteSpeedMaxUi);
        foreach (var slider in new[]
                 {
                     TelemetryGear1MaxSlider, TelemetryGear2MaxSlider, TelemetryGear3MaxSlider,
                     TelemetryGear4MaxSlider, TelemetryGear5MaxSlider, TelemetryGear6MaxSlider,
                 })
        {
            if (slider is null) continue;
            slider.Minimum = FromKmh(5);
            slider.Maximum = max;
            if (slider.Value > max)
                slider.Value = max;
        }
    }

    private const double MphPerKmh = 0.621371192237;

    private double AbsoluteSpeedMaxUi =>
        _telemetryUseMph ? TelemetryTuning.AbsoluteSpeedMaxKmh * MphPerKmh : TelemetryTuning.AbsoluteSpeedMaxKmh;

    private static double ToMph(double kmh) => kmh * MphPerKmh;
    private static double ToKmh(double mph) => mph / MphPerKmh;

    private double FromKmh(double kmh) => _telemetryUseMph ? ToMph(kmh) : kmh;
    private double ToStoredKmh(double ui) => _telemetryUseMph ? ToKmh(ui) : ui;

    private string FormatSpeedUi(double uiValue) =>
        _telemetryUseMph ? $"{uiValue:0} MPH" : $"{uiValue:0} km/h";

    private string FormatSpeedFromKmh(double kmh) => FormatSpeedUi(FromKmh(kmh));

    private string FormatRateUi(double uiValue) =>
        _telemetryUseMph ? $"{uiValue:0} MPH/s" : $"{uiValue:0} km/h/s";

    private void RefreshTelemetryTuningLabels()
    {
        if (TelemetrySpeedMaxValueText is not null && TelemetrySpeedMaxSlider is not null)
            TelemetrySpeedMaxValueText.Text = FormatSpeedUi(TelemetrySpeedMaxSlider.Value);
        if (TelemetryRpmMinValueText is not null && TelemetryRpmRange is not null)
            TelemetryRpmMinValueText.Text = $"{TelemetryRpmRange.LowerValue:0}";
        if (TelemetryRpmMaxValueText is not null && TelemetryRpmRange is not null)
            TelemetryRpmMaxValueText.Text = $"{TelemetryRpmRange.UpperValue:0}";
        if (TelemetryRpmRedlineValueText is not null && TelemetryRpmRedlineSlider is not null)
            TelemetryRpmRedlineValueText.Text = $"{TelemetryRpmRedlineSlider.Value:0}";
        if (TelemetryAccelValueText is not null && TelemetryAccelSlider is not null)
            TelemetryAccelValueText.Text = FormatRateUi(TelemetryAccelSlider.Value);
        if (TelemetryBrakeDynValueText is not null && TelemetryBrakeDynSlider is not null)
            TelemetryBrakeDynValueText.Text = FormatRateUi(TelemetryBrakeDynSlider.Value);
        if (TelemetryCoastValueText is not null && TelemetryCoastSlider is not null)
            TelemetryCoastValueText.Text = FormatRateUi(TelemetryCoastSlider.Value);
        if (TelemetryAeroDragValueText is not null && TelemetryAeroDragSlider is not null)
            TelemetryAeroDragValueText.Text = $"{TelemetryAeroDragSlider.Value:P0}";
        if (TelemetryGearPullValueText is not null && TelemetryGearPullSlider is not null)
            TelemetryGearPullValueText.Text = $"{TelemetryGearPullSlider.Value:P0}";
        if (TelemetryGearSettleValueText is not null && TelemetryGearSettleSlider is not null)
            TelemetryGearSettleValueText.Text = FormatRateUi(TelemetryGearSettleSlider.Value);
        if (TelemetryGear1MaxValueText is not null && TelemetryGear1MaxSlider is not null)
            TelemetryGear1MaxValueText.Text = FormatSpeedUi(TelemetryGear1MaxSlider.Value);
        if (TelemetryGear2MaxValueText is not null && TelemetryGear2MaxSlider is not null)
            TelemetryGear2MaxValueText.Text = FormatSpeedUi(TelemetryGear2MaxSlider.Value);
        if (TelemetryGear3MaxValueText is not null && TelemetryGear3MaxSlider is not null)
            TelemetryGear3MaxValueText.Text = FormatSpeedUi(TelemetryGear3MaxSlider.Value);
        if (TelemetryGear4MaxValueText is not null && TelemetryGear4MaxSlider is not null)
            TelemetryGear4MaxValueText.Text = FormatSpeedUi(TelemetryGear4MaxSlider.Value);
        if (TelemetryGear5MaxValueText is not null && TelemetryGear5MaxSlider is not null)
            TelemetryGear5MaxValueText.Text = FormatSpeedUi(TelemetryGear5MaxSlider.Value);
        if (TelemetryGear6MaxValueText is not null && TelemetryGear6MaxSlider is not null)
            TelemetryGear6MaxValueText.Text = FormatSpeedUi(TelemetryGear6MaxSlider.Value);
        if (TelemetryCrashDumpValueText is not null && TelemetryCrashDumpSlider is not null)
            TelemetryCrashDumpValueText.Text = $"{TelemetryCrashDumpSlider.Value:P0}";
        if (TelemetryRpmBounceAmountValueText is not null && TelemetryRpmBounceAmountSlider is not null)
            TelemetryRpmBounceAmountValueText.Text = $"{TelemetryRpmBounceAmountSlider.Value:P0}";
        if (TelemetryRpmBounceHzValueText is not null && TelemetryRpmBounceHzSlider is not null)
            TelemetryRpmBounceHzValueText.Text = $"{(int)TelemetryRpmBounceHzSlider.Value} Hz";
        if (TelemetryEngineVibrationScaleValueText is not null && TelemetryEngineVibrationScaleSlider is not null)
            TelemetryEngineVibrationScaleValueText.Text = $"{TelemetryEngineVibrationScaleSlider.Value:P0}";
        if (TelemetryRumbleScaleValueText is not null && TelemetryRumbleScaleSlider is not null)
            TelemetryRumbleScaleValueText.Text = $"{TelemetryRumbleScaleSlider.Value:P0}";
        if (TelemetryImpactScaleValueText is not null && TelemetryImpactScaleSlider is not null)
            TelemetryImpactScaleValueText.Text = $"{TelemetryImpactScaleSlider.Value:P0}";
        if (TelemetryRoadLoadScaleValueText is not null && TelemetryRoadLoadScaleSlider is not null)
            TelemetryRoadLoadScaleValueText.Text = $"{TelemetryRoadLoadScaleSlider.Value:P0}";
    }

    private void ApplyTelemetryLiveMeterRanges(AppSettings settings)
    {
        if (TelemetrySpeedBar is not null)
            TelemetrySpeedBar.Maximum = Math.Max(20, settings.TelemetrySpeedMaxKmh);
        if (TelemetryRpmBar is not null)
            TelemetryRpmBar.Maximum = Math.Max(1000, settings.TelemetryRpmMax);
    }

    private void SaveTelemetrySettingsFromUi()
    {
        if (TelemetryEnabledCheck is null || TelemetryHostBox is null || TelemetryPortBox is null)
            return;
        if (!int.TryParse(TelemetryPortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
            port = SimHubPacket.DefaultPort;
        var hz = TelemetryHzSlider is null
            ? SimHubPacket.DefaultSendHz
            : SimHubPacket.ClampSendHz((int)TelemetryHzSlider.Value);
        UpdateAppSettings(s =>
        {
            s.TelemetryEnabled = TelemetryEnabledCheck.IsChecked == true;
            s.TelemetryHost = string.IsNullOrWhiteSpace(TelemetryHostBox.Text)
                ? SimHubPacket.DefaultHost
                : TelemetryHostBox.Text.Trim();
            s.TelemetryPort = port;
            s.TelemetrySendHz = hz;
            if (TelemetrySpeedMaxSlider is not null)
            {
                s.TelemetrySpeedMinKmh = 0f;
                s.TelemetrySpeedMaxKmh = (float)ToStoredKmh(TelemetrySpeedMaxSlider.Value);
            }
            if (TelemetryRpmRange is not null)
            {
                s.TelemetryRpmMin = (float)TelemetryRpmRange.LowerValue;
                s.TelemetryRpmMax = (float)TelemetryRpmRange.UpperValue;
            }
            if (TelemetryRpmRedlineSlider is not null)
                s.TelemetryRpmRedline = (float)TelemetryRpmRedlineSlider.Value;
            if (TelemetryAccelSlider is not null)
                s.TelemetryAccelKmhPerSec = (float)ToStoredKmh(TelemetryAccelSlider.Value);
            if (TelemetryBrakeDynSlider is not null)
                s.TelemetryBrakeKmhPerSec = (float)ToStoredKmh(TelemetryBrakeDynSlider.Value);
            if (TelemetryCoastSlider is not null)
                s.TelemetryCoastKmhPerSec = (float)ToStoredKmh(TelemetryCoastSlider.Value);
            if (TelemetryAeroDragSlider is not null)
                s.TelemetryAeroDragScale = (float)TelemetryAeroDragSlider.Value;
            if (TelemetryGearPullSlider is not null)
                s.TelemetryGearPullScale = (float)TelemetryGearPullSlider.Value;
            if (TelemetryGearSettleSlider is not null)
                s.TelemetryGearSettleKmhPerSec = (float)ToStoredKmh(TelemetryGearSettleSlider.Value);
            if (TelemetryGear1MaxSlider is not null)
                s.TelemetryGear1MaxKmh = (float)ToStoredKmh(TelemetryGear1MaxSlider.Value);
            if (TelemetryGear2MaxSlider is not null)
                s.TelemetryGear2MaxKmh = (float)ToStoredKmh(TelemetryGear2MaxSlider.Value);
            if (TelemetryGear3MaxSlider is not null)
                s.TelemetryGear3MaxKmh = (float)ToStoredKmh(TelemetryGear3MaxSlider.Value);
            if (TelemetryGear4MaxSlider is not null)
                s.TelemetryGear4MaxKmh = (float)ToStoredKmh(TelemetryGear4MaxSlider.Value);
            if (TelemetryGear5MaxSlider is not null)
                s.TelemetryGear5MaxKmh = (float)ToStoredKmh(TelemetryGear5MaxSlider.Value);
            if (TelemetryGear6MaxSlider is not null)
                s.TelemetryGear6MaxKmh = (float)ToStoredKmh(TelemetryGear6MaxSlider.Value);
            if (TelemetryCrashDumpSlider is not null)
                s.TelemetryCrashDumpScale = (float)TelemetryCrashDumpSlider.Value;
            if (TelemetryRpmBounceAmountSlider is not null)
                s.TelemetryRpmBounceAmount = (float)TelemetryRpmBounceAmountSlider.Value;
            if (TelemetryRpmBounceHzSlider is not null)
                s.TelemetryRpmBounceHz = (float)TelemetryRpmBounceHzSlider.Value;
            if (TelemetryEngineVibrationScaleSlider is not null)
                s.TelemetryEngineVibrationScale = (float)TelemetryEngineVibrationScaleSlider.Value;
            if (TelemetryRumbleScaleSlider is not null)
                s.TelemetrySurfaceRumbleScale = (float)TelemetryRumbleScaleSlider.Value;
            if (TelemetryImpactScaleSlider is not null)
                s.TelemetryImpactScale = (float)TelemetryImpactScaleSlider.Value;
            if (TelemetryRoadLoadScaleSlider is not null)
                s.TelemetryRoadLoadScale = (float)TelemetryRoadLoadScaleSlider.Value;
        });
    }

    private void TelemetryRegister_Click(object sender, RoutedEventArgs e)
    {
        SaveTelemetrySettingsFromUi();
        var (ok, message) = SimHubRegistration.Register();
        StatusText.Text = ok
            ? "SimHub definition + RPM plugin registered. Activate G920 Emulator (estimated) in SimHub (9.11.5+), then restart SimHub."
            : "SimHub register failed: " + message;
        if (TelemetryStatusText is not null)
            TelemetryStatusText.Text = ok ? message : "Register failed: " + message;
    }

    private void TelemetryUnregister_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = SimHubRegistration.Unregister();
        StatusText.Text = ok
            ? "SimHub registration removed. Restart SimHub, then Register with SimHub to refresh the icon."
            : "SimHub unregister failed: " + message;
        if (TelemetryStatusText is not null)
            TelemetryStatusText.Text = ok ? message : "Unregister failed: " + message;
    }

    private void ApplyTelemetryLive()
    {
        if (TelemetryStatusText is null)
            return;
        TelemetryStatusText.Text = _bridge.TelemetryStatus;
        var t = _bridge.LatestTelemetry;
        if (TelemetryGearText is not null)
            TelemetryGearText.Text = "Gear " + (string.IsNullOrEmpty(t.Gear) ? "N" : t.Gear);
        if (TelemetrySteerText is not null)
            TelemetrySteerText.Text = $"Steering: {t.Steering:+0.00;-0.00;0.00}";
        if (TelemetrySteerBar is not null)
            TelemetrySteerBar.Value = t.Steering;
        if (TelemetryThrottleText is not null)
            TelemetryThrottleText.Text = $"Throttle: {t.Throttle * 100:0}%";
        if (TelemetryThrottleBar is not null)
            TelemetryThrottleBar.Value = t.Throttle;
        if (TelemetryBrakeText is not null)
            TelemetryBrakeText.Text = $"Brake: {t.Brake * 100:0}%";
        if (TelemetryBrakeBar is not null)
            TelemetryBrakeBar.Value = t.Brake;
        if (TelemetryClutchText is not null)
            TelemetryClutchText.Text = $"Clutch: {t.Clutch * 100:0}%";
        if (TelemetryClutchBar is not null)
            TelemetryClutchBar.Value = t.Clutch;
        if (TelemetrySpeedText is not null)
            TelemetrySpeedText.Text = $"Speed: {FormatSpeedFromKmh(t.SpeedKmh)} (estimated)";
        if (TelemetrySpeedBar is not null)
            TelemetrySpeedBar.Value = t.SpeedKmh;
        if (TelemetryRpmText is not null)
            TelemetryRpmText.Text = $"RPM: {t.EngineRpm:0} (estimated)";
        if (TelemetryRpmBar is not null)
            TelemetryRpmBar.Value = t.EngineRpm;
        if (TelemetryEngineVibText is not null)
            TelemetryEngineVibText.Text = $"Engine vibration: {t.EngineVibration * 100:0}%";
        if (TelemetryEngineVibBar is not null)
            TelemetryEngineVibBar.Value = t.EngineVibration;
        const float g = 9.80665f;
        var surgeG = t.LocalSurgeMs2 / g;
        var swayG = t.LocalSwayMs2 / g;
        var heaveG = t.LocalHeaveMs2 / g;
        UpdateTelemetryGForceCircle(surgeG, swayG);
        if (TelemetryGForceText is not null)
        {
            var totalXy = MathF.Sqrt(surgeG * surgeG + swayG * swayG);
            TelemetryGForceText.Text =
                $"{totalXy:0.00} g  ·  surge {surgeG:+0.00;-0.00;0.00}  sway {swayG:+0.00;-0.00;0.00}  heave {heaveG:0.00}";
        }
        if (TelemetryRumbleText is not null)
            TelemetryRumbleText.Text = $"Surface rumble: {t.SurfaceRumble * 100:0}%";
        if (TelemetryRumbleBar is not null)
            TelemetryRumbleBar.Value = t.SurfaceRumble;
        if (TelemetryImpactText is not null)
            TelemetryImpactText.Text = $"Impact: {t.Impact * 100:0}%";
        if (TelemetryImpactBar is not null)
            TelemetryImpactBar.Value = t.Impact;
        if (TelemetryLoadText is not null)
            TelemetryLoadText.Text = $"Road load: {t.RoadLoad * 100:0}%";
        if (TelemetryLoadBar is not null)
            TelemetryLoadBar.Value = t.RoadLoad;

        if (_telemetryDebugOverlay is { IsVisible: true })
        {
            var speedMax = TelemetrySpeedBar is not null ? (float)TelemetrySpeedBar.Maximum : 350f;
            var rpmMax = TelemetryRpmBar is not null ? (float)TelemetryRpmBar.Maximum : 8000f;
            _telemetryDebugOverlay.Update(
                t,
                _bridge.TelemetryStatus,
                FormatSpeedFromKmh(t.SpeedKmh) + " (estimated)",
                speedMax,
                rpmMax);
        }
    }

    /// <summary>
    /// Vehicle-frame G-G plot (matches SimHub LocalSurge/LocalSway signs).
    /// Accel (+surge) → up; brake (−surge) → down; left turn (+sway) → left.
    /// </summary>
    private void UpdateTelemetryGForceCircle(float surgeG, float swayG)
    {
        if (TelemetryGForceBall is null)
            return;

        const double maxG = 2.0;
        const double center = 84.0;
        const double radiusPx = 76.0; // outer ring inset
        const double ballR = 7.0;

        // Vehicle lateral: synthesizer +sway = left turn → ball to the left on the plot.
        var rightG = Math.Clamp((double)(-swayG), -maxG, maxG);
        // Vehicle longitudinal: +surge (accel) up, −surge (brake) down.
        var forwardG = Math.Clamp((double)surgeG, -maxG, maxG);

        var mag = Math.Sqrt(rightG * rightG + forwardG * forwardG);
        if (mag > maxG && mag > 1e-6)
        {
            rightG *= maxG / mag;
            forwardG *= maxG / mag;
        }

        var pxPerG = radiusPx / maxG;
        Canvas.SetLeft(TelemetryGForceBall, center + rightG * pxPerG - ballR);
        // Canvas Y grows downward; +forward (accel) is up on the plot.
        Canvas.SetTop(TelemetryGForceBall, center - forwardG * pxPerG - ballR);
    }

    private void MinimizeToTrayMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var enabled = MinimizeToTrayMenuItem?.IsChecked == true;
        UpdateAppSettings(s => s.MinimizeToSystemTray = enabled);
        if (enabled && WindowState == WindowState.Minimized)
            HideToTray();
        else if (!enabled)
            RestoreFromTray(keepMinimized: WindowState == WindowState.Minimized);
        StatusText.Text = enabled
            ? "Minimize to system tray on."
            : "Minimize to system tray off.";
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            if (_minimizeToTray)
                HideToTray();
            return;
        }

        _restoreWindowState = WindowState;
        if (_trayIcon?.Visible == true)
            RestoreFromTray(keepMinimized: false);
        else
            ShowInTaskbar = true;
    }

    private void HideToTray()
    {
        EnsureTrayIcon();
        if (_trayIcon is null) return;
        ShowInTaskbar = false;
        _trayIcon.Visible = true;
    }

    private void RestoreFromTray(bool keepMinimized)
    {
        ShowInTaskbar = true;
        if (_trayIcon is not null)
            _trayIcon.Visible = false;
        if (keepMinimized)
            return;
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _restoreWindowState == WindowState.Minimized
                ? WindowState.Normal
                : _restoreWindowState;
        Activate();
    }

    public void BringToForeground()
    {
        RestoreFromTray(keepMinimized: false);
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
        try
        {
            Topmost = true;
            Topmost = false;
        }
        catch { /* ignore */ }
    }

    private void TrayIcon_Clicked() => RestoreFromTray(keepMinimized: false);

    private void TrayIcon_RightClicked() => OpenSettingsMenu(fromTray: true);

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null) return;
        try
        {
            _trayIcon = new TrayIcon(this, "G920 Emulator v" + AppVersion.Display);
            _trayIcon.Clicked += TrayIcon_Clicked;
            _trayIcon.RightClicked += TrayIcon_RightClicked;
        }
        catch
        {
            _trayIcon = null;
        }
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null) return;
        try
        {
            _trayIcon.Clicked -= TrayIcon_Clicked;
            _trayIcon.RightClicked -= TrayIcon_RightClicked;
            _trayIcon.Dispose();
        }
        catch { /* ignore */ }
        _trayIcon = null;
    }

    private void DependenciesButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new DependenciesWindow { Owner = this };
        window.ShowDialog();
        UpdateDependencyUi();
    }

    private void RecheckDependencies_Click(object sender, RoutedEventArgs e)
    {
        UpdateDependencyUi();
        StatusText.Text = "Dependency check refreshed.";
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new AboutWindow { Owner = this };
        window.ShowDialog();
    }

    private void DebugSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_debugSession.IsActive)
        {
            _debugSession.Stop();
            VirtualG920Device.LogHostIngress = false;
            StatusText.Text = "Debug stopped — Export log is ready.";
        }
        else
        {
            _debugSession.Start();
            VirtualG920Device.LogHostIngress = true;
            StatusText.Text = "Debug logging on — reproduce the issue, then Stop and Export log.";
        }

        RefreshDebugSessionUi();
    }

    private void ExportDebugLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_debugSession.ExportReady)
        {
            MessageBox.Show(this,
                "Start Debug, reproduce the issue, then Stop Debug before exporting.",
                "Export log",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var versionLabel = AppVersion.Display;

        var dialog = new SaveFileDialog
        {
            Title = "Export diagnostics log",
            FileName = $"G920Emulator-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "Zip archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            DiagnosticsLiveSnapshot? live = null;
            try { live = CaptureDiagnosticsLiveSnapshot(); }
            catch { /* best-effort */ }

            var path = DiagnosticsExporter.ExportToZip(
                dialog.FileName,
                versionLabel,
                () =>
                {
                    var ok = WinUHidNative.TryProbeDriver(out var msg);
                    return (ok, msg ?? "");
                },
                live);

            var open = MessageBox.Show(
                this,
                "Diagnostics zip saved:\n\n" + path +
                "\n\nIncludes devices, HidHide, live FFB/OEM snapshot, and OEM effect logs from the debug session.\n\n" +
                "Open a GitHub issue and attach the zip:\n" +
                DiagnosticsExporter.GitHubIssuesUrl +
                "\n\nOpen the Issues page now?",
                "Export log",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (open == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(DiagnosticsExporter.GitHubIssuesUrl) { UseShellExecute = true });
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }

            StatusText.Text = "Diagnostics exported: " + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export log", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshDebugSessionUi()
    {
        if (DebugSessionButton is not null)
            DebugSessionButton.Content = _debugSession.IsActive ? "Stop debug" : "Debug";
        if (ExportDebugLogButton is not null)
            ExportDebugLogButton.IsEnabled = _debugSession.ExportReady && !_debugSession.IsActive;
    }

    private DiagnosticsLiveSnapshot CaptureDiagnosticsLiveSnapshot()
    {
        var ffb = _bridge.GetFfbDiagnostics();
        var ffbName = ffb.DeviceName;
        if (string.IsNullOrWhiteSpace(ffbName) && !string.IsNullOrWhiteSpace(_profile.FfbSourceDeviceId))
        {
            ffbName = _devices.FirstOrDefault(d =>
                string.Equals(d.Id, _profile.FfbSourceDeviceId, StringComparison.OrdinalIgnoreCase))?.Info.Name;
        }

        return new DiagnosticsLiveSnapshot
        {
            BridgeRunning = _bridge.IsRunning,
            LinkStatus = _bridge.LinkStatus,
            VirtualDeviceError = _virtual.LastError,
            VirtualPreviewMode = _virtual.IsPreviewMode,
            VirtualCol01Present = G920DeviceIdentityFix.IsVirtualCol01Present(),
            HostPathHint = ffb.HostPathHint,
            ActiveInputProfile = _profile.Name,
            ActiveFfbProfile = _profile.FfbProfileName,
            FfbSourceDeviceId = _profile.FfbSourceDeviceId ?? ffb.DeviceId,
            FfbSourceDeviceName = ffbName,
            MasterGain = _profile.FfbGain,
            FfbInvert = _profile.FfbInvert,
            EffectGains = _profile.FfbEffectGains,
            OutputFeel = _profile.FfbOutputFeel,
            Ffb = ffb,
            TelemetryStatus = _bridge.TelemetryStatus,
            Telemetry = _bridge.LatestTelemetry,
        };
    }

    private void AuthorLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void RefreshDevices(bool restoreHidden = false) =>
        _ = RefreshDevicesAsync(restoreHidden);

    private async Task RefreshDevicesAsync(bool restoreHidden = false)
    {
        if (_refreshDevicesBusy)
            return;
        _refreshDevicesBusy = true;
        try
        {
            if (restoreHidden)
                ClearHiddenDevices();

            var hidden = GetHiddenDeviceIds();
            // DI enumeration can stall the UI when alt-tabbing; always do it off-thread.
            var list = await Task.Run(() =>
                    _bridge.RefreshDevices().Where(d => !hidden.Contains(d.Id)).ToList())
                .ConfigureAwait(true);

            var selectedFfb = (FfbDeviceCombo.SelectedItem as DeviceRow)?.Id;
            _devices.Clear();
            foreach (var d in list)
                _devices.Add(new DeviceRow(d));

            var remapped = DeviceBindingResolver.RemapProfile(_profile, list);
            _bridge.Profile = _profile;

            if (selectedFfb is not null)
                FfbDeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.Id == selectedFfb);
            else if (!string.IsNullOrEmpty(_profile.FfbSourceDeviceId))
                FfbDeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.Id == _profile.FfbSourceDeviceId)
                    ?? _devices.FirstOrDefault(d => d.Info.SupportsForceFeedback);
            else
                FfbDeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.Info.SupportsForceFeedback);

            RebuildBindingRows();
            var where = _profiles.UsesPortableStorage ? "portable" : "AppData";
            var baseStatus = restoreHidden || hidden.Count == 0
                ? $"Found {_devices.Count} device(s). Profiles ({where}): {_profiles.ProfilesDirectory}"
                : $"Found {_devices.Count} device(s) ({hidden.Count} hidden). Profiles ({where}): {_profiles.ProfilesDirectory}";
            StatusText.Text = remapped > 0
                ? $"{baseStatus} · remapped {remapped} binding(s) to current devices"
                : baseStatus;
        }
        finally
        {
            _refreshDevicesBusy = false;
        }
    }

    private HashSet<string> GetHiddenDeviceIds()
    {
        var settings = _profiles.LoadSettings();
        return new HashSet<string>(
            settings.HiddenDeviceIds ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    private void ClearHiddenDevices()
    {
        var settings = _profiles.LoadSettings();
        if (settings.HiddenDeviceIds is null || settings.HiddenDeviceIds.Count == 0)
            return;
        UpdateAppSettings(s => s.HiddenDeviceIds = []);
    }

    private void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceRow row)
            return;

        e.Handled = true;

        UpdateAppSettings(s =>
        {
            s.HiddenDeviceIds ??= [];
            if (!s.HiddenDeviceIds.Contains(row.Id, StringComparer.OrdinalIgnoreCase))
                s.HiddenDeviceIds.Add(row.Id);
        });

        var wasFfb = FfbDeviceCombo.SelectedItem is DeviceRow ffb && ffb.Id == row.Id;
        _devices.Remove(row);
        if (wasFfb)
        {
            FfbDeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.Info.SupportsForceFeedback);
            if (FfbDeviceCombo.SelectedItem is DeviceRow next)
                SetFfbSource(next);
            else
            {
                _profile.FfbSourceDeviceId = null;
                _profile.FfbSourceProductId = null;
            }
        }

        RebuildBindingRows();
        StatusText.Text = $"Removed '{row.Info.Name}' from the list. Refresh devices to show it again.";
    }

    private void LoadProfileIntoUi(MappingProfile profile)
    {
        _profile = profile;
        _profiles.ApplyLinkedFfbProfile(_profile);
        if (_devices.Count > 0)
            DeviceBindingResolver.RemapProfile(_profile, _devices.Select(d => d.Info).ToList());
        _bridge.Profile = _profile;
        GainSlider.Value = _profile.FfbGain;
        InvertFfbCheck.IsChecked = _profile.FfbInvert;
        LoadEffectGainsIntoUi(_profile.FfbEffectGains);
        LoadFeelIntoUi(_profile.FfbOutputFeel);
        RefreshFfbProfilesCombo(_profile.FfbProfileName);
        _profile.ShifterMode = ShifterMode.ExclusiveHPattern;
        if (!string.IsNullOrEmpty(_profile.FfbSourceDeviceId))
            FfbDeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.Id == _profile.FfbSourceDeviceId)
                ?? FfbDeviceCombo.SelectedItem;
        RebuildBindingRows();
        RefreshFfbBindButtons();
    }

    private void RefreshSavedProfilesCombo(string? selectName)
    {
        _suppressProfileCombo = true;
        try
        {
            var names = _profiles.ListProfiles();
            SavedProfilesCombo.ItemsSource = names;
            if (!string.IsNullOrWhiteSpace(selectName) && names.Contains(selectName, StringComparer.OrdinalIgnoreCase))
                SavedProfilesCombo.SelectedItem = names.First(n => n.Equals(selectName, StringComparison.OrdinalIgnoreCase));
            else if (names.Count > 0)
                SavedProfilesCombo.SelectedItem = names[0];
            else
                SavedProfilesCombo.SelectedItem = null;
        }
        finally
        {
            _suppressProfileCombo = false;
        }
    }

    private void RefreshFfbProfilesCombo(string? selectName)
    {
        _suppressFfbProfileCombo = true;
        try
        {
            var names = _profiles.ListFfbProfiles();
            FfbProfilesCombo.ItemsSource = names;
            if (!string.IsNullOrWhiteSpace(selectName) && names.Contains(selectName, StringComparer.OrdinalIgnoreCase))
                FfbProfilesCombo.SelectedItem = names.First(n => n.Equals(selectName, StringComparison.OrdinalIgnoreCase));
            else if (names.Count > 0)
                FfbProfilesCombo.SelectedItem = names.FirstOrDefault(n => n.Equals("Raw", StringComparison.OrdinalIgnoreCase))
                    ?? names[0];
            else
                FfbProfilesCombo.SelectedItem = null;
        }
        finally
        {
            _suppressFfbProfileCombo = false;
        }
    }

    private void RefreshTelemetryProfilesCombo(string? selectName)
    {
        if (TelemetryProfilesCombo is null)
            return;

        _suppressTelemetryProfileCombo = true;
        try
        {
            var names = _profiles.ListTelemetryProfiles();
            TelemetryProfilesCombo.ItemsSource = names;
            if (!string.IsNullOrWhiteSpace(selectName) &&
                names.Contains(selectName, StringComparer.OrdinalIgnoreCase))
            {
                TelemetryProfilesCombo.SelectedItem =
                    names.First(n => n.Equals(selectName, StringComparison.OrdinalIgnoreCase));
            }
            else if (names.Count > 0)
            {
                TelemetryProfilesCombo.SelectedItem =
                    names.FirstOrDefault(n =>
                        n.Equals(TelemetryProfile.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
                    ?? names[0];
            }
            else
            {
                TelemetryProfilesCombo.SelectedItem = null;
            }
        }
        finally
        {
            _suppressTelemetryProfileCombo = false;
        }
    }

    private void SaveCurrentTelemetryProfile(bool quiet)
    {
        SaveTelemetrySettingsFromUi();
        var settings = _profiles.LoadSettings();
        var profile = _profiles.CaptureTelemetryFromSettings(settings);
        var name = string.IsNullOrWhiteSpace(profile.Name)
            ? TelemetryProfile.DefaultProfileName
            : profile.Name;
        _profiles.SaveTelemetry(profile, name);
        RefreshTelemetryProfilesCombo(profile.Name);
        if (!quiet)
            StatusText.Text = $"Saved telemetry profile '{profile.Name}'";
    }

    private void SaveTelemetryProfileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentTelemetryProfile(quiet: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save telemetry profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveTelemetryProfileAsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveTelemetrySettingsFromUi();
            var settings = _profiles.LoadSettings();
            var suggested = string.IsNullOrWhiteSpace(settings.LastTelemetryProfileName)
                ? "My Telemetry"
                : settings.LastTelemetryProfileName.Trim();
            if (suggested.Equals(TelemetryProfile.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
                suggested = "My Telemetry";

            var name = PromptForName("Save telemetry profile as", suggested);
            if (name is null) return;

            if (name.Equals(TelemetryProfile.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Choose a different name — Default is the built-in estimation preset.",
                    "Save telemetry As");
                return;
            }

            if (_profiles.TelemetryExists(name))
            {
                var overwrite = MessageBox.Show(
                    $"Telemetry profile '{name}' already exists. Overwrite?",
                    "Save telemetry As",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (overwrite != MessageBoxResult.Yes)
                    return;
            }

            var profile = TelemetryProfile.FromTuning(name, settings.ToTelemetryTuning());
            _profiles.SaveTelemetry(profile, name);
            UpdateAppSettings(s => s.LastTelemetryProfileName = name);
            RefreshTelemetryProfilesCombo(name);
            StatusText.Text = $"Saved telemetry profile '{name}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save telemetry As", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DefaultTelemetryProfileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var defaults = TelemetryProfile.CreateDefault();
            var settings = UpdateAppSettings(s => s.ApplyTelemetryTuning(defaults.Tuning));
            var selected = TelemetryProfilesCombo?.SelectedItem as string
                           ?? settings.LastTelemetryProfileName
                           ?? TelemetryProfile.DefaultProfileName;
            StatusText.Text = $"Telemetry sliders reset to defaults — Save to write '{selected}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Default telemetry", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteTelemetryProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var name = TelemetryProfilesCombo?.SelectedItem as string
                   ?? _profiles.LoadSettings().LastTelemetryProfileName;
        if (string.IsNullOrWhiteSpace(name) || !_profiles.TelemetryExists(name))
        {
            MessageBox.Show("Select a saved telemetry profile to delete.", "Delete telemetry profile");
            return;
        }

        if (name.Equals(TelemetryProfile.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "Default cannot be deleted — it is the built-in estimation preset.",
                "Delete telemetry profile");
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete telemetry profile '{name}'?",
            "Delete telemetry profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        _profiles.DeleteTelemetry(name);
        var fallback = TelemetryProfile.DefaultProfileName;
        if (_profiles.TelemetryExists(fallback))
        {
            var profile = _profiles.LoadTelemetry(fallback);
            UpdateAppSettings(s => _profiles.ApplyTelemetryProfileToSettings(s, profile));
        }
        else
        {
            UpdateAppSettings(s =>
            {
                s.ApplyTelemetryTuning(TelemetryTuning.CreateDefault());
                s.LastTelemetryProfileName = fallback;
            });
        }

        StatusText.Text = $"Deleted telemetry '{name}' — switched to {fallback}";
    }

    private void TelemetryProfilesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTelemetryProfileCombo) return;
        if (TelemetryProfilesCombo?.SelectedItem is not string name) return;
        if (!_profiles.TelemetryExists(name)) return;

        try
        {
            var profile = _profiles.LoadTelemetry(name);
            UpdateAppSettings(s => _profiles.ApplyTelemetryProfileToSettings(s, profile));
            StatusText.Text = $"Loaded telemetry profile '{name}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Load telemetry profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveCurrentFfbProfile(bool quiet)
    {
        var name = string.IsNullOrWhiteSpace(_profile.FfbProfileName) ? "Raw" : _profile.FfbProfileName.Trim();
        var ffb = _profiles.CaptureFfbFromMapping(_profile);
        _profiles.SaveFfb(ffb, name);
        _profile.FfbProfileName = ffb.Name;
        RefreshFfbProfilesCombo(ffb.Name);
        if (!quiet)
            StatusText.Text = $"Saved FFB profile '{ffb.Name}'";
    }

    private void SaveFfbButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncProfileFromUi();
            SaveCurrentFfbProfile(quiet: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save FFB profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveFfbAsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncProfileFromUi();
            var suggested = string.IsNullOrWhiteSpace(_profile.FfbProfileName) ? "My FFB" : _profile.FfbProfileName.Trim();
            if (suggested.Equals("Raw", StringComparison.OrdinalIgnoreCase))
                suggested = "My FFB";
            var name = PromptForName("Save FFB profile as", suggested);
            if (name is null) return;

            if (name.Equals("Raw", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Choose a different name — Raw is the built-in exact-mix preset.", "Save FFB As");
                return;
            }

            if (_profiles.FfbExists(name))
            {
                var overwrite = MessageBox.Show(
                    $"FFB profile '{name}' already exists. Overwrite?",
                    "Save FFB As",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (overwrite != MessageBoxResult.Yes)
                    return;
            }

            _profile.FfbProfileName = name;
            var ffb = _profiles.CaptureFfbFromMapping(_profile);
            _profiles.SaveFfb(ffb, name);
            RefreshFfbProfilesCombo(name);
            StatusText.Text = $"Saved FFB profile '{name}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save FFB As", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DefaultFfbButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var defaults = FfbProfile.CreateRaw();
            _profile.FfbGain = defaults.FfbGain;
            _profile.FfbInvert = defaults.FfbInvert;
            _profile.FfbEffectGains = defaults.EffectGains;
            _profile.FfbOutputFeel = defaults.OutputFeel;

            GainSlider.Value = _profile.FfbGain;
            InvertFfbCheck.IsChecked = _profile.FfbInvert;
            LoadEffectGainsIntoUi(_profile.FfbEffectGains);
            LoadFeelIntoUi(_profile.FfbOutputFeel);
            _bridge.Profile = _profile;
            ScheduleFfbProfilePush();

            var selected = FfbProfilesCombo.SelectedItem as string ?? _profile.FfbProfileName ?? "Raw";
            StatusText.Text = $"FFB sliders reset to defaults — Save to write '{selected}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Default FFB", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteFfbButton_Click(object sender, RoutedEventArgs e)
    {
        var name = FfbProfilesCombo.SelectedItem as string ?? _profile.FfbProfileName;
        if (string.IsNullOrWhiteSpace(name) || !_profiles.FfbExists(name))
        {
            MessageBox.Show("Select a saved FFB profile to delete.", "Delete FFB profile");
            return;
        }

        if (name.Equals("Raw", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Raw cannot be deleted — it is the built-in exact game-mix preset.", "Delete FFB profile");
            return;
        }

        var confirm = MessageBox.Show($"Delete FFB profile '{name}'?", "Delete FFB profile",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        _profiles.DeleteFfb(name);
        _profile.FfbProfileName = "Raw";
        _profiles.ApplyLinkedFfbProfile(_profile);
        GainSlider.Value = _profile.FfbGain;
        InvertFfbCheck.IsChecked = _profile.FfbInvert;
        LoadEffectGainsIntoUi(_profile.FfbEffectGains);
        LoadFeelIntoUi(_profile.FfbOutputFeel);
        _bridge.Profile = _profile;
        RefreshFfbProfilesCombo("Raw");
        StatusText.Text = $"Deleted FFB '{name}' — switched to Raw";
    }

    private void FfbProfilesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFfbProfileCombo) return;
        if (FfbProfilesCombo.SelectedItem is not string name) return;
        if (!_profiles.FfbExists(name)) return;

        try
        {
            _profile.FfbProfileName = name;
            _profiles.ApplyLinkedFfbProfile(_profile);
            GainSlider.Value = _profile.FfbGain;
            InvertFfbCheck.IsChecked = _profile.FfbInvert;
            LoadEffectGainsIntoUi(_profile.FfbEffectGains);
            LoadFeelIntoUi(_profile.FfbOutputFeel);
            _bridge.Profile = _profile;
            StatusText.Text = $"Loaded FFB profile '{name}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Load FFB profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RebuildBindingRows()
    {
        var selected = (BindingList.SelectedItem as BindingRow)?.Target;
        _bindings.Clear();
        foreach (var control in G920ControlInfo.UiOrder)
        {
            var binding = _profile.GetOrCreate(control);
            _bindings.Add(new BindingRow(control, binding, ResolveDeviceName, () => _profile.GearReverseOutputButton));
        }
        if (selected is G920Control target)
            BindingList.SelectedItem = _bindings.FirstOrDefault(b => b.Target == target);
    }

    private void ClearAllBindings_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Clear all G920 bindings in this profile?\n\nInvert and deadzone settings are also reset. Save afterward if you want to keep the change.",
            "Clear all bindings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        foreach (var binding in _profile.Bindings)
        {
            if (G920ControlInfo.IsFfbNudge(binding.Target))
                continue;
            binding.ClearSources();
            binding.Invert = false;
            binding.Deadzone = 0;
        }

        _bridge.Profile = _profile;
        RebuildBindingRows();
        StatusText.Text = "All bindings cleared.";
    }

    private string ResolveDeviceName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "?";
        return _devices.FirstOrDefault(d => d.Id == id)?.Info.Name ?? id[..Math.Min(8, id.Length)];
    }

    private string? ResolveProductId(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        return _devices.FirstOrDefault(d => d.Id == deviceId)?.Info.ProductId
               ?? _bridge.InputHub.FindDevice(deviceId)?.ProductId;
    }

    private void SetFfbSource(DeviceRow row)
    {
        _profile.FfbSourceDeviceId = row.Id;
        _profile.FfbSourceProductId = row.Info.ProductId;
    }

    private void RefreshLiveUi()
    {
        // Binding dialog already polls; never Poll DirectInput on the UI thread —
        // Start's FFB attach / RefreshDevices holds InputHub and freezes WPF when the wheel moves.
        if (_bindingDialogOpen || _bridgeBusy)
            return;

        if (!_bridge.IsRunning)
        {
            ScheduleLivePreviewPoll();
            return;
        }

        ApplyLive(_bridge.LatestState);
        ProcessFfbEffectNudges(_bridge.LatestDevices);
        var link = _bridge.LinkStatus;
        if (!string.IsNullOrWhiteSpace(link) && StatusText is not null &&
            !StatusText.Text.StartsWith("FFB ", StringComparison.Ordinal) &&
            !StatusText.Text.StartsWith("Return-to-center", StringComparison.Ordinal) &&
            !StatusText.Text.StartsWith("Starting", StringComparison.Ordinal) &&
            !StatusText.Text.StartsWith("Stopping", StringComparison.Ordinal) &&
            !StatusText.Text.StartsWith("Shutting", StringComparison.Ordinal))
        {
            StatusText.Text = link;
            _shownLinkStatus = link;
        }
        else if (string.IsNullOrWhiteSpace(link) && _shownLinkStatus is not null && StatusText is not null)
        {
            if (StatusText.Text == _shownLinkStatus)
                StatusText.Text = "Bridge running — virtual G920 active.";
            _shownLinkStatus = null;
        }

        RefreshFfbDiagnostics();
        ApplyTelemetryLive();
    }

    private void ScheduleLivePreviewPoll()
    {
        if (Interlocked.CompareExchange(ref _livePreviewPollInFlight, 1, 0) != 0)
            return;

        var profile = _profile;
        _ = Task.Run(() =>
        {
            try
            {
                if (_bridgeBusy || _bridge.IsRunning)
                    return;
                // PollForUi overlays pinned FFB (e.g. FFB debug Attach without bridge Start).
                var devices = _bridge.PollForUi();
                var mapped = new MapperEngine().Map(profile, devices);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_bridgeBusy && !_bridge.IsRunning)
                    {
                        ApplyLive(mapped);
                        ProcessFfbEffectNudges(devices);
                        RefreshFfbDiagnostics();
                    }
                }));
            }
            catch
            {
                // ignore preview poll failures
            }
            finally
            {
                Interlocked.Exchange(ref _livePreviewPollInFlight, 0);
            }
        });
    }

    private void ScheduleFfbProfilePush()
    {
        if (_ffbProfilePushTimer is null) return;
        _ffbProfilePushTimer.Stop();
        _ffbProfilePushTimer.Start();
    }

    private void FfbDebugExpander_ExpandedChanged(object sender, RoutedEventArgs e)
    {
        if (FfbDebugExpander?.IsExpanded == true)
            RefreshFfbDiagnostics();
    }

    private void RefreshFfbDiagnostics()
    {
        if (FfbDiagText is null) return;
        var overlayOn = _debugOverlay is { IsVisible: true };
        if (FfbDebugExpander?.IsExpanded != true && !overlayOn)
            return;

        // Cap UI DI/diagnostics work — was every 50ms and also polled the FFB joystick
        // on the UI thread (hangs when alt-tabbing while the bridge applies torque).
        var now = Environment.TickCount64;
        if (now - _lastFfbDiagUiTick < 250)
            return;
        _lastFfbDiagUiTick = now;

        var d = _bridge.GetFfbDiagnostics();
        var link = _bridge.LinkStatus;
        if (FfbDebugExpander?.IsExpanded == true)
            FfbDiagText.Text = DebugOverlayWindow.FormatEffects(d)
                + Environment.NewLine + Environment.NewLine
                + DebugOverlayWindow.FormatFfb(d, link);
        _debugOverlay?.UpdateFfb(d, link);
    }

    private async Task<bool> EnsureFfbAttachedForTestAsync()
    {
        SyncProfileFromUi();
        var hwnd = new WindowInteropHelper(this).Handle;
        _bridge.BindFfbWindow(hwnd);
        _bridge.Profile = _profile;

        if (_bridge.Ffb.IsReady &&
            string.Equals(_bridge.Ffb.ActiveDeviceId, _profile.FfbSourceDeviceId, StringComparison.OrdinalIgnoreCase))
            return true;

        string status = "";
        var ok = await Task.Run(() => _bridge.TryAttachFfb(out status)).ConfigureAwait(true);
        StatusText.Text = string.IsNullOrWhiteSpace(status)
            ? (ok ? "FFB: attached." : "FFB reattach failed.")
            : status;
        RefreshFfbDiagnostics();
        return ok && _bridge.Ffb.IsReady;
    }

    private async void FfbAttach_Click(object sender, RoutedEventArgs e) =>
        await EnsureFfbAttachedForTestAsync();

    private void FfbTestLeft_Click(object sender, RoutedEventArgs e) => ApplyFfbTestTorque(-0.45f);

    private void FfbTestRight_Click(object sender, RoutedEventArgs e) => ApplyFfbTestTorque(0.45f);

    private async void FfbTestCenter_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureFfbAttachedForTestAsync())
            return;

        // Toggle: Center again (or Release test) turns it off.
        if (_bridge.Ffb.TestAutoCenterActive)
        {
            _bridge.Ffb.ClearTestOverride();
            SetFfbTestSlider(0f);
            StatusText.Text = "Return-to-center off.";
            RefreshFfbDiagnostics();
            return;
        }

        _ffbPulseCts?.Cancel();
        _bridge.Ffb.StartTestAutoCenter(gain: 0.85f);
        SetFfbTestSlider(0f);
        StatusText.Text = "Return-to-center ON — turn the rim; it should pull back. Release test to stop.";
        RefreshFfbDiagnostics();
    }

    private async void FfbTestPulse_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureFfbAttachedForTestAsync())
            return;

        _ffbPulseCts?.Cancel();
        _ffbPulseCts = new CancellationTokenSource();
        var token = _ffbPulseCts.Token;
        try
        {
            StatusText.Text = "FFB pulse…";
            _bridge.Ffb.ApplyTestTorque(-0.5f);
            SetFfbTestSlider(-0.5f);
            await Task.Delay(250, token);
            _bridge.Ffb.ApplyTestTorque(0.5f);
            SetFfbTestSlider(0.5f);
            await Task.Delay(250, token);
            _bridge.Ffb.ApplyTestTorque(0f);
            SetFfbTestSlider(0f);
            StatusText.Text = "FFB pulse done. Click Release test to return control to the game.";
        }
        catch (TaskCanceledException)
        {
            // ignored
        }
        RefreshFfbDiagnostics();
    }

    private void FfbTestRelease_Click(object sender, RoutedEventArgs e)
    {
        _ffbPulseCts?.Cancel();
        _bridge.Ffb.ClearTestOverride();
        SetFfbTestSlider(0f);
        StatusText.Text = "FFB test released — game/HID++ can drive the base again.";
        RefreshFfbDiagnostics();
    }

    private void FfbTestTorqueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FfbTestTorqueValueText is null) return;
        FfbTestTorqueValueText.Text = $"{FfbTestTorqueSlider.Value:P0}";
        if (_ffbTestSliderSilent) return;
        ApplyFfbTestTorque((float)FfbTestTorqueSlider.Value);
    }

    private async void ApplyFfbTestTorque(float torque)
    {
        if (!await EnsureFfbAttachedForTestAsync())
            return;
        _ffbPulseCts?.Cancel();
        _bridge.Ffb.ApplyTestTorque(torque);
        SetFfbTestSlider(torque);
        StatusText.Text = $"FFB test torque {torque:+0.00;-0.00;0.00} (override active).";
        RefreshFfbDiagnostics();
    }

    private void SetFfbTestSlider(float torque)
    {
        if (FfbTestTorqueSlider is null) return;
        _ffbTestSliderSilent = true;
        try { FfbTestTorqueSlider.Value = torque; }
        finally { _ffbTestSliderSilent = false; }
        if (FfbTestTorqueValueText is not null)
            FfbTestTorqueValueText.Text = $"{torque:P0}";
    }

    private void ApplyLive(MappedG920State s)
    {
        GearText.Text = $"Gear: {s.ActiveGearLabel}";
        SteerText.Text = $"Steering: {s.Steering:F2}";
        SteerBar.Value = s.Steering;
        ThrottleText.Text = $"Throttle: {s.Throttle:P0}";
        ThrottleBar.Value = s.Throttle;
        BrakeText.Text = $"Brake: {s.Brake:P0}";
        BrakeBar.Value = s.Brake;
        ClutchText.Text = $"Clutch: {s.Clutch:P0}";
        ClutchBar.Value = s.Clutch;

        if (StripGearText is not null) StripGearText.Text = $"GEAR {s.ActiveGearLabel}";
        if (StripSteerText is not null) StripSteerText.Text = $"Steering: {s.Steering:F2}";
        if (StripSteerBar is not null) StripSteerBar.Value = s.Steering;
        if (StripThrottleText is not null) StripThrottleText.Text = $"Throttle: {s.Throttle:P0}";
        if (StripThrottleBar is not null) StripThrottleBar.Value = s.Throttle;
        if (StripBrakeText is not null) StripBrakeText.Text = $"Brake: {s.Brake:P0}";
        if (StripBrakeBar is not null) StripBrakeBar.Value = s.Brake;
        if (StripClutchText is not null) StripClutchText.Text = $"Clutch: {s.Clutch:P0}";
        if (StripClutchBar is not null) StripClutchBar.Value = s.Clutch;

        ButtonsText.Text = "Buttons: " + DebugOverlayWindow.FormatButtons(s);
        if (_debugOverlay is { IsVisible: true })
            _debugOverlay.UpdateInput(s);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshDevices(restoreHidden: true);

    private void SetBridgeControls(bool running, bool busy)
    {
        StartButton.IsEnabled = !busy;
        BridgeGlyph.Kind = running ? PackIconMaterialKind.Stop : PackIconMaterialKind.Play;
        if (busy)
        {
            BridgeLabel.Text = running ? "Stopping…" : "Starting…";
            StartButton.ToolTip = running ? "Stopping the virtual G920…" : "Starting the virtual G920…";
            return;
        }

        BridgeLabel.Text = running ? "Stop" : "Start";
        StartButton.Style = (Style)FindResource(running ? "BridgeToggleStop" : "BridgeToggleStart");
        StartButton.ToolTip = running ? "Stop the virtual G920" : "Start the virtual G920";
    }

    private void BridgeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_bridgeBusy) return;
        if (_bridge.IsRunning)
            StopButton_Click(sender, e);
        else
            StartButton_Click(sender, e);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var deps = ProbeDependencies();
            if (!deps.ReadyForGames)
            {
                var proceed = MessageBox.Show(
                    $"{string.Join(", ", deps.MissingRequiredNames)} required and not installed.\n\n" +
                    "WinUHid exposes the virtual G920 to games.\n" +
                    "HidHide hides your physical pad so the game only sees the G920.\n" +
                    "OEM/SDK registry pins are applied automatically while the bridge runs.\n\n" +
                    "Start anyway (preview / incomplete setup)?",
                    "Required dependencies missing",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (proceed != MessageBoxResult.Yes)
                {
                    DependenciesButton_Click(sender, e);
                    return;
                }
            }

            SyncProfileFromUi();
            AutoSaveCurrent();
            _bridge.Profile = _profile;

            // Ensure FFB combo selection is on the profile before attach.
            DeviceBindingResolver.RemapProfile(_profile, _devices.Select(d => d.Info).ToList());
            if (FfbDeviceCombo.SelectedItem is DeviceRow ffbRow)
                SetFfbSource(ffbRow);
            else if (string.IsNullOrWhiteSpace(_profile.FfbSourceDeviceId))
            {
                var auto = _devices.FirstOrDefault(d => d.Info.SupportsForceFeedback);
                if (auto is not null)
                {
                    FfbDeviceCombo.SelectedItem = auto;
                    SetFfbSource(auto);
                }
            }

            SetBridgeControls(running: false, busy: true);
            StatusText.Text = "Starting bridge…";
            _bridgeBusy = true;

            // Optional HidHide: auto-apply hide-all-except-emulator; optional restore point.
            // Do NOT run full GHubConflictRepair here — it previously removed WinUHid enumerators.
            var sessionStarted = false;
            var appSettings = _profiles.LoadSettings();
            var autoApplyHidHide = appSettings.AutoApplyHidHideConfigOnStart;
            var unloadHidHide = appSettings.UnloadHidHideConfigWhenStopped;
            DependencyChecker.HidHideSnapshot? hidHideRevert = null;
            var hidHideSaveRevert = false;

            if (autoApplyHidHide)
            {
                // Client holds an exclusive lock on the filter — CLI gets 0x0005 while it is open.
                if (DependencyChecker.IsHidHideClientRunning())
                {
                    var closeClient = MessageBox.Show(
                        this,
                        "HidHide Client is open.\n\n" +
                        "While that window is open, Windows blocks other apps from reading or changing HidHide " +
                        "(Access is denied / 0x0005).\n\n" +
                        "Close HidHide Client now so Start can continue?",
                        "HidHide Client is open",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Warning);
                    if (closeClient == MessageBoxResult.Cancel)
                    {
                        _bridgeBusy = false;
                        SetBridgeControls(running: false, busy: false);
                        StatusText.Text = "Start cancelled — close HidHide Client first.";
                        return;
                    }

                    if (closeClient == MessageBoxResult.Yes)
                    {
                        StatusText.Text = "Closing HidHide Client…";
                        await Task.Run(() => DependencyChecker.TryCloseHidHideClient()).ConfigureAwait(true);
                    }
                }
            }

            if (autoApplyHidHide && unloadHidHide)
            {
                // Ask first — reading HidHide is slow; don't pay for capture if the user picks No.
                var saveChoice = MessageBox.Show(
                    this,
                    "Save today's HidHide setup as your restore point?\n\n" +
                    "Yes — read HidHide now and put it back when you Stop\n" +
                    "No — apply session hide only (Stop won't restore)\n" +
                    "Cancel — don't start",
                    "HidHide restore point",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);
                if (saveChoice == MessageBoxResult.Cancel)
                {
                    _bridgeBusy = false;
                    SetBridgeControls(running: false, busy: false);
                    StatusText.Text = "Start cancelled.";
                    return;
                }

                if (saveChoice == MessageBoxResult.Yes)
                {
                    DependencyChecker.HidHideSnapshot? snap = null;
                    var captureError = "";
                    while (true)
                    {
                        if (DependencyChecker.IsHidHideClientRunning())
                        {
                            StatusText.Text = "Closing HidHide Client…";
                            await Task.Run(() => DependencyChecker.TryCloseHidHideClient()).ConfigureAwait(true);
                        }

                        StatusText.Text = "Reading HidHide configuration…";
                        (snap, captureError) = await Task.Run(DependencyChecker.TryCaptureHidHideSnapshotDetailed)
                            .ConfigureAwait(true);
                        if (snap is not null)
                            break;

                        var clientLocked =
                            DependencyChecker.IsHidHideClientRunning() ||
                            captureError.Contains("HidHide Client is open", StringComparison.OrdinalIgnoreCase) ||
                            captureError.Contains("0x0005", StringComparison.OrdinalIgnoreCase) ||
                            captureError.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);

                        var failChoice = MessageBox.Show(
                            this,
                            "Couldn't read your current HidHide setup.\n\n" +
                            captureError + "\n\n" +
                            (clientLocked
                                ? "HidHide Client must be closed (it locks the driver).\n\n" +
                                  "Yes — close HidHide Client and try again\n"
                                : "Windows may ask for admin permission.\n\n" +
                                  "Yes — try reading again\n") +
                            "No — start without a restore point (Stop won't put HidHide back)\n" +
                            "Cancel — don't start",
                            "HidHide",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Warning);
                        if (failChoice == MessageBoxResult.Yes)
                        {
                            if (clientLocked)
                                await Task.Run(() => DependencyChecker.TryCloseHidHideClient()).ConfigureAwait(true);
                            continue;
                        }

                        if (failChoice == MessageBoxResult.Cancel)
                        {
                            _bridgeBusy = false;
                            SetBridgeControls(running: false, busy: false);
                            StatusText.Text = "Start cancelled — HidHide could not be read.";
                            return;
                        }

                        snap = null;
                        break;
                    }

                    hidHideSaveRevert = snap is not null;
                    hidHideRevert = snap;
                }
            }

            try
            {
                await Task.Run(() =>
                {
                    if (autoApplyHidHide)
                    {
                        // Apply also needs the driver unlocked.
                        if (DependencyChecker.IsHidHideClientRunning())
                            DependencyChecker.TryCloseHidHideClient();

                        var (hhOk, hhMsg) = unloadHidHide
                            ? DependencyChecker.BeginHidHideSessionWithSnapshot(
                                hidHideSaveRevert ? hidHideRevert : null)
                            : DependencyChecker.RefreshHidHideSessionDevices();
                        if (!hhOk)
                            throw new InvalidOperationException(hhMsg);
                    }

                    try { LogiJoyHidBinder.TryRemoveLogitechCol01(); } catch { /* ignore */ }
                    OemRegistrationSession.BeginSession();
                    sessionStarted = true;
                    GHubGuard.StartAppWatch();
                    try { _ = GHubConflictRepair.RemoveDisconnectedVirtualNodes(); } catch { /* ignore */ }
                    _bridge.Start();

                    // Virtual G920 now exists — re-hide pads/wheels and keep the emulator visible.
                    if (autoApplyHidHide)
                        DependencyChecker.RefreshHidHideSessionDevices();
                }).ConfigureAwait(true);
            }
            catch
            {
                if (sessionStarted)
                {
                    try { GHubGuard.StopAppWatch(); } catch { /* ignore */ }
                    try { OemRegistrationSession.EndSession(); } catch { /* ignore */ }
                }
                if (hidHideSaveRevert || DependencyChecker.HasPendingHidHideSnapshot)
                {
                    try { DependencyChecker.EndHidHideSession(); } catch { /* ignore */ }
                }
                throw;
            }
            finally
            {
                _bridgeBusy = false;
            }

            // Re-enumerate DI after Start so bind/preview still see physical pads (whitelist).
            try { RefreshDevices(restoreHidden: false); } catch { /* ignore */ }
            try { UpdateDependencyUi(); } catch { /* ignore */ }

            SetBridgeControls(running: true, busy: false);

            // Sync combo if Start auto-selected an FFB device.
            if (!string.IsNullOrEmpty(_profile.FfbSourceDeviceId))
            {
                var currentId = (FfbDeviceCombo.SelectedItem as DeviceRow)?.Id;
                if (!string.Equals(currentId, _profile.FfbSourceDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    FfbDeviceCombo.SelectedItem =
                        _devices.FirstOrDefault(d => d.Id == _profile.FfbSourceDeviceId) ?? FfbDeviceCombo.SelectedItem;
                }
            }

            var col01Missing = !_virtual.IsPreviewMode &&
                               !string.IsNullOrWhiteSpace(_virtual.LastError) &&
                               _virtual.LastError.Contains("Col01", StringComparison.OrdinalIgnoreCase);
            StatusText.Text = _virtual.IsPreviewMode
                ? "Bridge running (preview — no virtual HID)."
                : col01Missing
                    ? "Bridge running — virtual G920 not visible. Stop, then Start again."
                    : _bridge.Ffb.IsReady
                        ? "Bridge running — virtual G920 active, FFB attached."
                        : "Bridge running — virtual G920 active.";
            if (autoApplyHidHide && hidHideSaveRevert)
                StatusText.Text += " HidHide restore point saved (restores on Stop).";
            else if (autoApplyHidHide)
                StatusText.Text += " HidHide session applied.";
            // Keep the footer short; FFB details live under FFB debug.
            DriverText.Text = !string.IsNullOrWhiteSpace(_virtual.LastError)
                ? _virtual.LastError
                : _bridge.Ffb.IsReady
                    ? "Virtual G920 started."
                    : "Virtual G920 started. Pick an FFB-capable device under Force Feedback if you want force feedback.";
            if (col01Missing)
            {
                MessageBox.Show(
                    "The virtual G920 did not stay enumerated after Start.\n\n" +
                    "Heat will fall back to a DualSense / pad layout when Col01 is missing.\n\n" +
                    "Click Stop bridge, then Start bridge again. Keep the bridge running while you launch Heat.",
                    "Virtual G920 not visible",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            UpdateDependencyUi();
        }
        catch (Exception ex)
        {
            _bridgeBusy = false;
            SetBridgeControls(running: false, busy: false);
            MessageBox.Show(ex.Message, "G920 Emulator", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = ex.Message;
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        SetBridgeControls(running: true, busy: true);
        StatusText.Text = "Stopping bridge…";
        _bridgeBusy = true;
        try
        {
            // Tear down off the UI thread — WinUHid stop + FFB detach must not freeze the window.
            // Always EndSession even if Stop hangs: otherwise OEM pins stay and the next
            // Start can fail while WinUHid is still destroying the previous device.
            try { GHubGuard.StopAppWatch(); } catch { /* ignore */ }
            var stop = Task.Run(() =>
            {
                try { _bridge.Stop(); } catch { /* ignore */ }
            });
            var finished = await Task.WhenAny(stop, Task.Delay(5000)).ConfigureAwait(true);
            if (finished != stop)
            {
                StatusText.Text = "Stopping bridge (waiting on driver)…";
                await Task.WhenAny(stop, Task.Delay(3000)).ConfigureAwait(true);
            }
            else
            {
                await stop.ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Stop failed: " + ex.Message;
        }
        finally
        {
            try { OemRegistrationSession.EndSession(); } catch { /* ignore */ }
        }

        var hidHideNote = "";
        if (_profiles.LoadSettings().UnloadHidHideConfigWhenStopped ||
            DependencyChecker.HasPendingHidHideSnapshot)
        {
            // Keep Start/Stop disabled until HidHide finishes — same as exit.
            StatusText.Text = "Restoring HidHide…";
            try
            {
                if (DependencyChecker.IsHidHideClientRunning())
                {
                    StatusText.Text = "Closing HidHide Client…";
                    await Task.Run(() => DependencyChecker.TryCloseHidHideClient()).ConfigureAwait(true);
                    StatusText.Text = "Restoring HidHide…";
                }

                var (ok, msg) = await Task.Run(() =>
                        DependencyChecker.EndHidHideSession(timeoutMs: DependencyChecker.HidHideRestoreStopTimeoutMs))
                    .ConfigureAwait(true);
                hidHideNote = ok ? " HidHide restored." : " " + msg;
            }
            catch (Exception ex)
            {
                hidHideNote = " HidHide restore failed: " + ex.Message;
            }
            finally
            {
                try { DependencyChecker.KillOrphanHidHideHelpers(); } catch { /* ignore */ }
            }
        }

        _bridgeBusy = false;
        SetBridgeControls(running: false, busy: false);
        StatusText.Text = "Bridge stopped. OEM/SDK restored (Forza-safe)." + hidHideNote;
        try { UpdateDependencyUi(); } catch { /* ignore */ }
        try { RefreshDevices(restoreHidden: false); } catch { /* ignore */ }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncProfileFromUi();
            var name = RequireProfileName();
            // Keep the linked FFB profile in sync with current sliders.
            SaveCurrentFfbProfile(quiet: true);
            _profiles.Save(_profile, name);
            RefreshSavedProfilesCombo(name);
            StatusText.Text = $"Saved input profile '{name}' (+ FFB '{_profile.FfbProfileName}')";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RenameProfileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncProfileFromUi();
            var current = CurrentInputProfileName();
            if (string.IsNullOrWhiteSpace(current) || !_profiles.Exists(current))
            {
                MessageBox.Show("Select a saved profile to rename.", "Rename profile");
                return;
            }

            var name = PromptForName("Rename profile", current);
            if (name is null || name.Equals(current, StringComparison.OrdinalIgnoreCase))
                return;

            if (_profiles.Exists(name))
            {
                MessageBox.Show($"A profile named '{name}' already exists.", "Rename profile",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveCurrentFfbProfile(quiet: true);
            _profiles.Save(_profile, name);
            _profiles.Delete(current);
            RefreshSavedProfilesCombo(name);
            StatusText.Text = $"Renamed '{current}' → '{name}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Rename profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveAsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncProfileFromUi();
            var suggested = CurrentInputProfileName();
            var name = PromptForName("Save profile as", suggested);
            if (name is null) return;

            if (_profiles.Exists(name))
            {
                var overwrite = MessageBox.Show(
                    $"Profile '{name}' already exists. Overwrite?",
                    "Save As",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (overwrite != MessageBoxResult.Yes)
                    return;
            }

            // Keep the linked FFB profile in sync with current sliders (same as Save).
            SaveCurrentFfbProfile(quiet: true);
            _profiles.Save(_profile, name);
            RefreshSavedProfilesCombo(name);
            StatusText.Text = $"Saved input profile '{name}' (+ FFB '{_profile.FfbProfileName}')";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save As", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var name = CurrentInputProfileName();
        if (string.IsNullOrWhiteSpace(name) || !_profiles.Exists(name))
        {
            MessageBox.Show("Select a saved profile to delete.", "Delete profile");
            return;
        }

        var confirm = MessageBox.Show($"Delete profile '{name}'?", "Delete profile",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        _profiles.Delete(name);
        RefreshSavedProfilesCombo(null);
        StatusText.Text = $"Deleted '{name}'";
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        SyncProfileFromUi();
        var dialog = new SaveFileDialog
        {
            Filter = "Profile (*.json)|*.json",
            FileName = (RequireProfileNameSafe()) + ".json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() == true)
        {
            _profile.Save(dialog.FileName);
            StatusText.Text = $"Exported {dialog.FileName}";
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Profile (*.json)|*.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true)
            return;

        var imported = MappingProfile.Load(dialog.FileName);
        if (string.IsNullOrWhiteSpace(imported.Name))
            imported.Name = Path.GetFileNameWithoutExtension(dialog.FileName);

        LoadProfileIntoUi(imported);
        var name = PromptForName("Import profile — save as", imported.Name) ?? imported.Name;
        _profiles.Save(_profile, name);
        RefreshSavedProfilesCombo(name);
        StatusText.Text = $"Imported and saved '{name}'";
    }

    private void SavedProfilesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileCombo) return;
        if (SavedProfilesCombo.SelectedItem is not string name) return;
        if (!_profiles.Exists(name)) return;

        try
        {
            // Load + LoadProfileIntoUi both ApplyLinkedFfbProfile (linked FFB gains/feel).
            var loaded = _profiles.Load(name);
            LoadProfileIntoUi(loaded);
            UpdateAppSettings(s =>
            {
                s.LastProfileName = name;
                s.LastFfbProfileName = loaded.FfbProfileName;
            });
            StatusText.Text = $"Loaded profile '{name}' (FFB '{loaded.FfbProfileName}')";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Load profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AutoSaveCurrent()
    {
        var name = CurrentInputProfileName();
        _profiles.Save(_profile, name);
        RefreshSavedProfilesCombo(name);
    }

    private string CurrentInputProfileName()
    {
        if (SavedProfilesCombo.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
            return selected.Trim();
        if (!string.IsNullOrWhiteSpace(_profile.Name))
            return _profile.Name.Trim();
        return "My Rig";
    }

    private string RequireProfileName()
    {
        var name = CurrentInputProfileName();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Choose a profile in the dropdown, or use Save As….");
        return name;
    }

    private string RequireProfileNameSafe()
    {
        var name = CurrentInputProfileName();
        return string.IsNullOrWhiteSpace(name) ? "profile" : name;
    }

    private string? PromptForName(string title, string suggested)
    {
        var dialog = new ProfileNameDialog(suggested) { Owner = this, Title = title };
        return dialog.ShowDialog() == true ? dialog.ProfileName : null;
    }

    private void SyncProfileFromUi()
    {
        _profile.Name = CurrentInputProfileName();
        _profile.FfbGain = GainSlider.Value;
        _profile.FfbInvert = InvertFfbCheck.IsChecked == true;
        SyncEffectGainsFromUi();
        SyncFeelFromUi();
        if (FfbDeviceCombo.SelectedItem is DeviceRow row)
            SetFfbSource(row);
        _profile.ShifterMode = ShifterMode.ExclusiveHPattern;
        _bridge.Profile = _profile;
    }

    private void BindingList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_bindingDialogOpen) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<System.Windows.Controls.Primitives.Thumb>(source) is not null) return;
        if (FindAncestor<Slider>(source) is not null) return;

        var item = FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is not BindingRow row) return;

        OpenBindDialog(row.Target);
    }

    private void DeadzoneSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Keep focus on the slider so dragging does not open the bind dialog.
        e.Handled = false;
        if (sender is Slider slider)
            slider.Focus();
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private bool OpenBindDialog(G920Control target, Window? owner = null)
    {
        var binding = _profile.GetOrCreate(target);
        _bindingDialogOpen = true;
        try
        {
            var dlg = new BindInputWindow(
                target,
                binding,
                _profile,
                // Must overlay pinned FFB buttons — plain InputHub.Poll skips Fanatec while
                // the bridge holds exclusive FFB (broke bind-on-the-fly after Start).
                () => _bridge.PollForUi(),
                () => _bridge.RefreshDevices(),
                ResolveDeviceName,
                ResolveProductId)
            {
                Owner = owner ?? this,
            };

            if (dlg.ShowDialog() != true || !dlg.Applied)
                return false;

            if (G920ControlInfo.IsFfbNudge(target))
                RefreshFfbBindButtons();
            else
            {
                RebuildBindingRows();
                BindingList.SelectedItem = _bindings.FirstOrDefault(b => b.Target == target);
            }
            var sources = binding.EffectiveSources;
            var label = G920ControlInfo.DisplayName(target);
            StatusText.Text = sources.Count == 0
                ? $"{label} cleared."
                : sources.Count == 1
                    ? $"Bound {label} ← {FormatSource(sources[0], ResolveDeviceName, G920ControlInfo.IsAxis(target))}"
                    : $"Bound {label} ← {sources.Count} sources";
            _bridge.Profile = _profile;
            return true;
        }
        finally
        {
            _bindingDialogOpen = false;
        }
    }

    private void FfbEffectBind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string effect)
            return;
        if (!G920ControlInfo.TryGetSliderBindById(effect, out var bind))
            return;
        var slider = EffectGainSliderFor(bind.Minus);
        if (slider is null)
            return;

        var storedDefault = _profile.GetFfbBindDefault(bind.Id, slider.Value);
        FfbEffectBindWindow? dlg = null;
        dlg = new FfbEffectBindWindow(
            bind.Display,
            bind.Id,
            bind.Minus,
            bind.Plus,
            bind.Default,
            _profile,
            slider.Minimum,
            slider.Maximum,
            storedDefault,
            slider.TickFrequency > 0 ? slider.TickFrequency : bind.FineStep,
            v => FormatFfbBindDefaultValue(bind.Id, v),
            ResolveDeviceName,
            target => OpenBindDialog(target, dlg))
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true)
            return;
        _profile.SetFfbBindDefault(bind.Id, dlg.DefaultValue);
        _bridge.Profile = _profile;
        AutoSaveCurrent();
        RefreshFfbBindButtons();
    }

    private static string FormatFfbBindDefaultValue(string sliderId, double value) => sliderId switch
    {
        "Master" or "Constant" or "Spring" or "Damper" or "Friction" or "Inertia"
            or "Periodic" or "Ramp" or "Custom" or "PeakSoft" or "Spike"
            or "CenterStrength" or "CenterRange" or "DampVel" or "DampDead" => $"{value:P0}",
        "SoftStart" => $"{value:0} ms",
        "Smoothing" or "Slew" or "Epsilon" => $"{value:0}",
        "Deadband" or "CenterDeadzone" => $"{value:0.###}",
        _ => value.ToString("0.##"),
    };

    private void RefreshFfbBindButtons()
    {
        if (BindFfbConstant is null && BindFfbMaster is null) return;
        var unboundStyle = (Style)FindResource("FfbBindButton");
        var boundStyle = (Style)FindResource("FfbBindButtonBound");
        foreach (var bind in G920ControlInfo.FfbSliderBinds)
        {
            if (FindName("BindFfb" + bind.Id) is not Button button)
                continue;

            var minusSources = _profile.GetOrCreate(bind.Minus).EffectiveSources;
            var plusSources = _profile.GetOrCreate(bind.Plus).EffectiveSources;
            var defaultSources = _profile.GetOrCreate(bind.Default).EffectiveSources;
            var minusBound = minusSources.Count > 0;
            var plusBound = plusSources.Count > 0;
            var defaultBound = defaultSources.Count > 0;
            var bound = minusBound || plusBound || defaultBound;
            button.Style = bound ? boundStyle : unboundStyle;
            button.Content = "Bind";

            if (!bound)
            {
                button.ToolTip = $"Assign hardware buttons to lower / raise / set default for {bind.Display} while you drive.";
                continue;
            }

            var minusLabel = minusBound
                ? minusSources.Count == 1
                    ? "− " + FormatSource(minusSources[0], ResolveDeviceName, axisTarget: false)
                    : $"− {minusSources.Count} sources"
                : "− not bound";
            var plusLabel = plusBound
                ? plusSources.Count == 1
                    ? "+ " + FormatSource(plusSources[0], ResolveDeviceName, axisTarget: false)
                    : $"+ {plusSources.Count} sources"
                : "+ not bound";
            var defaultValue = _profile.GetFfbBindDefault(bind.Id, EffectGainSliderFor(bind.Minus)?.Value ?? 0);
            var defaultLabel = defaultBound
                ? defaultSources.Count == 1
                    ? $"⌂ {FormatSource(defaultSources[0], ResolveDeviceName, axisTarget: false)} → {FormatFfbBindDefaultValue(bind.Id, defaultValue)}"
                    : $"⌂ {defaultSources.Count} sources → {FormatFfbBindDefaultValue(bind.Id, defaultValue)}"
                : "⌂ not bound";
            button.ToolTip = $"{minusLabel}\n{plusLabel}\n{defaultLabel}\nClick to change or clear.";
        }
    }

    private void ProcessFfbEffectNudges(IReadOnlyDictionary<string, DeviceState> devices)
    {
        if (devices.Count == 0)
            return;

        var now = Environment.TickCount64;
        const long coarseAfterMs = 550;
        const long coarseRepeatMs = 200;
        var controls = G920ControlInfo.FfbNudgeControls;
        for (var i = 0; i < controls.Length; i++)
        {
            var target = controls[i];
            var binding = _profile.Bindings.FirstOrDefault(b => b.Target == target);
            if (binding is null || binding.EffectiveSources.Count == 0)
            {
                _ffbNudgePressed[i] = false;
                continue;
            }

            var pressed = MapperEngine.IsPressed(_profile, devices, target);
            if (!pressed)
            {
                _ffbNudgePressed[i] = false;
                continue;
            }

            if (!_ffbNudgePressed[i])
            {
                _ffbNudgePressed[i] = true;
                _ffbNudgeHoldStart[i] = now;
                ApplyFfbEffectNudge(target, coarse: false);
                _ffbNudgeLastFire[i] = now;
                continue;
            }

            // Set-as-default is edge-triggered only (no hold repeat).
            if (G920ControlInfo.IsFfbDefault(target))
                continue;

            if (now - _ffbNudgeHoldStart[i] < coarseAfterMs)
                continue;
            if (now - _ffbNudgeLastFire[i] < coarseRepeatMs)
                continue;

            ApplyFfbEffectNudge(target, coarse: true);
            _ffbNudgeLastFire[i] = now;
        }
    }

    private void ApplyFfbEffectNudge(G920Control target, bool coarse)
    {
        var slider = EffectGainSliderFor(target);
        if (slider is null || !slider.IsEnabled)
            return;
        if (!G920ControlInfo.TryGetSliderBind(target, out var bind))
            return;

        if (G920ControlInfo.IsFfbDefault(target))
        {
            var snap = Math.Clamp(_profile.GetFfbBindDefault(bind.Id, slider.Value), slider.Minimum, slider.Maximum);
            var step = bind.FineStep;
            var decimals = step >= 1 ? 0 : step >= 0.01 ? 2 : 3;
            slider.Value = Math.Round(snap, decimals, MidpointRounding.AwayFromZero);
        }
        else
        {
            var delta = G920ControlInfo.FfbNudgeDelta(target, coarse);
            var step = Math.Abs(delta);
            var decimals = step >= 1 ? 0 : step >= 0.01 ? 2 : 3;
            var next = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
            slider.Value = Math.Round(next, decimals, MidpointRounding.AwayFromZero);
        }

        var valueText = NudgeValueTextFor(target)?.Text;
        ShowEffectChangeToast(bind.Category, bind.Display, string.IsNullOrWhiteSpace(valueText) ? slider.Value.ToString("0.##") : valueText);
    }

    private Slider? EffectGainSliderFor(G920Control target) => target switch
    {
        G920Control.FfbConstantMinus or G920Control.FfbConstantPlus or G920Control.FfbConstantDefault => GainConstantSlider,
        G920Control.FfbSpringMinus or G920Control.FfbSpringPlus or G920Control.FfbSpringDefault => GainSpringSlider,
        G920Control.FfbDamperMinus or G920Control.FfbDamperPlus or G920Control.FfbDamperDefault => GainDamperSlider,
        G920Control.FfbFrictionMinus or G920Control.FfbFrictionPlus or G920Control.FfbFrictionDefault => GainFrictionSlider,
        G920Control.FfbInertiaMinus or G920Control.FfbInertiaPlus or G920Control.FfbInertiaDefault => GainInertiaSlider,
        G920Control.FfbPeriodicMinus or G920Control.FfbPeriodicPlus or G920Control.FfbPeriodicDefault => GainPeriodicSlider,
        G920Control.FfbRampMinus or G920Control.FfbRampPlus or G920Control.FfbRampDefault => GainRampSlider,
        G920Control.FfbCustomMinus or G920Control.FfbCustomPlus or G920Control.FfbCustomDefault => GainCustomSlider,
        G920Control.FfbMasterMinus or G920Control.FfbMasterPlus or G920Control.FfbMasterDefault => GainSlider,
        G920Control.FfbSmoothingMinus or G920Control.FfbSmoothingPlus or G920Control.FfbSmoothingDefault => FeelSmoothingSlider,
        G920Control.FfbPeakSoftMinus or G920Control.FfbPeakSoftPlus or G920Control.FfbPeakSoftDefault => FeelPeakSoftSlider,
        G920Control.FfbSoftStartMinus or G920Control.FfbSoftStartPlus or G920Control.FfbSoftStartDefault => FeelSoftStartSlider,
        G920Control.FfbDeadbandMinus or G920Control.FfbDeadbandPlus or G920Control.FfbDeadbandDefault => FeelDeadbandSlider,
        G920Control.FfbSlewMinus or G920Control.FfbSlewPlus or G920Control.FfbSlewDefault => FeelSlewSlider,
        G920Control.FfbSpikeMinus or G920Control.FfbSpikePlus or G920Control.FfbSpikeDefault => FeelSpikeSlider,
        G920Control.FfbEpsilonMinus or G920Control.FfbEpsilonPlus or G920Control.FfbEpsilonDefault => FeelEpsilonSlider,
        G920Control.FfbCenterStrengthMinus or G920Control.FfbCenterStrengthPlus or G920Control.FfbCenterStrengthDefault => CenterStrengthSlider,
        G920Control.FfbCenterRangeMinus or G920Control.FfbCenterRangePlus or G920Control.FfbCenterRangeDefault => CenterRangeSlider,
        G920Control.FfbCenterDeadzoneMinus or G920Control.FfbCenterDeadzonePlus or G920Control.FfbCenterDeadzoneDefault => CenterDeadzoneSlider,
        G920Control.FfbDampVelMinus or G920Control.FfbDampVelPlus or G920Control.FfbDampVelDefault => DamperVelScaleSlider,
        G920Control.FfbDampDeadMinus or G920Control.FfbDampDeadPlus or G920Control.FfbDampDeadDefault => DamperDeadbandScaleSlider,
        _ => null,
    };

    private TextBlock? NudgeValueTextFor(G920Control target) => target switch
    {
        G920Control.FfbConstantMinus or G920Control.FfbConstantPlus or G920Control.FfbConstantDefault => GainConstantValueText,
        G920Control.FfbSpringMinus or G920Control.FfbSpringPlus or G920Control.FfbSpringDefault => GainSpringValueText,
        G920Control.FfbDamperMinus or G920Control.FfbDamperPlus or G920Control.FfbDamperDefault => GainDamperValueText,
        G920Control.FfbFrictionMinus or G920Control.FfbFrictionPlus or G920Control.FfbFrictionDefault => GainFrictionValueText,
        G920Control.FfbInertiaMinus or G920Control.FfbInertiaPlus or G920Control.FfbInertiaDefault => GainInertiaValueText,
        G920Control.FfbPeriodicMinus or G920Control.FfbPeriodicPlus or G920Control.FfbPeriodicDefault => GainPeriodicValueText,
        G920Control.FfbRampMinus or G920Control.FfbRampPlus or G920Control.FfbRampDefault => GainRampValueText,
        G920Control.FfbCustomMinus or G920Control.FfbCustomPlus or G920Control.FfbCustomDefault => GainCustomValueText,
        G920Control.FfbMasterMinus or G920Control.FfbMasterPlus or G920Control.FfbMasterDefault => GainValueText,
        G920Control.FfbSmoothingMinus or G920Control.FfbSmoothingPlus or G920Control.FfbSmoothingDefault => FeelSmoothingValueText,
        G920Control.FfbPeakSoftMinus or G920Control.FfbPeakSoftPlus or G920Control.FfbPeakSoftDefault => FeelPeakSoftValueText,
        G920Control.FfbSoftStartMinus or G920Control.FfbSoftStartPlus or G920Control.FfbSoftStartDefault => FeelSoftStartValueText,
        G920Control.FfbDeadbandMinus or G920Control.FfbDeadbandPlus or G920Control.FfbDeadbandDefault => FeelDeadbandValueText,
        G920Control.FfbSlewMinus or G920Control.FfbSlewPlus or G920Control.FfbSlewDefault => FeelSlewValueText,
        G920Control.FfbSpikeMinus or G920Control.FfbSpikePlus or G920Control.FfbSpikeDefault => FeelSpikeValueText,
        G920Control.FfbEpsilonMinus or G920Control.FfbEpsilonPlus or G920Control.FfbEpsilonDefault => FeelEpsilonValueText,
        G920Control.FfbCenterStrengthMinus or G920Control.FfbCenterStrengthPlus or G920Control.FfbCenterStrengthDefault => CenterStrengthValueText,
        G920Control.FfbCenterRangeMinus or G920Control.FfbCenterRangePlus or G920Control.FfbCenterRangeDefault => CenterRangeValueText,
        G920Control.FfbCenterDeadzoneMinus or G920Control.FfbCenterDeadzonePlus or G920Control.FfbCenterDeadzoneDefault => CenterDeadzoneValueText,
        G920Control.FfbDampVelMinus or G920Control.FfbDampVelPlus or G920Control.FfbDampVelDefault => DamperVelScaleValueText,
        G920Control.FfbDampDeadMinus or G920Control.FfbDampDeadPlus or G920Control.FfbDampDeadDefault => DamperDeadbandScaleValueText,
        _ => null,
    };

    private async void FfbDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FfbDeviceCombo.SelectedItem is not DeviceRow row)
            return;

        SetFfbSource(row);
        if (!_bridge.IsRunning)
            return;

        // Reattach off the UI thread so Exclusive acquire cannot stall report submits.
        string status = "";
        var ok = await Task.Run(() => _bridge.TryAttachFfb(out status)).ConfigureAwait(true);
        StatusText.Text = ok
            ? status
            : (string.IsNullOrWhiteSpace(status) ? "FFB reattach failed." : status);
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainValueText is null) return;
        GainValueText.Text = $"{GainSlider.Value:P0}";
        _profile.FfbGain = GainSlider.Value;
        _bridge.Ffb.Gain = GainSlider.Value;
    }

    private void EffectGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Sliders fire ValueChanged during InitializeComponent before sibling labels exist.
        if (_effectGainSliderSilent || !AreEffectGainControlsReady()) return;
        SyncEffectGainLabels();
        SyncEffectGainsFromUi();
        ScheduleFfbProfilePush();
    }

    private bool AreEffectGainControlsReady() =>
        GainConstantSlider is not null && GainSpringSlider is not null && GainDamperSlider is not null &&
        GainFrictionSlider is not null && GainInertiaSlider is not null && GainPeriodicSlider is not null &&
        GainRampSlider is not null && GainCustomSlider is not null &&
        GainConstantValueText is not null && GainSpringValueText is not null && GainDamperValueText is not null &&
        GainFrictionValueText is not null && GainInertiaValueText is not null && GainPeriodicValueText is not null &&
        GainRampValueText is not null && GainCustomValueText is not null;

    private void LoadEffectGainsIntoUi(FfbEffectGains? gains)
    {
        gains ??= FfbEffectGains.CreateDefault();
        gains.Clamp();
        if (GainConstantSlider is null) return;
        _effectGainSliderSilent = true;
        try
        {
            GainConstantSlider.Value = gains.ConstantForce;
            GainSpringSlider.Value = gains.SpringForce;
            GainDamperSlider.Value = gains.DamperForce;
            GainFrictionSlider.Value = gains.FrictionForce;
            GainInertiaSlider.Value = gains.InertiaForce;
            GainPeriodicSlider.Value = gains.Periodic;
            GainRampSlider.Value = gains.RampForce;
            GainCustomSlider.Value = gains.CustomForce;
            SyncEffectGainLabels();
        }
        finally
        {
            _effectGainSliderSilent = false;
        }
    }

    private void SyncEffectGainsFromUi()
    {
        if (!AreEffectGainControlsReady()) return;
        _profile.FfbEffectGains ??= FfbEffectGains.CreateDefault();
        var g = _profile.FfbEffectGains;
        g.ConstantForce = GainConstantSlider.Value;
        g.SpringForce = GainSpringSlider.Value;
        g.DamperForce = GainDamperSlider.Value;
        g.FrictionForce = GainFrictionSlider.Value;
        g.InertiaForce = GainInertiaSlider.Value;
        g.Periodic = GainPeriodicSlider.Value;
        g.RampForce = GainRampSlider.Value;
        g.CustomForce = GainCustomSlider.Value;
        g.Clamp();
    }

    private void SyncEffectGainLabels()
    {
        if (!AreEffectGainControlsReady()) return;
        GainConstantValueText.Text = $"{GainConstantSlider.Value:P0}";
        GainSpringValueText.Text = $"{GainSpringSlider.Value:P0}";
        GainDamperValueText.Text = $"{GainDamperSlider.Value:P0}";
        GainFrictionValueText.Text = $"{GainFrictionSlider.Value:P0}";
        GainInertiaValueText.Text = $"{GainInertiaSlider.Value:P0}";
        GainPeriodicValueText.Text = $"{GainPeriodicSlider.Value:P0}";
        GainRampValueText.Text = $"{GainRampSlider.Value:P0}";
        GainCustomValueText.Text = $"{GainCustomSlider.Value:P0}";
    }

    private bool AreFeelControlsReady() =>
        FeelSmoothingSlider is not null && FeelPeakSoftSlider is not null && FeelSoftStartSlider is not null &&
        FeelDeadbandSlider is not null && FeelSlewSlider is not null && FeelSpikeSlider is not null &&
        FeelEpsilonSlider is not null &&
        FeelSmoothingValueText is not null && FeelPeakSoftValueText is not null && FeelSoftStartValueText is not null &&
        FeelDeadbandValueText is not null && FeelSlewValueText is not null && FeelSpikeValueText is not null &&
        FeelEpsilonValueText is not null &&
        ForceCenterCheck is not null && CenterStrengthSlider is not null && CenterRangeSlider is not null &&
        CenterDeadzoneSlider is not null && CenterStrengthValueText is not null &&
        CenterRangeValueText is not null && CenterDeadzoneValueText is not null && CenterSpringPanel is not null &&
        InvertConstantForceCheck is not null && DamperVelScaleSlider is not null && DamperDeadbandScaleSlider is not null &&
        DamperVelScaleValueText is not null && DamperDeadbandScaleValueText is not null;

    private void ForceCenterCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_effectGainSliderSilent || !AreFeelControlsReady()) return;
        SyncFeelLabels();
        SyncFeelFromUi();
        ScheduleFfbProfilePush();
    }

    private void MixOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_effectGainSliderSilent || !AreFeelControlsReady()) return;
        SyncFeelFromUi();
        ScheduleFfbProfilePush();
    }

    private void FeelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_effectGainSliderSilent || !AreFeelControlsReady()) return;
        SyncFeelLabels();
        SyncFeelFromUi();
        ScheduleFfbProfilePush();
    }

    private void LoadFeelIntoUi(FfbOutputFeel? feel)
    {
        feel ??= FfbOutputFeel.CreateDefault();
        feel.Clamp();
        if (!AreFeelControlsReady()) return;
        _effectGainSliderSilent = true;
        try
        {
            FeelSmoothingSlider.Value = feel.SmoothingMs;
            FeelPeakSoftSlider.Value = feel.PeakSoftStart;
            FeelSoftStartSlider.Value = feel.SoftStartMs;
            FeelDeadbandSlider.Value = feel.Deadband;
            FeelSlewSlider.Value = feel.MaxSlewPerSecond;
            FeelSpikeSlider.Value = feel.MaxSpikeStep;
            FeelEpsilonSlider.Value = feel.MagnitudeEpsilon;
            ForceCenterCheck.IsChecked = feel.ForceCenterSpring;
            CenterStrengthSlider.Value = feel.CenterSpringStrength;
            CenterRangeSlider.Value = feel.CenterSpringRange;
            CenterDeadzoneSlider.Value = feel.CenterSpringDeadzone;
            InvertConstantForceCheck.IsChecked = feel.InvertConstantForce;
            DamperVelScaleSlider.Value = feel.DamperVelocityScale;
            DamperDeadbandScaleSlider.Value = feel.DamperDeadbandScale;
            SyncFeelLabels();
        }
        finally
        {
            _effectGainSliderSilent = false;
        }
    }

    private void SyncFeelFromUi()
    {
        if (!AreFeelControlsReady()) return;
        _profile.FfbOutputFeel ??= FfbOutputFeel.CreateDefault();
        var f = _profile.FfbOutputFeel;
        f.SmoothingMs = FeelSmoothingSlider.Value;
        f.PeakSoftStart = FeelPeakSoftSlider.Value;
        f.SoftStartMs = FeelSoftStartSlider.Value;
        f.Deadband = FeelDeadbandSlider.Value;
        f.MaxSlewPerSecond = FeelSlewSlider.Value;
        f.MaxSpikeStep = FeelSpikeSlider.Value;
        f.MagnitudeEpsilon = FeelEpsilonSlider.Value;
        f.ForceCenterSpring = ForceCenterCheck.IsChecked == true;
        f.CenterSpringStrength = CenterStrengthSlider.Value;
        f.CenterSpringRange = CenterRangeSlider.Value;
        f.CenterSpringDeadzone = CenterDeadzoneSlider.Value;
        f.InvertConstantForce = InvertConstantForceCheck.IsChecked == true;
        f.DamperVelocityScale = DamperVelScaleSlider.Value;
        f.DamperDeadbandScale = DamperDeadbandScaleSlider.Value;
        f.Clamp();
    }

    private void SyncFeelLabels()
    {
        if (!AreFeelControlsReady()) return;
        FeelSmoothingValueText.Text = $"{FeelSmoothingSlider.Value:0} ms";
        FeelPeakSoftValueText.Text = $"{FeelPeakSoftSlider.Value:P0}";
        FeelSoftStartValueText.Text = $"{FeelSoftStartSlider.Value:0}";
        FeelDeadbandValueText.Text = FeelDeadbandSlider.Value <= 0.0005
            ? "off"
            : FeelDeadbandSlider.Value.ToString("0.###");
        FeelSlewValueText.Text = FeelSlewSlider.Value <= 0.5
            ? "off"
            : $"{FeelSlewSlider.Value:0}/s";
        FeelSpikeValueText.Text = $"{FeelSpikeSlider.Value:P0}";
        FeelEpsilonValueText.Text = FeelEpsilonSlider.Value <= 0.5
            ? "off"
            : $"{FeelEpsilonSlider.Value:0}";
        CenterStrengthValueText.Text = $"{CenterStrengthSlider.Value:P0}";
        CenterRangeValueText.Text = $"{CenterRangeSlider.Value:P0}";
        CenterDeadzoneValueText.Text = CenterDeadzoneSlider.Value <= 0.0005
            ? "off"
            : $"{CenterDeadzoneSlider.Value:P1}";
        DamperVelScaleValueText.Text = $"{DamperVelScaleSlider.Value:P0}";
        DamperDeadbandScaleValueText.Text = $"{DamperDeadbandScaleSlider.Value:P0}";
        CenterSpringPanel.IsEnabled = ForceCenterCheck.IsChecked == true;
        CenterSpringPanel.Opacity = CenterSpringPanel.IsEnabled ? 1.0 : 0.5;
    }

    private enum FfbValueEditKind
    {
        Percent01to2,   // 0..2 shown as %
        Percent0to1,    // 0..1 shown as %
        Milliseconds,
        SoftStartMs,
        Deadband,
        SlewPerSec,
        Epsilon,
    }

    private void FfbValueLabel_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock label) return;
        if (!TryGetFfbSliderForLabel(label, out var slider, out var kind)) return;
        e.Handled = true;
        BeginFfbValueEdit(label, slider, kind);
    }

    private bool TryGetFfbSliderForLabel(TextBlock label, out Slider slider, out FfbValueEditKind kind)
    {
        if (ReferenceEquals(label, GainValueText)) { slider = GainSlider; kind = FfbValueEditKind.Percent01to2; return true; }
        if (ReferenceEquals(label, GainConstantValueText)) { slider = GainConstantSlider; kind = FfbValueEditKind.Percent01to2; return true; }
        if (ReferenceEquals(label, GainSpringValueText)) { slider = GainSpringSlider; kind = FfbValueEditKind.Percent01to2; return true; }
        if (ReferenceEquals(label, GainDamperValueText)) { slider = GainDamperSlider; kind = FfbValueEditKind.Percent01to2; return true; }
        if (ReferenceEquals(label, GainFrictionValueText)) { slider = GainFrictionSlider; kind = FfbValueEditKind.Percent01to2; return true; }
        if (ReferenceEquals(label, GainInertiaValueText)) { slider = GainInertiaSlider; kind = FfbValueEditKind.Percent01to2; return true; }
        if (ReferenceEquals(label, GainPeriodicValueText)) { slider = GainPeriodicSlider; kind = FfbValueEditKind.Percent01to2; return true; }
        if (ReferenceEquals(label, GainRampValueText)) { slider = GainRampSlider; kind = FfbValueEditKind.Percent01to2; return true; }
        if (ReferenceEquals(label, GainCustomValueText)) { slider = GainCustomSlider; kind = FfbValueEditKind.Percent01to2; return true; }
        if (ReferenceEquals(label, FeelSmoothingValueText)) { slider = FeelSmoothingSlider; kind = FfbValueEditKind.Milliseconds; return true; }
        if (ReferenceEquals(label, FeelPeakSoftValueText)) { slider = FeelPeakSoftSlider; kind = FfbValueEditKind.Percent0to1; return true; }
        if (ReferenceEquals(label, FeelSoftStartValueText)) { slider = FeelSoftStartSlider; kind = FfbValueEditKind.SoftStartMs; return true; }
        if (ReferenceEquals(label, FeelDeadbandValueText)) { slider = FeelDeadbandSlider; kind = FfbValueEditKind.Deadband; return true; }
        if (ReferenceEquals(label, FeelSlewValueText)) { slider = FeelSlewSlider; kind = FfbValueEditKind.SlewPerSec; return true; }
        if (ReferenceEquals(label, FeelSpikeValueText)) { slider = FeelSpikeSlider; kind = FfbValueEditKind.Percent0to1; return true; }
        if (ReferenceEquals(label, FeelEpsilonValueText)) { slider = FeelEpsilonSlider; kind = FfbValueEditKind.Epsilon; return true; }
        if (ReferenceEquals(label, CenterStrengthValueText)) { slider = CenterStrengthSlider; kind = FfbValueEditKind.Percent0to1; return true; }
        if (ReferenceEquals(label, CenterRangeValueText)) { slider = CenterRangeSlider; kind = FfbValueEditKind.Percent0to1; return true; }
        if (ReferenceEquals(label, CenterDeadzoneValueText)) { slider = CenterDeadzoneSlider; kind = FfbValueEditKind.Deadband; return true; }
        if (ReferenceEquals(label, DamperVelScaleValueText)) { slider = DamperVelScaleSlider; kind = FfbValueEditKind.Percent01to2; return true; }
        if (ReferenceEquals(label, DamperDeadbandScaleValueText)) { slider = DamperDeadbandScaleSlider; kind = FfbValueEditKind.Percent0to1; return true; }
        slider = null!;
        kind = default;
        return false;
    }

    private void BeginFfbValueEdit(TextBlock label, Slider slider, FfbValueEditKind kind)
    {
        CancelFfbValueEdit();
        CancelTelemetryValueEdit();
        if (label.Parent is not Panel panel) return;

        var edit = new TextBox
        {
            Width = Math.Max(48, label.Width),
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = label.FontSize,
            Text = FormatFfbEditSeed(slider.Value, kind),
            Tag = (slider, kind),
        };
        DockPanel.SetDock(edit, Dock.Right);

        var index = panel.Children.IndexOf(label);
        panel.Children.Remove(label);
        if (index < 0) panel.Children.Add(edit);
        else panel.Children.Insert(index, edit);

        _ffbValueEditBox = edit;
        _ffbValueEditLabel = label;
        edit.KeyDown += FfbValueEdit_KeyDown;
        edit.LostKeyboardFocus += FfbValueEdit_LostFocus;
        edit.Focus();
        edit.SelectAll();
    }

    private static string FormatFfbEditSeed(double value, FfbValueEditKind kind) => kind switch
    {
        FfbValueEditKind.Percent01to2 or FfbValueEditKind.Percent0to1 => $"{value * 100:0.##}",
        FfbValueEditKind.Milliseconds => $"{value:0}",
        FfbValueEditKind.SoftStartMs => $"{value:0}",
        FfbValueEditKind.Deadband => value <= 0.0005 ? "0" : value.ToString("0.###"),
        FfbValueEditKind.SlewPerSec => value <= 0.5 ? "0" : $"{value:0.##}",
        FfbValueEditKind.Epsilon => $"{value:0}",
        _ => value.ToString("0.###"),
    };

    private void FfbValueEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitFfbValueEdit(save: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelFfbValueEdit();
        }
    }

    private void FfbValueEdit_LostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitFfbValueEdit(save: true);

    private void CommitFfbValueEdit(bool save)
    {
        var edit = _ffbValueEditBox;
        var label = _ffbValueEditLabel;
        if (edit is null || label is null) return;

        // Clear first so LostFocus from removing the TextBox cannot re-enter.
        _ffbValueEditBox = null;
        _ffbValueEditLabel = null;

        Slider? slider = null;
        if (save && edit.Tag is ValueTuple<Slider, FfbValueEditKind> tag)
        {
            slider = tag.Item1;
            if (TryParseFfbEdit(edit.Text, tag.Item2, slider.Minimum, slider.Maximum, out var value))
                slider.Value = value;
        }

        EndFfbValueEdit(edit, label);
        if (AreEffectGainControlsReady())
            SyncEffectGainLabels();
        if (AreFeelControlsReady())
            SyncFeelLabels();
        if (slider is not null && ReferenceEquals(slider, GainSlider) && GainValueText is not null)
            GainValueText.Text = $"{GainSlider.Value:P0}";
    }

    private void CancelFfbValueEdit()
    {
        var edit = _ffbValueEditBox;
        var label = _ffbValueEditLabel;
        if (edit is null || label is null) return;
        _ffbValueEditBox = null;
        _ffbValueEditLabel = null;
        EndFfbValueEdit(edit, label);
    }

    private void EndFfbValueEdit(TextBox edit, TextBlock label)
    {
        edit.KeyDown -= FfbValueEdit_KeyDown;
        edit.LostKeyboardFocus -= FfbValueEdit_LostFocus;
        if (edit.Parent is Panel panel)
        {
            var index = panel.Children.IndexOf(edit);
            panel.Children.Remove(edit);
            if (index < 0) panel.Children.Add(label);
            else panel.Children.Insert(index, label);
        }
    }

    private static bool TryParseFfbEdit(string? text, FfbValueEditKind kind, double min, double max, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        var percent = s.EndsWith('%');
        if (percent) s = s[..^1].Trim();
        if (s.EndsWith("/s", StringComparison.OrdinalIgnoreCase))
            s = s[..^2].Trim();
        if (s.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            s = s[..^2].Trim();
        if (s.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            value = min;
            return true;
        }
        if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n) &&
            !double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out n))
            return false;

        value = kind switch
        {
            FfbValueEditKind.Percent01to2 or FfbValueEditKind.Percent0to1 =>
                percent || n > max + 0.0001 ? n / 100.0 : n,
            _ => n,
        };
        value = Math.Clamp(value, min, max);
        return true;
    }

    private enum TelemetryValueEditKind
    {
        Speed,      // MPH or km/h per Settings → Telemetry speed unit
        RatePerSec, // MPH/s or km/h/s per the same unit setting
        Percent0to2,
        Integer,
        Hertz,
    }

    private sealed record TelemetryEditBinding(
        Func<double> Get,
        Action<double> Set,
        double Min,
        double Max,
        TelemetryValueEditKind Kind);

    private void TelemetryValueLabel_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock label) return;
        if (!TryGetTelemetryEditBinding(label, out var binding)) return;
        e.Handled = true;
        BeginTelemetryValueEdit(label, binding);
    }

    private bool TryGetTelemetryEditBinding(TextBlock label, out TelemetryEditBinding binding)
    {
        if (ReferenceEquals(label, TelemetryHzValueText) && TelemetryHzSlider is not null)
        {
            binding = new(() => TelemetryHzSlider.Value, v => TelemetryHzSlider.Value = v,
                TelemetryHzSlider.Minimum, TelemetryHzSlider.Maximum, TelemetryValueEditKind.Hertz);
            return true;
        }

        if (ReferenceEquals(label, TelemetrySpeedMaxValueText) && TelemetrySpeedMaxSlider is not null)
        {
            binding = new(
                () => TelemetrySpeedMaxSlider.Value,
                v =>
                {
                    TelemetrySpeedMaxSlider.Value = v;
                    SyncGearMaxSliderRanges(TelemetrySpeedMaxSlider.Value);
                },
                TelemetrySpeedMaxSlider.Minimum, TelemetrySpeedMaxSlider.Maximum, TelemetryValueEditKind.Speed);
            return true;
        }

        if (ReferenceEquals(label, TelemetryRpmMinValueText) && TelemetryRpmRange is not null)
        {
            binding = new(
                () => TelemetryRpmRange.LowerValue,
                v => TelemetryRpmRange.LowerValue = Math.Min(v, TelemetryRpmRange.UpperValue),
                TelemetryRpmRange.Minimum, TelemetryRpmRange.Maximum, TelemetryValueEditKind.Integer);
            return true;
        }

        if (ReferenceEquals(label, TelemetryRpmMaxValueText) && TelemetryRpmRange is not null)
        {
            binding = new(
                () => TelemetryRpmRange.UpperValue,
                v =>
                {
                    TelemetryRpmRange.UpperValue = Math.Max(v, TelemetryRpmRange.LowerValue);
                    SyncRpmRedlineSliderRange(
                        TelemetryRpmRange.LowerValue,
                        TelemetryRpmRange.UpperValue,
                        TelemetryRpmRedlineSlider?.Value ?? TelemetryRpmRange.UpperValue);
                },
                TelemetryRpmRange.Minimum, TelemetryRpmRange.Maximum, TelemetryValueEditKind.Integer);
            return true;
        }

        if (TrySliderBinding(label, TelemetryRpmRedlineValueText, TelemetryRpmRedlineSlider, TelemetryValueEditKind.Integer, out binding))
            return true;

        if (TrySliderBinding(label, TelemetryAccelValueText, TelemetryAccelSlider, TelemetryValueEditKind.RatePerSec, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryBrakeDynValueText, TelemetryBrakeDynSlider, TelemetryValueEditKind.RatePerSec, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryCoastValueText, TelemetryCoastSlider, TelemetryValueEditKind.RatePerSec, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryAeroDragValueText, TelemetryAeroDragSlider, TelemetryValueEditKind.Percent0to2, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryGearPullValueText, TelemetryGearPullSlider, TelemetryValueEditKind.Percent0to2, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryGearSettleValueText, TelemetryGearSettleSlider, TelemetryValueEditKind.RatePerSec, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryGear1MaxValueText, TelemetryGear1MaxSlider, TelemetryValueEditKind.Speed, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryGear2MaxValueText, TelemetryGear2MaxSlider, TelemetryValueEditKind.Speed, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryGear3MaxValueText, TelemetryGear3MaxSlider, TelemetryValueEditKind.Speed, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryGear4MaxValueText, TelemetryGear4MaxSlider, TelemetryValueEditKind.Speed, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryGear5MaxValueText, TelemetryGear5MaxSlider, TelemetryValueEditKind.Speed, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryGear6MaxValueText, TelemetryGear6MaxSlider, TelemetryValueEditKind.Speed, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryCrashDumpValueText, TelemetryCrashDumpSlider, TelemetryValueEditKind.Percent0to2, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryRpmBounceAmountValueText, TelemetryRpmBounceAmountSlider, TelemetryValueEditKind.Percent0to2, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryRpmBounceHzValueText, TelemetryRpmBounceHzSlider, TelemetryValueEditKind.Hertz, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryEngineVibrationScaleValueText, TelemetryEngineVibrationScaleSlider, TelemetryValueEditKind.Percent0to2, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryRumbleScaleValueText, TelemetryRumbleScaleSlider, TelemetryValueEditKind.Percent0to2, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryImpactScaleValueText, TelemetryImpactScaleSlider, TelemetryValueEditKind.Percent0to2, out binding))
            return true;
        if (TrySliderBinding(label, TelemetryRoadLoadScaleValueText, TelemetryRoadLoadScaleSlider, TelemetryValueEditKind.Percent0to2, out binding))
            return true;

        binding = null!;
        return false;
    }

    private static bool TrySliderBinding(
        TextBlock label, TextBlock? expected, Slider? slider, TelemetryValueEditKind kind, out TelemetryEditBinding binding)
    {
        if (!ReferenceEquals(label, expected) || slider is null)
        {
            binding = null!;
            return false;
        }

        binding = new(() => slider.Value, v => slider.Value = v, slider.Minimum, slider.Maximum, kind);
        return true;
    }

    private void BeginTelemetryValueEdit(TextBlock label, TelemetryEditBinding binding)
    {
        CancelTelemetryValueEdit();
        CancelFfbValueEdit();
        if (label.Parent is not Panel panel) return;

        // Prefer the laid-out width; for narrow labels (e.g. RPM) ensure room to type 5 digits.
        var width = label.ActualWidth > 1 ? label.ActualWidth : Math.Max(56, label.MinWidth);
        if (width < 52) width = 52;
        var edit = new TextBox
        {
            Width = width,
            MinWidth = width,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = label.HorizontalAlignment,
            FontSize = label.FontSize,
            Text = FormatTelemetryEditSeed(binding.Get(), binding.Kind),
            Tag = binding,
            Margin = label.Margin,
        };
        if (DockPanel.GetDock(label) == Dock.Right)
            DockPanel.SetDock(edit, Dock.Right);

        var index = panel.Children.IndexOf(label);
        panel.Children.Remove(label);
        if (index < 0) panel.Children.Add(edit);
        else panel.Children.Insert(index, edit);

        _telemetryValueEditBox = edit;
        _telemetryValueEditLabel = label;
        edit.KeyDown += TelemetryValueEdit_KeyDown;
        edit.LostKeyboardFocus += TelemetryValueEdit_LostFocus;
        edit.Focus();
        edit.SelectAll();
    }

    private static string FormatTelemetryEditSeed(double value, TelemetryValueEditKind kind) => kind switch
    {
        TelemetryValueEditKind.Percent0to2 => $"{value * 100:0.##}",
        TelemetryValueEditKind.Hertz => $"{value:0}",
        TelemetryValueEditKind.Integer => $"{value:0}",
        TelemetryValueEditKind.Speed or TelemetryValueEditKind.RatePerSec => $"{value:0.##}",
        _ => value.ToString("0.###"),
    };

    private void TelemetryValueEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitTelemetryValueEdit(save: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelTelemetryValueEdit();
        }
    }

    private void TelemetryValueEdit_LostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitTelemetryValueEdit(save: true);

    private void CommitTelemetryValueEdit(bool save)
    {
        var edit = _telemetryValueEditBox;
        var label = _telemetryValueEditLabel;
        if (edit is null || label is null) return;

        _telemetryValueEditBox = null;
        _telemetryValueEditLabel = null;

        if (save && edit.Tag is TelemetryEditBinding binding &&
            TryParseTelemetryEdit(edit.Text, binding.Kind, binding.Min, binding.Max, out var value))
        {
            binding.Set(value);
            RefreshTelemetryTuningLabels();
            if (binding.Kind == TelemetryValueEditKind.Hertz)
            {
                if (TelemetryHzValueText is not null && TelemetryHzSlider is not null)
                    TelemetryHzValueText.Text = $"{(int)TelemetryHzSlider.Value} Hz";
            }

            SaveTelemetrySettingsFromUi();
        }

        EndTelemetryValueEdit(edit, label);
        RefreshTelemetryTuningLabels();
        if (TelemetryHzValueText is not null && TelemetryHzSlider is not null &&
            ReferenceEquals(label, TelemetryHzValueText))
            TelemetryHzValueText.Text = $"{(int)TelemetryHzSlider.Value} Hz";
    }

    private void CancelTelemetryValueEdit()
    {
        var edit = _telemetryValueEditBox;
        var label = _telemetryValueEditLabel;
        if (edit is null || label is null) return;
        _telemetryValueEditBox = null;
        _telemetryValueEditLabel = null;
        EndTelemetryValueEdit(edit, label);
        RefreshTelemetryTuningLabels();
        if (TelemetryHzValueText is not null && TelemetryHzSlider is not null &&
            ReferenceEquals(label, TelemetryHzValueText))
            TelemetryHzValueText.Text = $"{(int)TelemetryHzSlider.Value} Hz";
    }

    private void EndTelemetryValueEdit(TextBox edit, TextBlock label)
    {
        edit.KeyDown -= TelemetryValueEdit_KeyDown;
        edit.LostKeyboardFocus -= TelemetryValueEdit_LostFocus;
        if (edit.Parent is Panel panel)
        {
            var index = panel.Children.IndexOf(edit);
            panel.Children.Remove(edit);
            if (index < 0) panel.Children.Add(label);
            else panel.Children.Insert(index, label);
        }
    }

    private bool TryParseTelemetryEdit(
        string? text, TelemetryValueEditKind kind, double min, double max, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        var percent = s.EndsWith('%');
        if (percent) s = s[..^1].Trim();

        var forcedKmh = false;
        var forcedMph = false;
        if (s.EndsWith("km/h/s", StringComparison.OrdinalIgnoreCase))
        {
            forcedKmh = true;
            s = s[..^6].Trim();
        }
        else if (s.EndsWith("mph/s", StringComparison.OrdinalIgnoreCase))
        {
            forcedMph = true;
            s = s[..^5].Trim();
        }
        else if (s.EndsWith("km/h", StringComparison.OrdinalIgnoreCase))
        {
            forcedKmh = true;
            s = s[..^4].Trim();
        }
        else if (s.EndsWith("mph", StringComparison.OrdinalIgnoreCase))
        {
            forcedMph = true;
            s = s[..^3].Trim();
        }
        else if (s.EndsWith("hz", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^2].Trim();
        }
        else if (s.EndsWith("/s", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^2].Trim();
        }

        // "34 · 55" → take the first number.
        var sep = s.IndexOf('·');
        if (sep < 0) sep = s.IndexOf('|');
        if (sep >= 0) s = s[..sep].Trim();

        if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n) &&
            !double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out n))
            return false;

        value = kind switch
        {
            TelemetryValueEditKind.Percent0to2 => percent || n > max + 0.0001 ? n / 100.0 : n,
            TelemetryValueEditKind.Speed or TelemetryValueEditKind.RatePerSec =>
                forcedKmh ? FromKmh(n) :
                forcedMph ? (_telemetryUseMph ? n : ToKmh(n)) :
                n,
            _ => n,
        };
        value = Math.Clamp(value, min, max);
        return true;
    }

    private void InvertFfbCheck_Changed(object sender, RoutedEventArgs e)
    {
        _profile.FfbInvert = InvertFfbCheck.IsChecked == true;
        _bridge.Ffb.Invert = _profile.FfbInvert;
    }

    private sealed class DeviceRow(InputDeviceInfo info)
    {
        public InputDeviceInfo Info { get; } = info;
        public string Id => Info.Id;
        public string Display =>
            $"{Info.Kind}: {Info.Name}  [{Info.AxisCount} axes, {Info.ButtonCount} btn{(Info.SupportsForceFeedback ? ", FFB" : "")}]";
    }

    private sealed class BindingRow : INotifyPropertyChanged
    {
        private readonly Binding _binding;
        private readonly Func<string?, string> _nameResolver;

        private readonly Func<int> _gearReverseButton;

        public BindingRow(
            G920Control target,
            Binding binding,
            Func<string?, string> nameResolver,
            Func<int> gearReverseButton)
        {
            Target = target;
            _binding = binding;
            _nameResolver = nameResolver;
            _gearReverseButton = gearReverseButton;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public G920Control Target { get; }
        public string TargetName => G920ControlInfo.DisplayName(Target);

        private bool IsAxisTarget => G920ControlInfo.IsAxis(Target);
        private bool HasAxisSource => _binding.EffectiveSources.Any(s => !string.IsNullOrEmpty(s.Axis));

        public Visibility DeadzoneVisibility =>
            IsAxisTarget || HasAxisSource ? Visibility.Visible : Visibility.Collapsed;

        public string DeadzoneLabel => IsAxisTarget ? "Deadzone" : "Threshold";
        public double DeadzoneMinimum => IsAxisTarget ? 0 : 0.05;
        public double DeadzoneMaximum => IsAxisTarget ? 0.5 : 0.95;
        public string DeadzoneToolTip => IsAxisTarget
            ? "Ignore small axis movement near rest"
            : "Axis→button activates at or above this threshold";

        public double Deadzone
        {
            get
            {
                if (IsAxisTarget) return _binding.Deadzone;
                return _binding.Deadzone > 0.001 ? _binding.Deadzone : 0.5;
            }
            set
            {
                var clamped = Math.Clamp(value, DeadzoneMinimum, DeadzoneMaximum);
                if (Math.Abs(_binding.Deadzone - clamped) < 0.0001) return;
                _binding.Deadzone = clamped;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Deadzone)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeadzoneText)));
            }
        }

        public string DeadzoneText => $"{Deadzone:P0}";

        public string SourceText
        {
            get
            {
                var outBtn = Target == G920Control.GearR
                    ? $" → btn {Math.Clamp(_gearReverseButton() <= 0 ? 19 : _gearReverseButton(), 1, 19)}"
                    : "";
                var sources = _binding.EffectiveSources;
                if (sources.Count == 0) return "(click to bind)" + outBtn;
                var inv = _binding.Invert ? " · inverted" : "";
                var thr = HasAxisSource && !IsAxisTarget ? $" · thr {Deadzone:P0}" : "";
                if (sources.Count == 1)
                    return FormatSource(sources[0], _nameResolver, IsAxisTarget) + thr + inv + outBtn;
                var parts = sources.Select(s => FormatSource(s, _nameResolver, IsAxisTarget));
                return string.Join(" + ", parts) + thr + inv + outBtn;
            }
        }
    }

    private static string FormatSource(SourceRef source, Func<string?, string> nameResolver, bool axisTarget)
    {
        var dev = nameResolver(source.DeviceId);
        if (source.Axis is not null)
        {
            var mode = axisTarget
                ? "axis"
                : source.AxisFromCenter ? "axis→btn·center" : "axis→btn";
            return $"{dev} · {source.Axis} ({mode})";
        }
        if (source.IsHat) return $"{dev} · hat";
        if (source.Button is int b) return $"{dev} · button {b}";
        return dev;
    }
}

