using SteeringWheelEmulator.Core.Bridge;
using SteeringWheelEmulator.Core.Models;

namespace SteeringWheelEmulator.VirtualHid;

/// <summary>Creates the WinUHid virtual wheel for the selected <see cref="EmulatedDeviceKind"/>.</summary>
public static class VirtualDeviceFactory
{
    public static IVirtualG920Device Create(EmulatedDeviceKind kind) => kind switch
    {
        EmulatedDeviceKind.FanatecDd1PcComp => new VirtualFanatecDd1Device(EmulatedDeviceKind.FanatecDd1PcComp),
        _ => new VirtualG920Device(),
    };
}
