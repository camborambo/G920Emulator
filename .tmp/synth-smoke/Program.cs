using G920Emulator.Core.Models;
using G920Emulator.Core.Telemetry;

var synth = new TelemetrySynthesizer();
synth.Configure(TelemetryTuning.CreateDefault());
var mapped = new MappedG920State { Throttle = 1f, Brake = 0f, Clutch = 0f, Gear1 = true };
for (int i = 0; i < 120; i++)
{
    var f = synth.Update(mapped, 0f, null, knownGameRunning: true, dtSec: 1.0/60.0);
    if (i % 30 == 0) Console.WriteLine($"i={i} speed={f.SpeedKmh:0.0} rpm={f.EngineRpm:0} gear={f.Gear} session={f.SessionRunning}");
}
mapped = mapped with { Throttle = 0f };
for (int i = 0; i < 60; i++)
{
    var f = synth.Update(mapped, 0f, null, knownGameRunning: true, dtSec: 1.0/60.0);
    if (i % 20 == 0) Console.WriteLine($"coast i={i} speed={f.SpeedKmh:0.0}");
}
Console.WriteLine("ok");
