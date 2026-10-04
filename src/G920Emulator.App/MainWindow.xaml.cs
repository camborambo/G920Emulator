using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using G920Emulator.Core.Bridge;
using G920Emulator.Core.Models;
using G920Emulator.Core.Profiles;
using G920Emulator.Core.Setup;
using G920Emulator.VirtualHid;

namespace G920Emulator.App;

public partial class MainWindow : Window
{
    private readonly BridgeService _bridge = new();
    private readonly VirtualG920Device _virtual = new();
    private readonly ProfileStore _profiles = new();
    private readonly ObservableCollection<DeviceRow> _devices = [];
    private readonly ObservableCollection<BindingRow> _bindings = [];
    private readonly DispatcherTimer _uiTimer;
    private MappingProfile _profile = MappingProfile.CreateDefault();
    private bool _suppressProfileCombo;
    private bool _bindingDialogOpen;
    private bool _ffbTestSliderSilent;
    private CancellationTokenSource? _ffbPulseCts;

    public MainWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        BindingList.ItemsSource = _bindings;
        FfbDeviceCombo.ItemsSource = _devices;

        _bridge.AttachVirtualDevice(_virtual);
        _profile = _profiles.LoadLastOrDefault();
        LoadProfileIntoUi(_profile);
        RefreshSavedProfilesCombo(_profile.Name);

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _uiTimer.Tick += (_, _) => RefreshLiveUi();
        _uiTimer.Start();

        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _bridge.BindFfbWindow(hwnd);
            GHubGuard.StartAppWatch();
            RefreshDevices();
            UpdateDependencyUi();
            ApplyFfbDebugVisibility();
        };
        Closing += OnClosing;
    }

    private static readonly Brush OkChipBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0x4A));
    private static readonly Brush BadChipBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0x1F, 0x2A));
    private static readonly Brush SoftChipBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0x5A, 0x1F));
    private static readonly Brush WarnBannerBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x22, 0x18));
    private static readonly Brush OkBannerBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x29));
    private static readonly Brush WarnBorderBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x6A, 0x3A));
    private static readonly Brush OkBorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x40));

    private bool _exitTeardownStarted;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitTeardownStarted)
            return;

        e.Cancel = true;
        _exitTeardownStarted = true;
        _uiTimer.Stop();
        _ffbPulseCts?.Cancel();
        try { _bridge.Ffb.ClearTestOverride(); } catch { /* ignore */ }
        try
        {
            SyncProfileFromUi();
            if (!string.IsNullOrWhiteSpace(ProfileNameBox.Text))
                _profiles.Save(_profile, ProfileNameBox.Text.Trim());
        }
        catch { /* ignore autosave failures on close */ }

        StatusText.Text = "Shutting down…";
        // Tear down off the UI thread, then close for real.
        _ = Task.Run(() =>
        {
            try { GHubGuard.StopAppWatch(); } catch { /* ignore */ }
            try { _bridge.Dispose(); } catch { /* ignore */ }
            try { _virtual.Dispose(); } catch { /* ignore */ }
            try
            {
                Dispatcher.Invoke(() =>
                {
                    try { Close(); }
                    catch { /* ignore */ }
                });
            }
            catch { /* ignore */ }
        });
    }

    private DependencyReport ProbeDependencies() =>
        DependencyChecker.CheckAll(() =>
        {
            var ok = WinUHidNative.TryProbeDriver(out var msg);
            return (ok, msg);
        });

    private void UpdateDependencyUi()
    {
        var report = ProbeDependencies();
        var win = report.WinUHid;
        var hide = report.HidHide;

        if (win is not null)
        {
            WinUHidChip.Background = win.IsInstalled ? OkChipBrush : BadChipBrush;
            WinUHidChipText.Text = win.IsInstalled ? "WinUHid · Installed (required)" : "WinUHid · Missing (required)";
        }

        if (hide is not null)
        {
            HidHideChip.Background = hide.IsInstalled ? OkChipBrush : BadChipBrush;
            HidHideChipText.Text = hide.IsInstalled ? "HidHide · Installed (required)" : "HidHide · Missing (required)";
        }

        if (report.LogitechSdk is { } sdk)
        {
            LogiSdkChip.Background = sdk.IsInstalled ? OkChipBrush : BadChipBrush;
            LogiSdkChipText.Text = sdk.IsInstalled ? "Logitech SDK · Installed (required)" : "Logitech SDK · Missing (required)";
        }

        DependenciesBanner.Background = report.ReadyForGames ? OkBannerBrush : WarnBannerBrush;
        DependenciesBanner.BorderBrush = report.ReadyForGames ? OkBorderBrush : WarnBorderBrush;
        DependenciesActionButton.Content = report.ReadyForGames ? "Manage dependencies…" : "Fix dependencies…";

        if (report.ReadyForGames)
        {
            DependenciesSummaryText.Text =
                "WinUHid, HidHide and Logitech SDK ready. G HUB guard active. Use Dependencies → Configure HidHide if needed, then Start bridge.";
            StatusText.Text = "Dependencies OK. Map controls, then Start bridge.";
        }
        else
        {
            var missing = report.MissingRequiredNames;
            DependenciesSummaryText.Text =
                $"{string.Join(", ", missing)} required and missing. Mapping preview still works, but install them before playing.";
            StatusText.Text = $"{string.Join(" + ", missing)} missing — open Dependencies…";
        }
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

    private void AuthorLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void RefreshDevices(bool restoreHidden = false)
    {
        if (restoreHidden)
            ClearHiddenDevices();

        var hidden = GetHiddenDeviceIds();
        var list = _bridge.RefreshDevices()
            .Where(d => !hidden.Contains(d.Id))
            .ToList();
        var selectedFfb = (FfbDeviceCombo.SelectedItem as DeviceRow)?.Id;
        _devices.Clear();
        foreach (var d in list)
            _devices.Add(new DeviceRow(d));

        if (selectedFfb is not null)
            FfbDeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.Id == selectedFfb);
        else if (!string.IsNullOrEmpty(_profile.FfbSourceDeviceId))
            FfbDeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.Id == _profile.FfbSourceDeviceId)
                ?? _devices.FirstOrDefault(d => d.Info.SupportsForceFeedback);
        else
            FfbDeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.Info.SupportsForceFeedback);

        RebuildBindingRows();
        StatusText.Text = restoreHidden || hidden.Count == 0
            ? $"Found {_devices.Count} device(s). Profiles: {_profiles.ProfilesDirectory}"
            : $"Found {_devices.Count} device(s) ({hidden.Count} hidden). Profiles: {_profiles.ProfilesDirectory}";
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
        settings.HiddenDeviceIds = [];
        _profiles.SaveSettings(settings);
    }

    private void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceRow row)
        {
            StatusText.Text = "Select a device to remove from the list.";
            return;
        }

        var settings = _profiles.LoadSettings();
        settings.HiddenDeviceIds ??= [];
        if (!settings.HiddenDeviceIds.Contains(row.Id, StringComparer.OrdinalIgnoreCase))
            settings.HiddenDeviceIds.Add(row.Id);
        _profiles.SaveSettings(settings);

        var wasFfb = FfbDeviceCombo.SelectedItem is DeviceRow ffb && ffb.Id == row.Id;
        _devices.Remove(row);
        if (wasFfb)
        {
            FfbDeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.Info.SupportsForceFeedback);
            _profile.FfbSourceDeviceId = (FfbDeviceCombo.SelectedItem as DeviceRow)?.Id;
        }

        RebuildBindingRows();
        StatusText.Text = $"Removed '{row.Info.Name}' from the list. Refresh devices to show it again.";
    }

    private void LoadProfileIntoUi(MappingProfile profile)
    {
        _profile = profile;
        _bridge.Profile = profile;
        ProfileNameBox.Text = string.IsNullOrWhiteSpace(profile.Name) ? "My Rig" : profile.Name;
        GainSlider.Value = profile.FfbGain;
        InvertFfbCheck.IsChecked = profile.FfbInvert;
        foreach (ComboBoxItem item in ShifterModeCombo.Items)
        {
            if (Equals(item.Tag?.ToString(), profile.ShifterMode.ToString()))
            {
                ShifterModeCombo.SelectedItem = item;
                break;
            }
        }
        RebuildBindingRows();
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

    private void RefreshLiveUi()
    {
        if (!_bridge.IsRunning)
        {
            var devices = _bridge.InputHub.Poll();
            var mapped = new Core.Mapping.MapperEngine().Map(_profile, devices);
            ApplyLive(mapped);
        }
        else
        {
            ApplyLive(_bridge.LatestState);
        }

        RefreshFfbDiagnostics();
    }

    private void FfbDebugCheck_Changed(object sender, RoutedEventArgs e) => ApplyFfbDebugVisibility();

    private void ApplyFfbDebugVisibility()
    {
        var on = FfbDebugCheck?.IsChecked == true;
        if (FfbDebugPanel is not null)
            FfbDebugPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) RefreshFfbDiagnostics();
    }

    private void RefreshFfbDiagnostics()
    {
        if (FfbDiagText is null) return;
        if (FfbDebugCheck?.IsChecked != true)
            return;

        var d = _bridge.GetFfbDiagnostics();
        var ageIn = d.LastIncomingUtc is DateTime t
            ? $"{(DateTime.UtcNow - t).TotalSeconds:0.0}s ago"
            : "never";
        var ageOut = d.LastApplyUtc is DateTime a
            ? $"{(DateTime.UtcNow - a).TotalSeconds:0.0}s ago"
            : "never";

        FfbDiagText.Text =
            $"{d.Status}\n" +
            $"Attached: {d.IsAttached}  TestOverride: {d.TestOverrideActive}  Vendor: {d.VendorProfile}\n" +
            $"Device: {d.DeviceName ?? "(none)"}\n" +
            $"Coop: {d.CooperativeLevel}\n" +
            $"Axis: {d.AxisInfo}\n" +
            $"OEM FFB: {d.OemFfbStatus}  torque={d.OemFfbTorque:+0.00;-0.00;0.00}  playing={d.OemFfbPlaying}\n" +
            $"OEM effects seen: {(string.IsNullOrEmpty(d.OemFfbTypesSeen) ? "(none)" : d.OemFfbTypesSeen)}\n" +
            $"OEM effects playing: {(string.IsNullOrEmpty(d.OemFfbTypesPlaying) ? "(none)" : d.OemFfbTypesPlaying)}\n" +
            $"Host writes: {d.HostWriteCount}  last: {d.LastHostWriteHex}\n" +
            $"HID++ writes: {d.HidppWriteCount}  dl: {d.HidppDownloadCount}  play: {d.HidppPlayCount}\n" +
            $"HID++ slots: {d.HidppSlotsPlaying}/{d.HidppSlotsInUse} playing  last: {d.HidppLastFunction}\n" +
            $"HID++ torque: {d.HidppCurrentTorque:+0.00;-0.00;0.00}\n" +
            $"Path: {(string.IsNullOrEmpty(d.HostPathHint) ? "(settling…)" : d.HostPathHint)}\n" +
            $"Game/HID++ in: {d.LastIncomingTorque:+0.00;-0.00;0.00} ({d.IncomingUpdateCount} updates, {ageIn})\n" +
            $"Applied out: {d.LastCommandTorque:+0.00;-0.00;0.00}  mag={d.LastMagnitude} ({d.ApplyCount} applies, {ageOut})\n" +
            $"Error: {d.LastError ?? "(none)"}";
    }

    private bool EnsureFfbAttachedForTest()
    {
        SyncProfileFromUi();
        var hwnd = new WindowInteropHelper(this).Handle;
        _bridge.BindFfbWindow(hwnd);
        _bridge.Profile = _profile;

        if (_bridge.Ffb.IsReady &&
            string.Equals(_bridge.Ffb.ActiveDeviceId, _profile.FfbSourceDeviceId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!_bridge.TryAttachFfb(out var status))
        {
            StatusText.Text = status;
            RefreshFfbDiagnostics();
            return false;
        }

        StatusText.Text = status;
        RefreshFfbDiagnostics();
        return _bridge.Ffb.IsReady;
    }

    private void FfbAttach_Click(object sender, RoutedEventArgs e)
    {
        EnsureFfbAttachedForTest();
    }

    private void FfbTestLeft_Click(object sender, RoutedEventArgs e) => ApplyFfbTestTorque(-0.45f);

    private void FfbTestRight_Click(object sender, RoutedEventArgs e) => ApplyFfbTestTorque(0.45f);

    private void FfbTestCenter_Click(object sender, RoutedEventArgs e) => ApplyFfbTestTorque(0f);

    private async void FfbTestPulse_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureFfbAttachedForTest())
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

    private void ApplyFfbTestTorque(float torque)
    {
        if (!EnsureFfbAttachedForTest())
            return;
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

        var pressed = new List<string>();
        if (s.ButtonA) pressed.Add("A");
        if (s.ButtonB) pressed.Add("B");
        if (s.ButtonX) pressed.Add("X");
        if (s.ButtonY) pressed.Add("Y");
        if (s.ButtonLb || s.PaddleLeft) pressed.Add("LB");
        if (s.ButtonRb || s.PaddleRight) pressed.Add("RB");
        if (s.Hat >= 0) pressed.Add($"Hat{s.Hat}");
        ButtonsText.Text = "Buttons: " + (pressed.Count == 0 ? "—" : string.Join(" ", pressed));
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshDevices(restoreHidden: true);

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The Logitech SDK is bundled, so install it rather than nag.
            if (!DependencyChecker.CheckLogitechSteeringSdk().IsInstalled)
                G920OemRegistration.InstallSteeringWheelSdk();

            var deps = ProbeDependencies();
            if (!deps.ReadyForGames)
            {
                var proceed = MessageBox.Show(
                    $"{string.Join(", ", deps.MissingRequiredNames)} required and not installed.\n\n" +
                    "WinUHid exposes the virtual G920 to games.\n" +
                    "HidHide hides your physical pad so the game only sees the G920.\n" +
                    "Logitech SDK lets SDK games (NFS Heat, etc.) recognise it as a wheel.\n\n" +
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
            if (FfbDeviceCombo.SelectedItem is DeviceRow ffbRow)
                _profile.FfbSourceDeviceId = ffbRow.Id;
            else if (string.IsNullOrWhiteSpace(_profile.FfbSourceDeviceId))
            {
                var auto = _devices.FirstOrDefault(d => d.Info.SupportsForceFeedback);
                if (auto is not null)
                {
                    FfbDeviceCombo.SelectedItem = auto;
                    _profile.FfbSourceDeviceId = auto.Id;
                }
            }

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            StatusText.Text = "Starting bridge…";

            // Whitelist + hide DualSense from games. Emulator stays whitelisted so binds still work.
            // Do NOT run full GHubConflictRepair here — it previously removed WinUHid enumerators.
            var hidHideNote = "";
            await Task.Run(() =>
            {
                hidHideNote = DependencyChecker.ConfigureHidHideFully().Message;
                try
                {
                    // Safe leftovers only: OEM + Logi Col01 + disconnected C262 orphans.
                    LogiJoyHidBinder.TryRemoveLogitechCol01();
                    G920OemRegistration.EnsureRegistered();
                    _ = GHubConflictRepair.RemoveDisconnectedVirtualNodes();
                }
                catch { /* ignore */ }
                _bridge.Start();
            }).ConfigureAwait(true);

            // Re-enumerate DI after Start so bind/preview still see physical pads (whitelist).
            try { RefreshDevices(restoreHidden: false); } catch { /* ignore */ }

            StopButton.IsEnabled = true;

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

            var ffbStatus = _bridge.LastFfbStatus;
            var col01Missing = !_virtual.IsPreviewMode &&
                               !string.IsNullOrWhiteSpace(_virtual.LastError) &&
                               _virtual.LastError.Contains("Col01", StringComparison.OrdinalIgnoreCase);
            StatusText.Text = _virtual.IsPreviewMode
                ? "Bridge running in preview mode (no virtual HID)."
                : col01Missing
                    ? "Bridge running but virtual G920 is NOT visible to games — Stop, then Start again."
                    : string.IsNullOrWhiteSpace(ffbStatus)
                        ? "Bridge running — virtual G920 active."
                        : $"Bridge running — {ffbStatus}";
            DriverText.Text = _virtual.LastError ?? "Virtual G920 started.";
            if (!string.IsNullOrWhiteSpace(hidHideNote))
                DriverText.Text = $"{DriverText.Text} {hidHideNote}";
            if (!string.IsNullOrWhiteSpace(ffbStatus) && !_bridge.Ffb.IsReady)
                DriverText.Text = $"{DriverText.Text} {ffbStatus}";
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
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            MessageBox.Show(ex.Message, "G920 Emulator", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = ex.Message;
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        StatusText.Text = "Stopping bridge…";
        try
        {
            // Tear down off the UI thread — WinUHid stop + guard pnputil must not freeze the window.
            await Task.Run(() => _bridge.Stop()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Stop failed: " + ex.Message;
        }

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusText.Text = "Bridge stopped.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncProfileFromUi();
            var name = RequireProfileName();
            _profiles.Save(_profile, name);
            RefreshSavedProfilesCombo(name);
            StatusText.Text = $"Saved profile '{name}' to {_profiles.ProfilesDirectory}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveAsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncProfileFromUi();
            var suggested = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? "My Rig" : ProfileNameBox.Text.Trim();
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

            ProfileNameBox.Text = name;
            _profiles.Save(_profile, name);
            RefreshSavedProfilesCombo(name);
            StatusText.Text = $"Saved profile '{name}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save As", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var name = SavedProfilesCombo.SelectedItem as string ?? ProfileNameBox.Text.Trim();
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
        ProfileNameBox.Text = name;
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
            LoadProfileIntoUi(_profiles.Load(name));
            var settings = _profiles.LoadSettings();
            settings.LastProfileName = name;
            _profiles.SaveSettings(settings);
            StatusText.Text = $"Loaded profile '{name}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Load profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AutoSaveCurrent()
    {
        var name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "My Rig";
        ProfileNameBox.Text = name;
        _profiles.Save(_profile, name);
        RefreshSavedProfilesCombo(name);
    }

    private string RequireProfileName()
    {
        var name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Enter a profile name first.");
        return name;
    }

    private string RequireProfileNameSafe()
    {
        var name = ProfileNameBox.Text.Trim();
        return string.IsNullOrWhiteSpace(name) ? "profile" : name;
    }

    private string? PromptForName(string title, string suggested)
    {
        var dialog = new ProfileNameDialog(suggested) { Owner = this, Title = title };
        return dialog.ShowDialog() == true ? dialog.ProfileName : null;
    }

    private void SyncProfileFromUi()
    {
        _profile.Name = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? _profile.Name : ProfileNameBox.Text.Trim();
        _profile.FfbGain = GainSlider.Value;
        _profile.FfbInvert = InvertFfbCheck.IsChecked == true;
        if (FfbDeviceCombo.SelectedItem is DeviceRow row)
            _profile.FfbSourceDeviceId = row.Id;
        if (ShifterModeCombo.SelectedItem is ComboBoxItem modeItem &&
            Enum.TryParse<ShifterMode>(modeItem.Tag?.ToString(), out var mode))
            _profile.ShifterMode = mode;
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

    private void OpenBindDialog(G920Control target)
    {
        var binding = _profile.GetOrCreate(target);
        _bindingDialogOpen = true;
        try
        {
            var dlg = new BindInputWindow(
                target,
                binding,
                _profile,
                () => _bridge.InputHub.Poll(),
                () => _bridge.RefreshDevices(),
                ResolveDeviceName)
            {
                Owner = this,
            };

            if (dlg.ShowDialog() == true && dlg.Applied)
            {
                RebuildBindingRows();
                BindingList.SelectedItem = _bindings.FirstOrDefault(b => b.Target == target);
                var sources = binding.EffectiveSources;
                var label = G920ControlInfo.DisplayName(target);
                StatusText.Text = sources.Count == 0
                    ? $"{label} cleared."
                    : sources.Count == 1
                        ? $"Bound {label} ← {FormatSource(sources[0], ResolveDeviceName, G920ControlInfo.IsAxis(target))}"
                        : $"Bound {label} ← {sources.Count} sources";
                _bridge.Profile = _profile;
            }
        }
        finally
        {
            _bindingDialogOpen = false;
        }
    }

    private void FfbDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FfbDeviceCombo.SelectedItem is not DeviceRow row)
            return;

        _profile.FfbSourceDeviceId = row.Id;
        if (!_bridge.IsRunning)
            return;

        if (_bridge.TryAttachFfb(out var status))
            StatusText.Text = status;
        else
            StatusText.Text = string.IsNullOrWhiteSpace(status) ? "FFB reattach failed." : status;
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainValueText is null) return;
        GainValueText.Text = $"{GainSlider.Value:P0}";
        _profile.FfbGain = GainSlider.Value;
        _bridge.Ffb.Gain = GainSlider.Value;
    }

    private void InvertFfbCheck_Changed(object sender, RoutedEventArgs e)
    {
        _profile.FfbInvert = InvertFfbCheck.IsChecked == true;
        _bridge.Ffb.Invert = _profile.FfbInvert;
    }

    private void ShifterModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShifterModeCombo.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<ShifterMode>(item.Tag?.ToString(), out var mode))
            _profile.ShifterMode = mode;
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

