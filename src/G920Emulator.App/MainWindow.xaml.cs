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
using G920Emulator.Core.Ffb;
using G920Emulator.Core.Setup;
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
    private bool _bindingDialogOpen;
    private bool _ffbTestSliderSilent;
    private bool _effectGainSliderSilent;
    private string? _shownLinkStatus;
    private CancellationTokenSource? _ffbPulseCts;
    private long _lastFfbDiagUiTick;
    private bool _refreshDevicesBusy;
    /// <summary>True while Start/Stop/exit tears down DI — UI must not Poll InputHub.</summary>
    private volatile bool _bridgeBusy;
    private int _livePreviewPollInFlight;

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
        DeviceList.ItemsSource = _devices;
        BindingList.ItemsSource = _bindings;
        FfbDeviceCombo.ItemsSource = _devices;

        _bridge.AttachVirtualDevice(_virtual);
        _profile = _profiles.LoadLastOrDefault();
        LoadProfileIntoUi(_profile);
        RefreshSavedProfilesCombo(_profile.Name);
        RefreshFfbProfilesCombo(_profile.FfbProfileName);

        // 16 ms so live axis meters track the ~500 Hz bridge without looking lagged.
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _uiTimer.Tick += (_, _) => RefreshLiveUi();
        _uiTimer.Start();

        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _bridge.BindFfbWindow(hwnd);
            GHubGuard.StartAppWatch();
            _ = RefreshDevicesAsync(restoreHidden: false);
            UpdateDependencyUi();
            ApplyFfbDebugVisibility();
            RefreshDebugSessionUi();
        };
        Activated += (_, _) =>
        {
            // Re-bind HWND after alt-tab without doing DI work on the UI thread.
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                _bridge.BindFfbWindow(hwnd);
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
        {
            // Second Close after teardown — allow the window to shut down.
            e.Cancel = false;
            return;
        }

        e.Cancel = true;
        _exitTeardownStarted = true;
        _bridgeBusy = true;
        _uiTimer.Stop();
        _ffbProfilePushTimer.Stop();
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
        try
        {
            VirtualG920Device.LogHostIngress = false;
            _debugSession.Dispose();
        }
        catch { /* ignore */ }

        // Tear down off the UI thread. Cap wait — WinUHid/DI can hang forever and
        // Dispatcher.Invoke would deadlock if the UI were still inside a DI Poll.
        _ = Task.Run(() =>
        {
            try { GHubGuard.StopAppWatch(); } catch { /* ignore */ }

            var dispose = Task.Run(() =>
            {
                try { _bridge.Dispose(); } catch { /* ignore */ }
                try { _virtual.Dispose(); } catch { /* ignore */ }
            });
            try { dispose.Wait(3500); } catch { /* ignore */ }

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { Close(); }
                    catch { /* ignore */ }
                }));
            }
            catch { /* ignore */ }

            // Hard exit so abandoned native teardown / LongRunning tasks cannot keep the EXE alive.
            try { Thread.Sleep(1200); } catch { /* ignore */ }
            Environment.Exit(0);
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
                "WinUHid, HidHide and Logitech SDK ready. G HUB guard active. Configure HidHide yourself (Dependencies or HidHide Client), then Start bridge.";
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

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var versionLabel = version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";

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
        ProfileNameBox.Text = string.IsNullOrWhiteSpace(_profile.Name) ? "My Rig" : _profile.Name;
        GainSlider.Value = _profile.FfbGain;
        InvertFfbCheck.IsChecked = _profile.FfbInvert;
        LoadEffectGainsIntoUi(_profile.FfbEffectGains);
        LoadFeelIntoUi(_profile.FfbOutputFeel);
        RefreshFfbProfilesCombo(_profile.FfbProfileName);
        foreach (ComboBoxItem item in ShifterModeCombo.Items)
        {
            if (Equals(item.Tag?.ToString(), _profile.ShifterMode.ToString()))
            {
                ShifterModeCombo.SelectedItem = item;
                break;
            }
        }
        if (!string.IsNullOrEmpty(_profile.FfbSourceDeviceId))
            FfbDeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.Id == _profile.FfbSourceDeviceId)
                ?? FfbDeviceCombo.SelectedItem;
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
                var devices = _bridge.InputHub.Poll();
                var mapped = new Core.Mapping.MapperEngine().Map(profile, devices);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_bridgeBusy && !_bridge.IsRunning)
                        ApplyLive(mapped);
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

        // Cap UI DI/diagnostics work — was every 50ms and also polled the FFB joystick
        // on the UI thread (hangs when alt-tabbing while the bridge applies torque).
        var now = Environment.TickCount64;
        if (now - _lastFfbDiagUiTick < 250)
            return;
        _lastFfbDiagUiTick = now;

        var d = _bridge.GetFfbDiagnostics();
        // Use bridge-published rim only — never Poll the exclusive FFB device here.
        var rim = d.FfbRimSteer;
        var ageIn = d.LastIncomingUtc is DateTime t
            ? $"{(DateTime.UtcNow - t).TotalSeconds:0.0}s ago"
            : "never";
        var ageOut = d.LastApplyUtc is DateTime a
            ? $"{(DateTime.UtcNow - a).TotalSeconds:0.0}s ago"
            : "never";

        var link = _bridge.LinkStatus;
        FfbDiagText.Text =
            $"{d.Status}\n" +
            $"Bridge link: {(string.IsNullOrWhiteSpace(link) ? "OK" : link)}\n" +
            $"Attached: {d.IsAttached}  TestOverride: {d.TestOverrideActive}  AutoCenterTest: {d.TestAutoCenterActive}  Vendor: {d.VendorProfile}\n" +
            $"Device: {d.DeviceName ?? "(none)"}\n" +
            $"Coop: {d.CooperativeLevel}\n" +
            $"Axis: {d.AxisInfo}\n" +
            $"OEM FFB: {d.OemFfbStatus}  torque={d.OemFfbTorque:+0.00;-0.00;0.00}  playing={d.OemFfbPlaying}\n" +
            $"Rim (spring): {rim:+0.00;-0.00;0.00}  hw auto-center (test): {d.HardwareAutoCenter?.ToString() ?? "?"}\n" +
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

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            StatusText.Text = "Starting bridge…";
            _bridgeBusy = true;

            // Do not auto-configure HidHide — leave whitelist / hide lists to the user
            // (Dependencies → Configure HidHide, or HidHide Client).
            // Do NOT run full GHubConflictRepair here — it previously removed WinUHid enumerators.
            try
            {
                await Task.Run(() =>
                {
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
            }
            finally
            {
                _bridgeBusy = false;
            }

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
            // Keep the footer short; FFB details live under FFB debug.
            DriverText.Text = !string.IsNullOrWhiteSpace(_virtual.LastError)
                ? _virtual.LastError
                : _bridge.Ffb.IsReady
                    ? "Virtual G920 started."
                    : "Virtual G920 started. Pick an FFB-capable device under Force feedback if you want force feedback.";
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
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            MessageBox.Show(ex.Message, "G920 Emulator", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = ex.Message;
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        StatusText.Text = "Stopping bridge…";
        _bridgeBusy = true;
        try
        {
            // Tear down off the UI thread — WinUHid stop + FFB detach must not freeze the window.
            var stop = Task.Run(() => _bridge.Stop());
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
            _bridgeBusy = false;
        }

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusText.Text = "Bridge stopped.";
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
            // Load + LoadProfileIntoUi both ApplyLinkedFfbProfile (linked FFB gains/feel).
            var loaded = _profiles.Load(name);
            LoadProfileIntoUi(loaded);
            var settings = _profiles.LoadSettings();
            settings.LastProfileName = name;
            settings.LastFfbProfileName = loaded.FfbProfileName;
            _profiles.SaveSettings(settings);
            StatusText.Text = $"Loaded profile '{name}' (FFB '{loaded.FfbProfileName}')";
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
        SyncEffectGainsFromUi();
        SyncFeelFromUi();
        if (FfbDeviceCombo.SelectedItem is DeviceRow row)
            SetFfbSource(row);
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
                ResolveDeviceName,
                ResolveProductId)
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
        GainRampSlider is not null &&
        GainConstantValueText is not null && GainSpringValueText is not null && GainDamperValueText is not null &&
        GainFrictionValueText is not null && GainInertiaValueText is not null && GainPeriodicValueText is not null &&
        GainRampValueText is not null;

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
        CenterRangeValueText is not null && CenterDeadzoneValueText is not null && CenterSpringPanel is not null;

    private void ForceCenterCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_effectGainSliderSilent || !AreFeelControlsReady()) return;
        SyncFeelLabels();
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
        CenterSpringPanel.IsEnabled = ForceCenterCheck.IsChecked == true;
        CenterSpringPanel.Opacity = CenterSpringPanel.IsEnabled ? 1.0 : 0.5;
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

