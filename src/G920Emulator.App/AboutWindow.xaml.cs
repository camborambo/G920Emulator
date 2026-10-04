using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Win32;
using G920Emulator.Core.Setup;
using G920Emulator.VirtualHid;

namespace G920Emulator.App;

public partial class AboutWindow : Window
{
    private readonly string _versionLabel;

    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        _versionLabel = version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
        VersionText.Text = "Version " + _versionLabel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void AuthorLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export diagnostics",
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
            var path = DiagnosticsExporter.ExportToZip(
                dialog.FileName,
                _versionLabel,
                () =>
                {
                    var ok = WinUHidNative.TryProbeDriver(out var msg);
                    return (ok, msg ?? "");
                });

            var open = MessageBox.Show(
                this,
                "Diagnostics zip saved:\n\n" + path +
                "\n\nEmail it to " + DiagnosticsExporter.SupportEmail +
                " with a short description of the issue.\n\nOpen the zip location now?",
                "Export diagnostics",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (open == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
