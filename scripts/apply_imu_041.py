from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: str, old: str, new: str) -> None:
    p = ROOT / path
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


def write_text(path: str, content: str) -> None:
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(content, encoding="utf-8")


replace_once(
    "host/MotionProcessing.cs",
    """        Vector3 gyro = Vector3.Zero;
        bool gyroValid = false;

        if (settings.GyroSource == GyroSourceMode.AngularRate)
""",
    """        // DS4 compatibility accelerometer: OpenXR exposes controller orientation
        // and angular velocity but no raw Touch Plus accelerometer. A stationary
        // accelerometer measures specific force opposite gravity, so rotate LOCAL +Y
        // (1 g) into controller-local axes. This gravity-only path deliberately does
        // not finite-difference linear velocity, add another pose query, or share any
        // filter state with the established gyro path.
        Vector3 accelerationG = Vector3.Zero;
        bool accelerationValid = false;
        if (frame.Right.Active && frame.Right.OrientationValid)
        {
            Quaternion rawOrientation = frame.Right.Orientation;
            float orientationNorm = rawOrientation.LengthSquared();
            if (orientationNorm > 1e-10f && float.IsFinite(orientationNorm))
            {
                Quaternion q = Quaternion.Normalize(rawOrientation);
                accelerationG = Vector3.Transform(Vector3.UnitY, Quaternion.Conjugate(q));
                accelerationValid = IsFinite(accelerationG);
            }
        }

        Vector3 gyro = Vector3.Zero;
        bool gyroValid = false;

        if (settings.GyroSource == GyroSourceMode.AngularRate)
""",
)

replace_once(
    "host/MotionProcessing.cs",
    """        return new ProcessedMotion(gyroValid, gyro, steeringValid, steeringValue, steeringState);
""",
    """        return new ProcessedMotion(
            gyroValid,
            gyro,
            accelerationValid,
            accelerationG,
            steeringValid,
            steeringValue,
            steeringState);
""",
)

replace_once(
    "host/OutputBackends.cs",
    """    private const float GyroCountsPerDegreeSecond = 16.0f;
""",
    """    private const float GyroCountsPerDegreeSecond = 16.0f;
    private const float AccelCountsPerG = 8192.0f;
""",
)

replace_once(
    "host/OutputBackends.cs",
    """        // Acceleration remains zero on purpose: QuestPad's native gyro feature only
        // promises rotation-rate data and does not fabricate an accelerometer.
        pad.SubmitRawReport(report);
""",
    """        if (motion.AccelerationValid)
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
""",
)

replace_once(
    "host/OutputBackends.cs",
    """    private static short GyroRaw(float degreesPerSecond)
    {
        double raw = degreesPerSecond * GyroCountsPerDegreeSecond;
        return (short)Math.Clamp(Math.Round(raw), short.MinValue, short.MaxValue);
    }

""",
    """    private static short GyroRaw(float degreesPerSecond)
    {
        double raw = degreesPerSecond * GyroCountsPerDegreeSecond;
        return (short)Math.Clamp(Math.Round(raw), short.MinValue, short.MaxValue);
    }

    private static short AccelRaw(float g)
    {
        double raw = g * AccelCountsPerG;
        return (short)Math.Clamp(Math.Round(raw), short.MinValue, short.MaxValue);
    }

""",
)

for project in ("host/QuestPad.Host.csproj", "host/QuestPad.Host.Console.csproj"):
    replace_once(project, "<Version>0.4.0-test</Version>", "<Version>0.4.1-test</Version>")
    replace_once(project, "<FileVersion>0.4.0.0</FileVersion>", "<FileVersion>0.4.1.0</FileVersion>")

replace_once(
    "quest/build.gradle",
    "        versionCode 9\n        versionName '0.3.7-test'\n",
    "        versionCode 10\n        versionName '0.4.1-test'\n",
)

write_text(
    "tests/MotionSmoke/MotionSmoke.csproj",
    '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../../host/InputModels.cs" Link="InputModels.cs" />
    <Compile Include="../../host/MotionProcessing.cs" Link="MotionProcessing.cs" />
    <Compile Include="Program.cs" />
  </ItemGroup>
</Project>
''',
)

write_text(
    "tests/MotionSmoke/Program.cs",
    '''using System.Numerics;
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

Console.WriteLine("Motion smoke tests passed: gyro unchanged, gravity accelerometer valid.");
''',
)

replace_once(
    "README.md",
    """Only the **right Touch Plus controller** is used for aiming motion.

### Recommended: Angular-rate only
""",
    """Only the **right Touch Plus controller** is used for aiming motion.

For DS4 IMU compatibility, QuestPad also synthesizes a **gravity-only accelerometer** from the right controller orientation and writes it to the standard DS4 accelerometer fields at 8192 counts/g. This is not raw Touch Plus acceleration and deliberately omits translational acceleration; it exists to provide the stationary ~1 g reference expected by DS4 sensor consumers such as Steam calibration. The orientation comes from the same OpenXR locate already used by Angular-rate mode, so this adds no second tracking query and does not enter the gyro filtering path.

### Recommended: Angular-rate only
""",
)

replace_once(
    "BUILD_STATUS.md",
    """### v0.4.0 mapping candidate
""",
    """### v0.4.1 DS4 IMU compatibility candidate

Build-gated and awaiting Steam hardware validation:

- Angular-rate mode now publishes controller orientation obtained from the **same** inverse `xrLocateSpace` result already used for controller-local angular velocity; no second pose query or positional tracking is added.
- Windows rotates LOCAL +1 g into right-controller-local coordinates and writes the result to the standard DS4 accelerometer fields at 8192 counts/g.
- The synthesized accelerometer is gravity-only and intentionally does not finite-difference linear velocity, avoiding an additional noise source.
- Gyro calculation, scale and adaptive smoothing are unchanged and remain independent from the accelerometer path.
- A permanent motion smoke test verifies both gravity orientation behavior and unchanged angular-rate vector output with smoothing Off.
- Target hardware check: Steam Gyro Calibration should animate its accelerometer/stationary indicator and complete the drift/bias stage instead of stalling.

### v0.4.0 mapping candidate
""",
)

# Clarify protocol semantics without changing packet size/version.
replace_once(
    "PROTOCOL.md",
    """- `1` — right-controller angular-rate stream only;
""",
    """- `1` — right-controller angular-rate stream; the same locate may also populate right orientation validity/quaternion for gravity-only DS4 accelerometer compatibility, while position remains unused;
""",
)

# Remove all temporary patch machinery from the resulting commit.
for rel in (
    ".github/workflows/imu-0.4.1-once.yml",
    "scripts/imu-trigger.txt",
    "scripts/apply_imu_041.py",
):
    p = ROOT / rel
    if p.exists():
        p.unlink()

print("0.4.1 IMU patch applied successfully")
