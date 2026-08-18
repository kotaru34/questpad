using System.Diagnostics;
using System.Numerics;
using QuestPad.Host;

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Near(float actual, float expected, string message, float eps = 1e-4f)
{
    if (MathF.Abs(actual - expected) > eps)
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

static RuntimeSettingsSnapshot Settings() => new(
    OutputMode.DualShock4,
    GyroSourceMode.AngularRate,
    SmoothingLevel.Off,
    false,
    QuestViewMode.Black,
    SteeringMode.Off,
    SmoothingLevel.Off,
    240.0f,
    false,
    false);

static ControllerMotion Right(Quaternion orientation, Vector3 angular) => new(
    Active: true,
    OrientationValid: true,
    OrientationTracked: true,
    PositionValid: false,
    PositionTracked: false,
    AngularValid: true,
    Orientation: orientation,
    Position: Vector3.Zero,
    AngularVelocityLocal: angular);

var processor = new MotionProcessor();
Vector3 angular = new(0.125f, -0.25f, 0.5f);
var frame = new MotionFrame(
    1_000_000_000UL,
    default,
    Right(Quaternion.Identity, angular));

ProcessedMotion motion = processor.Process(frame, Settings());
Check(motion.GyroValid, "angular-rate gyro must remain valid");
Near(motion.GyroRadiansPerSecond.X, angular.X, "gyro X must be unchanged");
Near(motion.GyroRadiansPerSecond.Y, angular.Y, "gyro Y must be unchanged");
Near(motion.GyroRadiansPerSecond.Z, angular.Z, "gyro Z must be unchanged");
Check(motion.AccelerationValid, "identity orientation must produce valid gravity acceleration");
Near(motion.AccelerationG.X, 0.0f, "identity accel X");
Near(motion.AccelerationG.Y, 1.0f, "identity accel Y");
Near(motion.AccelerationG.Z, 0.0f, "identity accel Z");
Near(motion.AccelerationG.Length(), 1.0f, "gravity magnitude");

Quaternion rotated = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2.0f);
var tiltedFrame = new MotionFrame(
    2_000_000_000UL,
    default,
    Right(rotated, angular));
ProcessedMotion tilted = processor.Process(tiltedFrame, Settings());
Check(tilted.AccelerationValid, "tilted orientation must keep acceleration valid");
Near(tilted.AccelerationG.X, 1.0f, "tilted accel X", 2e-4f);
Near(tilted.AccelerationG.Y, 0.0f, "tilted accel Y", 2e-4f);
Near(tilted.AccelerationG.Z, 0.0f, "tilted accel Z", 2e-4f);
Near(tilted.GyroRadiansPerSecond.X, angular.X, "tilted gyro X must remain unchanged");
Near(tilted.GyroRadiansPerSecond.Y, angular.Y, "tilted gyro Y must remain unchanged");
Near(tilted.GyroRadiansPerSecond.Z, angular.Z, "tilted gyro Z must remain unchanged");

GyroStickCompatibility.SetSensitivity(1.0f);

// XInput compatibility path: with gyro invalid the physical right stick is untouched.
ProcessedMotion noGyro = motion with { GyroValid = false };
(float noGyroX, float noGyroY) = GyroStickCompatibility.Apply(0.25f, -0.4f, noGyro);
Near(noGyroX, 0.25f, "gyro-stick disabled X");
Near(noGyroY, -0.4f, "gyro-stick disabled Y");

// Candidate axis convention for hardware validation: controller-local +Y rotation
// drives right-stick X negative. 180 deg/s is the 1.0x full-stick reference.
ProcessedMotion yawMotion = motion with
{
    GyroRadiansPerSecond = new Vector3(0.0f, MathF.PI, 0.0f)
};
(float yawX, float yawY) = GyroStickCompatibility.Apply(0.0f, 0.0f, yawMotion);
Near(yawX, -1.0f, "gyro-stick +Y yaw at 180 deg/s");
Near(yawY, 0.0f, "gyro-stick yaw must not alter Y");

// Controller-local +X rotation drives right-stick Y positive. 90 deg/s is half scale.
ProcessedMotion pitchMotion = motion with
{
    GyroRadiansPerSecond = new Vector3(MathF.PI / 2.0f, 0.0f, 0.0f)
};
(float pitchX, float pitchY) = GyroStickCompatibility.Apply(0.0f, 0.0f, pitchMotion);
Near(pitchX, 0.0f, "gyro-stick pitch must not alter X");
Near(pitchY, 0.5f, "gyro-stick +X pitch at 90 deg/s");

// Sensitivity is a pure multiplier around the established 1.0x mapping.
GyroStickCompatibility.SetSensitivity(2.0f);
(float sensitiveX, float sensitiveY) = GyroStickCompatibility.Apply(0.0f, 0.0f, pitchMotion);
Near(sensitiveX, 0.0f, "2x sensitivity must not cross-couple X");
Near(sensitiveY, 1.0f, "2x sensitivity turns 90 deg/s into full-stick pitch");

GyroStickCompatibility.SetSensitivity(0.5f);
(float halfYawX, float halfYawY) = GyroStickCompatibility.Apply(0.0f, 0.0f, yawMotion);
Near(halfYawX, -0.5f, "0.5x sensitivity halves yaw output");
Near(halfYawY, 0.0f, "0.5x sensitivity must not cross-couple Y");

Check(Math.Abs(GyroStickCompatibility.NormalizeSensitivity(999.0f) - GyroStickCompatibility.MaxSensitivity) < 1e-4f,
    "gyro-stick sensitivity upper bound");
Check(Math.Abs(GyroStickCompatibility.NormalizeSensitivity(0.001f) - GyroStickCompatibility.MinSensitivity) < 1e-4f,
    "gyro-stick sensitivity lower bound");
GyroStickCompatibility.SetSensitivity(1.0f);

// Gyro is additive to the physical stick and clamps rather than wrapping/saturating
// the raw conversion later in the ViGEm backend.
ProcessedMotion additiveMotion = motion with
{
    GyroRadiansPerSecond = new Vector3(0.0f, -MathF.PI / 2.0f, 0.0f)
};
(float additiveX, float additiveY) = GyroStickCompatibility.Apply(0.75f, -0.2f, additiveMotion);
Near(additiveX, 1.0f, "gyro-stick additive X clamp");
Near(additiveY, -0.2f, "gyro-stick additive path preserves physical Y");

// A real DS4 sensor clock advances at 187,500 units/s (5.33 us/unit). The old
// QuestPad implementation advanced by one per host report, making sensor time about
// 2,600x too slow at 72 Hz. Verify the new monotonic conversion including 16-bit wrap.
long t0Ticks = Stopwatch.Frequency * 10L;
long t1Ticks = t0Ticks + Stopwatch.Frequency;
ushort t0 = Ds4SensorClock.FromStopwatchTicks(t0Ticks);
ushort t1 = Ds4SensorClock.FromStopwatchTicks(t1Ticks);
ushort wrappedDelta = unchecked((ushort)(t1 - t0));
Check(wrappedDelta == unchecked((ushort)187_500),
    $"DS4 sensor clock must advance 187500 units/s modulo 16-bit; got {wrappedDelta}");

Console.WriteLine("Motion smoke tests passed: gyro unchanged, gravity accelerometer valid, adjustable XInput gyro-stick mapping valid, DS4 sensor clock valid.");
