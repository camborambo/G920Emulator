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

        Apply(report.WinUHid, WinUHidStatusBadge, WinUHidStatusText, WinUHidHint, WinUHidDetail);
        Apply(report.HidHide, HidHideStatusBadge, HidHideStatusText, HidHideHint, HidHideDetail);

        if (report.ReadyForGames)
        {
            FooterText.Text = "Ready for games. Use Configure HidHide if you have not yet, then Start bridge.";
        }
        else
        {
            var missing = new List<string>();
            if (report.WinUHid?.IsInstalled != true) missing.Add("WinUHid");
            if (report.HidHide?.IsInstalled != true) missing.Add("HidHide");
            FooterText.Text = $"{string.Join(" and ", missing)} required and missing. Install before playing games.";
        }
    }

    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0x5A, 0x1F));

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
