using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

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

internal enum QuestViewMode
{
    Black,
    Passthrough
}

internal enum SteeringMode
{
    Off,
    Mounted,
    // Kept internally for compatibility with the v0.3 estimator experiments.
    // The public UI intentionally exposes only Mounted going forward.
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
    bool GyroStickLock,
    QuestViewMode QuestView,
    SteeringMode Steering,
    SmoothingLevel SteeringSmoothing,
    float SteeringRangeDegrees,
    bool SteeringGripClutch,
    bool SteeringInverted);

internal sealed class RuntimeSettings
{
    private const string SettingsFileName = "QuestPad.settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object gate = new();
    private readonly string settingsPath;
    private RuntimeSettingsSnapshot value = Defaults;

    private static RuntimeSettingsSnapshot Defaults => new(
        OutputMode.Xbox360,
        GyroSourceMode.Off,
        SmoothingLevel.Off,
        false,
        QuestViewMode.Black,
        SteeringMode.Off,
        SmoothingLevel.Light,
        240.0f,
        false,
        false);

    public RuntimeSettings()
    {
        string baseDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? AppContext.BaseDirectory;
        settingsPath = Path.Combine(baseDirectory, SettingsFileName);

        if (File.Exists(settingsPath))
        {
            try
            {
                RuntimeSettingsSnapshot loaded = JsonSerializer.Deserialize<RuntimeSettingsSnapshot>(
                    File.ReadAllText(settingsPath), JsonOptions);
                value = Normalize(loaded);
            }
            catch (Exception ex)
            {
                value = Defaults;
                Console.Error.WriteLine($"Could not read {SettingsFileName}; using defaults: {ex.Message}");
            }
        }
        else
        {
            SaveLocked();
        }
    }

    public RuntimeSettingsSnapshot Snapshot()
    {
        lock (gate) return value;
    }

    public void SetOutput(OutputMode output)
    {
        lock (gate)
        {
            // Output selection is authoritative and deliberately does not destroy an
            // already-enabled motion source. SetGyroSource still chooses DS4 by default,
            // preserving the established UX. If the user explicitly switches to Xbox
            // afterwards, the XInput backend keeps receiving gyro and converts it to an
            // additive right-stick compatibility signal for games with broken DS4 paths.
            value = value with { Output = output };
            SaveLocked();
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
            SaveLocked();
        }
    }

    public void SetGyroSmoothing(SmoothingLevel smoothing)
    {
        lock (gate)
        {
            value = value with { GyroSmoothing = smoothing };
            SaveLocked();
        }
    }

    public void SetGyroStickLock(bool enabled)
    {
        lock (gate)
        {
            value = value with { GyroStickLock = enabled };
            SaveLocked();
        }
    }

    public void SetQuestView(QuestViewMode view)
    {
        lock (gate)
        {
            value = value with { QuestView = view };
            SaveLocked();
        }
    }

    public void SetSteering(SteeringMode steering)
    {
        lock (gate)
        {
            value = value with { Steering = steering };
            SaveLocked();
        }
    }

    public void SetSteeringSmoothing(SmoothingLevel smoothing)
    {
        lock (gate)
        {
            value = value with { SteeringSmoothing = smoothing };
            SaveLocked();
        }
    }

    public void SetSteeringRange(float totalDegrees)
    {
        lock (gate)
        {
            value = value with { SteeringRangeDegrees = Math.Clamp(totalDegrees, 60.0f, 1080.0f) };
            SaveLocked();
        }
    }

    public void SetSteeringGripClutch(bool enabled)
    {
        lock (gate)
        {
            value = value with { SteeringGripClutch = enabled };
            SaveLocked();
        }
    }

    public void SetSteeringInverted(bool enabled)
    {
        lock (gate)
        {
            value = value with { SteeringInverted = enabled };
            SaveLocked();
        }
    }

    private static RuntimeSettingsSnapshot Normalize(RuntimeSettingsSnapshot loaded)
    {
        RuntimeSettingsSnapshot defaults = Defaults;
        OutputMode output = Enum.IsDefined(loaded.Output) ? loaded.Output : defaults.Output;
        GyroSourceMode gyro = Enum.IsDefined(loaded.GyroSource) ? loaded.GyroSource : defaults.GyroSource;
        SmoothingLevel gyroSmoothing = Enum.IsDefined(loaded.GyroSmoothing) ? loaded.GyroSmoothing : defaults.GyroSmoothing;
        QuestViewMode questView = Enum.IsDefined(loaded.QuestView) ? loaded.QuestView : defaults.QuestView;
        SteeringMode steering = Enum.IsDefined(loaded.Steering) ? loaded.Steering : defaults.Steering;
        SmoothingLevel steeringSmoothing = Enum.IsDefined(loaded.SteeringSmoothing) ? loaded.SteeringSmoothing : defaults.SteeringSmoothing;
        float steeringRange = float.IsFinite(loaded.SteeringRangeDegrees)
            ? Math.Clamp(loaded.SteeringRangeDegrees, 60.0f, 1080.0f)
            : defaults.SteeringRangeDegrees;

        // Keep a persisted explicit Xbox+gyro combination intact. This state can only
        // be reached by selecting Xbox after enabling gyro, so existing users who just
        // enable a gyro source still get the native DS4 backend automatically.
        return new RuntimeSettingsSnapshot(
            output,
            gyro,
            gyroSmoothing,
            loaded.GyroStickLock,
            questView,
            steering,
            steeringSmoothing,
            steeringRange,
            loaded.SteeringGripClutch,
            loaded.SteeringInverted);
    }

    private void SaveLocked()
    {
        string tempPath = settingsPath + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(value, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, settingsPath, true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            Console.Error.WriteLine($"Could not save {SettingsFileName}: {ex.Message}");
        }
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
    public bool TouchpadClick;

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
    bool AccelerationValid,
    Vector3 AccelerationG,
    bool SteeringValid,
    float SteeringNormalized,
    string SteeringState);

internal static class HostControlBits
{
    // QFB1 control word. The low two bits retain protocol-v2 motion request values.
    // Higher bits are orthogonal feature requests so adding passthrough does not alter
    // packet size or the established motion transport.
    public const ushort MotionMask = 0x0003;
    public const ushort MotionNone = 0;
    public const ushort MotionRightAngularRate = 1;
    public const ushort MotionRightTracked = 2;
    public const ushort MotionBothTracked = 3;
    public const ushort QuestPassthrough = 1 << 8;

    public static ushort For(RuntimeSettingsSnapshot settings)
    {
        ushort control = settings.Steering != SteeringMode.Off
            ? MotionBothTracked
            : settings.GyroSource switch
            {
                GyroSourceMode.AngularRate => MotionRightAngularRate,
                GyroSourceMode.CameraAssisted => MotionRightTracked,
                _ => MotionNone
            };

        if (settings.QuestView == QuestViewMode.Passthrough)
            control |= QuestPassthrough;

        return control;
    }
}
