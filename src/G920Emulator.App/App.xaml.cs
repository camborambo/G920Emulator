using System.Diagnostics;
using System.Windows;
using G920Emulator.Core.Setup;

namespace G920Emulator.App;

public partial class App : Application
{
    private const string AfterHidHideArg = "--after-hidhide";

    protected override void OnStartup(StartupEventArgs e)
    {
        // HidHide inverse-off only exposes cloaked pads to whitelisted processes that
        // start *after* registration. Configure before any DirectInput enumeration.
        var afterHidHide = e.Args.Any(a =>
            string.Equals(a, AfterHidHideArg, StringComparison.OrdinalIgnoreCase));

        DependencyChecker.HidHideEnsureResult? hidHide = null;
        try
        {
            hidHide = DependencyChecker.EnsureHidHideForEmulator();
        }
        catch
        {
            // Optional dependency — continue without it.
        }

        if (hidHide?.NeedsRelaunch == true && !afterHidHide)
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = AfterHidHideArg,
                        UseShellExecute = true,
                        WorkingDirectory = AppContext.BaseDirectory,
                    });
                }
            }
            catch
            {
                // Fall through and show the UI anyway.
                base.OnStartup(e);
                new MainWindow().Show();
                return;
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }
}
