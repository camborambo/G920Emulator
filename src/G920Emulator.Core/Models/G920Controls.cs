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
    /// <summary>Input-profile only: lower / raise / set-default for FFB sliders. Not virtual G920 controls.</summary>
    FfbConstantMinus,
    FfbConstantPlus,
    FfbConstantDefault,
    FfbSpringMinus,
    FfbSpringPlus,
    FfbSpringDefault,
    FfbDamperMinus,
    FfbDamperPlus,
    FfbDamperDefault,
    FfbFrictionMinus,
    FfbFrictionPlus,
    FfbFrictionDefault,
    FfbInertiaMinus,
    FfbInertiaPlus,
    FfbInertiaDefault,
    FfbPeriodicMinus,
    FfbPeriodicPlus,
    FfbPeriodicDefault,
    FfbRampMinus,
    FfbRampPlus,
    FfbRampDefault,
    FfbMasterMinus,
    FfbMasterPlus,
    FfbMasterDefault,
    FfbCustomMinus,
    FfbCustomPlus,
    FfbCustomDefault,
    FfbSmoothingMinus,
    FfbSmoothingPlus,
    FfbSmoothingDefault,
    FfbPeakSoftMinus,
    FfbPeakSoftPlus,
    FfbPeakSoftDefault,
    FfbSoftStartMinus,
    FfbSoftStartPlus,
    FfbSoftStartDefault,
    FfbDeadbandMinus,
    FfbDeadbandPlus,
    FfbDeadbandDefault,
    FfbSlewMinus,
    FfbSlewPlus,
    FfbSlewDefault,
    FfbSpikeMinus,
    FfbSpikePlus,
    FfbSpikeDefault,
    FfbEpsilonMinus,
    FfbEpsilonPlus,
    FfbEpsilonDefault,
    FfbCenterStrengthMinus,
    FfbCenterStrengthPlus,
    FfbCenterStrengthDefault,
    FfbCenterRangeMinus,
    FfbCenterRangePlus,
    FfbCenterRangeDefault,
    FfbCenterDeadzoneMinus,
    FfbCenterDeadzonePlus,
    FfbCenterDeadzoneDefault,
    FfbDampVelMinus,
    FfbDampVelPlus,
    FfbDampVelDefault,
    FfbDampDeadMinus,
    FfbDampDeadPlus,
    FfbDampDeadDefault,
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

    /// <summary>Input-profile FFB slider binds (not virtual G920 controls).</summary>
    public readonly record struct FfbSliderBind(
        string Id,
        string Category,
        string Display,
        G920Control Minus,
        G920Control Plus,
        G920Control Default,
        double FineStep,
        double CoarseStep);

    public static readonly FfbSliderBind[] FfbSliderBinds =
    [
        new("Master", "FFB profile", "Master", G920Control.FfbMasterMinus, G920Control.FfbMasterPlus, G920Control.FfbMasterDefault, 0.01, 0.05),
        new("Constant", "Effect gains", "Constant", G920Control.FfbConstantMinus, G920Control.FfbConstantPlus, G920Control.FfbConstantDefault, 0.01, 0.05),
        new("Spring", "Effect gains", "Spring", G920Control.FfbSpringMinus, G920Control.FfbSpringPlus, G920Control.FfbSpringDefault, 0.01, 0.05),
        new("Damper", "Effect gains", "Damper", G920Control.FfbDamperMinus, G920Control.FfbDamperPlus, G920Control.FfbDamperDefault, 0.01, 0.05),
        new("Friction", "Effect gains", "Friction", G920Control.FfbFrictionMinus, G920Control.FfbFrictionPlus, G920Control.FfbFrictionDefault, 0.01, 0.05),
        new("Inertia", "Effect gains", "Inertia", G920Control.FfbInertiaMinus, G920Control.FfbInertiaPlus, G920Control.FfbInertiaDefault, 0.01, 0.05),
        new("Periodic", "Effect gains", "Periodic", G920Control.FfbPeriodicMinus, G920Control.FfbPeriodicPlus, G920Control.FfbPeriodicDefault, 0.01, 0.05),
        new("Ramp", "Effect gains", "Ramp", G920Control.FfbRampMinus, G920Control.FfbRampPlus, G920Control.FfbRampDefault, 0.01, 0.05),
        new("Custom", "Effect gains", "Custom", G920Control.FfbCustomMinus, G920Control.FfbCustomPlus, G920Control.FfbCustomDefault, 0.01, 0.05),
        new("Smoothing", "Output feel", "Smoothing", G920Control.FfbSmoothingMinus, G920Control.FfbSmoothingPlus, G920Control.FfbSmoothingDefault, 1, 5),
        new("PeakSoft", "Output feel", "Peak soft", G920Control.FfbPeakSoftMinus, G920Control.FfbPeakSoftPlus, G920Control.FfbPeakSoftDefault, 0.01, 0.05),
        new("SoftStart", "Output feel", "Soft start", G920Control.FfbSoftStartMinus, G920Control.FfbSoftStartPlus, G920Control.FfbSoftStartDefault, 10, 50),
        new("Deadband", "Torque shaping", "Deadband", G920Control.FfbDeadbandMinus, G920Control.FfbDeadbandPlus, G920Control.FfbDeadbandDefault, 0.001, 0.005),
        new("Slew", "Torque shaping", "Slew", G920Control.FfbSlewMinus, G920Control.FfbSlewPlus, G920Control.FfbSlewDefault, 1, 5),
        new("Spike", "Torque shaping", "Spike cap", G920Control.FfbSpikeMinus, G920Control.FfbSpikePlus, G920Control.FfbSpikeDefault, 0.01, 0.05),
        new("Epsilon", "Torque shaping", "DI epsilon", G920Control.FfbEpsilonMinus, G920Control.FfbEpsilonPlus, G920Control.FfbEpsilonDefault, 1, 5),
        new("CenterStrength", "Centering", "Strength", G920Control.FfbCenterStrengthMinus, G920Control.FfbCenterStrengthPlus, G920Control.FfbCenterStrengthDefault, 0.01, 0.05),
        new("CenterRange", "Centering", "Range", G920Control.FfbCenterRangeMinus, G920Control.FfbCenterRangePlus, G920Control.FfbCenterRangeDefault, 0.01, 0.05),
        new("CenterDeadzone", "Centering", "Deadzone", G920Control.FfbCenterDeadzoneMinus, G920Control.FfbCenterDeadzonePlus, G920Control.FfbCenterDeadzoneDefault, 0.001, 0.005),
        new("DampVel", "Advanced mix", "Damp vel", G920Control.FfbDampVelMinus, G920Control.FfbDampVelPlus, G920Control.FfbDampVelDefault, 0.01, 0.05),
        new("DampDead", "Advanced mix", "Damp dead", G920Control.FfbDampDeadMinus, G920Control.FfbDampDeadPlus, G920Control.FfbDampDeadDefault, 0.01, 0.05),
    ];

    public static readonly G920Control[] FfbNudgeControls = BuildNudgeControls();

    /// <summary>Legacy alias: bind dialog id → lower / raise.</summary>
    public static readonly (string Effect, G920Control Minus, G920Control Plus)[] FfbEffectNudgePairs =
        FfbSliderBinds.Select(b => (b.Id, b.Minus, b.Plus)).ToArray();

    private static G920Control[] BuildNudgeControls()
    {
        var list = new G920Control[FfbSliderBinds.Length * 3];
        for (var i = 0; i < FfbSliderBinds.Length; i++)
        {
            list[i * 3] = FfbSliderBinds[i].Minus;
            list[i * 3 + 1] = FfbSliderBinds[i].Plus;
            list[i * 3 + 2] = FfbSliderBinds[i].Default;
        }
        return list;
    }

    public static bool TryGetSliderBindById(string id, out FfbSliderBind bind)
    {
        foreach (var candidate in FfbSliderBinds)
        {
            if (!string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
                continue;
            bind = candidate;
            return true;
        }
        bind = default;
        return false;
    }

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

    public static bool IsFfbDefault(G920Control control)
    {
        foreach (var bind in FfbSliderBinds)
        {
            if (control == bind.Default)
                return true;
        }
        return false;
    }

    public static double FfbNudgeDelta(G920Control control, bool coarse = false)
    {
        foreach (var bind in FfbSliderBinds)
        {
            if (control == bind.Plus)
                return coarse ? bind.CoarseStep : bind.FineStep;
            if (control == bind.Minus)
                return -(coarse ? bind.CoarseStep : bind.FineStep);
        }
        return 0;
    }

    public static bool TryGetSliderBind(G920Control control, out FfbSliderBind bind)
    {
        foreach (var candidate in FfbSliderBinds)
        {
            if (control != candidate.Minus && control != candidate.Plus && control != candidate.Default)
                continue;
            bind = candidate;
            return true;
        }
        bind = default;
        return false;
    }

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
        G920Control.FfbConstantDefault => "Constant default",
        G920Control.FfbSpringMinus => "Spring −",
        G920Control.FfbSpringPlus => "Spring +",
        G920Control.FfbSpringDefault => "Spring default",
        G920Control.FfbDamperMinus => "Damper −",
        G920Control.FfbDamperPlus => "Damper +",
        G920Control.FfbDamperDefault => "Damper default",
        G920Control.FfbFrictionMinus => "Friction −",
        G920Control.FfbFrictionPlus => "Friction +",
        G920Control.FfbFrictionDefault => "Friction default",
        G920Control.FfbInertiaMinus => "Inertia −",
        G920Control.FfbInertiaPlus => "Inertia +",
        G920Control.FfbInertiaDefault => "Inertia default",
        G920Control.FfbPeriodicMinus => "Periodic −",
        G920Control.FfbPeriodicPlus => "Periodic +",
        G920Control.FfbPeriodicDefault => "Periodic default",
        G920Control.FfbRampMinus => "Ramp −",
        G920Control.FfbRampPlus => "Ramp +",
        G920Control.FfbRampDefault => "Ramp default",
        G920Control.FfbMasterMinus => "Master −",
        G920Control.FfbMasterPlus => "Master +",
        G920Control.FfbMasterDefault => "Master default",
        G920Control.FfbCustomMinus => "Custom −",
        G920Control.FfbCustomPlus => "Custom +",
        G920Control.FfbCustomDefault => "Custom default",
        G920Control.FfbSmoothingMinus => "Smoothing −",
        G920Control.FfbSmoothingPlus => "Smoothing +",
        G920Control.FfbSmoothingDefault => "Smoothing default",
        G920Control.FfbPeakSoftMinus => "Peak soft −",
        G920Control.FfbPeakSoftPlus => "Peak soft +",
        G920Control.FfbPeakSoftDefault => "Peak soft default",
        G920Control.FfbSoftStartMinus => "Soft start −",
        G920Control.FfbSoftStartPlus => "Soft start +",
        G920Control.FfbSoftStartDefault => "Soft start default",
        G920Control.FfbDeadbandMinus => "Deadband −",
        G920Control.FfbDeadbandPlus => "Deadband +",
        G920Control.FfbDeadbandDefault => "Deadband default",
        G920Control.FfbSlewMinus => "Slew −",
        G920Control.FfbSlewPlus => "Slew +",
        G920Control.FfbSlewDefault => "Slew default",
        G920Control.FfbSpikeMinus => "Spike cap −",
        G920Control.FfbSpikePlus => "Spike cap +",
        G920Control.FfbSpikeDefault => "Spike cap default",
        G920Control.FfbEpsilonMinus => "DI epsilon −",
        G920Control.FfbEpsilonPlus => "DI epsilon +",
        G920Control.FfbEpsilonDefault => "DI epsilon default",
        G920Control.FfbCenterStrengthMinus => "Strength −",
        G920Control.FfbCenterStrengthPlus => "Strength +",
        G920Control.FfbCenterStrengthDefault => "Strength default",
        G920Control.FfbCenterRangeMinus => "Range −",
        G920Control.FfbCenterRangePlus => "Range +",
        G920Control.FfbCenterRangeDefault => "Range default",
        G920Control.FfbCenterDeadzoneMinus => "Deadzone −",
        G920Control.FfbCenterDeadzonePlus => "Deadzone +",
        G920Control.FfbCenterDeadzoneDefault => "Deadzone default",
        G920Control.FfbDampVelMinus => "Damp vel −",
        G920Control.FfbDampVelPlus => "Damp vel +",
        G920Control.FfbDampVelDefault => "Damp vel default",
        G920Control.FfbDampDeadMinus => "Damp dead −",
        G920Control.FfbDampDeadPlus => "Damp dead +",
        G920Control.FfbDampDeadDefault => "Damp dead default",
        _ => control.ToString(),
    };
}
