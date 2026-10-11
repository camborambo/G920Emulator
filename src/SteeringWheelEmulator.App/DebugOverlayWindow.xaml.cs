using System.Windows;
using System.Windows.Input;
using SteeringWheelEmulator.Core.Ffb;
using SteeringWheelEmulator.Core.Models;

namespace SteeringWheelEmulator.App;

public partial class DebugOverlayWindow : Window
{
    public event Action? ClosedByUser;

    public DebugOverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Topmost = true;
    }

    public void UpdateInput(MappedG920State s)
    {
        GearText.Text = $"GEAR {s.ActiveGearLabel}";
        SteerText.Text = $"Steering: {s.Steering:F2}";
        SteerBar.Value = s.Steering;
        ThrottleText.Text = $"Throttle: {s.Throttle:P0}";
        ThrottleBar.Value = s.Throttle;
        BrakeText.Text = $"Brake: {s.Brake:P0}";
        BrakeBar.Value = s.Brake;
        ClutchText.Text = $"Clutch: {s.Clutch:P0}";
        ClutchBar.Value = s.Clutch;
        ButtonsText.Text = "Buttons: " + FormatButtons(s);
    }

    public void UpdateFfb(FfbDiagnostics d, string? linkStatus)
    {
        EffectsText.Text = FormatEffects(d);
        FfbText.Text = FormatFfb(d, linkStatus);
    }

    public static string FormatEffects(FfbDiagnostics d) =>
        !string.IsNullOrEmpty(d.OemFfbEffectsDetail)
            ? d.OemFfbEffectsDetail
            : OemFfbSharedMemory.FormatOemEffects(
                d.OemTypesSeenMask, d.OemTypesPlayingMask, d.OemTypeTorqueDi, d.OemLastEffectType);

    public static string FormatButtons(MappedG920State s)
    {
        var pressed = new List<string>();
        if (s.ButtonA) pressed.Add("A");
        if (s.ButtonB) pressed.Add("B");
        if (s.ButtonX) pressed.Add("X");
        if (s.ButtonY) pressed.Add("Y");
        if (s.ButtonLb || s.PaddleLeft) pressed.Add("LB");
        if (s.ButtonRb || s.PaddleRight) pressed.Add("RB");
        if (s.ButtonView) pressed.Add("View");
        if (s.ButtonMenu) pressed.Add("Menu");
        if (s.ButtonLs) pressed.Add("LSB");
        if (s.ButtonRs) pressed.Add("RSB");
        if (s.Hat >= 0) pressed.Add($"Hat{s.Hat}");
        if (s.ActiveGearLabel is not "N")
            pressed.Add($"G{s.ActiveGearLabel}");
        return pressed.Count == 0 ? "-" : string.Join(" ", pressed);
    }

    public static string FormatFfb(FfbDiagnostics d, string? linkStatus)
    {
        var ageIn = d.LastIncomingUtc is DateTime t
            ? $"{(DateTime.UtcNow - t).TotalSeconds:0.0}s ago"
            : "never";
        var ageOut = d.LastApplyUtc is DateTime a
            ? $"{(DateTime.UtcNow - a).TotalSeconds:0.0}s ago"
            : "never";
        var link = string.IsNullOrWhiteSpace(linkStatus) ? "OK" : linkStatus;
        var nl = Environment.NewLine;
        string Row(string key, string value) => $"  {key,-15} {value}{nl}";

        return
            "Bridge" + nl +
            Row("status", string.IsNullOrWhiteSpace(d.Status) ? "-" : d.Status) +
            Row("link", link) +
            Row("attached", d.IsAttached ? "yes" : "no") +
            Row("vendor", d.VendorProfile) +
            Row("device", d.DeviceName ?? "(none)") +
            Row("test", $"override={d.TestOverrideActive}  auto-center={d.TestAutoCenterActive}") +
            nl +
            "OEM" + nl +
            Row("status", string.IsNullOrWhiteSpace(d.OemFfbStatus) ? "-" : d.OemFfbStatus) +
            Row("torque", $"{d.OemFfbTorque:+0.00;-0.00;0.00}") +
            Row("playing", d.OemFfbPlaying ? "yes" : "no") +
            Row("rim", $"{d.FfbRimSteer:+0.00;-0.00;0.00}") +
            Row("hw center", d.HardwareAutoCenter?.ToString() ?? "?") +
            nl +
            "HID++" + nl +
            Row("host writes", $"{d.HostWriteCount}  {d.LastHostWriteHex}") +
            Row("writes", $"{d.HidppWriteCount}  dl={d.HidppDownloadCount}  play={d.HidppPlayCount}") +
            Row("slots", $"{d.HidppSlotsPlaying}/{d.HidppSlotsInUse} playing  last={d.HidppLastFunction}") +
            Row("torque", $"{d.HidppCurrentTorque:+0.00;-0.00;0.00}") +
            Row("path", string.IsNullOrEmpty(d.HostPathHint) ? "(settling)" : d.HostPathHint) +
            nl +
            "Apply" + nl +
            Row("in", $"{d.LastIncomingTorque:+0.00;-0.00;0.00}  ({d.IncomingUpdateCount}, {ageIn})") +
            Row("out", $"{d.LastCommandTorque:+0.00;-0.00;0.00}  mag={d.LastMagnitude} ({d.ApplyCount}, {ageOut})") +
            Row("error", d.LastError ?? "(none)");
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
