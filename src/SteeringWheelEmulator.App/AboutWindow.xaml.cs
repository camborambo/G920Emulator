using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

using SteeringWheelEmulator.Core;

namespace SteeringWheelEmulator.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        VersionText.Text = "Version " + AppVersion.Display;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void AuthorLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
