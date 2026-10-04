using System.Runtime.InteropServices;
for (int i = 0; i < 4; i++)
{
    var hr = Native.XInputGetState(i, out var st);
    Console.WriteLine(hr == 0 ? $"XInput[{i}] CONNECTED pkt={st.dwPacketNumber} buttons=0x{st.Gamepad.wButtons:X4} LT={st.Gamepad.bLeftTrigger} RT={st.Gamepad.bRightTrigger} LX={st.Gamepad.sThumbLX}" : $"XInput[{i}] empty (0x{hr:X8})");
}
Console.WriteLine("XINPUT_DONE");
static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons; public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }
    [DllImport("xinput1_4.dll")]
    public static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);
}
