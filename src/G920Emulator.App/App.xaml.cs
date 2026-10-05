using System.Windows;

namespace G920Emulator.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // HidHide is never auto-configured at launch — users set it up in Dependencies
        // or HidHide Client so existing whitelist / hide lists are left alone.
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
