namespace SteeringWheelEmulator.Core.Models;

/// <summary>
/// Shared logical control catalog (<see cref="G920Control"/> targets) per emulated identity.
/// Fanatec uses the live ClubSport/Podium button map; G920 keeps the Xbox face layout.
/// </summary>
public sealed class WheelControlCatalog : IControlCatalog
{
    private readonly Func<G920Control, string>? _displayOverride;

    public WheelControlCatalog(
        EmulatedDeviceKind kind,
        int maxGearReverseOutputButton = 19,
        int defaultGearReverseOutputButton = 19,
        Func<G920Control, string>? displayOverride = null,
        IReadOnlyList<G920Control>? standardBindTargets = null,
        IReadOnlyList<G920Control>? customBindTargets = null,
        IReadOnlyList<G920Control>? buttonTargets = null,
        IReadOnlyList<G920Control>? axisTargets = null)
    {
        Kind = kind;
        MaxGearReverseOutputButton = maxGearReverseOutputButton;
        DefaultGearReverseOutputButton = defaultGearReverseOutputButton;
        _displayOverride = displayOverride;
        StandardBindTargets = standardBindTargets ?? G920ControlInfo.UiOrder;
        CustomBindTargets = customBindTargets ?? G920ControlInfo.CustomBindingTargets;
        AxisTargets = axisTargets ?? G920ControlInfo.AxisControls;
        ButtonTargets = buttonTargets ?? G920ControlInfo.ButtonControls;
        HatTargets = [G920Control.Hat, ..G920ControlInfo.HatDirectionControls];
    }

    public static WheelControlCatalog CreateFanatec(EmulatedDeviceKind kind) =>
        new(
            kind,
            maxGearReverseOutputButton: 32,
            // NFS Unbound Fanatec expects Gear R on button 12 (then 13–18 for 1–6).
            defaultGearReverseOutputButton: 12,
            displayOverride: FanatecDisplayName,
            standardBindTargets: G920ControlInfo.FanatecUiOrder,
            customBindTargets:
            [
                G920Control.Hat,
                ..G920ControlInfo.FanatecButtonControls,
            ],
            buttonTargets: G920ControlInfo.FanatecButtonControls,
            axisTargets: G920ControlInfo.FanatecAxisControls);

    public EmulatedDeviceKind Kind { get; }
    public IReadOnlyList<G920Control> StandardBindTargets { get; }
    public IReadOnlyList<G920Control> CustomBindTargets { get; }
    public IReadOnlyList<G920Control> AxisTargets { get; }
    public IReadOnlyList<G920Control> ButtonTargets { get; }
    public IReadOnlyList<G920Control> HatTargets { get; }
    public int MaxGearReverseOutputButton { get; }
    public int DefaultGearReverseOutputButton { get; }

    public string DisplayName(G920Control control) =>
        _displayOverride?.Invoke(control) ?? G920ControlInfo.DisplayName(control);

    public bool IsAxis(G920Control control) => AxisTargets.Contains(control);
    public bool IsButton(G920Control control) =>
        ButtonTargets.Contains(control) || control == G920Control.Hat;
    public bool IsHat(G920Control control) =>
        control == G920Control.Hat || G920ControlInfo.IsHatDirection(control);
    public bool IsCustomBindingTarget(G920Control control) =>
        CustomBindTargets.Contains(control);
    public bool IsStandardBindTarget(G920Control control) =>
        StandardBindTargets.Contains(control);

    private static string FanatecDisplayName(G920Control control) => control switch
    {
        G920Control.Steering => "Wheel",
        G920Control.Throttle => "Accelerator",
        G920Control.Brake => "Brake",
        G920Control.Clutch => "Clutch",
        G920Control.Handbrake => "Handbrake",
        G920Control.Hat => "Directional pad (hat)",
        G920Control.HatUp => "D-pad Up",
        G920Control.HatDown => "D-pad Down",
        G920Control.HatLeft => "D-pad Left",
        G920Control.HatRight => "D-pad Right",
        // NFS Fanatec face: virt button N (letter) — list order matches 1–4.
        G920Control.ButtonX => "1 (X)",
        G920Control.ButtonA => "2 (A)",
        G920Control.ButtonB => "3 (B)",
        G920Control.ButtonY => "4 (Y)",
        G920Control.PaddleRight => "5 (Right paddle)",
        G920Control.PaddleLeft => "6 (Left paddle)",
        G920Control.RightTrigger => "7 (RT)",
        G920Control.LeftTrigger => "8 (LT)",
        G920Control.ButtonView => "9 (View)",
        G920Control.ButtonMenu => "10 (Menu)",
        G920Control.ButtonRs => "11 (RSB)",
        // LSB is not virt 12 when Gear R defaults to 12 — packed on button 20.
        G920Control.ButtonLs => "20 (LSB)",
        G920Control.GearR => "Gear R",
        G920Control.Gear1 => "Gear 1",
        G920Control.Gear2 => "Gear 2",
        G920Control.Gear3 => "Gear 3",
        G920Control.Gear4 => "Gear 4",
        G920Control.Gear5 => "Gear 5",
        G920Control.Gear6 => "Gear 6",
        G920Control.Gear7 => "Gear 7",
        G920Control.Button21 => "21",
        G920Control.ButtonLb => "22 (LB)",
        G920Control.Button23 => "23",
        G920Control.Button24 => "24",
        G920Control.Button25 => "25",
        G920Control.ButtonRb => "26 (RB)",
        G920Control.Button27 => "27",
        G920Control.Button28 => "28",
        G920Control.Button29 => "29",
        G920Control.Button30 => "30",
        G920Control.Button31 => "31",
        G920Control.Button32 => "32",
        _ => G920ControlInfo.DisplayName(control),
    };
}
