from pathlib import Path

# ---- Quest: optional OpenXR controller battery telemetry ---------------------
cpp = Path('quest/src/main/cpp/questpad.cpp')
s = cpp.read_text(encoding='utf-8')

anchor = """XrActionStateVector2f getVec2(XrSession s, XrAction a) {
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO}; gi.action = a;
    XrActionStateVector2f st{XR_TYPE_ACTION_STATE_VECTOR2F};
    xrGetActionStateVector2f(s, &gi, &st);
    return st;
}

"""
insert = anchor + """struct BatteryReading {
    bool valid = false;
    bool charging = false;
    float level = 0.0f;
};

BatteryReading getBatteryState(XrSession session, XrPath userPath) {
    BatteryReading result{};
    XrBatteryStateDisplayEXT battery{XR_TYPE_BATTERY_STATE_DISPLAY_EXT};
    XrInteractionProfileState profile{XR_TYPE_INTERACTION_PROFILE_STATE};
    profile.next = &battery;
    if (XR_FAILED(xrGetCurrentInteractionProfile(session, userPath, &profile))) return result;
    if ((battery.stateFlags & XR_BATTERY_STATE_DISPLAY_STATE_VALID_BIT_EXT) == 0) return result;
    result.valid = true;
    result.charging = (battery.stateFlags & XR_BATTERY_STATE_DISPLAY_STATE_CHARGING_BIT_EXT) != 0;
    result.level = std::clamp(battery.batteryLevel, 0.0f, 1.0f);
    return result;
}

uint32_t packBatteryState(const BatteryReading& left, const BatteryReading& right) {
    uint32_t packed = 0;
    if (left.valid) {
        const uint32_t pct = static_cast<uint32_t>(std::lround(left.level * 100.0f));
        packed |= std::min(pct, 100u);
        packed |= 1u << 16;
        if (left.charging) packed |= 1u << 18;
    }
    if (right.valid) {
        const uint32_t pct = static_cast<uint32_t>(std::lround(right.level * 100.0f));
        packed |= std::min(pct, 100u) << 8;
        packed |= 1u << 17;
        if (right.charging) packed |= 1u << 19;
    }
    return packed;
}

"""
if anchor not in s:
    raise SystemExit('battery helper anchor not found')
s = s.replace(anchor, insert, 1)

old = """    const bool touchPlusExt = hasExtension(XR_META_TOUCH_CONTROLLER_PLUS_EXTENSION_NAME);
    if (touchPlusExt) extensions.push_back(XR_META_TOUCH_CONTROLLER_PLUS_EXTENSION_NAME);
"""
new = old + """    const bool batteryExt = hasExtension(XR_EXT_INTERACTION_PROFILE_BATTERY_STATE_DISPLAY_EXTENSION_NAME);
    if (batteryExt) extensions.push_back(XR_EXT_INTERACTION_PROFILE_BATTERY_STATE_DISPLAY_EXTENSION_NAME);
"""
if old not in s:
    raise SystemExit('extension block not found')
s = s.replace(old, new, 1)

old = """    BridgeServer bridge;
    bridge.start();

    bool resumed = false;
"""
new = """    XrPath leftUserPath = XR_NULL_PATH;
    XrPath rightUserPath = XR_NULL_PATH;
    xrStringToPath(instance, "/user/hand/left", &leftUserPath);
    xrStringToPath(instance, "/user/hand/right", &rightUserPath);
    LOGI("controller battery telemetry: %s", batteryExt ? "OpenXR extension available" : "runtime extension unavailable");

    BridgeServer bridge;
    bridge.start();

    bool resumed = false;
"""
if old not in s:
    raise SystemExit('bridge anchor not found')
s = s.replace(old, new, 1)

old = """    uint64_t exitPulseUntilNs = 0;
    int thermal = -1;
    uint64_t nextThermalPoll = 0;
"""
new = """    uint64_t exitPulseUntilNs = 0;
    int thermal = -1;
    uint64_t nextThermalPoll = 0;
    uint64_t nextBatteryPoll = 0;
    uint32_t batteryPacked = 0;
"""
if old not in s:
    raise SystemExit('telemetry locals anchor not found')
s = s.replace(old, new, 1)

old = """            if (pressed(actions.rThumb)) packet.buttons |= BTN_RTHUMB;
            if (pressed(actions.view)) packet.buttons |= BTN_VIEW;

            // Exit is expressed in Xbox terms: LS + RS + LB + RB for 3 s.
"""
new = """            if (pressed(actions.rThumb)) packet.buttons |= BTN_RTHUMB;
            if (pressed(actions.view)) packet.buttons |= BTN_VIEW;

            // Battery polling is intentionally slow: battery state is display telemetry,
            // not latency-sensitive controller input. The ratified OpenXR extension is
            // optional; runtimes that do not expose it simply leave both validity bits 0.
            if (batteryExt && packet.monotonicNs >= nextBatteryPoll) {
                const BatteryReading leftBattery = getBatteryState(session, leftUserPath);
                const BatteryReading rightBattery = getBatteryState(session, rightUserPath);
                batteryPacked = packBatteryState(leftBattery, rightBattery);
                nextBatteryPoll = packet.monotonicNs + 5'000'000'000ULL;
            }
            packet.reserved = batteryPacked;

            // Exit is expressed in Xbox terms: LS + RS + LB + RB for 3 s.
"""
if old not in s:
    raise SystemExit('battery polling anchor not found')
s = s.replace(old, new, 1)
cpp.write_text(s, encoding='utf-8')

# OpenXR 1.1.58 added XR_EXT_interaction_profile_battery_state_display.
gradle = Path('quest/build.gradle')
g = gradle.read_text(encoding='utf-8')
g = g.replace("versionCode 2", "versionCode 3")
g = g.replace("versionName '0.2.0'", "versionName '0.2.1'")
g = g.replace("openxr_loader_for_android:1.1.53", "openxr_loader_for_android:1.1.58")
gradle.write_text(g, encoding='utf-8')

# ---- Windows tray ------------------------------------------------------------
Path('host/TrayStatus.cs').write_text(r'''using System.Drawing;
using System.Windows.Forms;

namespace QuestPad.Host;

internal readonly record struct HostSnapshot(
    bool Connected,
    bool GamepadAvailable,
    bool GamepadPaused,
    double Hz,
    long Drops,
    string Thermal,
    int? LeftBattery,
    int? RightBattery);

internal sealed class HostStatus
{
    private readonly object _gate = new();
    private HostSnapshot _value = new(false, false, false, 0, 0, "N/A", null, null);

    public HostSnapshot Snapshot()
    {
        lock (_gate) return _value;
    }

    public void SetConnection(bool connected)
    {
        lock (_gate) _value = _value with { Connected = connected, Hz = connected ? _value.Hz : 0 };
    }

    public void SetGamepadAvailable(bool available)
    {
        lock (_gate) _value = _value with { GamepadAvailable = available };
    }

    public void SetPaused(bool paused)
    {
        lock (_gate) _value = _value with { GamepadPaused = paused };
    }

    public void UpdateTelemetry(double hz, long drops, string thermal, int? leftBattery, int? rightBattery)
    {
        lock (_gate)
            _value = _value with
            {
                Hz = hz,
                Drops = drops,
                Thermal = thermal,
                LeftBattery = leftBattery,
                RightBattery = rightBattery
            };
    }
}

internal sealed class TrayStatus : IDisposable
{
    private readonly HostStatus _status;
    private readonly Action<bool> _setPaused;
    private readonly Action _exit;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disposed;
    private ApplicationContext? _context;
    private NotifyIcon? _icon;

    public TrayStatus(HostStatus status, Action<bool> setPaused, Action exit)
    {
        _status = status;
        _setPaused = setPaused;
        _exit = exit;
        _thread = new Thread(Run) { IsBackground = true, Name = "QuestPad tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        _context = new ApplicationContext();

        var connection = new ToolStripMenuItem("Quest: waiting") { Enabled = false };
        var leftBattery = new ToolStripMenuItem("Left controller: n/a") { Enabled = false };
        var rightBattery = new ToolStripMenuItem("Right controller: n/a") { Enabled = false };
        var thermal = new ToolStripMenuItem("Thermal: n/a") { Enabled = false };
        var cadence = new ToolStripMenuItem("Input: 0.0 Hz") { Enabled = false };
        var pause = new ToolStripMenuItem("Pause gamepad output") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => _setPaused(pause.Checked);
        var exit = new ToolStripMenuItem("Exit QuestPad Host");
        exit.Click += (_, _) => _exit();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            connection, leftBattery, rightBattery, thermal, cadence,
            new ToolStripSeparator(), pause, new ToolStripSeparator(), exit
        });

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "QuestPad — waiting for Quest",
            ContextMenuStrip = menu,
            Visible = true
        };

        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) =>
        {
            var s = _status.Snapshot();
            connection.Text = s.Connected ? "Quest: connected" : "Quest: waiting";
            leftBattery.Text = $"Left controller: {BatteryText(s.LeftBattery)}";
            rightBattery.Text = $"Right controller: {BatteryText(s.RightBattery)}";
            thermal.Text = $"Thermal: {s.Thermal}";
            cadence.Text = $"Input: {s.Hz:F1} Hz   drops: {s.Drops}";
            if (pause.Checked != s.GamepadPaused) pause.Checked = s.GamepadPaused;
            pause.Enabled = s.GamepadAvailable;

            string state = s.Connected ? "connected" : "waiting";
            string bat = $"L {BatteryText(s.LeftBattery)} R {BatteryText(s.RightBattery)}";
            string text = $"QuestPad — {state} — {bat}";
            _icon.Text = text.Length <= 127 ? text : text[..127];
        };
        timer.Start();
        _ready.Set();
        Application.Run(_context);
        timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        menu.Dispose();
        timer.Dispose();
    }

    private static string BatteryText(int? value) => value.HasValue ? $"{value.Value}%" : "n/a";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context is not null)
        {
            try { _context.ExitThread(); } catch { }
        }
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
''', encoding='utf-8')

csproj = Path('host/QuestPad.Host.csproj')
c = csproj.read_text(encoding='utf-8')
c = c.replace('<TargetFramework>net8.0</TargetFramework>', '<TargetFramework>net8.0-windows</TargetFramework>\n    <UseWindowsForms>true</UseWindowsForms>')
csproj.write_text(c, encoding='utf-8')

program = Path('host/Program.cs')
p = program.read_text(encoding='utf-8')
p = p.replace(
    "    private static readonly CancellationTokenSource Cancel = new();\n",
    "    private static readonly CancellationTokenSource Cancel = new();\n"
    "    private static readonly HostStatus Status = new();\n"
    "    private static volatile bool EmulationPaused;\n")
p = p.replace(
    "        bool noGamepad = false;\n        bool noAdb = false;\n",
    "        bool noGamepad = false;\n        bool noAdb = false;\n        bool noTray = false;\n")
p = p.replace(
    "                case \"--no-adb\":\n                    noAdb = true;\n                    break;\n",
    "                case \"--no-adb\":\n                    noAdb = true;\n                    break;\n"
    "                case \"--no-tray\":\n                    noTray = true;\n                    break;\n")
anchor = """        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Cancel.Cancel();
        };

"""
replacement = anchor + """        using TrayStatus? tray = noTray ? null : new TrayStatus(
            Status,
            paused =>
            {
                EmulationPaused = paused;
                Status.SetPaused(paused);
                if (paused) Volatile.Write(ref RumblePacked, 0);
            },
            () => Cancel.Cancel());

"""
if anchor not in p:
    raise SystemExit('tray startup anchor not found')
p = p.replace(anchor, replacement, 1)
p = p.replace(
    '                Console.WriteLine("Virtual Xbox 360 controller: connected");\n',
    '                Status.SetGamepadAvailable(true);\n                Console.WriteLine("Virtual Xbox 360 controller: connected");\n')
p = p.replace(
    "                pad = null;\n            }\n        }\n\n        try\n",
    "                pad = null;\n                Status.SetGamepadAvailable(false);\n            }\n        }\n\n        try\n")
p = p.replace(
    '                Console.WriteLine("Waiting for QuestPad on Quest...");\n                await tcp.ConnectAsync("127.0.0.1", Port, ct);\n                Console.WriteLine("QuestPad transport connected");\n',
    '                Status.SetConnection(false);\n                Console.WriteLine("Waiting for QuestPad on Quest...");\n                await tcp.ConnectAsync("127.0.0.1", Port, ct);\n                Status.SetConnection(true);\n                Console.WriteLine("QuestPad transport connected");\n')
p = p.replace(
    "                    if (pad is not null)\n                    {\n                        // FLAG_FOCUSED = bit 1. Quest sends a neutral packet on focus loss,\n",
    "                    if (pad is not null)\n                    {\n                        if (EmulationPaused)\n                        {\n                            mapper.Reset();\n                            Volatile.Write(ref RumblePacked, 0);\n                            Neutral(pad);\n                        }\n                        // FLAG_FOCUSED = bit 1. Quest sends a neutral packet on focus loss,\n")
# Turn the existing focus branch into an else-if after the pause branch.
p = p.replace(
    "                        if ((p.Flags & 0x2u) == 0)\n",
    "                        else if ((p.Flags & 0x2u) == 0)\n", 1)

old = """                        double hz = windowPackets / printSeconds;
                        lastPrintTicks = now;
                        windowPackets = 0;
                        Console.Write(
                            $"\\r{hz,5:F1} Hz  seq {p.Sequence,8}  L {p.LX,6:F2},{p.LY,6:F2}  R {p.RX,6:F2},{p.RY,6:F2}  " +
                            $"LT {p.LT:F2} RT {p.RT:F2}  grip {p.LG:F2}/{p.RG:F2}  " +
                            $"therm {ThermalName(p.Thermal),8}  drops {dropped}      ");
"""
new = """                        double hz = windowPackets / printSeconds;
                        lastPrintTicks = now;
                        windowPackets = 0;
                        var (leftBattery, rightBattery) = DecodeBatteries(p.Reserved);
                        Status.UpdateTelemetry(hz, dropped, ThermalName(p.Thermal), leftBattery, rightBattery);
                        string batteryText = $"bat L {BatteryText(leftBattery),4} R {BatteryText(rightBattery),4}";
                        Console.Write(
                            $"\\r{hz,5:F1} Hz  seq {p.Sequence,8}  L {p.LX,6:F2},{p.LY,6:F2}  R {p.RX,6:F2},{p.RY,6:F2}  " +
                            $"LT {p.LT:F2} RT {p.RT:F2}  grip {p.LG:F2}/{p.RG:F2}  " +
                            $"{batteryText}  therm {ThermalName(p.Thermal),8}  drops {dropped}      ");
"""
if old not in p:
    raise SystemExit('telemetry print block not found')
p = p.replace(old, new, 1)

p = p.replace(
    "                mapper.Reset();\n                Volatile.Write(ref RumblePacked, 0);\n",
    "                Status.SetConnection(false);\n                mapper.Reset();\n                Volatile.Write(ref RumblePacked, 0);\n", 1)

anchor = """    private static string ThermalName(int t) => t switch
    {
        0 => "NONE", 1 => "LIGHT", 2 => "MODERATE", 3 => "SEVERE",
        4 => "CRITICAL", 5 => "EMERGENCY", 6 => "SHUTDOWN", _ => t.ToString()
    };

"""
replacement = anchor + """    private static (int? left, int? right) DecodeBatteries(uint packed)
    {
        int? left = (packed & (1u << 16)) != 0 ? (int)(packed & 0xFFu) : null;
        int? right = (packed & (1u << 17)) != 0 ? (int)((packed >> 8) & 0xFFu) : null;
        return (left, right);
    }

    private static string BatteryText(int? value) => value.HasValue ? $"{value.Value}%" : "n/a";

"""
if anchor not in p:
    raise SystemExit('battery decode anchor not found')
p = p.replace(anchor, replacement, 1)
p = p.replace(
    '        Console.WriteLine("QuestPad.Host [--adb PATH] [--serial SERIAL] [--no-gamepad] [--no-adb]");\n',
    '        Console.WriteLine("QuestPad.Host [--adb PATH] [--serial SERIAL] [--no-gamepad] [--no-adb] [--no-tray]");\n')
p = p.replace(
    '        Console.WriteLine("  --no-adb      assume tcp:38888 is already reachable (developer testing)");\n',
    '        Console.WriteLine("  --no-adb      assume tcp:38888 is already reachable (developer testing)");\n        Console.WriteLine("  --no-tray     disable the Windows notification-area status icon");\n')
p = p.replace(
    '        Console.WriteLine("  both stick clicks + both grips for 3 s -> exit QuestPad");\n',
    '        Console.WriteLine("  LS + RS + LB + RB for 3 s -> exit QuestPad (haptic countdown)");\n')
program.write_text(p, encoding='utf-8')

# ---- Docs --------------------------------------------------------------------
protocol = Path('PROTOCOL.md')
pr = protocol.read_text(encoding='utf-8')
pr = pr.replace('| 64 | u32 | reserved |', '| 64 | u32 | controller battery display telemetry |')
pr += '''\n## Controller battery telemetry (offset 64)\n\nThe existing 32-bit reserved field is used without changing protocol v1 packet size:\n\n- bits 0..7: left controller battery percentage (0..100)\n- bits 8..15: right controller battery percentage (0..100)\n- bit 16: left percentage valid\n- bit 17: right percentage valid\n- bit 18: left controller charging\n- bit 19: right controller charging\n\nBattery data comes from the optional ratified `XR_EXT_interaction_profile_battery_state_display` extension. If the Quest OpenXR runtime does not expose the extension, validity bits remain clear and hosts must display battery state as unavailable.\n'''
protocol.write_text(pr, encoding='utf-8')

readme = Path('README.md')
r = readme.read_text(encoding='utf-8')
r = r.replace('- Low CPU/GPU performance hints and thermal telemetry.\n', '- Low CPU/GPU performance hints, thermal telemetry, and controller battery display when supported by the runtime.\n')
r = r.replace('- Xbox rumble bridged back to Touch Plus haptics.\n', '- Xbox rumble bridged back to Touch Plus haptics.\n- Windows notification-area tray with connection, thermal, input-rate and controller-battery status plus pause/exit controls.\n')
r = r.replace('The host automatically creates the ADB forward to `tcp:38888` and reconnects after temporary transport loss.\n', 'The host automatically creates the ADB forward to `tcp:38888` and reconnects after temporary transport loss. A tray icon is enabled by default; use `--no-tray` for console-only operation.\n')
r = r.replace('Longer thermal/transport soak testing and broader game compatibility testing are still in progress.', 'Controller battery display uses the optional ratified OpenXR battery-state extension and therefore appears as `n/a` if the installed Quest runtime does not expose it. Longer thermal/transport soak testing and broader game compatibility testing are still in progress.')
readme.write_text(r, encoding='utf-8')

status = Path('BUILD_STATUS.md')
b = status.read_text(encoding='utf-8')
b = b.replace('- Xbox rumble is successfully forwarded back to the Touch Plus controllers.\n', '- Xbox rumble is successfully forwarded back to the Touch Plus controllers.\n- Windows tray status/control UI is implemented; hardware validation of the tray and controller-battery telemetry is pending.\n')
b = b.replace('- Exit gesture and haptic countdown behavior across Horizon/OpenXR runtime states.\n', '- Exit gesture and haptic countdown behavior across Horizon/OpenXR runtime states.\n- Whether the current Quest OpenXR runtime exposes `XR_EXT_interaction_profile_battery_state_display` for Touch Plus, and the granularity/accuracy of the reported percentages.\n')
status.write_text(b, encoding='utf-8')
