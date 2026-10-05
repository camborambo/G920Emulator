namespace G920Emulator.Core.Models;

/// <summary>Fixed virtual G920 + Driving Force Shifter control targets.</summary>
public enum G920Control
{
    Steering,
    Throttle,
    Brake,
    Clutch,
    Hat,
    /// <summary>Button-sourced D-pad up (synthesized into virtual hat when no POV is bound/active).</summary>
    HatUp,
    HatDown,
    HatLeft,
    HatRight,
    ButtonA,
    ButtonB,
    ButtonX,
    ButtonY,
    ButtonLb,
    ButtonRb,
    ButtonView,
    ButtonMenu,
    ButtonLs,
    ButtonRs,
    PaddleLeft,
    PaddleRight,
    Gear1,
    Gear2,
    Gear3,
    Gear4,
    Gear5,
    Gear6,
    GearR,
}

public static class G920ControlInfo
{
    public static readonly G920Control[] AxisControls =
    [
        G920Control.Steering,
        G920Control.Throttle,
        G920Control.Brake,
        G920Control.Clutch,
    ];

    /// <summary>H-pattern order shown in the UI: Reverse first, then 1–6.</summary>
    public static readonly G920Control[] GearControls =
    [
        G920Control.GearR,
        G920Control.Gear1,
        G920Control.Gear2,
        G920Control.Gear3,
        G920Control.Gear4,
        G920Control.Gear5,
        G920Control.Gear6,
    ];

    /// <summary>Cardinal D-pad bindings for pads without a POV hat.</summary>
    public static readonly G920Control[] HatDirectionControls =
    [
        G920Control.HatUp,
        G920Control.HatDown,
        G920Control.HatLeft,
        G920Control.HatRight,
    ];

    public static readonly G920Control[] ButtonControls =
    [
        ..HatDirectionControls,
        G920Control.ButtonA,
        G920Control.ButtonB,
        G920Control.ButtonX,
        G920Control.ButtonY,
        G920Control.ButtonLb,
        G920Control.ButtonRb,
        G920Control.ButtonView,
        G920Control.ButtonMenu,
        G920Control.ButtonLs,
        G920Control.ButtonRs,
        ..GearControls,
    ];

    /// <summary>Binding list order (axes, hat / D-pad directions, face buttons, gears R→6).</summary>
    public static readonly G920Control[] UiOrder =
    [
        G920Control.Steering,
        G920Control.Throttle,
        G920Control.Brake,
        G920Control.Clutch,
        G920Control.Hat,
        ..HatDirectionControls,
        G920Control.ButtonA,
        G920Control.ButtonB,
        G920Control.ButtonX,
        G920Control.ButtonY,
        G920Control.ButtonLb,
        G920Control.ButtonRb,
        G920Control.ButtonView,
        G920Control.ButtonMenu,
        G920Control.ButtonLs,
        G920Control.ButtonRs,
        ..GearControls,
    ];

    public static bool IsAxis(G920Control control) => AxisControls.Contains(control);
    public static bool IsGear(G920Control control) => GearControls.Contains(control);
    public static bool IsHatDirection(G920Control control) => HatDirectionControls.Contains(control);
    public static bool IsButton(G920Control control) =>
        ButtonControls.Contains(control) || control == G920Control.Hat;

    public static string DisplayName(G920Control control) => control switch
    {
        G920Control.Steering => "Steering",
        G920Control.Throttle => "Throttle",
        G920Control.Brake => "Brake",
        G920Control.Clutch => "Clutch",
        // Face controls — names match Logitech G920 Driving Force user guide.
        G920Control.Hat => "Directional pad (hat)",
        G920Control.HatUp => "D-pad Up",
        G920Control.HatDown => "D-pad Down",
        G920Control.HatLeft => "D-pad Left",
        G920Control.HatRight => "D-pad Right",
        G920Control.ButtonA => "A",
        G920Control.ButtonB => "B",
        G920Control.ButtonX => "X",
        G920Control.ButtonY => "Y",
        G920Control.ButtonLb => "Left bumper",
        G920Control.ButtonRb => "Right bumper",
        G920Control.ButtonView => "View button",
        G920Control.ButtonMenu => "Menu button",
        G920Control.ButtonLs => "LSB",
        G920Control.ButtonRs => "RSB",
        // Legacy paddle targets (merged into bumpers on load; same DI bits).
        G920Control.PaddleLeft => "Left bumper",
        G920Control.PaddleRight => "Right bumper",
        G920Control.Gear1 => "Gear 1",
        G920Control.Gear2 => "Gear 2",
        G920Control.Gear3 => "Gear 3",
        G920Control.Gear4 => "Gear 4",
        G920Control.Gear5 => "Gear 5",
        G920Control.Gear6 => "Gear 6",
        G920Control.GearR => "Gear R",
        _ => control.ToString(),
    };
}
