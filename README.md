# QuestPad

**Use Meta Quest Touch Plus controllers as a low-latency Windows gamepad, with native DS4 gyro and an optional mixed-reality passthrough view.**

QuestPad turns a Meta Quest 3 / Quest 3S into a lightweight controller bridge. A minimal native OpenXR application reads Touch Plus controls and forwards them over USB/ADB to a self-contained Windows host. Windows can expose either a virtual Xbox 360/XInput controller or a virtual DualShock 4 with native motion fields.

The design priorities are low latency, low Quest-side thermal load, reliable system-wide controller semantics, and keeping the normal gamepad path independent from VR scene rendering.

## Highlights

- Native Quest C++/OpenXR app — no Unity runtime.
- ~72 Hz Quest sampling and USB/ADB TCP transport with `TCP_NODELAY`.
- Xbox 360/XInput backend for broad game compatibility.
- DualShock 4 backend with native right-Touch gyro.
- **Angular-rate gyro is the recommended motion path** after extended real-hardware gameplay testing.
- Adaptive Windows-side gyro smoothing: Off / Light / Medium / Strong.
- Optional instant **right-stick gyro lock** with no time debounce; Quest motion acquisition stays active so gyro resumes on the next input frame.
- Two Quest view modes:
  - **Black / zero-layer** — default PC-only, minimum-workload mode.
  - **Passthrough / MR** — optional `XR_FB_passthrough` compositor layer so the physical room remains visible around other Quest windows.
- No eye swapchains and no rendered Quest scene in either mode.
- Motion queries are off when no motion feature requests them.
- Sustained-low CPU/GPU hints and Android thermal telemetry.
- Quest battery-temperature trend and controller battery telemetry in the Windows tray.
- Real held Start/Menu and View/Back semantics, D-pad modifier, Guide chord, rumble bridge and safe exit gesture.
- One deliberately limited **Mounted steering experiment** is retained, but QuestPad is not trying to replace a real multi-turn HID steering wheel.

## Requirements

### Quest

- Meta Quest 3 or Quest 3S with Touch Plus controllers.
- Developer Mode / USB debugging enabled.
- QuestPad APK installed as a developer/unknown-source application.

### Windows

- Windows 10 or 11 x64.
- `adb.exe` available.
- ViGEmBus installed for the current virtual Xbox 360 / DualShock 4 backends.

> ViGEmBus is archived upstream. QuestPad keeps raw input, processing and output backends separated so a future replacement does not require redesigning the Quest transport.

## Quick start

1. Install/update the Quest APK:

   ```powershell
   adb install -r .\quest-debug.apk
   ```

2. Start **QuestPad** from Developer / Unknown Sources on Quest.
3. Start `QuestPad.Host.exe` on Windows.
4. The default configuration is the familiar virtual Xbox 360 controller with gyro off and the Quest in black/zero-layer mode.

If ADB is not in `PATH`:

```powershell
.\QuestPad.Host.exe --adb "C:\path\to\adb.exe"
```

For a real terminal and diagnostics:

```powershell
.\QuestPad.Host.Console.exe --help
```

## Controls

| Touch Plus | Logical / Xbox control |
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
| Hold left Menu alone for 0.50 s | held Start / Menu |
| Menu + right stick | D-pad, including diagonals |
| Menu + R3 | held Back / View |
| Menu + LT + RT for 0.75 s | Guide |

The Meta/System button remains Horizon-owned.

On the DS4 backend the same physical locations map naturally to Cross/Circle/Square/Triangle, L1/R1, L2/R2, Options, Share and PS. See [MAPPING.md](MAPPING.md).

### Exit gesture

Hold **LS + RS + LB + RB** for **3 seconds**. `LB/RB` are the grip squeezes. Haptic cues occur at one second, two seconds and confirmation.

## Native gyro

Native motion requires the **DualShock 4** output backend because XInput has no gyro fields. Selecting a non-off gyro source automatically switches to DS4.

Only the **right Touch Plus controller** is used for aiming motion.

### Recommended: Angular-rate only

Tray:

`Gyro source (right Touch) -> Angular-rate only (recommended)`

QuestPad consumes the right controller's OpenXR angular-velocity stream and forwards controller-local angular rate to the DS4 motion report. Windows does not consume absolute controller orientation or position for this mode.

Extended Quest 3 gameplay testing found this path better for aiming than the camera-assisted derivative path. Adding optical tracked-pose derivation introduced extra noise and variability rather than improving control, while Angular-rate-only remained usable when the controller was outside headset camera FOV.

This is **not raw physical MEMS access**: public OpenXR does not expose the Touch Plus gyroscope directly, so Horizon may still perform internal sensor fusion.

### Diagnostic: Camera-assisted tracked pose

Tray:

`Gyro source (right Touch) -> Camera-assisted tracked pose (diagnostic A/B)`

This mode deliberately requires `POSITION_TRACKED=1` and derives angular rate from successive tracked orientations. It remains available for comparison/debugging, but is no longer the recommended gameplay path.

### Horizon initialization quirk

On the development Quest 3, after a full headset reboot Horizon must see a controller optically once before motion becomes valid. After that initial acquisition, angular-rate gyro has continued working outside camera FOV and across QuestPad restarts.

### Gyro smoothing

QuestPad provides Off / Light / Medium / Strong adaptive smoothing. It runs on Windows, so it does not add Quest-side tracking work. Hardware gameplay testing found it particularly useful for visible hand tremor during micro-aim.

If a game already has excellent native gyro filtering, `Off` avoids double filtering.

### Optional right-stick gyro lock

Tray:

`Lock gyro while using right stick`

This option is **off by default**. When enabled, QuestPad suppresses gyro in the same input frame that the raw right-stick magnitude crosses the engage threshold. There is no timer or debounce: the only added delay is the normal input cadence.

The detector is radial and uses hysteresis (`0.12` engage / `0.08` release) so ordinary stick drift near the mapper's deadzone cannot flap the lock state. While locked, QuestPad resets the Windows gyro smoothing state instead of accumulating hidden filtered motion.

The lock is host-side only. Quest continues to stream the requested angular-rate data, so releasing the stick can resume gyro on the next received input frame without an OpenXR mode transition or motion reacquisition.

CLI equivalent:

```powershell
.\QuestPad.Host.Console.exe --gyro rate --gyro-stick-lock on
```

## Quest view modes

The Quest view mode is independent from the controller backend and gyro mode.

### Black / zero-layer — PC-only default

Tray:

`Quest view -> Black / zero-layer (PC-only)`

QuestPad submits **zero OpenXR composition layers**, renders no scene, creates no eye swapchains and applies its minimum-brightness override. This remains the thermal/power baseline and is the preferred mode when the Quest is only being used as the controller bridge.

### Passthrough — MR mode

Tray:

`Quest view -> Passthrough (MR)`

QuestPad uses `XR_FB_passthrough` to submit a single full-room reconstruction passthrough compositor layer. It does **not** ask for raw camera frames and still renders no scene or eye swapchains. When enabled, QuestPad restores normal/system display brightness so the room is actually visible.

The hardened v0.3.2 lifecycle pre-creates the passthrough feature/layer during OpenXR initialization and keeps them paused until requested. Composition is submitted only while the runtime reports `shouldRender == XR_TRUE`. When passthrough is disabled — or the Windows bridge disconnects — the layer is paused and QuestPad returns to the zero-layer, minimum-brightness baseline.

The first MR implementation could make QuestPad appear for roughly a second and then close when passthrough was activated. The v0.3.2 diagnostic lifecycle/JNI hardening fixed that failure on the development Quest 3, and passthrough now activates and displays normally on hardware.

The Windows tray reports one of:

- `black / zero-layer`
- `passthrough starting / paused`
- `passthrough active`
- `passthrough unavailable`

### Mixed Reality Link / Windows App workflow

During development testing, Quest multitasking allowed PC virtual screens from Microsoft's Mixed Reality Link / Windows App workflow to remain visible while QuestPad stayed active as the controller bridge. Taking the PC-screen window out of controller focus allowed Touch Plus input to continue reaching QuestPad as a gamepad.

That makes Passthrough mode useful for a setup like:

```text
USB cable
  |- QuestPad ADB controller bridge
  |- optional gnirehtet-style network tunnel
  `- PC virtual-screen / MR workflow

Quest view
  |- physical room through passthrough
  `- floating PC screens from the separate MR app
```

QuestPad does not bundle, launch or configure Mixed Reality Link, Windows App or gnirehtet; they are independent components that can coexist with the bridge when the Quest runtime permits the multitasking arrangement.

## Thermal behaviour

QuestPad still asks the runtime for the supported refresh rate nearest 72 Hz and requests sustained-low CPU/GPU performance levels when available.

For useful comparisons, treat these as separate workloads:

1. Black / zero-layer + gyro Off — lowest-workload baseline.
2. Black / zero-layer + Angular-rate gyro.
3. Passthrough + gyro Off.
4. Passthrough + Angular-rate gyro.

Passthrough necessarily asks Horizon/compositor to produce the room view and uses normal display brightness, so it should be treated as a convenience/MR mode rather than the minimum-power mode. The tray exposes both Android thermal state and an ADB battery-temperature reading to make A/B testing easier.

## Mounted steering experiment

QuestPad retains one steering prototype for people who want to attach two Touch controllers to a rigid ring/plate. It replaces only logical Left Stick X while keeping triggers, buttons, right stick and haptics available.

It is intentionally **experimental and limited**:

- only the Mounted / rigid mode is user-facing;
- explicit `Center + arm steering` is required;
- tracking/geometry faults force LX to zero and persistent faults disarm the estimator;
- optional light-grip clutch and steering smoothing remain available;
- it is not intended to imitate a true multi-turn HID racing wheel.

There is no active plan to expand this into a full wheel subsystem. The code remains useful as an experiment without distracting from QuestPad's main gamepad/gyro goal.

## Windows tray

`QuestPad.Host.exe` is tray-first and opens no console window. The tray exposes:

- Quest connection and gamepad state;
- Quest view: Black / Passthrough;
- Xbox 360 / DS4 output backend;
- gyro source, validity and smoothing;
- optional right-stick gyro lock;
- limited mounted-steering experiment;
- controller batteries and source;
- Android thermal state and Quest battery temperature;
- live input rate/drop count;
- Pause output;
- Exit host.

## CLI

```text
--adb PATH
--serial SERIAL
--quest-view black|passthrough
--passthrough
--output xbox|ds4
--gyro off|rate|camera
--gyro-smoothing off|light|medium|strong
--gyro-stick-lock on|off
--steering off|mounted
--steering-range DEG
--steering-smoothing off|light|medium|strong
--steering-clutch on|off
--steering-invert on|off
--steering-arm
--no-gamepad
--no-adb
--no-tray
--help, -h
```

Examples:

```powershell
# Recommended native gyro + light tremor filtering
.\QuestPad.Host.Console.exe --gyro rate --gyro-smoothing light

# Recommended gyro, but suppress it instantly while manually using the right stick
.\QuestPad.Host.Console.exe --gyro rate --gyro-smoothing light --gyro-stick-lock on

# MR view with native gyro
.\QuestPad.Host.Console.exe --quest-view passthrough --gyro rate --gyro-smoothing light

# Lowest-workload baseline
.\QuestPad.Host.Console.exe --quest-view black --gyro off --output xbox
```

## Rumble and battery telemetry

Game rumble is returned over the same full-duplex TCP connection:

- large motor -> left Touch Plus
- small motor -> right Touch Plus

Rumble stops on pause, focus loss, disconnect or exit.

Controller battery percentage prefers `XR_EXT_interaction_profile_battery_state_display`; the Windows host uses a slow ADB/`OVRRemoteService` fallback when Horizon does not expose usable percentages. Battery polling is isolated from the real-time input loop.

## Safety behaviour

- OpenXR focus loss -> neutral virtual controller.
- USB/TCP loss or 250 ms watchdog -> neutral controller and reconnect.
- Host disconnect also removes the passthrough request, returning QuestPad to zero-layer mode.
- Tray pause -> neutral controller and rumble off while transport stays alive.
- Stale gyro is never repeated as a valid sample.
- Right-stick gyro lock suppresses output without stopping Quest motion acquisition and clears smoothing state while locked.
- Exit gesture neutralizes before requesting OpenXR exit.
- Quest TCP server remains loopback-only and is reached through ADB forwarding.

Quest Guardian/boundary behaviour can interfere with deliberately off-head/out-of-boundary experiments. QuestPad does not depend on boundary data and does not alter Guardian settings.

## Architecture

```text
Touch Plus / OpenXR
        |
        v
QuestPad Quest app
  - buttons/sticks/triggers
  - on-demand angular-rate motion
  - optional XR_FB_passthrough compositor layer
        |
        | USB / ADB TCP
        v
Windows logical processing
  - ControllerMapper
  - MotionProcessor + adaptive smoothing
  - optional host-side right-stick gyro lock
  - limited SteeringEstimator experiment
        |
        +--> Xbox360Backend
        `--> DualShock4Backend + native gyro
```

Passthrough is a Quest compositor/view concern; it does not change the controller packet cadence or virtual-controller mapping. The right-stick gyro lock is entirely host-side and likewise does not change protocol v2.

## Building

Quest APK:

```bash
gradle :quest:assembleDebug
```

Windows tray host:

```powershell
dotnet publish host/QuestPad.Host.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Windows console host:

```powershell
dotnet publish host/QuestPad.Host.Console.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

No Visual Studio or Android Studio is required when using GitHub Actions artifacts.

## Diagnostics

```powershell
.\QuestPad.Host.Console.exe --no-gamepad
adb logcat -s QuestPad
```

See [PROTOCOL.md](PROTOCOL.md), [MAPPING.md](MAPPING.md), and [BUILD_STATUS.md](BUILD_STATUS.md).

## License

MIT — see [LICENSE](LICENSE).

QuestPad is an independent project and is not affiliated with or endorsed by Meta Platforms, Microsoft, Sony Interactive Entertainment, or Nefarius Software Solutions.
