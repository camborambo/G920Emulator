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
    /// <summary>Input-profile only: lower Constant effect gain. Not a virtual G920 control.</summary>
    FfbConstantMinus,
    FfbConstantPlus,
    FfbSpringMinus,
    FfbSpringPlus,
    FfbDamperMinus,
    FfbDamperPlus,
    FfbFrictionMinus,
    FfbFrictionPlus,
    FfbInertiaMinus,
    FfbInertiaPlus,
    FfbPeriodicMinus,
    FfbPeriodicPlus,
    FfbRampMinus,
    FfbRampPlus,
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

    /// <summary>Per-effect −/+ binds stored on the input profile; not shown in the G920 binding list.</summary>
    public static readonly G920Control[] FfbNudgeControls =
    [
        G920Control.FfbConstantMinus,
        G920Control.FfbConstantPlus,
        G920Control.FfbSpringMinus,
        G920Control.FfbSpringPlus,
        G920Control.FfbDamperMinus,
        G920Control.FfbDamperPlus,
        G920Control.FfbFrictionMinus,
        G920Control.FfbFrictionPlus,
        G920Control.FfbInertiaMinus,
        G920Control.FfbInertiaPlus,
        G920Control.FfbPeriodicMinus,
        G920Control.FfbPeriodicPlus,
        G920Control.FfbRampMinus,
        G920Control.FfbRampPlus,
    ];

    /// <summary>Effect gain rows: display name → lower / raise bind targets.</summary>
    public static readonly (string Effect, G920Control Minus, G920Control Plus)[] FfbEffectNudgePairs =
    [
        ("Constant", G920Control.FfbConstantMinus, G920Control.FfbConstantPlus),
        ("Spring", G920Control.FfbSpringMinus, G920Control.FfbSpringPlus),
        ("Damper", G920Control.FfbDamperMinus, G920Control.FfbDamperPlus),
        ("Friction", G920Control.FfbFrictionMinus, G920Control.FfbFrictionPlus),
        ("Inertia", G920Control.FfbInertiaMinus, G920Control.FfbInertiaPlus),
        ("Periodic", G920Control.FfbPeriodicMinus, G920Control.FfbPeriodicPlus),
        ("Ramp", G920Control.FfbRampMinus, G920Control.FfbRampPlus),
    ];

    public static bool TryGetFfbEffectNudgePair(string effect, out G920Control minus, out G920Control plus)
    {
        foreach (var pair in FfbEffectNudgePairs)
        {
            if (!string.Equals(pair.Effect, effect, StringComparison.OrdinalIgnoreCase))
                continue;
            minus = pair.Minus;
            plus = pair.Plus;
            return true;
        }

        minus = default;
        plus = default;
        return false;
    }

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

    public static bool IsFfbNudge(G920Control control) => FfbNudgeControls.Contains(control);

    public static double FfbNudgeDelta(G920Control control) => control switch
    {
        G920Control.FfbConstantPlus or G920Control.FfbSpringPlus or G920Control.FfbDamperPlus
            or G920Control.FfbFrictionPlus or G920Control.FfbInertiaPlus
            or G920Control.FfbPeriodicPlus or G920Control.FfbRampPlus => 0.05,
        G920Control.FfbConstantMinus or G920Control.FfbSpringMinus or G920Control.FfbDamperMinus
            or G920Control.FfbFrictionMinus or G920Control.FfbInertiaMinus
            or G920Control.FfbPeriodicMinus or G920Control.FfbRampMinus => -0.05,
        _ => 0,
    };

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
        G920Control.FfbConstantMinus => "Constant −",
        G920Control.FfbConstantPlus => "Constant +",
        G920Control.FfbSpringMinus => "Spring −",
        G920Control.FfbSpringPlus => "Spring +",
        G920Control.FfbDamperMinus => "Damper −",
        G920Control.FfbDamperPlus => "Damper +",
        G920Control.FfbFrictionMinus => "Friction −",
        G920Control.FfbFrictionPlus => "Friction +",
        G920Control.FfbInertiaMinus => "Inertia −",
        G920Control.FfbInertiaPlus => "Inertia +",
        G920Control.FfbPeriodicMinus => "Periodic −",
        G920Control.FfbPeriodicPlus => "Periodic +",
        G920Control.FfbRampMinus => "Ramp −",
        G920Control.FfbRampPlus => "Ramp +",
        _ => control.ToString(),
    };
}
