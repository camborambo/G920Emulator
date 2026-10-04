using Windows.Gaming.Input;

Console.WriteLine("RawGameControllers:");
foreach (var c in RawGameController.RawGameControllers)
{
    Console.WriteLine($"  raw name='{c.DisplayName}' vid=0x{c.HardwareVendorId:X4} pid=0x{c.HardwareProductId:X4} buttons={c.ButtonCount} axes={c.AxisCount} switches={c.SwitchCount}");
    try
    {
        if (Gamepad.FromGameController(c) is Gamepad gp)
            Console.WriteLine("    -> Gamepad.FromGameController YES");
    }
    catch (Exception ex) { Console.WriteLine("    -> Gamepad.FromGameController: " + ex.GetType().Name); }
    try
    {
        if (RacingWheel.FromGameController(c) is RacingWheel rw)
            Console.WriteLine($"    -> RacingWheel.FromGameController YES wheel={rw.WheelMotor}");
        else
            Console.WriteLine("    -> RacingWheel.FromGameController null");
    }
    catch (Exception ex) { Console.WriteLine("    -> RacingWheel.FromGameController: " + ex.GetType().Name + " " + ex.Message); }
}

Console.WriteLine("Gamepads count=" + Gamepad.Gamepads.Count);
foreach (var g in Gamepad.Gamepads) Console.WriteLine("  gamepad present");

Console.WriteLine("RacingWheels count=" + RacingWheel.RacingWheels.Count);
foreach (var r in RacingWheel.RacingWheels) Console.WriteLine("  racing wheel present");
Console.WriteLine("DONE");
