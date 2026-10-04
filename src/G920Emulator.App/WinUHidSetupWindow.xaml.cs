using System.Windows;
using G920Emulator.Core.Setup;
using G920Emulator.VirtualHid;

namespace G920Emulator.App;

public partial class WinUHidSetupWindow : Window
{
    private readonly WinUHidSetupService _setup = new();
    private readonly BundledWinUHidInstaller _bundled = new();

    public bool DriverBecameAvailable { get; private set; }

    public WinUHidSetupWindow()
    {
        InitializeComponent();
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
        try
        {
            InstallButton.IsEnabled = false;
            var result = _bundled.Install();
            MessageBox.Show(this, result.Message,
                result.NeedsReboot ? "Reboot required" : (result.Success ? "Installed" : "Install"),
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information :
                    result.NeedsReboot ? MessageBoxImage.Warning : MessageBoxImage.Warning);

            if (result.NeedsReboot)
            {
                var reboot = MessageBox.Show(this,
                    "Reboot now so test signing can take effect?",
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
        }
    }
}
