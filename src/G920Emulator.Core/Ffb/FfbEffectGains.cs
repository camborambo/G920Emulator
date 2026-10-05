namespace G920Emulator.Core.Ffb;

/// <summary>
/// Per DirectInput effect-type gains (0..2 = 0–200%).
/// Applied in <c>g920ffb.dll</c> before effects are mixed into the output torque.
/// </summary>
public sealed class FfbEffectGains
{
    public const double MaxGain = 2.0;

    public const int TypeCount = 12;

    // DIEFT type ids
    public const int Constant = 0;
    public const int Ramp = 1;
    public const int Square = 2;
    public const int Sine = 3;
    public const int Triangle = 4;
    public const int SawUp = 5;
    public const int SawDown = 6;
    public const int Spring = 7;
    public const int Damper = 8;
    public const int Inertia = 9;
    public const int Friction = 10;
    public const int Custom = 11;

    public double ConstantForce { get; set; } = 1.0;
    public double RampForce { get; set; } = 1.0;
    /// <summary>Applies to Square, Sine, Triangle, Sawtooth Up/Down.</summary>
    public double Periodic { get; set; } = 1.0;
    public double SpringForce { get; set; } = 1.0;
    public double DamperForce { get; set; } = 1.0;
    public double InertiaForce { get; set; } = 1.0;
    public double FrictionForce { get; set; } = 1.0;
    public double CustomForce { get; set; } = 1.0;

    public void Clamp()
    {
        ConstantForce = ClampGain(ConstantForce);
        RampForce = ClampGain(RampForce);
        Periodic = ClampGain(Periodic);
        SpringForce = ClampGain(SpringForce);
        DamperForce = ClampGain(DamperForce);
        InertiaForce = ClampGain(InertiaForce);
        FrictionForce = ClampGain(FrictionForce);
        CustomForce = ClampGain(CustomForce);
    }

    /// <summary>DI type id → gain (0–2).</summary>
    public double ForType(int typeId) => typeId switch
    {
        Constant => ConstantForce,
        Ramp => RampForce,
        Square or Sine or Triangle or SawUp or SawDown => Periodic,
        Spring => SpringForce,
        Damper => DamperForce,
        Inertia => InertiaForce,
        Friction => FrictionForce,
        Custom => CustomForce,
        _ => 1.0,
    };

    /// <summary>16 slots of UINT16 gains (10000 = 100%, up to 20000 = 200%).</summary>
    public ushort[] ToSharedMemoryGains()
    {
        Clamp();
        var gains = new ushort[16];
        for (var i = 0; i < gains.Length; i++)
            gains[i] = ToDi(ForType(i));
        return gains;
    }

    public static FfbEffectGains CreateDefault() => new();

    private static double ClampGain(double v) => Math.Clamp(v, 0, MaxGain);

    private static ushort ToDi(double gain) =>
        (ushort)Math.Clamp((int)Math.Round(gain * 10000), 0, (int)(MaxGain * 10000));
}
