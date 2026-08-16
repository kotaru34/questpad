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
- Low CPU/GPU performance hints, thermal telemetry, and controller battery display when supported by the runtime.
- Analog sticks and triggers, face buttons, shoulders, stick clicks, D-pad layer, Start/View/Guide.
- Xbox rumble bridged back to Touch Plus haptics.
- Windows notification-area tray with connection, thermal, input-rate and controller-battery status plus pause/exit controls.
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
   adb install -r .\quest-debug.apk
   ```

3. Start **QuestPad** from Developer / Unknown Sources on the headset.
4. Start the Windows host:

   ```powershell
   .\QuestPad.Host.exe --adb "C:\path\to\adb.exe"
   ```

   If `adb.exe` is already in `PATH`, simply run:

   ```powershell
   .\QuestPad.Host.exe
   ```

5. Windows should now expose a virtual Xbox 360 controller. `joy.cpl` is a convenient way to verify it before launching a game.

The host automatically creates the ADB forward to `tcp:38888` and reconnects after temporary transport loss. A tray icon is enabled by default; use `--no-tray` for console-only operation.

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
.\QuestPad.Host.exe --no-gamepad
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

Controller battery display uses the optional ratified OpenXR battery-state extension and therefore appears as `n/a` if the installed Quest runtime does not expose it. Longer thermal/transport soak testing and broader game compatibility testing are still in progress. See [BUILD_STATUS.md](BUILD_STATUS.md).

## Protocol

The input protocol and reverse haptic packet are documented in [PROTOCOL.md](PROTOCOL.md).

## License

MIT — see [LICENSE](LICENSE).

QuestPad is an independent project and is not affiliated with or endorsed by Meta Platforms, Microsoft, or Nefarius Software Solutions.
