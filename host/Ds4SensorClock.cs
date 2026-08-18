using System.Diagnostics;

namespace QuestPad.Host;

internal static class Ds4SensorClock
{
    // DualShock 4's 16-bit sensor timestamp advances in 5.33 us units.
    // Linux hid-playstation converts it with timestamp_us = timestamp * 16 / 3,
    // which is exactly 187,500 counter units per second.
    private const ulong UnitsPerSecond = 187_500UL;

    public static ushort Now() => FromStopwatchTicks(Stopwatch.GetTimestamp());

    internal static ushort FromStopwatchTicks(long ticks)
    {
        if (ticks <= 0) return 0;

        ulong frequency = (ulong)Stopwatch.Frequency;
        ulong value = (ulong)ticks;
        ulong seconds = value / frequency;
        ulong remainder = value % frequency;

        // Split whole seconds from the remainder so long-running hosts never risk
        // overflowing value * 187500 before the natural 16-bit DS4 wrap.
        ulong units = seconds * UnitsPerSecond + remainder * UnitsPerSecond / frequency;
        return (ushort)units;
    }
}

// Temporary 0.4.2 A/B switch. Keep this session-only so a diagnostic choice does not
// silently become a persistent compatibility setting. If hardware testing proves the
// gravity accelerometer universally safe, this switch can disappear again.
internal static class Ds4Diagnostics
{
    public static volatile bool AccelerometerEnabled = true;
}
