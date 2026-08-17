using System.Buffers.Binary;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace QuestPad.Host;

internal interface IOutputBackend : IDisposable
{
    OutputMode Mode { get; }
    string Name { get; }
    void Apply(LogicalGamepadState state, ProcessedMotion motion, int? batteryPercent);
    void Neutral();
}

internal sealed class OutputBackendManager : IDisposable
{
    private readonly ViGEmClient client;
    private readonly Action<byte, byte> rumble;
    private IOutputBackend? backend;

    public OutputBackendManager(Action<byte, byte> rumble)
    {
        this.rumble = rumble;
        client = new ViGEmClient();
    }

    public IOutputBackend? Current => backend;

    public IOutputBackend Ensure(OutputMode mode)
    {
        if (backend?.Mode == mode) return backend;

        if (backend is not null)
        {
            try { backend.Neutral(); } catch { }
            backend.Dispose();
            backend = null;
        }

        backend = mode switch
        {
            OutputMode.DualShock4 => new DualShock4Backend(client, rumble),
            _ => new Xbox360Backend(client, rumble)
        };
        return backend;
    }

    public void Dispose()
    {
        if (backend is not null)
        {
            try { backend.Neutral(); } catch { }
            backend.Dispose();
            backend = null;
        }
        client.Dispose();
    }
}

internal sealed class Xbox360Backend : IOutputBackend
{
    private readonly IXbox360Controller pad;

    public Xbox360Backend(ViGEmClient client, Action<byte, byte> rumble)
    {
        pad = client.CreateXbox360Controller();
        pad.AutoSubmitReport = false;
        pad.FeedbackReceived += (_, e) => rumble(e.LargeMotor, e.SmallMotor);
        pad.Connect();
        Neutral();
    }

    public OutputMode Mode => OutputMode.Xbox360;
    public string Name => "Xbox 360 / XInput";

    public void Apply(LogicalGamepadState s, ProcessedMotion motion, int? batteryPercent)
    {
        // ViGEm emulates an Xbox 360 *wired* endpoint. XInput therefore has no
        // feeder-side battery field to populate; keep the parameter only so all
        // backends share one interface.
        _ = batteryPercent;
        pad.ResetReport();
        pad.SetAxisValue(Xbox360Axis.LeftThumbX, ToShort(s.LX));
        pad.SetAxisValue(Xbox360Axis.LeftThumbY, ToShort(s.LY));
        pad.SetAxisValue(Xbox360Axis.RightThumbX, ToShort(s.RX));
        pad.SetAxisValue(Xbox360Axis.RightThumbY, ToShort(s.RY));
        pad.SetSliderValue(Xbox360Slider.LeftTrigger, ToByte01(s.LT));
        pad.SetSliderValue(Xbox360Slider.RightTrigger, ToByte01(s.RT));

        Set(Xbox360Button.LeftShoulder, s.LB);
        Set(Xbox360Button.RightShoulder, s.RB);
        Set(Xbox360Button.A, s.A);
        Set(Xbox360Button.B, s.B);
        Set(Xbox360Button.X, s.X);
        Set(Xbox360Button.Y, s.Y);
        Set(Xbox360Button.LeftThumb, s.L3);
        Set(Xbox360Button.RightThumb, s.R3);
        Set(Xbox360Button.Up, s.DpadUp);
        Set(Xbox360Button.Down, s.DpadDown);
        Set(Xbox360Button.Left, s.DpadLeft);
        Set(Xbox360Button.Right, s.DpadRight);
        Set(Xbox360Button.Back, s.View);
        Set(Xbox360Button.Start, s.Menu);
        Set(Xbox360Button.Guide, s.Guide);
        pad.SubmitReport();
    }

    public void Neutral()
    {
        pad.ResetReport();
        pad.SubmitReport();
    }

    public void Dispose()
    {
        // IXbox360Controller is not IDisposable in ViGEm.NET. Disconnect releases the
        // bus target; the owning ViGEmClient is disposed by OutputBackendManager.
        try { pad.Disconnect(); } catch { }
    }

    private void Set(Xbox360Button button, bool pressed) => pad.SetButtonState(button, pressed);

    private static short ToShort(float value)
    {
        double x = Math.Clamp(value, -1.0f, 1.0f);
        if (x <= -1.0) return short.MinValue;
        return (short)Math.Round(x * short.MaxValue);
    }

    private static byte ToByte01(float value) =>
        (byte)Math.Clamp(Math.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0), 0, 255);
}

internal sealed class DualShock4Backend : IOutputBackend
{
    private readonly IDualShock4Controller pad;
    private ushort timestamp;
    private int lastBatteryPercent = 100;

    // A physical DS4 exposes calibrated sensor data through its HID feature report.
    // ViGEm emulates that device-side calibration while DS4_REPORT_EX carries the
    // signed raw samples. About 16 raw counts per degree/second matches the native
    // DS4 sensor scale closely; keep the conversion isolated here for hardware/game
    // validation and future per-backend calibration without touching Quest transport.
    private const float GyroCountsPerDegreeSecond = 16.0f;
    private const float AccelCountsPerG = 8192.0f;

    public DualShock4Backend(ViGEmClient client, Action<byte, byte> rumble)
    {
        pad = client.CreateDualShock4Controller();
#pragma warning disable CS0618
        pad.FeedbackReceived += (_, e) => rumble(e.LargeMotor, e.SmallMotor);
#pragma warning restore CS0618
        pad.Connect();
        Neutral();
    }

    public OutputMode Mode => OutputMode.DualShock4;
    public string Name => "DualShock 4 / native motion";

    public void Apply(LogicalGamepadState s, ProcessedMotion motion, int? batteryPercent)
    {
        if (batteryPercent.HasValue)
            lastBatteryPercent = Math.Clamp(batteryPercent.Value, 0, 100);

        byte[] report = BuildBaseReport(s, lastBatteryPercent);
        timestamp++;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(9, 2), timestamp);

        if (motion.GyroValid)
        {
            const float radToDeg = 180.0f / MathF.PI;
            // OpenXR controller-local +X/+Y/+Z is kept as the first experimental
            // mapping. Axis/sign tuning is intentionally isolated to these three
            // assignments so Strinova and other native-motion games can validate it.
            short gx = GyroRaw(motion.GyroRadiansPerSecond.X * radToDeg);
            short gy = GyroRaw(motion.GyroRadiansPerSecond.Y * radToDeg);
            short gz = GyroRaw(motion.GyroRadiansPerSecond.Z * radToDeg);
            BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(12, 2), gx);
            BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(14, 2), gy);
            BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(16, 2), gz);
        }

        if (motion.AccelerationValid)
        {
            // A real DS4 carries signed accelerometer samples at 8192 counts/g.
            // QuestPad supplies the gravity/specific-force component only, derived
            // from the right Touch orientation. Translational acceleration is not
            // fabricated from noisy finite differences.
            BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(18, 2), AccelRaw(motion.AccelerationG.X));
            BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(20, 2), AccelRaw(motion.AccelerationG.Y));
            BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(22, 2), AccelRaw(motion.AccelerationG.Z));
        }

        pad.SubmitRawReport(report);
    }

    public void Neutral()
    {
        pad.SubmitRawReport(BuildBaseReport(LogicalGamepadState.Neutral(), lastBatteryPercent));
    }

    public void Dispose()
    {
        try { pad.Disconnect(); } catch { }
        pad.Dispose();
    }

    private static byte[] BuildBaseReport(LogicalGamepadState s, int batteryPercent)
    {
        byte[] r = new byte[63];
        r[0] = ToDs4Axis(s.LX);
        r[1] = ToDs4Axis(-s.LY);
        r[2] = ToDs4Axis(s.RX);
        r[3] = ToDs4Axis(-s.RY);

        ushort buttons = DpadNibble(s);
        if (s.X) buttons |= 1 << 4;          // Square
        if (s.A) buttons |= 1 << 5;          // Cross
        if (s.B) buttons |= 1 << 6;          // Circle
        if (s.Y) buttons |= 1 << 7;          // Triangle
        if (s.LB) buttons |= 1 << 8;         // L1
        if (s.RB) buttons |= 1 << 9;         // R1
        if (s.LT > 0.05f) buttons |= 1 << 10;// L2 digital flag
        if (s.RT > 0.05f) buttons |= 1 << 11;// R2 digital flag
        if (s.View) buttons |= 1 << 12;       // Share
        if (s.Menu) buttons |= 1 << 13;       // Options
        if (s.L3) buttons |= 1 << 14;
        if (s.R3) buttons |= 1 << 15;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4, 2), buttons);

        r[6] = 0;
        if (s.Guide) r[6] |= 0x01;          // PS Home
        if (s.TouchpadClick) r[6] |= 0x02;  // Touchpad click
        r[7] = ToByte01(s.LT);
        r[8] = ToByte01(s.RT);

        // DS4 USB input report byte 30 (29 in ViGEm's 63-byte buffer because
        // Report ID 0x01 is omitted) is status[0]. Bits 0..3 encode battery
        // capacity and bit 4 is cable/charging state. Quest Touch controllers
        // are battery-powered, so keep cable=0 and expose the weakest controller
        // as the virtual pad's single battery. Real DS4 firmware reports coarse
        // 10%-wide bins; level 0 is commonly presented as ~5%, which explains
        // Steam's previous constant 5% warning when this byte was left at zero.
        r[29] = ToDs4BatteryLevel(batteryPercent);
        return r;
    }

    private static ushort DpadNibble(LogicalGamepadState s)
    {
        if (s.DpadUp && s.DpadLeft) return 7;
        if (s.DpadUp && s.DpadRight) return 1;
        if (s.DpadDown && s.DpadLeft) return 5;
        if (s.DpadDown && s.DpadRight) return 3;
        if (s.DpadUp) return 0;
        if (s.DpadRight) return 2;
        if (s.DpadDown) return 4;
        if (s.DpadLeft) return 6;
        return 8;
    }

    private static byte ToDs4BatteryLevel(int batteryPercent)
    {
        int percent = Math.Clamp(batteryPercent, 0, 100);
        return (byte)(percent >= 100 ? 10 : percent / 10);
    }

    private static short GyroRaw(float degreesPerSecond)
    {
        double raw = degreesPerSecond * GyroCountsPerDegreeSecond;
        return (short)Math.Clamp(Math.Round(raw), short.MinValue, short.MaxValue);
    }

    private static short AccelRaw(float g)
    {
        double raw = g * AccelCountsPerG;
        return (short)Math.Clamp(Math.Round(raw), short.MinValue, short.MaxValue);
    }

    private static byte ToDs4Axis(float value) =>
        (byte)Math.Clamp(Math.Round((Math.Clamp(value, -1.0f, 1.0f) + 1.0f) * 127.5f), 0, 255);

    private static byte ToByte01(float value) =>
        (byte)Math.Clamp(Math.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f), 0, 255);
}
