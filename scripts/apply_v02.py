from pathlib import Path
import shutil

cpp = Path('quest/src/main/cpp/questpad.cpp')
s = cpp.read_text(encoding='utf-8')

s = s.replace(
    "constexpr uint64_t kExitHoldNs = 3'000'000'000ULL;\n",
    "constexpr uint64_t kExitHoldNs = 3'000'000'000ULL;\n"
    "constexpr uint64_t kExitPulseNs = 125'000'000ULL;\n"
    "constexpr float kShoulderPressThreshold = 0.62f;\n"
    "constexpr float kShoulderReleaseThreshold = 0.45f;\n")

s = s.replace(
    "    uint64_t exitStartNs = 0;\n    int thermal = -1;\n",
    "    uint64_t exitStartNs = 0;\n"
    "    bool leftShoulderPressed = false;\n"
    "    bool rightShoulderPressed = false;\n"
    "    uint8_t exitPulseStage = 0;\n"
    "    uint8_t exitPulseStrength = 0;\n"
    "    uint64_t exitPulseUntilNs = 0;\n"
    "    int thermal = -1;\n")

needle = """            if (rg.isActive) packet.rg = std::clamp(rg.currentState, 0.0f, 1.0f);\n\n            auto pressed = [&](XrAction action) {\n"""
replacement = """            if (rg.isActive) packet.rg = std::clamp(rg.currentState, 0.0f, 1.0f);\n\n            // Treat the Touch Plus grip squeezes as the logical Xbox LB/RB buttons.\n            // Hysteresis matches the Windows mapper so the exit gesture works on the\n            // same comfortable squeeze used during normal gameplay instead of requiring\n            // an unusually hard >75% grip press.\n            auto updateShoulder = [](bool& latched, float value) {\n                if (latched) {\n                    if (value <= kShoulderReleaseThreshold) latched = false;\n                } else if (value >= kShoulderPressThreshold) {\n                    latched = true;\n                }\n            };\n            updateShoulder(leftShoulderPressed, packet.lg);\n            updateShoulder(rightShoulderPressed, packet.rg);\n\n            auto pressed = [&](XrAction action) {\n"""
if needle not in s:
    raise SystemExit('could not find grip/action insertion point')
s = s.replace(needle, replacement, 1)

old = """            const bool exitChord =\n                (packet.buttons & BTN_LTHUMB) && (packet.buttons & BTN_RTHUMB) &&\n                packet.lg > 0.75f && packet.rg > 0.75f;\n            if (exitChord) {\n                if (exitStartNs == 0) exitStartNs = packet.monotonicNs;\n                packet.flags |= FLAG_EXIT_ARMED;\n                // Do not leak the exit chord into the emulated controller.\n                packet.buttons &= ~(BTN_LTHUMB | BTN_RTHUMB);\n                packet.lg = 0.0f;\n                packet.rg = 0.0f;\n                if (packet.monotonicNs - exitStartNs >= kExitHoldNs && !exitRequested) {\n                    LOGI(\"exit chord held for 3 seconds; requesting exit\");\n                    // Make this and all subsequent packets neutral while the runtime\n                    // transitions through STOPPING -> EXITING.\n                    packet.lx = packet.ly = packet.rx = packet.ry = 0.0f;\n                    packet.lt = packet.rt = packet.lg = packet.rg = 0.0f;\n                    packet.buttons = 0;\n                    packet.flags &= ~FLAG_FOCUSED;\n                    exitRequested = true;\n                    const XrResult exitResult = xrRequestExitSession(session);\n                    if (XR_FAILED(exitResult)) {\n                        LOGW(\"xrRequestExitSession failed: %d\", exitResult);\n                        quit = true;\n                    }\n                }\n            } else {\n                exitStartNs = 0;\n            }\n"""
new = """            // Exit is expressed in Xbox terms: LS + RS + LB + RB for 3 s.\n            // LB/RB are the physical Touch Plus grip squeezes, normalized above with\n            // hysteresis. This keeps the gesture deliberate but comfortable.\n            const bool exitChord =\n                (packet.buttons & BTN_LTHUMB) && (packet.buttons & BTN_RTHUMB) &&\n                leftShoulderPressed && rightShoulderPressed;\n            if (exitChord) {\n                if (exitStartNs == 0) exitStartNs = packet.monotonicNs;\n                packet.flags |= FLAG_EXIT_ARMED;\n\n                // Do not leak LS/RS/LB/RB into the emulated controller while the exit\n                // gesture is armed.\n                packet.buttons &= ~(BTN_LTHUMB | BTN_RTHUMB);\n                packet.lg = 0.0f;\n                packet.rg = 0.0f;\n\n                const uint64_t heldNs = packet.monotonicNs - exitStartNs;\n                auto cueExitStage = [&](uint8_t stage, uint8_t strength) {\n                    if (exitPulseStage < stage) {\n                        exitPulseStage = stage;\n                        exitPulseStrength = strength;\n                        exitPulseUntilNs = packet.monotonicNs + kExitPulseNs;\n                    }\n                };\n                if (heldNs >= 1'000'000'000ULL) cueExitStage(1, 80);\n                if (heldNs >= 2'000'000'000ULL) cueExitStage(2, 150);\n                if (heldNs >= kExitHoldNs && !exitRequested) {\n                    cueExitStage(3, 255);\n                    LOGI(\"LS+RS+LB+RB held for 3 seconds; requesting exit\");\n                    packet.lx = packet.ly = packet.rx = packet.ry = 0.0f;\n                    packet.lt = packet.rt = packet.lg = packet.rg = 0.0f;\n                    packet.buttons = 0;\n                    packet.flags &= ~FLAG_FOCUSED;\n                    exitRequested = true;\n                    const XrResult exitResult = xrRequestExitSession(session);\n                    if (XR_FAILED(exitResult)) {\n                        LOGW(\"xrRequestExitSession failed: %d\", exitResult);\n                        quit = true;\n                    }\n                }\n            } else {\n                exitStartNs = 0;\n                exitPulseStage = 0;\n            }\n"""
if old not in s:
    raise SystemExit('could not find old exit chord block')
s = s.replace(old, new, 1)

old = """        if (!effectiveFocused || rumble == 0) {\n            if (lastRumble != 0) {\n                setHaptic(session, actions.lHaptic, 0);\n                setHaptic(session, actions.rHaptic, 0);\n            }\n            lastRumble = 0;\n            nextHapticRefresh = 0;\n        } else if (rumble != lastRumble || packet.monotonicNs >= nextHapticRefresh) {\n"""
new = """        const bool exitCueActive =\n            exitPulseStrength != 0 && packet.monotonicNs < exitPulseUntilNs;\n        if (exitCueActive) {\n            // Exit countdown feedback intentionally overrides game rumble for a very\n            // short pulse on both controllers: 1 s, 2 s, then confirmation at 3 s.\n            setHaptic(session, actions.lHaptic, exitPulseStrength);\n            setHaptic(session, actions.rHaptic, exitPulseStrength);\n            lastRumble = 0;\n            nextHapticRefresh = 0;\n        } else if (!effectiveFocused || rumble == 0) {\n            if (lastRumble != 0 || exitPulseStrength != 0) {\n                setHaptic(session, actions.lHaptic, 0);\n                setHaptic(session, actions.rHaptic, 0);\n            }\n            exitPulseStrength = 0;\n            lastRumble = 0;\n            nextHapticRefresh = 0;\n        } else if (rumble != lastRumble || packet.monotonicNs >= nextHapticRefresh) {\n"""
if old not in s:
    raise SystemExit('could not find haptic arbitration block')
s = s.replace(old, new, 1)

old = """        if (effectiveFocused && XR_SUCCEEDED(xrSyncActions(session, &sync))) {\n"""
new = """        if (!effectiveFocused) {\n            leftShoulderPressed = false;\n            rightShoulderPressed = false;\n            exitStartNs = 0;\n            exitPulseStage = 0;\n        }\n\n        if (effectiveFocused && XR_SUCCEEDED(xrSyncActions(session, &sync))) {\n"""
if old not in s:
    raise SystemExit('could not find focused action block')
s = s.replace(old, new, 1)
cpp.write_text(s, encoding='utf-8')

gradle = Path('quest/build.gradle')
g = gradle.read_text(encoding='utf-8')
g = g.replace("versionCode 1", "versionCode 2")
g = g.replace("versionName '0.1.0-wip'", "versionName '0.2.0'")
gradle.write_text(g, encoding='utf-8')

Path('README.md').write_text('''# QuestPad

**Use Meta Quest Touch Plus controllers as a low-latency Xbox-compatible gamepad on Windows.**

QuestPad turns a Meta Quest 3 / Quest 3S into a lightweight controller bridge. The headset runs a minimal native OpenXR application, reads the Touch Plus controls, and forwards them over USB/ADB to Windows. The Windows host exposes them as a real virtual Xbox 360/XInput controller, so games see a gamepad rather than keyboard/mouse emulation.

QuestPad is designed for situations where the Touch Plus form factor is useful outside VR, while keeping latency, thermal load, and accidental headset interaction as low as practical.

## Highlights

- Native Quest OpenXR app — no Unity runtime.
- Real Xbox 360/XInput device on Windows via ViGEmBus.
- ~72 Hz controller sampling on Quest.
- USB/ADB transport with `TCP_NODELAY`, reconnect, and watchdog safety.
- No scene rendering and zero submitted composition layers.
- Low CPU/GPU performance hints and thermal telemetry.
- Analog sticks and triggers, face buttons, shoulders, stick clicks, D-pad layer, Start/View/Guide.
- Xbox rumble bridged back to Touch Plus haptics.
- Focus loss and transport loss force a neutral controller state.
- Deliberate `LS + RS + LB + RB` 3-second exit gesture with haptic countdown cues.

## Requirements

### Quest

- Meta Quest 3 or Quest 3S with Touch Plus controllers.
- Developer Mode / USB debugging enabled.
- QuestPad APK installed as a developer/unknown-source application.

### Windows

- Windows 10 or 11 x64.
- ADB available (`adb.exe`).
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) installed for virtual Xbox 360 output.

> ViGEmBus is archived upstream, but remains a practical XInput backend. QuestPad keeps its controller mapping separated from transport so the output backend can be replaced later.

## Quick start

1. Build or download `quest-debug.apk` and `QuestPad.Host.exe` from the GitHub Actions build artifacts.
2. Install/update the Quest app:

   ```powershell
   adb install -r .\\quest-debug.apk
   ```

3. Start **QuestPad** from Developer / Unknown Sources on the headset.
4. Start the Windows host:

   ```powershell
   .\\QuestPad.Host.exe --adb "C:\\path\\to\\adb.exe"
   ```

   If `adb.exe` is already in `PATH`, simply run:

   ```powershell
   .\\QuestPad.Host.exe
   ```

5. Windows should now expose a virtual Xbox 360 controller. `joy.cpl` is a convenient way to verify it before launching a game.

The host automatically creates the ADB forward to `tcp:38888` and reconnects after temporary transport loss.

## Default controls

| Touch Plus | Xbox/XInput |
|---|---|
| Left stick | Left Stick |
| Right stick | Right Stick |
| Left trigger | LT |
| Right trigger | RT |
| Left grip | LB |
| Right grip | RB |
| X / Y | X / Y |
| A / B | A / B |
| Left stick click | LS / L3 |
| Right stick click | RS / R3 |
| Tap left Menu | Start / Menu |
| Hold Menu + right stick | D-pad (8 directions) |
| Menu + R3 | Back / View |
| Menu + LT + RT for 0.75 s | Guide |

The Meta/System button is intentionally left to Horizon OS.

### Exit QuestPad

Hold **LS + RS + LB + RB** for **3 seconds**.

On Touch Plus, `LB/RB` are the **grip squeezes**, not the index-finger triggers. QuestPad gives short haptic cues at 1 s and 2 s, then a stronger confirmation at 3 s before neutralizing the virtual controller and requesting OpenXR session exit.

See [MAPPING.md](MAPPING.md) for the complete mapping rationale and modifier behavior.

## Rumble

QuestPad forwards Xbox 360 rumble back to the Touch Plus controllers:

- large/low-frequency motor → left Touch Plus controller
- small/high-frequency motor → right Touch Plus controller

Rumble is stopped on focus loss, disconnect, or exit.

## Thermal / display design

QuestPad is intentionally not a VR renderer. The Quest application:

- requests the supported refresh rate nearest 72 Hz;
- submits **zero OpenXR composition layers**;
- does not create eye swapchains or render a scene;
- does not query controller poses for the gamepad bridge;
- requests sustained-low CPU/GPU performance levels when supported;
- asks Android for minimum window brightness on a best-effort basis;
- reports Android thermal status to the Windows host.

A true display-power-off mode is not enabled by default because Horizon may suspend or de-focus the immersive OpenXR session, which would stop controller input. The current approach prioritizes stable input while keeping application-side GPU load effectively minimal.

## Safety behavior

QuestPad is fail-safe by default:

- OpenXR focus loss → neutral gamepad state.
- USB/TCP loss or a 250 ms packet watchdog timeout → neutral gamepad state and reconnect.
- Exit gesture → LS/RS/LB/RB are suppressed while armed, then a final neutral state is sent before exit.
- Quest transport is bound to loopback and reached from Windows through ADB forwarding.

## Building

The repository includes a GitHub Actions workflow that builds both sides automatically.

### Quest APK

The Quest app is native C++/OpenXR with Gradle + CMake and targets `arm64-v8a`.

```bash
gradle :quest:assembleDebug
```

### Windows host

```powershell
dotnet publish host/QuestPad.Host.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

No Visual Studio or Android Studio is required when using the GitHub Actions artifacts.

## Diagnostics

Transport-only testing is available without ViGEm:

```powershell
.\\QuestPad.Host.exe --no-gamepad
```

The independent diagnostic client in [`diagnostic/`](diagnostic/) can also validate the Quest → ADB → Windows path.

Quest logs:

```powershell
adb logcat -s QuestPad
```

## Current status

QuestPad is an early public project. Real Quest 3 testing has confirmed:

- ~71.9 Hz transport cadence;
- zero observed packet drops in the initial transport test;
- automatic reconnect after transport loss;
- `thermal=NONE` during the initial observed run;
- successful virtual Xbox 360 creation;
- successful control of a real Windows game through XInput;
- working game rumble on Touch Plus controllers.

Longer thermal/transport soak testing and broader game compatibility testing are still in progress. See [BUILD_STATUS.md](BUILD_STATUS.md).

## Protocol

The input protocol and reverse haptic packet are documented in [PROTOCOL.md](PROTOCOL.md).

## License

MIT — see [LICENSE](LICENSE).

QuestPad is an independent project and is not affiliated with or endorsed by Meta Platforms, Microsoft, or Nefarius Software Solutions.
''', encoding='utf-8')

Path('MAPPING.md').write_text('''# QuestPad controller mapping

QuestPad exposes a complete Xbox 360/XInput control surface while keeping normal Touch Plus controls direct and synthetic controls ergonomic.

## Direct controls

| Touch Plus | Virtual Xbox 360 |
|---|---|
| Left thumbstick | Left Stick |
| Right thumbstick | Right Stick |
| Left trigger | LT (analog) |
| Right trigger | RT (analog) |
| Left grip squeeze | LB |
| Right grip squeeze | RB |
| A / B / X / Y | A / B / X / Y |
| Left stick click | LS / L3 |
| Right stick click | RS / R3 |

Grip-to-shoulder conversion uses hysteresis: press at 0.62, release below 0.45. This avoids button chatter without requiring an unnecessarily hard squeeze.

## Menu modifier layer

The physical **left Menu** button acts as a modifier because it is available to applications. The Meta/System button remains owned by Horizon OS.

| Gesture | Virtual Xbox 360 |
|---|---|
| Tap and release Menu | Start / Menu |
| Hold Menu + right stick ↑ | D-pad Up |
| Hold Menu + right stick ↓ | D-pad Down |
| Hold Menu + right stick ← | D-pad Left |
| Hold Menu + right stick → | D-pad Right |
| Hold Menu + right stick diagonally | matching D-pad diagonal |
| Hold Menu + R3 | Back / View |
| Hold Menu + LT + RT for 0.75 s | Guide |

### Why this layout

- Menu is on the left controller, leaving the right thumb free for the D-pad layer.
- Right-stick camera output is suppressed while Menu is held for D-pad use.
- Menu + R3 is a cross-hand chord and does not require one thumb to press two controls at once.
- Guide is intentionally deliberate because it is rarely needed during moment-to-moment gameplay.
- A plain Menu press becomes Start only after release and only if no modifier action was used.
- D-pad directions use hysteresis to avoid chatter around direction boundaries.

## Exiting QuestPad

Hold **LS + RS + LB + RB** for **3 seconds**.

Here `LB/RB` mean the physical **left/right grip squeezes**. The exit detector uses the same comfortable logical shoulder thresholds as normal gameplay rather than requiring a >75% squeeze.

While the exit gesture is armed:

- LS, RS, LB and RB are suppressed from the virtual controller;
- both Touch Plus controllers pulse after 1 second;
- a stronger pulse is sent after 2 seconds;
- a final confirmation pulse is sent at 3 seconds;
- the virtual gamepad is neutralized before the OpenXR session is asked to exit.

Releasing any part of the gesture before 3 seconds cancels the exit sequence.

## Touch Plus inputs intentionally left unused

Touch Plus exposes additional capacitive/touch-style signals that do not have direct equivalents on a standard Xbox 360 controller. Mapping normal finger-rest states to gameplay buttons would increase accidental input, so these signals are reserved for future optional profiles rather than enabled by default.

## Haptics

Xbox 360 rumble is bridged back to Touch Plus through OpenXR haptics:

- large motor → left controller
- small motor → right controller

Exit countdown cues temporarily take priority over game rumble. Rumble is cleared on focus loss, disconnect, and application exit.
''', encoding='utf-8')

Path('BUILD_STATUS.md').write_text('''# Project status

QuestPad is functional on real Quest 3 hardware and is currently in early public testing.

## Verified on hardware

- Native Quest OpenXR application builds and runs on Quest 3.
- Quest → USB/ADB → Windows transport works at approximately 72 Hz.
- Initial observed transport run reported `drops=0` and `thermal=NONE`.
- Host watchdog detects a closed transport and reconnects automatically.
- Windows host successfully creates an Xbox 360-compatible virtual controller through ViGEmBus.
- A real Windows game recognizes and accepts QuestPad as a game controller rather than keyboard/mouse emulation.
- Xbox rumble is successfully forwarded back to the Touch Plus controllers.

## Still being validated

- Long-duration (60+ minute) thermal and transport soak tests.
- Broad compatibility across different Windows games.
- Controller input behavior when headset/controller pose tracking quality degrades.
- Exit gesture and haptic countdown behavior across Horizon/OpenXR runtime states.
- Subjective and instrumented end-to-end input latency.

## Stability target

A stable release should sustain long gameplay sessions without stuck controls, recurrent ADB reconnects, unexpected thermal escalation, or perceptible latency regression compared with a conventional wireless gamepad.
''', encoding='utf-8')

shutil.rmtree('.bootstrap', ignore_errors=True)
for stale in [
    'NEXT_TEST.md',
    'scripts/patch-haptics.py',
    'scripts/patch-host-full-gamepad.py',
    '.github/workflows/apply-haptics.yml',
    '.github/workflows/apply-host-full-gamepad.yml',
    '.github/workflows/fix-feedback-race.yml',
    '.github/workflows/apply-v02-polish.yml',
    'scripts/apply_v02.py',
]:
    p = Path(stale)
    if p.exists():
        p.unlink()
