# QuestPad WIP v0.1

Quest 3 / Quest 3S Touch Plus -> native OpenXR -> USB ADB TCP bridge -> Windows virtual Xbox 360 controller.

This is deliberately built as a **low-load controller bridge**, not a VR renderer:

- Native C++ OpenXR app on Quest, no Unity.
- Explicitly requests the supported display refresh rate nearest 72 Hz.
- A tiny 16x16 EGL pbuffer exists only because the immersive OpenXR session requires a graphics binding.
- `xrEndFrame()` submits **zero composition layers**; QuestPad itself renders nothing.
- Requests minimum Android window brightness while the activity is alive (best-effort; Horizon/OpenXR may override panel behavior).
- When available, `XR_EXT_performance_settings` requests `SUSTAINED_LOW` for both CPU and GPU. `POWER_SAVINGS` is deliberately avoided because low-latency responsiveness matters more than shaving the last bit of power.
- No in-app controller UI: controller actions are only forwarded to the host.
- Losing OpenXR focus produces neutral controller packets.
- Deliberate exit chord: hold **both stick clicks + both grips** for 3 seconds. The chord is suppressed from the emulated controller while armed.
- Windows watchdog behavior is neutral-on-disconnect/reconnect.
- Windows output is a real ViGEm Xbox 360 virtual controller, not WASD/mouse emulation.

## Status

This is a first hardware-test build. The Windows diagnostic receiver is locally buildable and the repository includes CI for the Quest APK and the full Windows XInput host. The parts that still need validation on a real Quest 3 are called out below.

## Build without Visual Studio / Android Studio

The included GitHub Actions workflow builds both artifacts. Put this directory in a GitHub repository and run the `build` workflow. It produces:

- `quest-debug.apk`
- `QuestPad.Host.exe` (self-contained win-x64)

No Visual Studio is required on the gaming laptop.

## Install / first test

1. Install the Quest APK:

   ```powershell
   adb install -r .\quest-debug.apk
   ```

2. Install the official ViGEmBus driver on Windows if you want the Xbox 360 output. For transport-only testing, skip this and run the host with `--no-gamepad`.

3. Start QuestPad from Unknown Sources / Developer apps on the Quest.

4. Start the host:

   ```powershell
   .\QuestPad.Host.exe
   ```

   If MQDH's ADB is not in PATH:

   ```powershell
   .\QuestPad.Host.exe --adb "C:\path\to\adb.exe"
   ```

The host creates:

```text
adb forward tcp:38888 tcp:38888
```

and connects to `127.0.0.1:38888`.

## Mapping

| Touch Plus | Xbox 360 |
|---|---|
| left thumbstick | LS |
| right thumbstick | RS |
| left trigger | LT analog |
| right trigger | RT analog |
| left grip | LB |
| right grip | RB |
| A/B/X/Y | A/B/X/Y |
| thumbstick clicks | L3/R3 |
| left Menu | Start/Menu |

The Meta/system button is intentionally not consumed.

## Safety / failure behavior

- When OpenXR session focus is lost, QuestPad transmits a neutral state.
- On USB/TCP loss, the Windows host resets the virtual gamepad and reconnects.
- Exit chord sends a final neutral report before requesting session exit.
- TCP is loopback-only on the Quest; it is reached from Windows only through ADB forwarding.

## Thermal design

The current design minimizes load instead of attempting unsupported headset power hacks:

- no eye swapchains;
- no scene;
- no passthrough;
- no controller pose queries;
- no composition layers;
- 72 Hz default app cadence;
- low CPU/GPU performance hint when the runtime supports it;
- best-effort minimum window brightness;
- Android thermal status is included in every packet and printed by the host (`NONE`, `LIGHT`, `MODERATE`, ...).

A true display-power-off mode is intentionally **not** enabled in v0.1 because it may cause the immersive session to lose focus and stop controller input. It can be tested later as a separate experimental mode.

## Hardware validation checklist

Before calling this stable, test on the actual Quest 3 for at least 60 minutes:

- both sticks remain analog and centered correctly;
- A/B/X/Y, triggers, grips and thumb clicks remain active while the headset sits on a desk;
- controller input remains available when controller positional tracking is imperfect;
- thermal status stays `NONE`/`LIGHT` and the headset does not become uncomfortably hot;
- no packet stalls or recurrent ADB disconnects;
- virtual X360 controller remains present across game launches;
- focus-loss correctly neutralizes the controller;
- exit chord works and cannot be accidentally triggered during normal play.

## Logging

Quest:

```powershell
adb logcat -s QuestPad
```

Transport-only Windows diagnostic:

```powershell
QuestPad-Diagnostic-win64.exe --adb "C:\path\to\adb.exe"
```

The diagnostic is intentionally independent of .NET/ViGEm and exists to validate the Quest → USB/ADB → Windows input path before involving the virtual controller driver.

There is also a tiny Go diagnostic client in `diagnostic/` that can be cross-compiled without .NET.
