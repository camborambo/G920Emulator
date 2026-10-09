using System.Windows;
using System.Windows.Input;
using G920Emulator.Core.Telemetry;

namespace G920Emulator.App;

public partial class TelemetryDebugOverlayWindow : Window
{
    public event Action? ClosedByUser;

    public TelemetryDebugOverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Topmost = true;
    }

    public void Update(in TelemetryFrame t, string status, string speedLabel, float speedMaxKmh, float rpmMax)
    {
        StatusText.Text = string.IsNullOrWhiteSpace(status) ? "Telemetry: -" : status;
        GearText.Text = "GEAR " + (string.IsNullOrEmpty(t.Gear) ? "N" : t.Gear);
        SessionText.Text = t.SessionRunning
            ? (t.SessionPaused ? "paused" : "running")
            : "idle";

        SpeedText.Text = "Speed: " + speedLabel;
        SpeedBar.Maximum = Math.Max(20, speedMaxKmh);
        SpeedBar.Value = t.SpeedKmh;

        RpmText.Text = $"RPM: {t.EngineRpm:0}";
        RpmBar.Maximum = Math.Max(1000, rpmMax);
        RpmBar.Value = t.EngineRpm;

        EngineVibText.Text = $"Engine vibration: {t.EngineVibration * 100:0}%";
        EngineVibBar.Value = t.EngineVibration;

        SteerText.Text = $"Steering: {t.Steering:+0.00;-0.00;0.00}";
        SteerBar.Value = t.Steering;
        ThrottleText.Text = $"Throttle: {t.Throttle * 100:0}%";
        ThrottleBar.Value = t.Throttle;
        BrakeText.Text = $"Brake: {t.Brake * 100:0}%";
        BrakeBar.Value = t.Brake;
        ClutchText.Text = $"Clutch: {t.Clutch * 100:0}%";
        ClutchBar.Value = t.Clutch;

        PacketText.Text = FormatPacket(t);
    }

    public static string FormatPacket(in TelemetryFrame t)
    {
        const float g = 9.80665f;
        var nl = Environment.NewLine;
        string Row(string key, string value) => $"  {key,-18} {value}{nl}";

        return
            "Simulated" + nl +
            Row("speed km/h", $"{t.SpeedKmh:0.0}") +
            Row("gear speed %", $"{t.GearSpeedFrac * 100:0.0}%") +
            Row("rpm", $"{t.EngineRpm:0} ({(t.EngineMaxRpm > 1 ? t.EngineRpm / t.EngineMaxRpm * 100 : 0):0.0}% of max)") +
            Row("max / redline", $"{t.EngineMaxRpm:0} / {t.EngineShiftRpm:0}") +
            Row("ignition", t.EngineIgnitionOn ? "on" : "off") +
            Row("started", t.EngineStarted ? "yes" : "no") +
            Row("engine vib", $"{t.EngineVibration:0.00}") +
            nl +
            "Controls" + nl +
            Row("throttle", $"{t.Throttle:0.00}") +
            Row("brake", $"{t.Brake:0.00}") +
            Row("clutch", $"{t.Clutch:0.00}") +
            Row("steering", $"{t.Steering:+0.00;-0.00;0.00}") +
            Row("gear", string.IsNullOrEmpty(t.Gear) ? "N" : t.Gear) +
            Row("max gears", $"{t.MaxGears}") +
            Row("handbrake", t.HandbrakeHeld ? "held" : "-") +
            Row("NOS / turbo", t.NosHeld ? "held" : "-") +
            nl +
            "G-force" + nl +
            Row("surge g", $"{t.LocalSurgeMs2 / g:+0.00;-0.00;0.00}") +
            Row("sway g", $"{t.LocalSwayMs2 / g:+0.00;-0.00;0.00}") +
            Row("heave g", $"{t.LocalHeaveMs2 / g:0.00}") +
            nl +
            "FFB → SimHub" + nl +
            Row("rumble", $"{t.SurfaceRumble:0.00}") +
            Row("impact", $"{t.Impact:0.00}") +
            Row("road load", $"{t.RoadLoad:0.00}") +
            Row("cf / spring", $"{t.FfbConstant:+0.00;-0.00;0.00} / {t.FfbSpring:+0.00;-0.00;0.00}") +
            Row("damper / per", $"{t.FfbDamper:+0.00;-0.00;0.00} / {t.FfbPeriodic:+0.00;-0.00;0.00}") +
            nl +
            "Road vib" + nl +
            Row("susp FL/FR", $"{t.SuspensionVelocityFrontLeftMps:+0.00;-0.00;0.00} / {t.SuspensionVelocityFrontRightMps:+0.00;-0.00;0.00}") +
            Row("susp RL/RR", $"{t.SuspensionVelocityRearLeftMps:+0.00;-0.00;0.00} / {t.SuspensionVelocityRearRightMps:+0.00;-0.00;0.00}") +
            Row("tyre FL/FR", $"{t.TyreContactSurfaceFrontLeft} / {t.TyreContactSurfaceFrontRight}") +
            Row("tyre RL/RR", $"{t.TyreContactSurfaceRearLeft} / {t.TyreContactSurfaceRearRight}");
    }

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        ClosedByUser?.Invoke();
        Close();
    }
}
