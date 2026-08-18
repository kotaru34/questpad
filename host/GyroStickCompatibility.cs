namespace QuestPad.Host;

internal static class GyroStickCompatibility
{
    private const float RadToDeg = 180.0f / MathF.PI;
    internal const float FullStickDegreesPerSecond = 180.0f;

    public static (float X, float Y) Apply(float stickX, float stickY, ProcessedMotion motion)
    {
        float x = Math.Clamp(stickX, -1.0f, 1.0f);
        float y = Math.Clamp(stickY, -1.0f, 1.0f);
        if (!motion.GyroValid)
            return (x, y);

        float yaw = -motion.GyroRadiansPerSecond.Y * RadToDeg / FullStickDegreesPerSecond;
        float pitch = motion.GyroRadiansPerSecond.X * RadToDeg / FullStickDegreesPerSecond;
        if (!float.IsFinite(yaw) || !float.IsFinite(pitch))
            return (x, y);

        return (
            Math.Clamp(x + yaw, -1.0f, 1.0f),
            Math.Clamp(y + pitch, -1.0f, 1.0f));
    }
}
