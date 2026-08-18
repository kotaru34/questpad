using System.Numerics;

namespace QuestPad.Host;

internal static class GyroStickCompatibility
{
    private const float RadToDeg = 180.0f / MathF.PI;
    internal const float FullStickDegreesPerSecond = 180.0f;
    internal const float MinSensitivity = 0.10f;
    internal const float MaxSensitivity = 5.00f;

    // JibbSmart/GamepadMotionHelpers player-space gyro uses a relaxed projection
    // of the controller's Y/Z angular velocity onto gravity. 1.41 is its established
    // default and lets wrist roll contribute naturally to horizontal aim without
    // letting the projection exceed the original Y/Z angular-speed magnitude.
    private const float PlayerSpaceYawRelaxFactor = 1.41f;

    private static volatile float sensitivity = 1.0f;

    public static float Sensitivity => sensitivity;

    public static float NormalizeSensitivity(float value)
    {
        if (!float.IsFinite(value)) return 1.0f;
        return Math.Clamp(value, MinSensitivity, MaxSensitivity);
    }

    public static void SetSensitivity(float value) =>
        sensitivity = NormalizeSensitivity(value);

    public static (float X, float Y) Apply(float stickX, float stickY, ProcessedMotion motion)
    {
        float x = Math.Clamp(stickX, -1.0f, 1.0f);
        float y = Math.Clamp(stickY, -1.0f, 1.0f);
        if (!motion.GyroValid)
            return (x, y);

        Vector3 gyro = motion.GyroRadiansPerSecond;
        float yawRate = PlayerSpaceYaw(gyro, motion.AccelerationValid, motion.AccelerationG);
        float pitchRate = gyro.X;
        if (!float.IsFinite(yawRate) || !float.IsFinite(pitchRate))
            return (x, y);

        float scale = Sensitivity * RadToDeg / FullStickDegreesPerSecond;
        float yaw = yawRate * scale;
        float pitch = pitchRate * scale;

        return (
            Math.Clamp(x + yaw, -1.0f, 1.0f),
            Math.Clamp(y + pitch, -1.0f, 1.0f));
    }

    internal static float PlayerSpaceYaw(Vector3 gyro, bool gravityValid, Vector3 gravity)
    {
        // Preserve the original 0.4.3/0.4.4 mapping exactly whenever orientation /
        // synthetic gravity is unavailable. This also makes transport degradation
        // deterministic instead of changing the meaning of the gyro mid-frame.
        if (!gravityValid || !IsFinite(gravity))
            return -gyro.Y;

        float gravityLengthSq = gravity.LengthSquared();
        if (!float.IsFinite(gravityLengthSq) || gravityLengthSq < 1e-6f)
            return -gyro.Y;

        Vector3 g = gravity / MathF.Sqrt(gravityLengthSq);

        // Player-space gyro from GamepadMotionHelpers:
        //   worldYaw = -(gravY * gyroY + gravZ * gyroZ)
        //   yaw = sign(worldYaw) * min(abs(worldYaw) * relax,
        //                              length(gyroYZ))
        // At the normal identity pose g=(0,+1,0), this is exactly -gyro.Y,
        // matching QuestPad's old Xbox mapping. When the wrist rolls, gyro.Z is
        // blended in according to gravity so a circular wrist gesture remains a
        // circular camera gesture instead of collapsing toward a '+' shape.
        float worldYaw = -(g.Y * gyro.Y + g.Z * gyro.Z);
        float yzMagnitude = MathF.Sqrt(gyro.Y * gyro.Y + gyro.Z * gyro.Z);
        float magnitude = MathF.Min(MathF.Abs(worldYaw) * PlayerSpaceYawRelaxFactor, yzMagnitude);
        return worldYaw < 0.0f ? -magnitude : magnitude;
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
