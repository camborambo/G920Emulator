using SharpDX.DirectInput;

using var di = new DirectInput();
foreach (var inst in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
{
    using var j = new Joystick(di, inst.InstanceGuid);
    j.Acquire();
    j.Poll();
    var s = j.GetCurrentState();
    Console.WriteLine($"DEVICE {inst.Type} '{inst.InstanceName}' id={inst.InstanceGuid:D}");
    Console.WriteLine($"  X={s.X} Y={s.Y} Z={s.Z} RX={s.RotationX} RY={s.RotationY} RZ={s.RotationZ}");
    Console.WriteLine($"  pov0={s.PointOfViewControllers.FirstOrDefault()}");
    var btns = string.Join(",", Enumerable.Range(0, Math.Min(20, s.Buttons.Length)).Where(i => s.Buttons[i]).Select(i => i + 1));
    Console.WriteLine($"  pressedButtons=[{btns}]");
}
