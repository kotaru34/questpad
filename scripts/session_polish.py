from pathlib import Path
import math
import struct
import zlib

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: str, old: str, new: str) -> None:
    p = ROOT / path
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}: {old[:100]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


def write_text(path: str, text: str) -> None:
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")


# ---------------------------------------------------------------------------
# Windows host lifecycle: one explicit exit intent in each direction.
# ---------------------------------------------------------------------------
replace_once(
    "host/Program.cs",
    "    private const uint FlagPassthroughActive = 1u << 6;\n",
    "    private const uint FlagPassthroughActive = 1u << 6;\n"
    "    private const uint FlagQuestUserExitRequested = 1u << 7;\n"
    "    private const ushort ControlQuestShutdown = 1u << 9;\n",
)

replace_once(
    "host/Program.cs",
    "    private static volatile bool DisarmSteeringRequested;\n"
    "    private static int RumblePacked; // high byte = large motor, low byte = small motor\n",
    "    private static volatile bool DisarmSteeringRequested;\n"
    "    private static readonly object ActiveStreamGate = new();\n"
    "    private static readonly SemaphoreSlim FeedbackSendGate = new(1, 1);\n"
    "    private static NetworkStream? ActiveStream;\n"
    "    private static int HostShutdownRequested;\n"
    "    private static volatile bool QuestUserExitRequested;\n"
    "    private static volatile bool GracefulQuestShutdownSent;\n"
    "    private static int RumblePacked; // high byte = large motor, low byte = small motor\n",
)

replace_once(
    "host/Program.cs",
    "        Console.CancelKeyPress += (_, e) =>\n"
    "        {\n"
    "            e.Cancel = true;\n"
    "            Cancel.Cancel();\n"
    "        };\n",
    "        using var singleInstance = new Mutex(true, @\"Local\\QuestPad.Host\", out bool ownsInstance);\n"
    "        if (!ownsInstance)\n"
    "        {\n"
    "            FatalError(\"QuestPad Host is already running. Use the existing tray instance instead of starting a second bridge.\");\n"
    "            return 6;\n"
    "        }\n\n"
    "        Console.CancelKeyPress += (_, e) =>\n"
    "        {\n"
    "            e.Cancel = true;\n"
    "            RequestHostShutdown();\n"
    "        };\n",
)

replace_once(
    "host/Program.cs",
    "            () => Cancel.Cancel());\n",
    "            RequestHostShutdown);\n",
)

replace_once(
    "host/Program.cs",
    "            outputs?.Dispose();\n"
    "            if (adb is not null)\n"
    "                RunAdb(adb, serial, \"forward\", \"--remove\", $\"tcp:{Port}\");\n",
    "            outputs?.Dispose();\n"
    "            if (Volatile.Read(ref HostShutdownRequested) != 0 && !QuestUserExitRequested && adb is not null && serial is not null)\n"
    "            {\n"
    "                if (GracefulQuestShutdownSent)\n"
    "                    await Task.Delay(200);\n"
    "                // ADB is the last-resort lifecycle backstop. On the normal path the\n"
    "                // protocol shutdown bit lets NativeActivity clean up first; force-stop\n"
    "                // is harmless if the process already exited and covers paused XR or a\n"
    "                // bridge that disconnected before receiving the final control report.\n"
    "                RunAdb(adb, serial, \"shell\", \"am\", \"force-stop\", AdbQuestDeviceSelector.QuestPadPackage);\n"
    "            }\n"
    "            if (adb is not null)\n"
    "                RunAdb(adb, serial, \"forward\", \"--remove\", $\"tcp:{Port}\");\n",
)

replace_once(
    "host/Program.cs",
    "                using NetworkStream stream = tcp.GetStream();\n"
    "                byte[] packetBytes = new byte[PacketSize];\n",
    "                using NetworkStream stream = tcp.GetStream();\n"
    "                SetActiveStream(stream);\n"
    "                byte[] packetBytes = new byte[PacketSize];\n",
)

replace_once(
    "host/Program.cs",
    "                    previousSeq = p.Sequence;\n"
    "                    windowPackets++;\n\n"
    "                    RuntimeSettingsSnapshot cfg = Settings.Snapshot();\n",
    "                    previousSeq = p.Sequence;\n"
    "                    windowPackets++;\n\n"
    "                    if ((p.Flags & FlagQuestUserExitRequested) != 0)\n"
    "                    {\n"
    "                        QuestUserExitRequested = true;\n"
    "                        Volatile.Write(ref RumblePacked, 0);\n"
    "                        try { outputs?.Current?.Neutral(); } catch { }\n"
    "                        Console.WriteLine(\"\\nQuest exit gesture requested full QuestPad shutdown; closing Windows host.\");\n"
    "                        Cancel.Cancel();\n"
    "                        break;\n"
    "                    }\n\n"
    "                    RuntimeSettingsSnapshot cfg = Settings.Snapshot();\n",
)

replace_once(
    "host/Program.cs",
    "                }\n"
    "            }\n"
    "            catch (OperationCanceledException) when (ct.IsCancellationRequested)\n"
    "            {\n"
    "                break;\n"
    "            }\n"
    "            catch (Exception ex)\n"
    "            {\n"
    "                Status.SetConnection(false);\n",
    "                }\n"
    "                SetActiveStream(null);\n"
    "            }\n"
    "            catch (OperationCanceledException) when (ct.IsCancellationRequested)\n"
    "            {\n"
    "                SetActiveStream(null);\n"
    "                break;\n"
    "            }\n"
    "            catch (Exception ex)\n"
    "            {\n"
    "                SetActiveStream(null);\n"
    "                Status.SetConnection(false);\n",
)

replace_once(
    "host/Program.cs",
    "    private static bool UpdateGyroStickLock(\n",
    "    private static void RequestHostShutdown()\n"
    "    {\n"
    "        if (Interlocked.Exchange(ref HostShutdownRequested, 1) != 0) return;\n"
    "        _ = Task.Run(async () =>\n"
    "        {\n"
    "            try\n"
    "            {\n"
    "                GracefulQuestShutdownSent = await TrySendQuestShutdownToActiveStreamAsync();\n"
    "            }\n"
    "            finally\n"
    "            {\n"
    "                Cancel.Cancel();\n"
    "            }\n"
    "        });\n"
    "    }\n\n"
    "    private static async Task<bool> TrySendQuestShutdownToActiveStreamAsync()\n"
    "    {\n"
    "        NetworkStream? stream;\n"
    "        lock (ActiveStreamGate) stream = ActiveStream;\n"
    "        if (stream is null || !stream.CanWrite) return false;\n\n"
    "        try\n"
    "        {\n"
    "            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));\n"
    "            await SendFeedbackAsync(stream, 0, ControlQuestShutdown, timeout.Token);\n"
    "            Console.WriteLine(\"\\nQuest bridge shutdown requested over protocol.\");\n"
    "            return true;\n"
    "        }\n"
    "        catch (Exception ex)\n"
    "        {\n"
    "            Console.Error.WriteLine($\"Quest protocol shutdown was not delivered: {ex.Message}\");\n"
    "            return false;\n"
    "        }\n"
    "    }\n\n"
    "    private static void SetActiveStream(NetworkStream? stream)\n"
    "    {\n"
    "        lock (ActiveStreamGate) ActiveStream = stream;\n"
    "    }\n\n"
    "    private static bool UpdateGyroStickLock(\n",
)

replace_once(
    "host/Program.cs",
    "    private static async Task SendFeedbackAsync(NetworkStream stream, int packed, ushort control, CancellationToken ct)\n"
    "    {\n"
    "        byte[] feedback = new byte[FeedbackSize];\n"
    "        BinaryPrimitives.WriteUInt32LittleEndian(feedback.AsSpan(0, 4), FeedbackMagic);\n"
    "        feedback[4] = (byte)((packed >> 8) & 0xFF);\n"
    "        feedback[5] = (byte)(packed & 0xFF);\n"
    "        BinaryPrimitives.WriteUInt16LittleEndian(feedback.AsSpan(6, 2), control);\n"
    "        await stream.WriteAsync(feedback, ct);\n"
    "    }\n",
    "    private static async Task SendFeedbackAsync(NetworkStream stream, int packed, ushort control, CancellationToken ct)\n"
    "    {\n"
    "        await FeedbackSendGate.WaitAsync(ct);\n"
    "        try\n"
    "        {\n"
    "            byte[] feedback = new byte[FeedbackSize];\n"
    "            BinaryPrimitives.WriteUInt32LittleEndian(feedback.AsSpan(0, 4), FeedbackMagic);\n"
    "            feedback[4] = (byte)((packed >> 8) & 0xFF);\n"
    "            feedback[5] = (byte)(packed & 0xFF);\n"
    "            BinaryPrimitives.WriteUInt16LittleEndian(feedback.AsSpan(6, 2), control);\n"
    "            await stream.WriteAsync(feedback, ct);\n"
    "        }\n"
    "        finally\n"
    "        {\n"
    "            FeedbackSendGate.Release();\n"
    "        }\n"
    "    }\n",
)

# ---------------------------------------------------------------------------
# Quest lifecycle protocol: host-requested exit and explicit user-exit marker.
# ---------------------------------------------------------------------------
replace_once(
    "quest/src/main/cpp/questpad.cpp",
    "constexpr uint16_t kControlPassthrough = 1u << 8;\n",
    "constexpr uint16_t kControlPassthrough = 1u << 8;\n"
    "constexpr uint16_t kControlHostShutdown = 1u << 9;\n",
)

replace_once(
    "quest/src/main/cpp/questpad.cpp",
    "    FLAG_PASSTHROUGH_ACTIVE = 1u << 6,\n"
    "};\n",
    "    FLAG_PASSTHROUGH_ACTIVE = 1u << 6,\n"
    "    FLAG_USER_EXIT_REQUESTED = 1u << 7,\n"
    "};\n",
)

replace_once(
    "quest/src/main/cpp/questpad.cpp",
    "        bool finishActivityAfterFrame = false;\n"
    "        FeedbackState feedback = bridge.pollFeedback();\n"
    "        uint16_t motionRequest = feedback.control & 0x3u;\n"
    "        bool wantPassthrough = (feedback.control & kControlPassthrough) != 0;\n"
    "        passthrough.setEnabled(wantPassthrough, app->activity);\n",
    "        bool finishActivityAfterFrame = false;\n"
    "        FeedbackState feedback = bridge.pollFeedback();\n"
    "        uint16_t motionRequest = feedback.control & 0x3u;\n"
    "        bool hostShutdownRequested = (feedback.control & kControlHostShutdown) != 0;\n"
    "        bool wantPassthrough = !hostShutdownRequested && (feedback.control & kControlPassthrough) != 0;\n"
    "        passthrough.setEnabled(wantPassthrough, app->activity);\n"
    "        if (hostShutdownRequested && !exitRequested) {\n"
    "            LOGI(\"Windows host requested Quest bridge shutdown\");\n"
    "            exitRequested = true;\n"
    "            finishActivityAfterFrame = true;\n"
    "        }\n",
)

replace_once(
    "quest/src/main/cpp/questpad.cpp",
    "                    packet.monotonicNs = monoNs();\n"
    "                    packet.thermalStatus = thermal;\n\n"
    "                    // On Android the Activity lifecycle owns session teardown. Khronos'\n",
    "                    packet.monotonicNs = monoNs();\n"
    "                    packet.thermalStatus = thermal;\n"
    "                    packet.flags |= FLAG_USER_EXIT_REQUESTED;\n\n"
    "                    // On Android the Activity lifecycle owns session teardown. Khronos'\n",
)

replace_once(
    "quest/src/main/cpp/questpad.cpp",
    "            LOGI(\"exit chord completed; finishing Android NativeActivity\");\n",
    "            LOGI(\"QuestPad shutdown requested; finishing Android NativeActivity\");\n",
)

replace_once(
    "quest/src/main/cpp/questpad.cpp",
    "    ici.applicationInfo.applicationVersion = 4;\n",
    "    ici.applicationInfo.applicationVersion = 5;\n",
)

# ---------------------------------------------------------------------------
# App identity / icons.
# ---------------------------------------------------------------------------
for project in ("host/QuestPad.Host.csproj", "host/QuestPad.Host.Console.csproj"):
    replace_once(
        project,
        "    <InvariantGlobalization>true</InvariantGlobalization>\n",
        "    <InvariantGlobalization>true</InvariantGlobalization>\n"
        "    <ApplicationIcon>questpad.ico</ApplicationIcon>\n"
        "    <Product>QuestPad</Product>\n"
        "    <Description>Meta Quest Touch Plus controller bridge for Windows</Description>\n"
        "    <Version>0.3.6-test</Version>\n"
        "    <FileVersion>0.3.6.0</FileVersion>\n",
    )

replace_once(
    "host/TrayStatus.cs",
    "    private static Icon CreateTrayIcon()\n"
    "    {\n"
    "        using var bitmap = new Bitmap(32, 32);\n",
    "    private static Icon CreateTrayIcon()\n"
    "    {\n"
    "        try\n"
    "        {\n"
    "            string? executable = Environment.ProcessPath;\n"
    "            if (!string.IsNullOrWhiteSpace(executable))\n"
    "            {\n"
    "                Icon? associated = Icon.ExtractAssociatedIcon(executable);\n"
    "                if (associated is not null) return associated;\n"
    "            }\n"
    "        }\n"
    "        catch { }\n\n"
    "        using var bitmap = new Bitmap(32, 32);\n",
)

replace_once(
    "quest/src/main/AndroidManifest.xml",
    "        android:hasCode=\"false\"\n"
    "        android:label=\"QuestPad\"\n",
    "        android:hasCode=\"false\"\n"
    "        android:icon=\"@mipmap/ic_launcher\"\n"
    "        android:roundIcon=\"@mipmap/ic_launcher\"\n"
    "        android:label=\"QuestPad\"\n",
)

replace_once("quest/build.gradle", "        versionCode 7\n        versionName '0.3.3-test'\n", "        versionCode 8\n        versionName '0.3.6-test'\n")

legacy_vector = '''<?xml version="1.0" encoding="utf-8"?>
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="108dp"
    android:height="108dp"
    android:viewportWidth="108"
    android:viewportHeight="108">
    <path
        android:fillColor="#1670DC"
        android:pathData="M54,4 C81.614,4 104,26.386 104,54 C104,81.614 81.614,104 54,104 C26.386,104 4,81.614 4,54 C4,26.386 26.386,4 54,4 Z" />
    <path
        android:fillColor="#00000000"
        android:strokeColor="#FFFFFFFF"
        android:strokeWidth="9"
        android:strokeLineCap="round"
        android:strokeLineJoin="round"
        android:pathData="M54,28 C68.359,28 80,39.641 80,54 C80,68.359 68.359,80 54,80 C39.641,80 28,68.359 28,54 C28,39.641 39.641,28 54,28" />
    <path
        android:fillColor="#00000000"
        android:strokeColor="#FFFFFFFF"
        android:strokeWidth="9"
        android:strokeLineCap="round"
        android:pathData="M67,67 L82,82" />
</vector>
'''
foreground_vector = '''<?xml version="1.0" encoding="utf-8"?>
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="108dp"
    android:height="108dp"
    android:viewportWidth="108"
    android:viewportHeight="108">
    <path
        android:fillColor="#00000000"
        android:strokeColor="#FFFFFFFF"
        android:strokeWidth="9"
        android:strokeLineCap="round"
        android:strokeLineJoin="round"
        android:pathData="M54,28 C68.359,28 80,39.641 80,54 C80,68.359 68.359,80 54,80 C39.641,80 28,68.359 28,54 C28,39.641 39.641,28 54,28" />
    <path
        android:fillColor="#00000000"
        android:strokeColor="#FFFFFFFF"
        android:strokeWidth="9"
        android:strokeLineCap="round"
        android:pathData="M67,67 L82,82" />
</vector>
'''
write_text("quest/src/main/res/mipmap-anydpi/ic_launcher.xml", legacy_vector)
write_text("quest/src/main/res/drawable/ic_questpad_foreground.xml", foreground_vector)
write_text(
    "quest/src/main/res/values/colors.xml",
    '<?xml version="1.0" encoding="utf-8"?>\n<resources>\n    <color name="questpad_icon_blue">#1670DC</color>\n</resources>\n',
)
write_text(
    "quest/src/main/res/mipmap-anydpi-v26/ic_launcher.xml",
    '''<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@color/questpad_icon_blue" />
    <foreground android:drawable="@drawable/ic_questpad_foreground" />
</adaptive-icon>
''',
)

svg = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-label="QuestPad logo">
  <circle cx="256" cy="256" r="238" fill="#1670dc"/>
  <circle cx="256" cy="246" r="122" fill="none" stroke="#fff" stroke-width="48"/>
  <path d="M316 306 L386 376" fill="none" stroke="#fff" stroke-width="48" stroke-linecap="round"/>
</svg>
'''
write_text("assets/questpad-logo.svg", svg)


def png_chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def render_png(size: int) -> bytes:
    blue = (22, 112, 220, 255)
    transparent = (0, 0, 0, 0)
    white = (255, 255, 255, 255)
    pixels = bytearray()
    cx = cy = size / 2.0
    outer = size * 0.465
    qcx = size * 0.5
    qcy = size * 0.48
    qr = size * 0.235
    stroke = max(1.0, size * 0.09)
    x1, y1 = size * 0.615, size * 0.605
    x2, y2 = size * 0.755, size * 0.745

    def segdist(px, py):
        vx, vy = x2 - x1, y2 - y1
        wx, wy = px - x1, py - y1
        denom = vx * vx + vy * vy
        t = 0 if denom == 0 else max(0.0, min(1.0, (wx * vx + wy * vy) / denom))
        dx, dy = px - (x1 + t * vx), py - (y1 + t * vy)
        return math.hypot(dx, dy)

    for y in range(size):
        pixels.append(0)  # PNG filter byte
        py = y + 0.5
        for x in range(size):
            px = x + 0.5
            d_outer = math.hypot(px - cx, py - cy)
            color = blue if d_outer <= outer else transparent
            if color[3]:
                d_q = abs(math.hypot(px - qcx, py - qcy) - qr)
                if d_q <= stroke / 2.0 or segdist(px, py) <= stroke / 2.0:
                    color = white
            pixels.extend(color)

    raw = bytes(pixels)
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", header) + png_chunk(b"IDAT", zlib.compress(raw, 9)) + png_chunk(b"IEND", b"")


sizes = [16, 24, 32, 48, 64, 128, 256]
images = [(s, render_png(s)) for s in sizes]
header = struct.pack("<HHH", 0, 1, len(images))
offset = 6 + 16 * len(images)
entries = []
body = bytearray()
for size, data in images:
    wh = 0 if size == 256 else size
    entries.append(struct.pack("<BBBBHHII", wh, wh, 0, 0, 1, 32, len(data), offset))
    body.extend(data)
    offset += len(data)
(ROOT / "host/questpad.ico").write_bytes(header + b"".join(entries) + bytes(body))

# ---------------------------------------------------------------------------
# Docs: describe current behaviour, not speculative knobs.
# ---------------------------------------------------------------------------
replace_once(
    "README.md",
    "# QuestPad\n",
    '<p align="center"><img src="assets/questpad-logo.svg" alt="QuestPad logo" width="96"></p>\n\n# QuestPad\n',
)
replace_once(
    "README.md",
    "- Quest battery-temperature trend and controller battery telemetry in the Windows tray.\n",
    "- Quest battery-temperature trend and controller battery telemetry in the Windows tray.\n"
    "- Safe ADB device selection, automatic Quest APK launch, portable settings and Black-mode brightness restore.\n"
    "- Bidirectional graceful lifecycle: exiting either side intentionally closes the other side too; ordinary transport loss still reconnects.\n"
    "- Single-instance Windows host guard prevents duplicate ADB/ViGEm/brightness ownership.\n",
)
replace_once(
    "README.md",
    "2. Start **QuestPad** from Developer / Unknown Sources on Quest.\n3. Start `QuestPad.Host.exe` on Windows.\n4. The default configuration is the familiar virtual Xbox 360 controller with gyro off and the Quest in black/zero-layer mode.\n",
    "2. Connect the Quest over USB and authorize USB debugging.\n3. Start `QuestPad.Host.exe` on Windows. The host identifies the Quest, creates the device-specific ADB forward and launches the installed QuestPad APK automatically.\n4. The default configuration is the familiar virtual Xbox 360 controller with gyro off and the Quest in black/zero-layer mode.\n\nUse `--no-quest-autostart` only when you deliberately want to launch the Quest side yourself. A connected phone is never selected as the transport target.\n",
)
replace_once(
    "README.md",
    "Hold **LS + RS + LB + RB** for **3 seconds**. `LB/RB` are the grip squeezes. Haptic cues occur at one second, two seconds and confirmation.\n",
    "Hold **LS + RS + LB + RB** for **3 seconds**. `LB/RB` are the grip squeezes. Haptic cues occur at one second, two seconds and confirmation. This is an explicit *Exit QuestPad* action: the Quest app sends a final neutral/user-exit packet and the Windows host closes too. A normal USB/TCP dropout does **not** trigger this behaviour.\n",
)
replace_once(
    "README.md",
    "QuestPad submits **zero OpenXR composition layers**, renders no scene, creates no eye swapchains and applies its minimum-brightness override. This remains the thermal/power baseline and is the preferred mode when the Quest is only being used as the controller bridge.\n",
    "QuestPad submits **zero OpenXR composition layers**, renders no scene and creates no eye swapchains. The Windows host snapshots the Quest system brightness and brightness mode over the already-selected ADB device, forces the display to minimum brightness while Black is active, and restores the exact saved values on MR mode or shutdown. A portable recovery file protects the saved values across a host crash. This remains the thermal/power baseline and is the preferred mode when the Quest is only being used as the controller bridge.\n",
)
replace_once(
    "README.md",
    "QuestPad uses `XR_FB_passthrough` to submit a single full-room reconstruction passthrough compositor layer. It does **not** ask for raw camera frames and still renders no scene or eye swapchains. When enabled, QuestPad restores normal/system display brightness so the room is actually visible.\n",
    "QuestPad uses `XR_FB_passthrough` to submit a single full-room reconstruction passthrough compositor layer. It does **not** ask for raw camera frames and still renders no scene or eye swapchains. When enabled, the Windows brightness manager restores the saved normal/system display brightness so the room is actually visible.\n",
)
replace_once(
    "README.md",
    "- Exit host.\n",
    "- Exit QuestPad (gracefully closes the Quest bridge too).\n",
)
replace_once(
    "README.md",
    "```text\n--adb PATH\n--serial SERIAL\n--quest-view black|passthrough\n",
    "```text\n--adb PATH\n--serial SERIAL\n--quest-autostart on|off\n--no-quest-autostart\n--quest-brightness on|off\n--no-quest-brightness\n--quest-view black|passthrough\n",
)
replace_once(
    "README.md",
    "- Host disconnect also removes the passthrough request, returning QuestPad to zero-layer mode.\n",
    "- Unexpected USB/TCP loss neutralizes the controller and leaves the Windows host alive to reconnect.\n"
    "- Intentional Windows-host exit sends a protocol shutdown request to the Quest bridge, with ADB force-stop only as a lifecycle backstop.\n"
    "- Intentional Quest exit-chord completion is explicitly flagged so the Windows host exits too; it is not inferred from transport loss.\n",
)

replace_once(
    "PROTOCOL.md",
    "- bit 6: QuestPad passthrough layer is currently active\n",
    "- bit 6: QuestPad passthrough layer is currently active\n- bit 7: the Quest-side 3-second exit chord completed and the user explicitly requested full QuestPad shutdown\n",
)
replace_once(
    "PROTOCOL.md",
    "- bit 8 (`0x0100`): request Quest compositor passthrough\n",
    "- bit 8 (`0x0100`): request Quest compositor passthrough\n- bit 9 (`0x0200`): request graceful QuestPad NativeActivity shutdown\n",
)
replace_once(
    "PROTOCOL.md",
    "The host may combine bit 8 with any motion request. For example, angular-rate gyro + MR passthrough uses motion request `1` plus `0x0100`.\n",
    "The host may combine bit 8 with any motion request. For example, angular-rate gyro + MR passthrough uses motion request `1` plus `0x0100`. Bit 9 is terminal lifecycle control: the Quest side neutralizes input, disables passthrough, finishes the current XR frame and closes its NativeActivity.\n\nThe reverse direction is equally explicit. When the local 3-second exit chord completes, Quest sends one final neutral packet with status bit 7 before closing. Windows interprets only that flag as a user-requested remote exit; an EOF, watchdog or USB disconnect continues to mean *reconnect*, not *quit*.\n",
)
replace_once(
    "PROTOCOL.md",
    "The host sends feedback/control whenever state changes and at least every 100 ms as a keepalive. If the Windows connection disappears, QuestPad sees a zero control word, disables passthrough and returns to its zero-layer/low-brightness baseline.\n",
    "The host sends feedback/control whenever state changes and at least every 100 ms as a keepalive. If the Windows connection disappears unexpectedly, QuestPad sees a zero control word and disables passthrough; the Windows host keeps its existing reconnect behaviour. Black-mode physical display brightness is managed separately by the Windows ADB brightness manager, which snapshots/restores the Quest system brightness and keeps a portable crash-recovery file.\n",
)
replace_once(
    "PROTOCOL.md",
    "Control bit 8 is clear. QuestPad submits **zero composition layers** and keeps its existing minimum-brightness override. This is the default PC-only / lowest-workload display mode.\n",
    "Control bit 8 is clear. QuestPad submits **zero composition layers**. This is the default PC-only / lowest-workload compositor mode. When the normal Windows host is in use, its ADB brightness manager independently forces the selected Quest display to minimum brightness and restores the captured system value/mode when leaving Black or shutting down.\n",
)
replace_once(
    "PROTOCOL.md",
    "Control bit 8 is set. When `XR_FB_passthrough` is supported, QuestPad lazily creates a reconstruction passthrough feature/layer, starts/resumes it, restores normal/system display brightness, and submits exactly one `XrCompositionLayerPassthroughFB` as the backmost/only QuestPad composition layer.\n",
    "Control bit 8 is set. When `XR_FB_passthrough` is supported, QuestPad starts/resumes its pre-created reconstruction passthrough feature/layer and submits exactly one `XrCompositionLayerPassthroughFB` as the backmost/only QuestPad composition layer. The Windows ADB brightness manager restores the saved normal/system brightness for this mode.\n",
)

replace_once(
    "BUILD_STATUS.md",
    "- Held Start/Menu and held View/Back semantics work correctly.\n",
    "- Held Start/Menu and held View/Back semantics work correctly.\n"
    "- The 3-second exit chord now closes the Quest NativeActivity cleanly without the previous Android ANR.\n"
    "- Windows ADB selection correctly identifies the real Quest 3 instead of a phone, and Windows-host startup can launch the Quest APK automatically.\n"
    "- Black-mode system brightness control and exact restore on MR/exit are verified on the Quest 3.\n"
    "- Portable Windows settings persist next to the executable.\n",
)
replace_once(
    "BUILD_STATUS.md",
    "- Adaptive smoothing is clearly useful for real hand tremor during precise aiming and is being kept as-is.\n",
    "- Adaptive smoothing is clearly useful for real hand tremor during precise aiming and is being kept as-is.\n"
    "- The optional right-stick gyro lock has been hardware-tested and feels responsive in gameplay.\n",
)
replace_once(
    "BUILD_STATUS.md",
    "- minimum Android window brightness override;\n",
    "- minimum Quest **system** brightness driven by the Windows ADB manager, with saved-value/mode restore and portable crash recovery;\n",
)
replace_once(
    "BUILD_STATUS.md",
    "- normal/system display brightness while passthrough is active;\n- passthrough paused and minimum brightness restored when the mode is disabled;\n",
    "- saved normal/system display brightness restored by the Windows ADB manager while passthrough is active;\n- passthrough paused and minimum system brightness restored when the mode is disabled;\n",
)
replace_once(
    "BUILD_STATUS.md",
    "## Architecture direction\n",
    "## Release polish in the current test branch\n\n"
    "Implemented and build-gated, with the new bidirectional lifecycle still awaiting the next Quest 3 hardware pass:\n\n"
    "- Windows Exit / Ctrl+C sends an explicit protocol shutdown request to the Quest app; ADB force-stop is retained only as the final lifecycle backstop.\n"
    "- Quest exit-chord completion carries an explicit final status flag that closes the Windows host, while accidental transport loss still reconnects.\n"
    "- a named Windows single-instance guard prevents two hosts from fighting over ADB forwarding, ViGEm and brightness ownership;\n"
    "- the Windows executables, tray and Quest launcher now share one QuestPad application mark.\n"
    "- Quest-side settings UI, user-selectable XR polling frequency and a manual session-restart menu were deliberately **not** added: the Windows tray is already the single control surface, ~72 Hz remains the validated low-load baseline, and normal watchdog/autostart recovery already covers the common restart case.\n\n"
    "## Architecture direction\n",
)

# The launcher/support claim remains generic: Quest 3 is hardware-validated; 3S is
# intentionally supported by model-agnostic Quest identity logic but not claimed as
# hardware-tested without a physical 3S.

# Remove this one-shot generator from the resulting commit.
Path(__file__).unlink()
print("session polish applied successfully")
