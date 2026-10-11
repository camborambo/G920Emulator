using SteeringWheelEmulator.Core.Models;

namespace SteeringWheelEmulator.Core.Mapping;

/// <summary>
/// Builds a numbered HID input report matching a real G920 (046D:C262) report ID 1.
/// Layout (10 bytes total including report ID):
///   [0]     report id = 1
///   [1..3]  hat (low nibble, 0xF = null/center) + 19 buttons + 1 pad bit
///   [4..5]  steering X (16-bit little-endian, 0..65535, center 32768)
///   [6]     throttle Y  (0xFF released … 0x00 fully pressed - Logitech inverted)
///   [7]     brake Z     (inverted)
///   [8]     clutch Rz   (inverted)
///   [9]     3 vendor bits + 5 pad
/// </summary>
public static class G920ReportBuilder
{
    public const byte ReportId = 0x01;
    public const int ReportSize = 10;

    public static byte[] Build(MappedG920State state)
    {
        var report = new byte[ReportSize];
        report[0] = ReportId;

        byte hatNibble = state.Hat is >= 0 and <= 7 ? (byte)state.Hat : (byte)0x0F;

        // Face/paddles: DI buttons 1-10 (0-based bits). Real G920: RB = button 4, LB = button 5.
        // Paddles share the matching bumper bit.
        //
        // Gears 1-6: LGS / G920 Driving Force Shifter buttons 13-18.
        // Reverse: profile-selected DI button (default 19 = LGS; Unbound uses 12).
        uint buttons = 0;
        if (state.ButtonA) buttons |= 1u << 0;
        if (state.ButtonB) buttons |= 1u << 1;
        if (state.ButtonX) buttons |= 1u << 2;
        if (state.ButtonY) buttons |= 1u << 3;
        if (state.ButtonRb) buttons |= 1u << 4;   // Button 4 (Right bumper / right paddle)
        if (state.ButtonLb) buttons |= 1u << 5;   // Button 5 (Left bumper / left paddle)
        if (state.PaddleRight) buttons |= 1u << 4;
        if (state.PaddleLeft) buttons |= 1u << 5;
        if (state.ButtonView) buttons |= 1u << 6;
        if (state.ButtonMenu) buttons |= 1u << 7;
        if (state.ButtonLs) buttons |= 1u << 8;
        if (state.ButtonRs) buttons |= 1u << 9;
        if (state.Gear1) buttons |= 1u << 12; // Button 13
        if (state.Gear2) buttons |= 1u << 13; // Button 14
        if (state.Gear3) buttons |= 1u << 14; // Button 15
        if (state.Gear4) buttons |= 1u << 15; // Button 16
        if (state.Gear5) buttons |= 1u << 16; // Button 17
        if (state.Gear6) buttons |= 1u << 17; // Button 18
        if (state.GearR)
        {
            var reverseBtn = state.GearReverseOutputButton <= 0 ? 19 : state.GearReverseOutputButton;
            reverseBtn = Math.Clamp(reverseBtn, 1, 19);
            buttons |= 1u << (reverseBtn - 1);
        }

        uint packed = hatNibble | (buttons << 4);
        report[1] = (byte)(packed & 0xFF);
        report[2] = (byte)((packed >> 8) & 0xFF);
        report[3] = (byte)((packed >> 16) & 0xFF);

        var steering = (ushort)Math.Clamp((int)Math.Round((state.Steering + 1f) * 0.5f * 65535f), 0, 65535);
        report[4] = (byte)(steering & 0xFF);
        report[5] = (byte)(steering >> 8);

        // Real G920 pedals: 0xFF = released, 0x00 = pressed.
        report[6] = ToInvertedPedal(state.Throttle);
        report[7] = ToInvertedPedal(state.Brake);
        report[8] = ToInvertedPedal(state.Clutch);
        report[9] = 0x07;
        return report;
    }

    private static byte ToInvertedPedal(float value01)
    {
        var pressed = Math.Clamp((int)Math.Round(value01 * 255f), 0, 255);
        return (byte)(255 - pressed);
    }
}
