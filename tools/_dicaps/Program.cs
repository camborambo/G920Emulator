using SharpDX.DirectInput;
using var di = new DirectInput();
foreach (var inst in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
{
    if (!inst.ProductName.Contains("G920", StringComparison.OrdinalIgnoreCase) &&
        !inst.InstanceName.Contains("G920", StringComparison.OrdinalIgnoreCase))
        continue;
    Console.WriteLine($"INST type={inst.Type} subtype={inst.Subtype} usage={inst.UsagePage:X}/{inst.Usage:X}");
    Console.WriteLine($" name='{inst.InstanceName}' prod='{inst.ProductName}'");
    Console.WriteLine($" product={inst.ProductGuid} ffDriver={inst.ForceFeedbackDriverGuid}");
    using var joy = new Joystick(di, inst.InstanceGuid);
    joy.Acquire();
    var caps = joy.Capabilities;
    Console.WriteLine($" caps axes={caps.AxeCount} buttons={caps.ButtonCount} povs={caps.PovCount} ff={caps.ForceFeedbackSamplePeriod} type={caps.Type} subtype={caps.Subtype} flags={caps.Flags}");
    foreach (var o in joy.GetObjects())
        Console.WriteLine($"  obj name='{o.Name}' offset={o.Offset} aspect={o.Aspect} objectId={o.ObjectId} dimension={o.Dimension}");
}
Console.WriteLine("DONE");
