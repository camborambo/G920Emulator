using System.Windows;
using System.Windows.Controls;
using G920Emulator.Core.Profiles;

namespace G920Emulator.App;

public partial class SettingsWindow : Window
{
    private readonly ProfileStore _profiles;
    private readonly Action<AppSettings> _onApplied;
    private readonly Action? _openDependencies;
    private bool _suppress;

    public SettingsWindow(
        ProfileStore profiles,
        Action<AppSettings> onApplied,
        Action? openDependencies = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _profiles = profiles;
        _onApplied = onApplied;
        _openDependencies = openDependencies;
        LoadFromSettings(_profiles.LoadSettings());
    }

    private void LoadFromSettings(AppSettings s)
    {
        _suppress = true;
        try
        {
            MinimizeToTrayCheck.IsChecked = s.MinimizeToSystemTray;
            CheckForUpdatesCheck.IsChecked = s.CheckForUpdates;
            TelemetryKmhCheck.IsChecked =
                string.Equals(s.TelemetrySpeedUnit, "kmh", StringComparison.OrdinalIgnoreCase);
            FfbDebugOverlayCheck.IsChecked = s.DebugOverlay;
            TelemetryDebugOverlayCheck.IsChecked = s.TelemetryDebugOverlay;
            EffectChangesOverlayCheck.IsChecked = s.EffectChangesOverlay;

            switch (s.HidHideApplyMode)
            {
                case HidHideApplyMode.HideAll:
                    HidHideAllRadio.IsChecked = true;
                    break;
                case HidHideApplyMode.HideBound:
                    HidHideBoundRadio.IsChecked = true;
                    break;
                default:
                    HidHideOffRadio.IsChecked = true;
                    break;
            }

            HidHideRestoreCheck.IsChecked = s.UnloadHidHideConfigWhenStopped;
            UpdateHidHideRestoreEnabled();

            if (s.FfbCooperativeMode == FfbCooperativeMode.NonExclusive)
                FfbNonExclusiveRadio.IsChecked = true;
            else
                FfbExclusiveRadio.IsChecked = true;
        }
        finally
        {
            _suppress = false;
        }
    }

    private void SettingsTab_Checked(object sender, RoutedEventArgs e)
    {
        if (GeneralPanel is null) return;
        GeneralPanel.Visibility = GeneralTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OverlaysPanel.Visibility = OverlaysTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HidHidePanel.Visibility = HidHideTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HidHideMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        UpdateHidHideRestoreEnabled();
        Persist();
    }

    private void FfbCoop_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        Persist();
    }

    private void UpdateHidHideRestoreEnabled()
    {
        var modeOn = HidHideOffRadio?.IsChecked != true;
        if (HidHideRestoreCheck is null) return;
        HidHideRestoreCheck.IsEnabled = modeOn;
        if (!modeOn)
            HidHideRestoreCheck.IsChecked = false;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        Persist();
    }

    private void Persist()
    {
        var mode = HidHideApplyMode.Off;
        if (HidHideAllRadio.IsChecked == true) mode = HidHideApplyMode.HideAll;
        else if (HidHideBoundRadio.IsChecked == true) mode = HidHideApplyMode.HideBound;

        var settings = _profiles.LoadSettings();
        settings.MinimizeToSystemTray = MinimizeToTrayCheck.IsChecked == true;
        settings.CheckForUpdates = CheckForUpdatesCheck.IsChecked == true;
        settings.TelemetrySpeedUnit = TelemetryKmhCheck.IsChecked == true ? "kmh" : "mph";
        settings.DebugOverlay = FfbDebugOverlayCheck.IsChecked == true;
        settings.TelemetryDebugOverlay = TelemetryDebugOverlayCheck.IsChecked == true;
        settings.EffectChangesOverlay = EffectChangesOverlayCheck.IsChecked == true;
        settings.HidHideApplyMode = mode;
        settings.UnloadHidHideConfigWhenStopped = mode != HidHideApplyMode.Off &&
                                                  HidHideRestoreCheck.IsChecked == true;
        settings.FfbCooperativeMode = FfbNonExclusiveRadio.IsChecked == true
            ? FfbCooperativeMode.NonExclusive
            : FfbCooperativeMode.Exclusive;
        settings.NormalizeHidHide();
        settings.NormalizeFfbCooperative();
        _profiles.SaveSettings(settings);
        _onApplied(settings);
    }

    private void Dependencies_Click(object sender, RoutedEventArgs e) =>
        _openDependencies?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
