using System.Numerics;

namespace QuestPad.Host;

internal enum OutputMode
{
    Xbox360,
    DualShock4
}

internal enum GyroSourceMode
{
    Off,
    CameraAssisted,
    AngularRate
}

internal enum SteeringMode
{
    Off,
    Mounted,
    FreeAir,
    Hybrid
}

internal enum SmoothingLevel
{
    Off,
    Light,
    Medium,
    Strong
}

internal readonly record struct RuntimeSettingsSnapshot(
    OutputMode Output,
    GyroSourceMode GyroSource,
    SmoothingLevel GyroSmoothing,
    SteeringMode Steering,
    SmoothingLevel SteeringSmoothing,
    float SteeringRangeDegrees);

internal sealed class RuntimeSettings
{
    private readonly object gate = new();
    private RuntimeSettingsSnapshot value = new(
        OutputMode.Xbox360,
        GyroSourceMode.Off,
        SmoothingLevel.Off,
        SteeringMode.Off,
        SmoothingLevel.Light,
        240.0f);

    public RuntimeSettingsSnapshot Snapshot()
    {
        lock (gate) return value;
    }

    public void SetOutput(OutputMode output)
    {
        lock (gate)
        {
            // Native gyro has nowhere to go in XInput. Selecting Xbox explicitly
            // therefore disables gyro instead of silently spending Quest tracking
            // power on data that the active backend cannot expose.
            value = value with
            {
                Output = output,
                GyroSource = output == OutputMode.Xbox360 ? GyroSourceMode.Off : value.GyroSource
            };
        }
    }

    public void SetGyroSource(GyroSourceMode source)
    {
        lock (gate)
        {
            value = value with
            {
                GyroSource = source,
                Output = source == GyroSourceMode.Off ? value.Output : OutputMode.DualShock4
            };
        }
    }

    public void SetGyroSmoothing(SmoothingLevel smoothing)
    {
        lock (gate) value = value with { GyroSmoothing = smoothing };
    }

    public void SetSteering(SteeringMode steering)
    {
        lock (gate) value = value with { Steering = steering };
    }

    public void SetSteeringSmoothing(SmoothingLevel smoothing)
    {
        lock (gate) value = value with { SteeringSmoothing = smoothing };
    }

    public void SetSteeringRange(float totalDegrees)
    {
        lock (gate) value = value with { SteeringRangeDegrees = Math.Clamp(totalDegrees, 60.0f, 1080.0f) };
    }
}

internal sealed class LogicalGamepadState
{
    public float LX;
    public float LY;
    public float RX;
    public float RY;
    public float LT;
    public float RT;
    public bool LB;
    public bool RB;
    public bool A;
    public bool B;
    public bool X;
    public bool Y;
    public bool L3;
    public bool R3;
    public bool DpadUp;
    public bool DpadDown;
    public bool DpadLeft;
    public bool DpadRight;
    public bool View;
    public bool Menu;
    public bool Guide;

    public LogicalGamepadState Clone() => (LogicalGamepadState)MemberwiseClone();

    public static LogicalGamepadState Neutral() => new();
}

[Flags]
internal enum MotionValidity : uint
{
    None = 0,
    LeftActive = 1u << 0,
    LeftOrientationValid = 1u << 1,
    LeftOrientationTracked = 1u << 2,
    LeftPositionValid = 1u << 3,
    LeftPositionTracked = 1u << 4,
    LeftAngularValid = 1u << 5,
    RightActive = 1u << 8,
    RightOrientationValid = 1u << 9,
    RightOrientationTracked = 1u << 10,
    RightPositionValid = 1u << 11,
    RightPositionTracked = 1u << 12,
    RightAngularValid = 1u << 13,
    MotionQueried = 1u << 16,
}

internal readonly record struct ControllerMotion(
    bool Active,
    bool OrientationValid,
    bool OrientationTracked,
    bool PositionValid,
    bool PositionTracked,
    bool AngularValid,
    Quaternion Orientation,
    Vector3 Position,
    Vector3 AngularVelocityLocal);

internal readonly record struct MotionFrame(
    ulong QuestTimestampNs,
    ControllerMotion Left,
    ControllerMotion Right)
{
    public bool AnyMotion => Left.Active || Right.Active;
}

internal readonly record struct ProcessedMotion(
    bool GyroValid,
    Vector3 GyroRadiansPerSecond,
    bool SteeringValid,
    float SteeringNormalized,
    string SteeringState);

internal static class HostControlBits
{
    // QFB1 reserved bits. These are requests from the host; the Quest side never
    // enables pose queries unless at least one motion feature asks for them.
    public const ushort MotionNone = 0;
    public const ushort MotionRightAngularRate = 1;
    public const ushort MotionRightTracked = 2;
    public const ushort MotionBothTracked = 3;

    public static ushort For(RuntimeSettingsSnapshot settings)
    {
        if (settings.Steering != SteeringMode.Off)
            return MotionBothTracked;
        return settings.GyroSource switch
        {
            GyroSourceMode.AngularRate => MotionRightAngularRate,
            GyroSourceMode.CameraAssisted => MotionRightTracked,
            _ => MotionNone
        };
    }
}
