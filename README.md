# QuestPad

**Use Meta Quest Touch Plus controllers as a low-latency Windows gamepad — now with experimental native gyro and steering-wheel modes.**

QuestPad turns a Meta Quest 3 / Quest 3S into a lightweight controller bridge. A minimal native OpenXR application reads Touch Plus controls and forwards them over USB/ADB to a Windows host. The host can expose either a virtual Xbox 360/XInput controller or an experimental virtual DualShock 4 with native motion fields.

The project is designed around low latency, low Quest-side thermal load, and keeping ordinary gamepad use independent from VR rendering.

## Highlights

- Native Quest OpenXR app — no Unity runtime.
- Xbox 360/XInput output through ViGEmBus for maximum compatibility.
- Experimental DualShock 4 backend with native gyro reports.
- Two right-Touch gyro acquisition modes for A/B testing:
  - **Camera-assisted tracked pose** — requires optical positional tracking and derives angular rate from tracked orientation.
  - **Angular-rate only** — QuestPad consumes only OpenXR angular velocity; Windows does not consume optical pose data.
- Optional Windows-side adaptive gyro smoothing: Off / Light / Medium / Strong.
- Experimental two-controller steering-wheel modes: Mounted, Free-air, and Hybrid.
- ~72 Hz Quest sampling and USB/ADB transport with `TCP_NODELAY`.
- No scene rendering and zero submitted composition layers.
- Motion queries are **off when no motion feature requests them**.
- Sustained-low CPU/GPU hints and Android thermal telemetry.
- Controller battery display with OpenXR plus best-effort ADB fallback.
- Full gamepad surface: analog sticks/triggers, face buttons, shoulders, stick clicks, D-pad layer, Start/View/Guide.
- Genuine held Start/Menu and Back/View semantics.
- Game rumble bridged back to Touch Plus haptics.
- Tray-first Windows UI; separate console build for CLI/logs.
- Fail-safe neutralization on focus/transport loss.
- `LS + RS + LB + RB` 3-second QuestPad exit gesture with haptic countdown.

## Requirements

### Quest

- Meta Quest 3 or Quest 3S with Touch Plus controllers.
- Developer Mode / USB debugging enabled.
- QuestPad APK installed as a developer/unknown-source application.

### Windows

- Windows 10 or 11 x64.
- ADB available (`adb.exe`).
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) installed for the current Xbox 360 and DualShock 4 virtual-controller backends.

> ViGEmBus is archived upstream. QuestPad now keeps raw input, motion processing, logical controller state, and output backends separated so a future backend can replace it without redesigning the Quest transport.

## Quick start

1. Build or download `quest-debug.apk`, `QuestPad.Host.exe`, and `QuestPad.Host.Console.exe` from GitHub Actions.
2. Install/update the Quest app:

   ```powershell
   adb install -r .\quest-debug.apk
   ```

3. Start **QuestPad** from Developer / Unknown Sources on Quest.
4. Start `QuestPad.Host.exe` on Windows. The normal build is tray-only and opens no terminal window.
5. By default Windows gets the same virtual Xbox 360 controller as earlier QuestPad versions.

If `adb.exe` is not in `PATH`:

```powershell
.\QuestPad.Host.exe --adb "C:\path\to\adb.exe"
```

For logs and command-line testing use:

```powershell
.\QuestPad.Host.Console.exe --help
```

## Default controls

| Touch Plus | Xbox / logical gamepad |
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

The Meta/System button remains Horizon-owned.

When the DS4 backend is selected, the physical Touch controls remain in the same locations: A/B/X/Y become Cross/Circle/Square/Triangle respectively, grips become L1/R1, triggers become L2/R2, Menu becomes Options, View becomes Share and Guide becomes PS.

### Exit QuestPad

Hold **LS + RS + LB + RB** for **3 seconds**. `LB/RB` are the grip squeezes, not the index triggers. Haptic cues occur at 1 s, 2 s and confirmation at 3 s.

See [MAPPING.md](MAPPING.md) for modifier details.

## Native gyro experiment

Native gyro requires the **DualShock 4** output backend. Selecting either gyro source in the tray automatically switches to DS4. Selecting Xbox again turns gyro off.

Only the **right Touch Plus controller** is used for aiming motion.

### A — Camera-assisted tracked pose

Tray path:

`Gyro source (right Touch) -> Camera-assisted tracked pose`

QuestPad requests the right controller tracked pose. The Windows processor requires `POSITION_TRACKED=1`, then derives angular rate from successive tracked orientation samples. If the controller leaves the Quest cameras, the tray reports that this source is waiting for optical tracking instead of silently reusing stale motion.

This mode exists primarily as an A/B reference for accuracy and thermal testing.

### B — Angular-rate only

Tray path:

`Gyro source (right Touch) -> Angular-rate only (no optical data consumed)`

QuestPad requests only the controller angular-velocity path and sends that controller-local rate to Windows. The Windows host does not consume absolute controller orientation or position for gyro aiming.

**Important:** this is not raw physical MEMS access. Public OpenXR does not expose Touch Plus raw gyroscope samples. Horizon can still perform internal tracking/sensor fusion; QuestPad simply avoids requesting/using optical pose data in this mode.

### Horizon initialization quirk

On the Quest 3 used during development, after a full headset reboot Horizon had to see a Touch Plus controller with the cameras **once** before controller motion became valid. After that acquisition, angular velocity continued working while the controller stayed outside camera FOV and across QuestPad restarts.

This behaviour is runtime-dependent and is documented rather than hidden.

### Gyro smoothing

The tray exposes:

- Off — default; game receives the least processed QuestPad signal.
- Light
- Medium
- Strong

Filtering is performed on Windows with an adaptive One Euro-style filter, so it adds no Quest-side tracking workload. It is intended as an optional tremor/jitter aid. If a game already provides good native gyro steadiness/smoothing, leave QuestPad smoothing **Off** to avoid double filtering.

The DS4 gyro scale and axis signs are currently experimental and require real-game validation. Do not treat v0.3 motion calibration as final until it has been tested in multiple native-gyro games.

## Steering-wheel modes

Steering is experimental and currently outputs through the selected **gamepad backend** by replacing only **Left Stick X**. Buttons, triggers, grips, right stick, modifier controls and haptics remain available.

A future native HID wheel backend is deliberately left as a separate output layer; the current estimator does not depend on ViGEm/XInput internally.

### Mounted / rigid wheel

Intended for two Touch Plus controllers attached to a rigid ring, plate, cardboard wheel or 3D-printed frame.

1. Put the wheel at physical center.
2. Select `Steering wheel mode -> Mounted / rigid wheel`.
3. Click **Calibrate steering center**.
4. Turn a little left and right so QuestPad can learn the actual rotation axis of that particular mounting.

Mounted mode uses both controller orientations as the primary rigid-body signal. It compares their relative orientation to the calibrated relationship, rejects large tracking/mounting spikes, applies optional adaptive smoothing, and does not use untracked XYZ positions.

### Free-air optical wheel

This mode uses the line between the two controller positions as an imaginary steering wheel. It requires **both controllers to have `POSITION_TRACKED=1`**. If either loses optical tracking, free-air geometry is considered unavailable.

This makes it possible to place the Quest off-head — for example in front of the user or near a monitor — and use its cameras as a stationary Touch Plus tracker, provided Horizon keeps the XR session focused and can see the controllers.

### Hybrid

Hybrid uses optical two-hand geometry while both controllers are position-tracked and falls back to the mounted/orientation estimator when optical tracking disappears.

### Tracking safety

Quest/OpenXR may return `POSITION_VALID=1` even after `POSITION_TRACKED` becomes 0. QuestPad therefore follows a strict rule:

> **XYZ position is consumed only while `POSITION_TRACKED=1`; otherwise it is ignored completely.**

Brief tracking dropouts hold the last reliable wheel value instead of automatically centering the car. If the estimator remains unavailable, the physical left stick becomes the fallback steering input.

Steering range defaults to **240° total lock-to-lock** and the tray currently offers 180°, 240° and 360° presets. The CLI accepts 60..1080°.

## Thermal / display design

QuestPad remains intentionally not a VR renderer. The Quest application:

- requests the supported refresh rate nearest 72 Hz;
- submits **zero OpenXR composition layers**;
- creates no eye swapchains and renders no scene;
- requests sustained-low CPU/GPU performance levels when supported;
- asks Android for minimum window brightness on a best-effort basis;
- reports Android thermal status to Windows;
- performs controller motion `xrLocateSpace()` work only when the host requests a gyro/steering mode.

That last point makes `Gyro Off + Steering Off` the thermal baseline for comparison.

For meaningful A/B testing, compare similar-length sessions with:

1. motion Off;
2. Camera-assisted gyro;
3. Angular-rate-only gyro;
4. optionally steering with both controllers tracked.

Watch the tray thermal field and practical headset temperature/battery behaviour. The motion modes have not yet completed long thermal-soak validation.

## Windows tray

`QuestPad.Host.exe` runs without a console window. The tray now exposes:

- Quest connection and gamepad state;
- active Xbox/DS4 output backend;
- gyro source and validity;
- gyro smoothing;
- steering mode and estimator state;
- steering calibration/range;
- left/right controller battery and source;
- Quest thermal state;
- live input rate/drop count;
- Pause gamepad output;
- Exit QuestPad Host.

`QuestPad.Host.Console.exe` shares exactly the same processing/backend code and adds terminal output/CLI flags.

## Command-line options

```text
--adb PATH
--serial SERIAL
--output xbox|ds4
--gyro off|camera|rate
--gyro-smoothing off|light|medium|strong
--steering off|mounted|freeair|hybrid
--steering-range DEG
--steering-smoothing off|light|medium|strong
--no-gamepad
--no-adb
--no-tray
--help, -h
```

Examples:

```powershell
# Camera-assisted native gyro
.\QuestPad.Host.Console.exe --output ds4 --gyro camera

# Angular-rate-only native gyro with light tremor filtering
.\QuestPad.Host.Console.exe --gyro rate --gyro-smoothing light

# Mounted wheel, 240 degrees lock-to-lock
.\QuestPad.Host.Console.exe --steering mounted --steering-range 240
```

## Rumble

Game rumble continues to use the same reverse QFB1 stream:

- large motor -> left Touch Plus
- small motor -> right Touch Plus

Rumble is stopped on pause, focus loss, disconnect or exit.

## Controller battery telemetry

QuestPad prefers `XR_EXT_interaction_profile_battery_state_display`. When Horizon does not return valid OpenXR battery data, the Windows host uses a slow ADB-side fallback. The tray displays `OpenXR`, `ADB`, mixed fallback or `n/a`.

The fallback is isolated from the real-time input loop and polls slowly, so it does not block the ~72 Hz controller stream.

## Safety behavior

- OpenXR focus loss -> neutral virtual controller.
- USB/TCP loss or 250 ms packet watchdog -> neutral controller and reconnect.
- Exit chord suppresses its game inputs while armed and neutralizes before exit.
- Tray pause -> neutral output and rumble off while transport stays alive.
- Motion validity is explicit; stale gyro is never repeated as a valid sample.
- Optical steering position requires `POSITION_TRACKED=1`.
- Quest server remains loopback-only and is reached through ADB forwarding.

During development, Quest Guardian/boundary behaviour was observed to interfere with some deliberately off-head/out-of-boundary placement tests. That is separate from controller tracking itself. QuestPad does not depend on boundary data and does not attempt to alter the user's safety configuration.

## Architecture

```text
Touch Plus / OpenXR
        |
        v
Quest raw input + optional motion transport
        |
        v
Windows logical processing
  - ControllerMapper
  - MotionProcessor
  - SteeringEstimator
        |
        v
LogicalGamepadState / motion state
        |
        +--> Xbox360Backend
        +--> DualShock4Backend
        `--> future native HID wheel backend
```

This separation is intentional. A future HID wheel can consume the same logical steering state without coupling wheel estimation to ViGEm.

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

The separate `motionprobe/` application remains in the repository for low-level tracking experiments without gamepad emulation.

## Current status

The stable Xbox path has been hardware-tested on Quest 3 with ~71.9 Hz transport, reconnect/watchdog behaviour, real-game XInput control, Touch Plus rumble, and ADB battery telemetry.

Tracking probes have additionally confirmed on the development Quest that:

- controller angular velocity remains valid outside camera FOV after initial acquisition;
- optical `POSITION_TRACKED` can disappear while orientation/angular velocity remain tracked;
- off-head Quest placement can still provide optical controller tracking when controllers are visible;
- Android thermal status remained `NONE` in the observed probe runs.

**The integrated v0.3 DS4 gyro and steering backends are experimental and require real-game/hardware validation before they should be called stable.** In particular, DS4 gyro scale/axis calibration, game compatibility, wheel feel, and long thermal A/B runs are the next validation targets.

See [BUILD_STATUS.md](BUILD_STATUS.md), [MAPPING.md](MAPPING.md), and [PROTOCOL.md](PROTOCOL.md).

## License

MIT — see [LICENSE](LICENSE).

QuestPad is an independent project and is not affiliated with or endorsed by Meta Platforms, Microsoft, Sony Interactive Entertainment, or Nefarius Software Solutions.
