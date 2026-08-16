# QuestPad

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
- Controller battery display with OpenXR first and a best-effort ADB fallback for Horizon runtimes that do not expose OpenXR battery data yet.
- Analog sticks and triggers, face buttons, shoulders, stick clicks, D-pad layer, Start/View/Guide.
- Genuine held Start/Menu and Back/View semantics for games that distinguish tap from hold.
- Xbox rumble bridged back to Touch Plus haptics.
- Tray-first Windows UI with connection, gamepad, thermal, input-rate, drop-count and controller-battery status plus pause/exit controls.
- Separate console build for command-line flags, diagnostics and logs.
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

1. Build or download `quest-debug.apk`, `QuestPad.Host.exe`, and `QuestPad.Host.Console.exe` from the GitHub Actions artifacts.
2. Install/update the Quest app:

   ```powershell
   adb install -r .\quest-debug.apk
   ```

3. Start **QuestPad** from Developer / Unknown Sources on the headset.
4. Start `QuestPad.Host.exe` on Windows. The normal build is tray-only and does not open a terminal window.
5. Windows should now expose a virtual Xbox 360 controller. `joy.cpl` is a convenient way to verify it before launching a game.

If `adb.exe` is not in `PATH`, launch the host with an explicit path:

```powershell
.\QuestPad.Host.exe --adb "C:\path\to\adb.exe"
```

The tray build accepts the same command-line flags, but intentionally has no console output. For logs, diagnostics, scripting, or `--help`, use the console companion:

```powershell
.\QuestPad.Host.Console.exe --adb "C:\path\to\adb.exe"
```

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
| Tap left Menu | Start / Menu tap |
| Hold left Menu by itself for 0.50 s | Start / Menu held until release |
| Hold Menu + right stick | D-pad (8 directions) |
| Menu + R3 | Back / View; held while R3 remains held |
| Menu + LT + RT for 0.75 s | Guide |

The Meta/System button is intentionally left to Horizon OS.

For modifier gestures, begin the modifier action before the 0.50-second plain-Menu hold commits. Once a plain hold has committed to Start/Menu, it remains Start/Menu until release instead of changing modes unexpectedly.

### Exit QuestPad

Hold **LS + RS + LB + RB** for **3 seconds**.

On Touch Plus, `LB/RB` are the **grip squeezes**, not the index-finger triggers. QuestPad gives short haptic cues at 1 s and 2 s, then a stronger confirmation at 3 s before neutralizing the virtual controller and requesting OpenXR session exit.

See [MAPPING.md](MAPPING.md) for the complete mapping rationale and modifier behavior.

## Rumble

QuestPad forwards Xbox 360 rumble back to the Touch Plus controllers:

- large/low-frequency motor → left Touch Plus controller
- small/high-frequency motor → right Touch Plus controller

Rumble is stopped on focus loss, disconnect, or exit.

## Controller battery telemetry

QuestPad prefers the ratified `XR_EXT_interaction_profile_battery_state_display` OpenXR extension. If the installed Horizon runtime does not return valid battery data, the Windows host falls back to querying the paired controller status over the existing ADB connection.

The tray shows both controller percentages and the active source (`OpenXR`, `ADB`, or a mixed fallback). The ADB fallback is deliberately isolated from the real-time input loop and polls only every 10 seconds, so battery monitoring cannot add controller latency.

The ADB-side service is a Horizon implementation detail rather than a public API. If Meta changes or removes it in a future OS release, controller battery display may return to `n/a`; the gamepad bridge itself is unaffected.

## Motion / gyro potential

Touch Plus has tracked 6DoF controller poses. OpenXR exposes controller `grip/pose` and `aim/pose`, and tracked spaces can provide orientation, position, linear velocity and angular velocity. QuestPad does not currently forward motion data because the Xbox 360/XInput report itself has no gyro or accelerometer fields.

A future motion profile can use the right Touch Plus controller as the default gyro/aim source, use the left controller instead, or synthesize a two-hand virtual-gamepad frame from both tracked poses. The right-hand source is the simplest and most stable default; combining two independent controllers into one virtual rigid-body orientation is possible but necessarily more heuristic.

## Thermal / display design

QuestPad is intentionally not a VR renderer. The Quest application:

- requests the supported refresh rate nearest 72 Hz;
- submits **zero OpenXR composition layers**;
- does not create eye swapchains or render a scene;
- currently does not query controller poses for the gamepad bridge;
- requests sustained-low CPU/GPU performance levels when supported;
- asks Android for minimum window brightness on a best-effort basis;
- reports Android thermal status to the Windows host.

A true display-power-off mode is not enabled by default because Horizon may suspend or de-focus the immersive OpenXR session, which would stop controller input. The current approach prioritizes stable input while keeping application-side GPU load effectively minimal.

## Windows tray

`QuestPad.Host.exe` is the normal desktop build. It runs without a console window and exposes status through the Windows notification area:

- Quest transport connection;
- virtual gamepad active / paused / unavailable;
- left and right controller battery;
- battery telemetry source;
- Quest thermal state;
- live input rate and packet-drop count;
- **Pause gamepad output**;
- **Exit QuestPad Host**.

For terminal use, use `QuestPad.Host.Console.exe`. Both executables share the same controller, transport and safety code.

## Safety behavior

QuestPad is fail-safe by default:

- OpenXR focus loss → neutral gamepad state.
- USB/TCP loss or a 250 ms packet watchdog timeout → neutral gamepad state and reconnect.
- Exit gesture → LS/RS/LB/RB are suppressed while armed, then a final neutral state is sent before exit.
- Pausing from the Windows tray → neutral gamepad state and rumble off.
- Quest transport is bound to loopback and reached from Windows through ADB forwarding.

## Command-line options

```text
--adb PATH       explicit adb.exe path
--serial SERIAL  select a Quest when multiple Android devices are connected
--no-gamepad     transport/input diagnostics only; do not create XInput device
--no-adb         assume tcp:38888 is already reachable
--no-tray        disable the notification-area icon
--help, -h       show help
```

Use `QuestPad.Host.Console.exe` when command-line output is needed.

## Building

The repository includes a GitHub Actions workflow that builds both sides automatically.

### Quest APK

The Quest app is native C++/OpenXR with Gradle + CMake and targets `arm64-v8a`.

```bash
gradle :quest:assembleDebug
```

### Windows tray host

```powershell
dotnet publish host/QuestPad.Host.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Windows console host

```powershell
dotnet publish host/QuestPad.Host.Console.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

No Visual Studio or Android Studio is required when using the GitHub Actions artifacts.

## Diagnostics

For verbose transport testing without ViGEm:

```powershell
.\QuestPad.Host.Console.exe --no-gamepad
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
- working game rumble on Touch Plus controllers;
- controller battery telemetry through the ADB fallback when OpenXR battery state is unavailable.

Longer thermal/transport soak testing and broader game compatibility testing are still in progress. See [BUILD_STATUS.md](BUILD_STATUS.md).

## Protocol

The input protocol and reverse haptic packet are documented in [PROTOCOL.md](PROTOCOL.md).

## License

MIT — see [LICENSE](LICENSE).

QuestPad is an independent project and is not affiliated with or endorsed by Meta Platforms, Microsoft, or Nefarius Software Solutions.
