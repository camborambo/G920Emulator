namespace G920Emulator.Core.Models;

public sealed class InputDeviceInfo
{
    /// <summary>DirectInput instance GUID (session / plug identity).</summary>
    public required string Id { get; init; }

    /// <summary>DirectInput product GUID (stable across replugs for the same hardware).</summary>
    public required string ProductId { get; init; }

    public required string Name { get; init; }
    public required string ProductName { get; init; }
    public bool SupportsForceFeedback { get; init; }
    public int AxisCount { get; init; }
    public int ButtonCount { get; init; }
    public bool HasHat { get; init; }
    public DeviceKind Kind { get; init; }
}

public enum DeviceKind
{
    Unknown,
    Wheelbase,
    Pedals,
    Shifter,
    Gamepad,
    MultiAxis,
}

public sealed class DeviceState
{
    public required string DeviceId { get; init; }
    public Dictionary<string, float> Axes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool[] Buttons { get; init; } = [];
    public int Hat { get; init; } = -1; // -1 = centered, else 0-7 clockwise from N
}

public sealed class MappedG920State
{
    public float Steering { get; set; }      // -1 .. 1
    public float Throttle { get; set; }      // 0 .. 1
    public float Brake { get; set; }         // 0 .. 1
    public float Clutch { get; set; }        // 0 .. 1
    public int Hat { get; set; } = -1;
    public bool ButtonA { get; set; }
    public bool ButtonB { get; set; }
    public bool ButtonX { get; set; }
    public bool ButtonY { get; set; }
    public bool ButtonLb { get; set; }
    public bool ButtonRb { get; set; }
    public bool ButtonView { get; set; }
    public bool ButtonMenu { get; set; }
    public bool ButtonLs { get; set; }
    public bool ButtonRs { get; set; }
    public bool PaddleLeft { get; set; }
    public bool PaddleRight { get; set; }
    public bool Gear1 { get; set; }
    public bool Gear2 { get; set; }
    public bool Gear3 { get; set; }
    public bool Gear4 { get; set; }
    public bool Gear5 { get; set; }
    public bool Gear6 { get; set; }
    public bool GearR { get; set; }

    /// <summary>Virtual DI button (1-19) used when <see cref="GearR"/> is active.</summary>
    public int GearReverseOutputButton { get; set; } = 19;

    public string ActiveGearLabel
    {
        get
        {
            if (GearR) return "R";
            if (Gear1) return "1";
            if (Gear2) return "2";
            if (Gear3) return "3";
            if (Gear4) return "4";
            if (Gear5) return "5";
            if (Gear6) return "6";
            return "N";
        }
    }
}
