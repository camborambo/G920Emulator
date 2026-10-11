using SteeringWheelEmulator.Core.Models;

namespace SteeringWheelEmulator.Core.Mapping;

/// <summary>
/// Fanatec DD1 HID input report matching live PC Comp DI faces:
///   X = Wheel, Y = Combined Pedals (center rest), Z = Accelerator,
///   Rz = Brake, Slider = Clutch, Dial = Handbrake (DI Slider1).
/// Buttons: 5 right paddle … 26 RB (see catalog). Report ID 1, 18 bytes incl. id.
/// </summary>
public static class FanatecDd1ReportBuilder
{
    public const byte ReportId = 0x01;
    public const int ReportSize = 18;

    public static byte[] Build(MappedG920State state)
    {
        var report = new byte[ReportSize];
        report[0] = ReportId;

        uint buttons = 0;
        // NFS / ClubSport face order on Fanatec: 1=X, 2=A, 3=B, 4=Y
        if (state.ButtonX) buttons |= 1u << 0;   // 1 X
        if (state.ButtonA) buttons |= 1u << 1;   // 2 A
        if (state.ButtonB) buttons |= 1u << 2;   // 3 B
        if (state.ButtonY) buttons |= 1u << 3;   // 4 Y
        if (state.PaddleRight) buttons |= 1u << 4; // 5
        if (state.PaddleLeft) buttons |= 1u << 5;  // 6
        if (state.RightTrigger) buttons |= 1u << 6; // 7 RT
        if (state.LeftTrigger) buttons |= 1u << 7;  // 8 LT
        if (state.ButtonView) buttons |= 1u << 8;   // 9 View
        if (state.ButtonMenu) buttons |= 1u << 9;   // 10 Menu
        if (state.ButtonRs) buttons |= 1u << 10;    // 11 RSB
        // LSB must not sit on bit 11 when Gear R defaults to virt button 12 (NFS).
        if (state.ButtonLs) buttons |= 1u << 19;    // 20 LSB

        // H-pattern: Gear R..7 consecutive from GearReverseOutputButton (default 12).
        var reverseBtn = state.GearReverseOutputButton <= 0 ? 12 : state.GearReverseOutputButton;
        reverseBtn = Math.Clamp(reverseBtn, 1, 26); // leave room for Gear7 (+6)
        if (state.GearR) buttons |= 1u << (reverseBtn - 1);
        if (state.Gear1) buttons |= 1u << reverseBtn;
        if (state.Gear2) buttons |= 1u << (reverseBtn + 1);
        if (state.Gear3) buttons |= 1u << (reverseBtn + 2);
        if (state.Gear4) buttons |= 1u << (reverseBtn + 3);
        if (state.Gear5) buttons |= 1u << (reverseBtn + 4);
        if (state.Gear6) buttons |= 1u << (reverseBtn + 5);
        if (state.Gear7) buttons |= 1u << (reverseBtn + 6);

        if (state.Button21) buttons |= 1u << 20; // 21
        if (state.ButtonLb) buttons |= 1u << 21; // 22 LB
        if (state.Button23) buttons |= 1u << 22; // 23
        if (state.Button24) buttons |= 1u << 23; // 24
        if (state.Button25) buttons |= 1u << 24; // 25
        if (state.ButtonRb) buttons |= 1u << 25; // 26 RB
        if (state.Button27) buttons |= 1u << 26; // 27
        if (state.Button28) buttons |= 1u << 27;
        if (state.Button29) buttons |= 1u << 28;
        if (state.Button30) buttons |= 1u << 29;
        if (state.Button31) buttons |= 1u << 30;
        if (state.Button32) buttons |= 1u << 31;

        report[1] = (byte)(buttons & 0xFF);
        report[2] = (byte)((buttons >> 8) & 0xFF);
        report[3] = (byte)((buttons >> 16) & 0xFF);
        report[4] = (byte)((buttons >> 24) & 0xFF);

        byte hatNibble = state.Hat is >= 0 and <= 7 ? (byte)state.Hat : (byte)0x0F;
        report[5] = hatNibble;

        var steering = (ushort)Math.Clamp((int)Math.Round((state.Steering + 1f) * 0.5f * 65535f), 0, 65535);
        report[6] = (byte)(steering & 0xFF);
        report[7] = (byte)(steering >> 8);

        // Live PC Comp: Y = Combined Pedals (center rest). Low Y = brake side, high = throttle.
        // Z / Rz / Slider are inverted like real Fanatec/G920 pedals: max = released, 0 = pressed.
        // Writing 0 at idle made Unbound treat unbound Accel/Brake/Clutch as fully on.
        var thr = Math.Clamp(state.Throttle, 0f, 1f);
        var brk = Math.Clamp(state.Brake, 0f, 1f);
        var clutch = Math.Clamp(state.Clutch, 0f, 1f);
        var handbrake = Math.Clamp(state.Handbrake, 0f, 1f);
        var combined = Math.Clamp(0.5f + (thr - brk) * 0.5f, 0f, 1f);
        WriteAxis16(report, 8, combined);           // Y Combined Pedals (center rest)
        WriteAxis16(report, 10, 1f - thr);         // Z Accelerator (inverted)
        WriteAxis16(report, 12, 1f - brk);         // Rz Brake (inverted)
        WriteAxis16(report, 14, 1f - clutch);      // Slider Clutch (inverted)
        // Dial / DI Slider1: 0 = released, 1 = pulled (same as ClubSport / HBP handbrake).
        WriteAxis16(report, 16, handbrake);
        return report;
    }

    public static byte[] BuildIdle() => Build(new MappedG920State());

    private static void WriteAxis16(byte[] report, int offset, float value01)
    {
        var v = (ushort)Math.Clamp((int)Math.Round(Math.Clamp(value01, 0f, 1f) * 65535f), 0, 65535);
        report[offset] = (byte)(v & 0xFF);
        report[offset + 1] = (byte)(v >> 8);
    }
}
