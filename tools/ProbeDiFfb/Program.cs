using SharpDX.DirectInput;

// Acquire the virtual G920 via DirectInput and try CreateEffect — same path Unbound uses.
using var di = new DirectInput();
var sticks = di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
Console.WriteLine($"DI devices: {sticks.Count}");
Joystick? g920 = null;
foreach (var inst in sticks)
{
    Console.WriteLine($"  {inst.InstanceName} | {inst.ProductName} | PG={inst.ProductGuid} | Type={inst.Type} | FF={inst.ForceFeedbackDriverGuid}");
    var pg = inst.ProductGuid.ToByteArray();
    var vid = BitConverter.ToUInt16(pg, 0);
    var pid = BitConverter.ToUInt16(pg, 2);
    var isG920 = (vid == 0x046D && pid == 0xC262) ||
                 inst.ProductName.Contains("G920", StringComparison.OrdinalIgnoreCase) ||
                 inst.InstanceName.Contains("G920", StringComparison.OrdinalIgnoreCase) ||
                 inst.ProductName.Contains("Driving Force", StringComparison.OrdinalIgnoreCase);
    if (isG920 && g920 is null)
    {
        try
        {
            g920 = new Joystick(di, inst.InstanceGuid);
            Console.WriteLine($"  -> selected InstanceGuid={inst.InstanceGuid} FFDriver={inst.ForceFeedbackDriverGuid}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  -> open failed: {ex.Message}");
        }
    }
}

if (g920 is null)
{
    Console.WriteLine("FAIL: virtual G920 not found in DirectInput.");
    return 1;
}

try
{
    g920.SetCooperativeLevel(IntPtr.Zero, CooperativeLevel.Background | CooperativeLevel.Exclusive);
}
catch
{
    try { g920.SetCooperativeLevel(IntPtr.Zero, CooperativeLevel.Background | CooperativeLevel.NonExclusive); }
    catch (Exception ex) { Console.WriteLine("Coop failed: " + ex.Message); }
}

try { g920.Acquire(); Console.WriteLine("Acquire OK"); }
catch (Exception ex) { Console.WriteLine("Acquire failed: " + ex.Message); }

try
{
    var caps = g920.Capabilities;
    Console.WriteLine($"Caps: Axes={caps.AxeCount} Buttons={caps.ButtonCount} FFSamplePeriod={caps.ForceFeedbackSamplePeriod} FFMinTime={caps.ForceFeedbackMinimumTimeResolution} Flags={caps.Flags}");
}
catch (Exception ex) { Console.WriteLine("Caps: " + ex.Message); }

Effect? effect = null;
try
{
    var pars = new EffectParameters
    {
        Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
        Duration = int.MaxValue,
        SamplePeriod = 0,
        Gain = 10_000,
        TriggerButton = -1,
        TriggerRepeatInterval = 0,
        StartDelay = 0,
        Axes = [0], // DIJOFS_X
        Directions = [0],
        Parameters = new ConstantForce { Magnitude = 5_000 },
    };
    effect = new Effect(g920, EffectGuid.ConstantForce, pars);
    Console.WriteLine("CreateEffect ConstantForce OK");
    effect.Start();
    Console.WriteLine("Effect.Start OK — hold 2s");
    Thread.Sleep(2000);
    effect.Stop();
    Console.WriteLine("Effect.Stop OK");
}
catch (Exception ex)
{
    Console.WriteLine("CreateEffect/Start FAILED: " + ex);
}

effect?.Dispose();
g920.Unacquire();
g920.Dispose();
Console.WriteLine("Done — check emulator Host/HID++ write counters.");
return 0;
