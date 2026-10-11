using System.Windows;
using SteeringWheelEmulator.Core.Setup;
using SteeringWheelEmulator.VirtualHid;

namespace SteeringWheelEmulator.App;

public partial class WinUHidSetupWindow : Window
{
    private readonly WinUHidSetupService _setup = new();
    private readonly BundledWinUHidInstaller _bundled = new();

    public bool DriverBecameAvailable { get; private set; }

    public WinUHidSetupWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var status = _setup.GetStatus(WinUHidNativeProbe);
        StatusSummary.Text = status.Summary;
        StatusDetail.Text = string.IsNullOrWhiteSpace(status.Detail)
            ? (status.DriverResponding
                ? "WinUHid is ready."
                : "Click Install WinUHid to set up the bundled driver.")
            : status.Detail;
        PackageText.Text = _bundled.DescribePackage();
        InstallButton.IsEnabled = _bundled.DetectBundledPackage()?.HasUserDll == true;
        FooterText.Text = status.DriverResponding ? "Driver OK" : "Driver not ready yet";
        if (status.DriverResponding)
            DriverBecameAvailable = true;
    }

    private static bool WinUHidNativeProbe() => WinUHidNative.TryProbeDriver(out _);

    private void Recheck_Click(object sender, RoutedEventArgs e) => RefreshStatus();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        var pending = BundledWinUHidInstaller.HasPendingInstall;
        var confirm = MessageBox.Show(this,
            pending
                ? "Finish WinUHid install?\n\n" +
                  "This pass installs the driver while test signing is on, then turns test signing back off so Forza Horizon 6 can launch.\n\n" +
                  "You will need one more reboot after this.\n\nContinue?"
                : "Install WinUHid (Forza-friendly flow)?\n\n" +
                  "1. Enable test signing if needed (Secure Boot must be off in UEFI/BIOS for this step)\n" +
                  "2. Install the driver\n" +
                  "3. Turn test signing back off automatically\n" +
                  "4. Reboot when prompted\n\n" +
                  "Afterward: WinUHid usually keeps working, Forza can launch, and you can re-enable Secure Boot in BIOS.\n\n" +
                  "If test signing is not live yet, you may reboot once and click Install again to finish.\n\n" +
                  "Continue?",
            "Install WinUHid",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            InstallButton.IsEnabled = false;
            UninstallButton.IsEnabled = false;
            var result = _bundled.Install();
            MessageBox.Show(this, result.Message,
                result.NeedsReboot ? "Reboot required" : (result.Success ? "Installed" : "Install"),
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (result.NeedsReboot)
            {
                var after = BundledWinUHidInstaller.HasPendingInstall
                    ? "After reboot: open the app and click Install WinUHid once more to finish."
                    : "After reboot: open the app and click Recheck - do not Install again.\nWinUHid should stay installed with test signing off.";
                var reboot = MessageBox.Show(this,
                    "Reboot now?\n\n" + after,
                    "Reboot",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (reboot == MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "shutdown",
                        Arguments = "/r /t 0",
                        UseShellExecute = true,
                        Verb = "runas",
                    });
                }
            }

            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Install WinUHid", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshStatus();
        }
        finally
        {
            InstallButton.IsEnabled = true;
            UninstallButton.IsEnabled = true;
        }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Remove WinUHid and turn off Windows test signing?\n\n" +
            "Reboot is required afterwards so Forza Horizon 6 can launch.\n" +
            "Stop the bridge first. Continue?",
            "Uninstall WinUHid",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            InstallButton.IsEnabled = false;
            UninstallButton.IsEnabled = false;
            var result = _bundled.Uninstall(disableTestSigning: true);
            if (result.NeedsReboot && result.Success)
            {
                var reboot = MessageBox.Show(this,
                    result.Message + "\n\nReboot now?",
                    "Uninstall WinUHid",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (reboot == MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "shutdown",
                        Arguments = "/r /t 0",
                        UseShellExecute = true,
                        Verb = "runas",
                    });
                }
            }
            else
            {
                MessageBox.Show(this, result.Message, "Uninstall WinUHid", MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }

            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Uninstall WinUHid", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshStatus();
        }
        finally
        {
            InstallButton.IsEnabled = true;
            UninstallButton.IsEnabled = true;
        }
    }
}
