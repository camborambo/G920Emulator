using SharpDX.DirectInput;
using var di = new DirectInput();
foreach (var d in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
  Console.WriteLine($"DI type={d.Type} subtype={d.Subtype} usage={d.UsagePage:X}/{d.Usage:X} name='{d.InstanceName}' prod='{d.ProductName}' product={d.ProductGuid} ff={d.ForceFeedbackDriverGuid}");
Console.WriteLine("DI_DONE");
