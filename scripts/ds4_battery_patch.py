from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def replace_once(path: str, old: str, new: str) -> None:
    p = ROOT / path
    text = p.read_text(encoding='utf-8')
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{path}: expected one match, got {count}: {old[:120]!r}')
    p.write_text(text.replace(old, new, 1), encoding='utf-8')

# Feed one aggregate battery value into every virtual report. X360 deliberately
# ignores it because ViGEm exposes that endpoint as a wired XInput controller.
replace_once(
    'host/OutputBackends.cs',
    '    void Apply(LogicalGamepadState state, ProcessedMotion motion);\n',
    '    void Apply(LogicalGamepadState state, ProcessedMotion motion, int? batteryPercent);\n')

replace_once(
    'host/OutputBackends.cs',
    '    public void Apply(LogicalGamepadState s, ProcessedMotion motion)\n    {\n        pad.ResetReport();\n',
    '    public void Apply(LogicalGamepadState s, ProcessedMotion motion, int? batteryPercent)\n    {\n        // ViGEm emulates an Xbox 360 *wired* endpoint. XInput therefore has no\n        // feeder-side battery field to populate; keep the parameter only so all\n        // backends share one interface.\n        _ = batteryPercent;\n        pad.ResetReport();\n')

replace_once(
    'host/OutputBackends.cs',
    '    private readonly IDualShock4Controller pad;\n    private ushort timestamp;\n',
    '    private readonly IDualShock4Controller pad;\n    private ushort timestamp;\n    private int lastBatteryPercent = 100;\n')

replace_once(
    'host/OutputBackends.cs',
    '    public void Apply(LogicalGamepadState s, ProcessedMotion motion)\n    {\n        byte[] report = BuildBaseReport(s);\n',
    '    public void Apply(LogicalGamepadState s, ProcessedMotion motion, int? batteryPercent)\n    {\n        if (batteryPercent.HasValue)\n            lastBatteryPercent = Math.Clamp(batteryPercent.Value, 0, 100);\n\n        byte[] report = BuildBaseReport(s, lastBatteryPercent);\n')

replace_once(
    'host/OutputBackends.cs',
    '        pad.SubmitRawReport(BuildBaseReport(LogicalGamepadState.Neutral()));\n',
    '        pad.SubmitRawReport(BuildBaseReport(LogicalGamepadState.Neutral(), lastBatteryPercent));\n')

replace_once(
    'host/OutputBackends.cs',
    '    private static byte[] BuildBaseReport(LogicalGamepadState s)\n    {\n        byte[] r = new byte[63];\n',
    '''    private static byte[] BuildBaseReport(LogicalGamepadState s, int batteryPercent)
    {
        byte[] r = new byte[63];
''')

replace_once(
    'host/OutputBackends.cs',
    '        r[7] = ToByte01(s.LT);\n        r[8] = ToByte01(s.RT);\n        return r;\n',
    '''        r[7] = ToByte01(s.LT);
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
''')

replace_once(
    'host/OutputBackends.cs',
    '    private static short GyroRaw(float degreesPerSecond)\n',
    '''    private static byte ToDs4BatteryLevel(int batteryPercent)
    {
        int percent = Math.Clamp(batteryPercent, 0, 100);
        return (byte)(percent >= 100 ? 10 : percent / 10);
    }

    private static short GyroRaw(float degreesPerSecond)
''')

# Use the weaker of the two controllers. One known side is better than no data;
# unknown-at-start is intentionally reported as full rather than the old false 5%.
replace_once(
    'host/Program.cs',
    '                            backend.Apply(state, motion);\n',
    '                            backend.Apply(state, motion, AggregateControllerBattery(Status.Snapshot()));\n')

replace_once(
    'host/Program.cs',
    '    private static (int? left, int? right) DecodeBatteries(uint packed)\n',
    '''    private static int AggregateControllerBattery(HostSnapshot snapshot)
    {
        if (snapshot.LeftBattery.HasValue && snapshot.RightBattery.HasValue)
            return Math.Min(snapshot.LeftBattery.Value, snapshot.RightBattery.Value);
        if (snapshot.LeftBattery.HasValue) return snapshot.LeftBattery.Value;
        if (snapshot.RightBattery.HasValue) return snapshot.RightBattery.Value;
        return 100;
    }

    private static (int? left, int? right) DecodeBatteries(uint packed)
''')

for project in ('host/QuestPad.Host.csproj', 'host/QuestPad.Host.Console.csproj'):
    replace_once(project, '<Version>0.3.7-test</Version>', '<Version>0.3.8-test</Version>')
    replace_once(project, '<FileVersion>0.3.7.0</FileVersion>', '<FileVersion>0.3.8.0</FileVersion>')

print('DS4 battery telemetry patch applied')
