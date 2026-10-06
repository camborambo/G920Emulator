using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using G920Emulator.Core.Setup;
using G920Emulator.VirtualHid;

namespace G920Emulator.App;

public partial class DependenciesWindow : Window
{
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0x4A));
    private static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0x1F, 0x2A));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0x5A, 0x1F));

    public DependenciesWindow()
    {
        InitializeComponent();
        Refresh();
    }

    public void Refresh()
    {
        var report = DependencyChecker.CheckAll(() =>
        {
            var ok = WinUHidNative.TryProbeDriver(out var msg);
            return (ok, msg);
        });

        Apply(report.TestSigning, TestSigningStatusBadge, TestSigningStatusText, TestSigningHint, TestSigningDetail);
        var tsLabel = report.TestSigning?.StatusLabel ?? "";
        TestSigningStatusBadge.Background = tsLabel switch
        {
            "Off (OK)" => OkBrush,
            "Enabled" => report.WinUHid?.IsInstalled == true ? WarnBrush : OkBrush,
            "Reboot required" => WarnBrush,
            _ => BadBrush,
        };
        TestSigningRequiredText.Text = report.WinUHid?.IsInstalled == true ? "OPTIONAL" : "FOR INSTALL";
        TestSigningRequiredBadge.Background = report.WinUHid?.IsInstalled == true
            ? WarnBrush
            : new SolidColorBrush(Color.FromRgb(0x6F, 0x3A, 0x1F));

        var testSigningLive = tsLabel.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        EnableTestSigningButton.IsEnabled = tsLabel.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
        DisableTestSigningButton.IsEnabled = testSigningLive;

        Apply(report.WinUHid, WinUHidStatusBadge, WinUHidStatusText, WinUHidHint, WinUHidDetail);
        if (report.WinUHid is { StatusLabel: "Reboot required" or "Not responding" })
            WinUHidStatusBadge.Background = WarnBrush;

        var oemState = OemRegistrationSession.GetUiState();
        OemSdkStatusText.Text = oemState switch
        {
            OemSessionUiState.Active => "ACTIVE",
            OemSessionUiState.NeedsRestore => "NEEDS RESTORE",
            _ => "IDLE",
        };
        OemSdkStatusBadge.Background = oemState switch
        {
            OemSessionUiState.Active => OkBrush,
            OemSessionUiState.NeedsRestore => BadBrush,
            _ => OkBrush,
        };
        OemSdkDetail.Text = oemState switch
        {
            OemSessionUiState.Active => "Pins applied for this bridge session. Stop bridge to restore system registration.",
            OemSessionUiState.NeedsRestore => "Leftover pins detected. Click Restore system registration (stop the bridge first if it is running).",
            _ => "Idle — session pins restored. Nothing to do.",
        };
        RestoreOemSdkButton.IsEnabled = oemState == OemSessionUiState.NeedsRestore;
        FullCleanRestoreButton.IsEnabled = oemState != OemSessionUiState.Active;

        var guardOn = GHubGuard.IsAppWatchRunning;
        GHubGuardBadge.Background = guardOn ? OkBrush : WarnBrush;
        GHubGuardStatusText.Text = guardOn ? "ACTIVE" : "OFF";
        GHubGuardDetail.Text = guardOn
            ? $"Guarding while bridge runs. Repairs since launch: {GHubGuard.AppWatchRestoreCount}"
            : "Off while bridge is stopped.";

        Apply(report.HidHide, HidHideStatusBadge, HidHideStatusText, HidHideHint, HidHideDetail);

        if (tsLabel.Equals("Reboot required", StringComparison.OrdinalIgnoreCase))
            FooterText.Text = BundledWinUHidInstaller.HasPendingInstall
                ? "Reboot, then click Install WinUHid once more (installs driver + turns test signing off)."
                : "Test signing staged — reboot once, then continue setup.";
        else if (testSigningLive && report.WinUHid?.IsInstalled == true)
            FooterText.Text = "Test signing still ON — Disable and reboot for Forza. WinUHid can stay installed.";
        else if (testSigningLive)
            FooterText.Text = "Test signing ON — click Install WinUHid (it will turn test signing off afterwards).";
        else if (oemState == OemSessionUiState.NeedsRestore)
            FooterText.Text = "OEM/SDK leftovers from a previous session — click Restore system registration.";
        else if (report.ReadyForGames)
            FooterText.Text = "Ready for games (including Forza if test signing is off). Configure HidHide if needed, then Start bridge.";
        else
            FooterText.Text = $"{string.Join(", ", report.MissingRequiredNames)} required and missing. Finish setup before playing.";
    }

    private void RestoreOemSdk_Click(object sender, RoutedEventArgs e)
    {
        if (OemRegistrationSession.IsActive)
        {
            MessageBox.Show(this, "Stop the bridge first, then restore.", "Restore system registration",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            OemRegistrationSession.EndSession();
            var msg = G920OemRegistration.RestoreSystemLogitechRegistration(restoreLogitechOemClsid: false);
            Refresh();
            MessageBox.Show(this, msg, "Restore system registration", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Restore system registration", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EnableTestSigning_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Enable Windows test signing (test mode)?\n\n" +
            "Only needed to install WinUHid. Prefer Install WinUHid — it enables test signing, installs the driver, then turns test signing off again.\n\n" +
            "IMPORTANT — Secure Boot:\n" +
            "• Secure Boot must be DISABLED in UEFI/BIOS for this step\n" +
            "• If Secure Boot is still on, Enable will fail\n" +
            "• After WinUHid is installed and test signing is off, you can turn Secure Boot back on\n\n" +
            "After Enable you must reboot, then Install WinUHid (which turns test signing off again).\n" +
            "Forza Horizon 6 will not launch while test mode stays on.\n\n" +
            "Continue?",
            "Enable test signing",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            FooterText.Text = "Enabling test signing…";
            EnableTestSigningButton.IsEnabled = false;
            var setup = new WinUHidSetupService();
            var msg = setup.EnableTestSigningElevated();
            Refresh();

            var reboot = MessageBox.Show(this,
                msg + "\n\nReboot now so test signing takes effect?\n\n" +
                "After reboot: click Install WinUHid (it installs the driver and turns test signing back off).",
                "Reboot required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (reboot == MessageBoxResult.Yes)
                StartReboot();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Enable test signing", MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
        }
    }

    private void DisableTestSigning_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Disable Windows test signing?\n\n" +
            "• Needed so Forza Horizon 6 can launch\n" +
            "• Reboot required afterwards\n" +
            "• WinUHid will stop working until you Enable test signing again\n\n" +
            "Continue?",
            "Disable test signing",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            FooterText.Text = "Disabling test signing…";
            DisableTestSigningButton.IsEnabled = false;
            var msg = new WinUHidSetupService().DisableTestSigningElevated();
            Refresh();

            var reboot = MessageBox.Show(this,
                msg + "\n\nReboot now?",
                "Reboot required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (reboot == MessageBoxResult.Yes)
                StartReboot();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Disable test signing", MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
        }
    }

    private void FullCleanRestore_Click(object sender, RoutedEventArgs e)
    {
        if (OemRegistrationSession.IsActive)
        {
            MessageBox.Show(this, "Stop the bridge first, then run Full clean restore.", "Full clean restore",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            "Optional nuclear cleanup — not required for Forza Horizon 6.\n" +
            "(For FH6 splash exit, use Disable test signing or Uninstall WinUHid instead.)\n\n" +
            "This removes:\n" +
            "• App OEM / Logitech SDK leftovers and COM\n" +
            "• ProgramData SDK cache\n" +
            "• Stale DirectInput / orphan virtual G920 nodes\n" +
            "• Restores Logitech hidpp_forcefeedback DLL if renamed by older repair\n" +
            "• WinUHid device + driver package\n" +
            "• Windows test signing (turned off — reboot required)\n\n" +
            "Not changed: HidHide, Secure Boot (BIOS), or your profiles.\n\n" +
            "Approve UAC if prompted. Continue?",
            "Full clean restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            FooterText.Text = "Running full clean restore…";
            FullCleanRestoreButton.IsEnabled = false;
            var result = FullCleanRestore.Run(uninstallWinUHid: true);
            Refresh();
            var detail = result.Summary + "\n\n" + string.Join("\n", result.Steps.Select(s => "• " + s));
            MessageBox.Show(this, detail, "Full clean restore",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Full clean restore", MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
        }
    }

    private void UninstallWinUHid_Click(object sender, RoutedEventArgs e)
    {
        if (OemRegistrationSession.IsActive)
        {
            MessageBox.Show(this, "Stop the bridge first.", "Uninstall WinUHid",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            "Remove WinUHid and turn off Windows test signing?\n\n" +
            "• Needed so Forza Horizon 6 can launch after using the emulator\n" +
            "• Reboot required after test signing is turned off\n" +
            "• HidHide is left alone\n\n" +
            "Approve UAC if prompted. Continue?",
            "Uninstall WinUHid",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            FooterText.Text = "Uninstalling WinUHid and disabling test signing…";
            var result = new BundledWinUHidInstaller().Uninstall(disableTestSigning: true);
            Refresh();
            if (result.NeedsReboot && result.Success)
            {
                var reboot = MessageBox.Show(this,
                    result.Message + "\n\nReboot now?",
                    "Uninstall WinUHid",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (reboot == MessageBoxResult.Yes)
                    StartReboot();
            }
            else
            {
                MessageBox.Show(this, result.Message, "Uninstall WinUHid",
                    MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Uninstall WinUHid", MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
        }
    }

    private static void StartReboot()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = "/r /t 0",
            UseShellExecute = true,
            Verb = "runas",
        });
    }

    private static void Apply(
        DependencyInfo? info,
        System.Windows.Controls.Border badge,
        System.Windows.Controls.TextBlock status,
        System.Windows.Controls.TextBlock hint,
        System.Windows.Controls.TextBlock detail)
    {
        if (info is null) return;
        status.Text = info.StatusLabel.ToUpperInvariant();
        badge.Background = info.IsInstalled
            ? OkBrush
            : info.Requirement == DependencyRequirement.Required ? BadBrush : WarnBrush;
        hint.Text = info.InstallOrSetupHint ?? "";
        detail.Text = info.Detail;
    }

    private void Recheck_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetupWinUHid_Click(object sender, RoutedEventArgs e)
    {
        var window = new WinUHidSetupWindow { Owner = this };
        window.ShowDialog();
        Refresh();
    }

    private void RepairGHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FooterText.Text = "Repairing G HUB leftovers…";
            var summary = GHubConflictRepair.Repair();
            FooterText.Text = "G HUB repair finished. Start bridge, then launch the game.";
            MessageBox.Show(
                this,
                summary +
                "\n\nHidHide was not changed — configure it yourself if games still see your pad.\n\n" +
                "Next: Start bridge, confirm the G920 in joy.cpl, then launch the game.",
                "Repair G HUB leftovers",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Repair G HUB leftovers", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Refresh();
    }

    private void ConfigureHidHide_Click(object sender, RoutedEventArgs e)
    {
        var installed = DependencyChecker.CheckHidHide().IsInstalled;
        if (!installed)
        {
            var download = MessageBox.Show(
                "HidHide is not installed yet.\n\nOpen the download page now?",
                "Configure HidHide",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (download == MessageBoxResult.Yes)
                DependencyChecker.OpenUrl(DependencyChecker.HidHideReleasesUrl);
            return;
        }

        FooterText.Text = "Configuring HidHide…";
        DependencyChecker.HidHideEnsureResult result;
        try
        {
            result = DependencyChecker.ConfigureHidHideFully();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Configure HidHide", MessageBoxButton.OK, MessageBoxImage.Warning);
            FooterText.Text = ex.Message;
            return;
        }

        FooterText.Text = result.Message;
        Refresh();

        if (result.NeedsRelaunch)
        {
            var relaunch = MessageBox.Show(
                result.Message + "\n\nRelaunch G920 Emulator now so it can keep seeing your pad?",
                "Configure HidHide",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (relaunch == MessageBoxResult.Yes)
            {
                try
                {
                    var exe = Environment.ProcessPath;
                    if (!string.IsNullOrWhiteSpace(exe))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exe,
                            Arguments = "--after-hidhide",
                            UseShellExecute = true,
                            WorkingDirectory = AppContext.BaseDirectory,
                        });
                        Application.Current.Shutdown();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not relaunch: " + ex.Message, "Configure HidHide",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        else
        {
            MessageBox.Show(result.Message, "Configure HidHide", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void DownloadHidHide_Click(object sender, RoutedEventArgs e) =>
        DependencyChecker.OpenUrl(DependencyChecker.HidHideReleasesUrl);

    private void OpenHidHideClient_Click(object sender, RoutedEventArgs e)
    {
        DependencyChecker.TryOpenHidHideClient(out var message);
        FooterText.Text = message;
        Refresh();
    }

    private void OpenHidHideDocs_Click(object sender, RoutedEventArgs e) =>
        DependencyChecker.OpenUrl(DependencyChecker.HidHideRepoUrl);
}
