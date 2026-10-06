using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace G920Emulator.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = "Version " + (version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build}");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void AuthorLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
