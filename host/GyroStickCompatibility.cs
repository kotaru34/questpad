namespace QuestPad.Host;

internal static class GyroStickCompatibility
{
    private const float RadToDeg = 180.0f / MathF.PI;
    internal const float FullStickDegreesPerSecond = 180.0f;
    internal const float MinSensitivity = 0.10f;
    internal const float MaxSensitivity = 5.00f;
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

        float scale = Sensitivity;
        float yaw = -motion.GyroRadiansPerSecond.Y * RadToDeg / FullStickDegreesPerSecond * scale;
        float pitch = motion.GyroRadiansPerSecond.X * RadToDeg / FullStickDegreesPerSecond * scale;
        if (!float.IsFinite(yaw) || !float.IsFinite(pitch))
            return (x, y);

        return (
            Math.Clamp(x + yaw, -1.0f, 1.0f),
            Math.Clamp(y + pitch, -1.0f, 1.0f));
    }
}
