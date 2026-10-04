using SharpDX.DirectInput;
using var di = new DirectInput();
var devices = di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
    .Select(i => {
        var j = new Joystick(di, i.InstanceGuid);
        j.Properties.BufferSize = 128;
        j.Acquire();
        return (Inst: i, Joy: j);
    }).ToList();

Console.WriteLine($"Monitoring {devices.Count} device(s) for 4s...");
foreach (var d in devices)
    Console.WriteLine($"  {d.Inst.Type} '{d.Inst.InstanceName}' buttons={d.Joy.Capabilities.ButtonCount} povs={d.Joy.Capabilities.PovCount}");

var end = DateTime.UtcNow.AddSeconds(4);
var last = new Dictionary<Guid, string>();
while (DateTime.UtcNow < end)
{
    foreach (var (inst, joy) in devices)
    {
        joy.Poll();
        var s = joy.GetCurrentState();
        var pressed = new List<string>();
        for (int b = 0; b < s.Buttons.Length; b++)
            if (s.Buttons[b]) pressed.Add($"B{b+1}");
        for (int p = 0; p < s.PointOfViewControllers.Length; p++)
        {
            var v = s.PointOfViewControllers[p];
            if (v >= 0) pressed.Add($"POV{p}={v}");
        }
        // notable axes (non-center / non-rest)
        void Axis(string n, int v, int center, int dead)
        {
            if (Math.Abs(v - center) > dead) pressed.Add($"{n}={v}");
        }
        Axis("X", s.X, 32767, 2500);
        Axis("Y", s.Y, 32767, 2500);
        Axis("Z", s.Z, 32767, 2500);
        Axis("RX", s.RotationX, 32767, 2500);
        Axis("RY", s.RotationY, 32767, 2500);
        Axis("RZ", s.RotationZ, 32767, 2500);
        // pedals often 0..65535 with rest at 0 or 65535
        if (s.X is < 500 or > 65000) { /* skip extreme only if needed */ }
        var line = pressed.Count == 0 ? "(idle)" : string.Join(",", pressed);
        if (!last.TryGetValue(inst.InstanceGuid, out var prev) || prev != line)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{inst.Type}] {inst.InstanceName}: {line}");
            last[inst.InstanceGuid] = line;
        }
    }
    Thread.Sleep(50);
}
Console.WriteLine("DONE");
foreach (var d in devices) d.Joy.Dispose();
