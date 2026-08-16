using System.Numerics;

namespace QuestPad.Host;

internal sealed class MotionProcessor
{
    private readonly OneEuroVector gyroFilter = new();
    private readonly SteeringEstimator steering = new();
    private Quaternion previousCameraOrientation = Quaternion.Identity;
    private ulong previousCameraTimestamp;
    private bool havePreviousCamera;

    public void Reset()
    {
        gyroFilter.Reset();
        steering.ResetTracking();
        previousCameraOrientation = Quaternion.Identity;
        previousCameraTimestamp = 0;
        havePreviousCamera = false;
    }

    public void CalibrateSteering(MotionFrame frame) => steering.Calibrate(frame);

    public ProcessedMotion Process(MotionFrame frame, RuntimeSettingsSnapshot settings)
    {
        Vector3 gyro = Vector3.Zero;
        bool gyroValid = false;

        if (settings.GyroSource == GyroSourceMode.AngularRate)
        {
            // Windows consumes only the controller-local OpenXR angular-rate vector.
            // This is still Horizon/OpenXR data rather than raw MEMS access.
            if (frame.Right.Active && frame.Right.AngularValid)
            {
                gyro = frame.Right.AngularVelocityLocal;
                gyroValid = IsFinite(gyro);
            }
            havePreviousCamera = false;
        }
        else if (settings.GyroSource == GyroSourceMode.CameraAssisted)
        {
            // Deliberately require optical positional tracking for the A/B experiment.
            // Rate is derived from successive tracked orientations instead of using
            // XrSpaceVelocity, making this an explicitly camera-assisted reference.
            if (frame.Right.Active && frame.Right.OrientationTracked && frame.Right.PositionTracked)
            {
                Quaternion q = NormalizeSafe(frame.Right.Orientation);
                if (havePreviousCamera && frame.QuestTimestampNs > previousCameraTimestamp)
                {
                    double dt = (frame.QuestTimestampNs - previousCameraTimestamp) / 1_000_000_000.0;
                    if (dt is > 0.001 and < 0.100)
                    {
                        Vector3 worldRate = QuaternionDeltaRate(previousCameraOrientation, q, (float)dt);
                        gyro = Vector3.Transform(worldRate, Quaternion.Conjugate(q));
                        gyroValid = IsFinite(gyro);
                    }
                }
                previousCameraOrientation = q;
                previousCameraTimestamp = frame.QuestTimestampNs;
                havePreviousCamera = true;
            }
            else
            {
                havePreviousCamera = false;
            }
        }
        else
        {
            havePreviousCamera = false;
        }

        if (gyroValid)
            gyro = gyroFilter.Filter(gyro, frame.QuestTimestampNs, settings.GyroSmoothing);
        else
            gyroFilter.Reset();

        (bool steeringValid, float steeringValue, string steeringState) = settings.Steering == SteeringMode.Off
            ? (false, 0.0f, "off")
            : steering.Update(frame, settings);

        return new ProcessedMotion(gyroValid, gyro, steeringValid, steeringValue, steeringState);
    }

    private static Vector3 QuaternionDeltaRate(Quaternion previous, Quaternion current, float dt)
    {
        Quaternion delta = NormalizeSafe(current * Quaternion.Conjugate(previous));
        if (delta.W < 0)
            delta = Negate(delta);

        float sinHalf = new Vector3(delta.X, delta.Y, delta.Z).Length();
        if (sinHalf < 1e-7f) return Vector3.Zero;
        float angle = 2.0f * MathF.Atan2(sinHalf, Math.Clamp(delta.W, -1.0f, 1.0f));
        Vector3 axis = new(delta.X / sinHalf, delta.Y / sinHalf, delta.Z / sinHalf);
        return axis * (angle / dt);
    }

    internal static Quaternion NormalizeSafe(Quaternion q)
    {
        float n = q.LengthSquared();
        return n > 1e-10f && float.IsFinite(n) ? Quaternion.Normalize(q) : Quaternion.Identity;
    }

    internal static Quaternion Negate(Quaternion q) => new(-q.X, -q.Y, -q.Z, -q.W);

    internal static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

internal sealed class SteeringEstimator
{
    private bool calibrated;
    private Quaternion baseLeft;
    private Quaternion baseRight;
    private Quaternion baseRelative;
    private Quaternion previousCommonDelta = Quaternion.Identity;
    private Vector3 previousSpan;
    private bool havePreviousCommon;
    private bool havePreviousSpan;
    private Vector3 wheelAxis;
    private bool haveAxis;
    private float axisEvidence;
    private float angleRadians;
    private ulong lastGoodNs;
    private readonly OneEuroScalar steeringFilter = new();

    public void Calibrate(MotionFrame frame)
    {
        if (!frame.Left.OrientationValid || !frame.Right.OrientationValid)
            return;

        baseLeft = MotionProcessor.NormalizeSafe(frame.Left.Orientation);
        baseRight = MotionProcessor.NormalizeSafe(frame.Right.Orientation);
        baseRelative = MotionProcessor.NormalizeSafe(Quaternion.Conjugate(baseLeft) * baseRight);

        // Each hand is allowed to have an arbitrary mounting orientation. The wheel
        // rigid-body rotation is measured as qNow * inverse(qAtCalibration), so two
        // differently/mirrored mounted Touch controllers still produce the same delta.
        previousCommonDelta = Quaternion.Identity;
        havePreviousCommon = true;

        havePreviousSpan = frame.Left.PositionTracked && frame.Right.PositionTracked;
        if (havePreviousSpan)
            previousSpan = frame.Right.Position - frame.Left.Position;

        wheelAxis = Vector3.Zero;
        haveAxis = false;
        axisEvidence = 0;
        angleRadians = 0;
        lastGoodNs = frame.QuestTimestampNs;
        calibrated = true;
        steeringFilter.Reset();
    }

    public void ResetTracking()
    {
        havePreviousCommon = false;
        havePreviousSpan = false;
        lastGoodNs = 0;
        steeringFilter.Reset();
    }

    public (bool valid, float value, string state) Update(MotionFrame frame, RuntimeSettingsSnapshot settings)
    {
        if (!calibrated)
            return (false, 0, "needs calibration");

        LearnAxis(frame);
        if (!haveAxis)
            return (false, 0, "learning wheel axis — turn left/right");

        bool orientationGood = frame.Left.Active && frame.Right.Active &&
            frame.Left.OrientationTracked && frame.Right.OrientationTracked;
        bool opticalGood = frame.Left.PositionTracked && frame.Right.PositionTracked;

        Quaternion commonDelta = previousCommonDelta;
        float mountError = 0;
        string orientationHealth = "orientation unavailable";
        if (orientationGood)
            commonDelta = FuseRigidDelta(frame, out mountError, out orientationHealth);

        bool useOptical = settings.Steering switch
        {
            SteeringMode.FreeAir => opticalGood,
            SteeringMode.Hybrid => opticalGood,
            _ => false
        };
        bool canUseOrientation = settings.Steering != SteeringMode.FreeAir && orientationGood;

        bool updated = false;
        string source = "holding last value";

        if (useOptical)
        {
            // Position is consumed ONLY with PT=1 for both controllers. PV=1/PT=0
            // values seen in Horizon tests are deliberately ignored as stale/predicted.
            Vector3 span = frame.Right.Position - frame.Left.Position;
            if (span.LengthSquared() > 0.01f)
            {
                if (havePreviousSpan)
                {
                    float step = SignedProjectedAngle(previousSpan, span, wheelAxis);
                    if (MathF.Abs(step) < Degrees(45))
                    {
                        angleRadians += step;
                        updated = true;
                        source = settings.Steering == SteeringMode.Hybrid ? "hybrid optical" : "free-air optical";
                    }
                    else
                    {
                        source = "rejected optical steering spike";
                    }
                }
                previousSpan = span;
                havePreviousSpan = true;
            }

            // Keep the orientation baseline synchronized while optical steering is in
            // charge. A later Hybrid fallback can then take over without a jump.
            if (orientationGood)
            {
                previousCommonDelta = commonDelta;
                havePreviousCommon = true;
            }
        }
        else
        {
            havePreviousSpan = false;
        }

        if (!updated && canUseOrientation)
        {
            if (havePreviousCommon)
            {
                Quaternion stepQ = MotionProcessor.NormalizeSafe(commonDelta * Quaternion.Conjugate(previousCommonDelta));
                float step = TwistAngle(stepQ, wheelAxis);

                // Large relative-mount disagreement means one controller probably
                // slipped or one tracking sample jumped. FuseRigidDelta already trusts
                // the hand closest to the predicted wheel state; clamp remaining
                // implausible one-frame wheel rotation as a second safety barrier.
                float maxStep = mountError > Degrees(25) ? Degrees(10) : Degrees(45);
                if (MathF.Abs(step) <= maxStep)
                {
                    angleRadians += step;
                    updated = true;
                    source = mountError > Degrees(8)
                        ? $"mounted orientation — {orientationHealth}, mismatch {RadiansToDegrees(mountError):F1}°"
                        : "mounted orientation";
                }
                else
                {
                    source = "rejected tracking/mounting spike";
                }
            }

            previousCommonDelta = commonDelta;
            havePreviousCommon = true;
        }
        else if (!orientationGood && !useOptical)
        {
            havePreviousCommon = false;
        }

        if (updated)
            lastGoodNs = frame.QuestTimestampNs;

        float halfRange = Math.Max(1.0f, settings.SteeringRangeDegrees * 0.5f);
        float raw = Math.Clamp(RadiansToDegrees(angleRadians) / halfRange, -1.0f, 1.0f);
        float filtered = steeringFilter.Filter(raw, frame.QuestTimestampNs, settings.SteeringSmoothing);

        // Brief dropouts freeze the last wheel position. Do not auto-center a car just
        // because Horizon idled/lost a controller. After 500 ms the host falls back to
        // the physical left stick by treating steering as invalid.
        bool recent = lastGoodNs != 0 && frame.QuestTimestampNs >= lastGoodNs &&
            frame.QuestTimestampNs - lastGoodNs <= 500_000_000UL;
        return (updated || recent, filtered, source);
    }

    private Quaternion FuseRigidDelta(MotionFrame frame, out float mountError, out string health)
    {
        Quaternion left = MotionProcessor.NormalizeSafe(frame.Left.Orientation);
        Quaternion right = MotionProcessor.NormalizeSafe(frame.Right.Orientation);

        Quaternion leftDelta = MotionProcessor.NormalizeSafe(left * Quaternion.Conjugate(baseLeft));
        Quaternion rightDelta = MotionProcessor.NormalizeSafe(right * Quaternion.Conjugate(baseRight));
        AlignHemisphere(leftDelta, ref rightDelta);

        Quaternion relative = MotionProcessor.NormalizeSafe(Quaternion.Conjugate(left) * right);
        Quaternion relativeError = MotionProcessor.NormalizeSafe(relative * Quaternion.Conjugate(baseRelative));
        mountError = QuaternionAngle(relativeError);

        float pairDisagreement = QuaternionDistance(leftDelta, rightDelta);
        if (pairDisagreement <= Degrees(6) || !havePreviousCommon)
        {
            health = "both controllers fused";
            return Average(leftDelta, rightDelta);
        }

        // If one controller shifts in a loose mount or suffers a tracking jump, choose
        // the observation nearest the already-established wheel trajectory rather than
        // averaging the bad sample halfway into the steering output.
        float leftInnovation = QuaternionDistance(previousCommonDelta, leftDelta);
        float rightInnovation = QuaternionDistance(previousCommonDelta, rightDelta);
        if (leftInnovation + Degrees(1) < rightInnovation)
        {
            health = "right outlier suppressed";
            return leftDelta;
        }
        if (rightInnovation + Degrees(1) < leftInnovation)
        {
            health = "left outlier suppressed";
            return rightDelta;
        }

        // Ambiguous disagreement (for example gradual physical creep) is safer to
        // average than to invent which mount moved. The mismatch is surfaced in tray
        // status so the user can re-center if it keeps growing.
        health = "mount disagreement averaged";
        return Average(leftDelta, rightDelta);
    }

    private void LearnAxis(MotionFrame frame)
    {
        if (!frame.Left.AngularValid || !frame.Right.AngularValid ||
            !frame.Left.OrientationValid || !frame.Right.OrientationValid)
            return;

        Quaternion lq = MotionProcessor.NormalizeSafe(frame.Left.Orientation);
        Quaternion rq = MotionProcessor.NormalizeSafe(frame.Right.Orientation);
        Vector3 lw = Vector3.Transform(frame.Left.AngularVelocityLocal, lq);
        Vector3 rw = Vector3.Transform(frame.Right.AngularVelocityLocal, rq);
        Vector3 candidate = (lw + rw) * 0.5f;
        float speed = candidate.Length();
        if (speed < 0.20f) return;

        Vector3 n = candidate / speed;
        if (!haveAxis && axisEvidence == 0)
            wheelAxis = n;
        else
        {
            if (Vector3.Dot(n, wheelAxis) < 0) n = -n;
            wheelAxis = Vector3.Normalize(Vector3.Lerp(wheelAxis, n, 0.12f));
        }

        axisEvidence += Math.Min(speed / 72.0f, 0.15f);
        if (axisEvidence >= 0.30f)
            haveAxis = true;
    }

    private static Quaternion Average(Quaternion a, Quaternion b)
    {
        AlignHemisphere(a, ref b);
        return MotionProcessor.NormalizeSafe(new Quaternion(
            a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W));
    }

    private static void AlignHemisphere(Quaternion reference, ref Quaternion q)
    {
        if (Quaternion.Dot(reference, q) < 0)
            q = MotionProcessor.Negate(q);
    }

    private static float QuaternionDistance(Quaternion a, Quaternion b)
    {
        a = MotionProcessor.NormalizeSafe(a);
        b = MotionProcessor.NormalizeSafe(b);
        float dot = Math.Clamp(MathF.Abs(Quaternion.Dot(a, b)), 0.0f, 1.0f);
        return 2.0f * MathF.Acos(dot);
    }

    private static float TwistAngle(Quaternion q, Vector3 axis)
    {
        q = MotionProcessor.NormalizeSafe(q);
        axis = Vector3.Normalize(axis);
        float projected = q.X * axis.X + q.Y * axis.Y + q.Z * axis.Z;
        return WrapPi(2.0f * MathF.Atan2(projected, q.W));
    }

    private static float SignedProjectedAngle(Vector3 from, Vector3 to, Vector3 axis)
    {
        axis = Vector3.Normalize(axis);
        Vector3 a = from - axis * Vector3.Dot(from, axis);
        Vector3 b = to - axis * Vector3.Dot(to, axis);
        if (a.LengthSquared() < 1e-8f || b.LengthSquared() < 1e-8f) return 0;
        a = Vector3.Normalize(a);
        b = Vector3.Normalize(b);
        float sin = Vector3.Dot(axis, Vector3.Cross(a, b));
        float cos = Math.Clamp(Vector3.Dot(a, b), -1.0f, 1.0f);
        return MathF.Atan2(sin, cos);
    }

    private static float QuaternionAngle(Quaternion q)
    {
        q = MotionProcessor.NormalizeSafe(q);
        float w = Math.Clamp(MathF.Abs(q.W), 0.0f, 1.0f);
        return 2.0f * MathF.Acos(w);
    }

    private static float WrapPi(float x)
    {
        while (x > MathF.PI) x -= 2 * MathF.PI;
        while (x < -MathF.PI) x += 2 * MathF.PI;
        return x;
    }

    private static float Degrees(float v) => v * MathF.PI / 180.0f;
    private static float RadiansToDegrees(float v) => v * 180.0f / MathF.PI;
}

internal sealed class OneEuroVector
{
    private readonly OneEuroScalar x = new();
    private readonly OneEuroScalar y = new();
    private readonly OneEuroScalar z = new();

    public Vector3 Filter(Vector3 value, ulong timestampNs, SmoothingLevel level) =>
        new(x.Filter(value.X, timestampNs, level), y.Filter(value.Y, timestampNs, level), z.Filter(value.Z, timestampNs, level));

    public void Reset() { x.Reset(); y.Reset(); z.Reset(); }
}

internal sealed class OneEuroScalar
{
    private bool initialized;
    private float previousRaw;
    private float previousFiltered;
    private float previousDerivative;
    private ulong previousNs;

    public float Filter(float value, ulong timestampNs, SmoothingLevel level)
    {
        if (level == SmoothingLevel.Off)
        {
            previousRaw = previousFiltered = value;
            previousDerivative = 0;
            previousNs = timestampNs;
            initialized = true;
            return value;
        }

        if (!initialized || timestampNs <= previousNs)
        {
            previousRaw = previousFiltered = value;
            previousDerivative = 0;
            previousNs = timestampNs;
            initialized = true;
            return value;
        }

        float dt = Math.Clamp((timestampNs - previousNs) / 1_000_000_000.0f, 0.001f, 0.050f);
        (float minCutoff, float beta, float dCutoff) = level switch
        {
            SmoothingLevel.Light => (3.0f, 0.10f, 5.0f),
            SmoothingLevel.Medium => (1.8f, 0.08f, 4.0f),
            SmoothingLevel.Strong => (1.0f, 0.06f, 3.0f),
            _ => (1000.0f, 0.0f, 1000.0f)
        };

        float derivative = (value - previousRaw) / dt;
        float dAlpha = Alpha(dCutoff, dt);
        float filteredDerivative = Lerp(previousDerivative, derivative, dAlpha);
        float cutoff = minCutoff + beta * MathF.Abs(filteredDerivative);
        float filtered = Lerp(previousFiltered, value, Alpha(cutoff, dt));

        previousRaw = value;
        previousFiltered = filtered;
        previousDerivative = filteredDerivative;
        previousNs = timestampNs;
        return filtered;
    }

    public void Reset()
    {
        initialized = false;
        previousRaw = previousFiltered = previousDerivative = 0;
        previousNs = 0;
    }

    private static float Alpha(float cutoff, float dt)
    {
        float tau = 1.0f / (2.0f * MathF.PI * cutoff);
        return 1.0f / (1.0f + tau / dt);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
