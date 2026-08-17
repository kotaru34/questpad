# Project status

QuestPad is functional on real Quest 3 hardware and is in active public testing. The original Xbox/XInput gamepad path remains the stable baseline. Right-Touch DS4 gyro has completed extended real-game validation, with Angular-rate-only established as the preferred gameplay path. The v0.3.2 diagnostic passthrough hardening also fixed the Quest app exit that occurred when MR mode was first enabled; passthrough now activates and displays normally on hardware.

## Verified on hardware — baseline

- Native Quest C++/OpenXR application runs on Quest 3.
- Quest -> USB/ADB -> Windows transport sustains approximately 72 Hz.
- Initial long-enough observations reported `drops=0` and Android thermal `NONE`.
- Host watchdog detects transport loss and reconnects automatically.
- Windows creates an Xbox 360-compatible virtual controller through ViGEmBus.
- Real Windows games recognize and accept QuestPad as a controller.
- Game rumble is forwarded to Touch Plus haptics.
- Tray-first Windows host runs without a terminal; a separate console build retains CLI/logging.
- Controller battery percentage works through the ADB fallback when the OpenXR battery extension returns no usable value.
- Quest battery temperature is available as a slow ADB-side trend in the tray.
- Held Start/Menu and held View/Back semantics work correctly.
- The 3-second exit chord now closes the Quest NativeActivity cleanly without the previous Android ANR.
- Windows ADB selection correctly identifies the real Quest 3 instead of a phone, and Windows-host startup can launch the Quest APK automatically.
- Black-mode system brightness control and exact restore on MR/exit are verified on the Quest 3.
- Portable Windows settings persist next to the executable.

## Verified motion behaviour

- After a full Quest reboot, Horizon on the development headset needs to see a controller optically once before motion becomes valid.
- After that acquisition, controller angular velocity remains valid with the Touch controller entirely outside camera FOV.
- Angular velocity survives QuestPad app restarts without another optical acquisition until the headset is rebooted.
- Controller tracking can become temporarily inactive while idle and reacquire when the controller moves again.
- Orientation/angular velocity can remain tracked while optical position tracking is lost (`PT=0`).
- `PV=1` can remain set while `PT=0`; QuestPad therefore treats XYZ as trustworthy only while `PT=1`.
- Off-head Quest placement can still provide optical controller tracking when the cameras can see the controllers.
- Guardian/boundary behaviour interfered with one deliberately out-of-boundary placement test; QuestPad does not depend on boundary data.

## Gyro validation

Implemented:

- DS4 extended-report native gyro fields sourced from the right Touch controller.
- Recommended Angular-rate-only path using controller-local OpenXR angular velocity.
- Camera-assisted tracked-pose derivative retained only as a diagnostic A/B path.
- Adaptive Windows-side smoothing: Off / Light / Medium / Strong.
- Optional host-side right-stick gyro lock with immediate frame-level engage/release, radial `0.12 / 0.08` hysteresis and no time debounce.
- Stick lock keeps Quest angular-rate acquisition running and resets the Windows gyro smoothing state while locked, so unlock can resume on the next input frame without a stale-filter kick.

Observed on hardware/gameplay:

- Both integrated gyro modes produce usable motion input.
- Extended gameplay sessions confirm Angular-rate-only is the better aiming path; adding optical tracked-pose derivation introduces extra noise/variability rather than improving control.
- Adaptive smoothing is clearly useful for real hand tremor during precise aiming and is being kept as-is.
- The optional right-stick gyro lock has been hardware-tested and feels responsive in gameplay.
- Angular-rate-only remains usable outside Quest camera FOV after Horizon's one-time post-reboot acquisition.

Still worth validating more broadly:

- Hardware feel of the new right-stick gyro lock across games that mix stick and gyro aiming.
- DS4 gyro scale/sign behaviour across several native-motion PC games.
- Long-session latency and thermal comparison with gyro Off.
- Per-game interaction with games that already apply their own gyro filtering.

## Quest view modes

### Black / zero-layer

This is the established default and thermal baseline:

- zero submitted OpenXR composition layers;
- no eye swapchains or rendered scene;
- minimum Quest **system** brightness driven by the Windows ADB manager, with saved-value/mode restore and portable crash recovery;
- motion acquisition only when the host requests it.

### Passthrough / MR — activation verified on Quest 3

The passthrough path uses optional `XR_FB_passthrough` support:

- host-controlled `Black / zero-layer` vs `Passthrough / MR` toggle;
- passthrough feature/layer objects are pre-created during initialization and kept paused until requested, matching the hardened v0.3.2 diagnostic lifecycle;
- one compositor passthrough layer when active, still with no eye swapchains or Quest-rendered scene;
- passthrough composition is omitted when OpenXR reports `shouldRender == XR_FALSE`;
- no raw camera-frame API or camera permission;
- saved normal/system display brightness restored by the Windows ADB manager while passthrough is active;
- passthrough paused and minimum system brightness restored when the mode is disabled;
- host disconnect clears the passthrough request and returns QuestPad to zero-layer mode;
- availability/active state reported back to the Windows tray through unused protocol-v2 flag bits.

Hardware result:

- the earlier failure where enabling MR made QuestPad appear briefly and then close is fixed by the v0.3.2 diagnostic lifecycle hardening;
- passthrough now activates and displays normally on the Quest 3 development headset.

Remaining soak/interaction targets:

- toggle repeatedly between Black and Passthrough over long sessions;
- verify coexistence/focus transitions with the Mixed Reality Link / Windows App multitasking workflow;
- compare Android thermal state, battery temperature and practical headset warmth between modes;
- confirm Windows-host disconnect always returns the Quest app to black/zero-layer mode during extended use.

## Steering decision

QuestPad is no longer pursuing a full steering-wheel subsystem. A real multi-turn HID wheel is outside the useful scope of the project and one-controller gyro already covers lightweight motion steering well.

One **Mounted / rigid steering experiment** is retained for curiosity/testing:

- two-controller rigid-body estimator;
- explicit Center + arm;
- deterministic first-turn direction plus optional inversion;
- hard LX=0 on invalid/disarmed states;
- tracking/geometry safety disarm;
- optional light-grip clutch;
- Windows-side steering smoothing.

The old Free-air and Hybrid prototypes remain internal legacy code for now but are no longer exposed in the normal tray or CLI and are not an active development target.

## Release polish in the current test branch

Hardware-verified on Quest 3:

- Windows Exit / Ctrl+C sends an explicit protocol shutdown request and closes the Quest app cleanly; ADB force-stop remains only the final lifecycle backstop.
- Quest exit-chord completion carries an explicit final status flag and closes the Windows host too.
- The same bidirectional shutdown behaviour works while MR passthrough is active.
- the named Windows single-instance guard correctly rejects a second host instance without disturbing the active bridge;

New v0.3.7 recovery candidate, build-gated and awaiting the focused unplug/replug hardware retest:

- unexpected USB/ADB loss still neutralizes output and **does not** quit either side;
- the host now waits for the same selected Quest ADB serial instead of blindly retrying a stale localhost forward;
- when that Quest returns, the host removes/recreates `tcp:38888` and retries the existing Quest bridge first;
- the Quest app is relaunched only when autostart is enabled **and** its process is no longer running, avoiding unnecessary OpenXR-session restarts on ordinary cable reconnects.
- the Windows executables, tray and Quest launcher now share one QuestPad application mark.
- Quest-side settings UI, user-selectable XR polling frequency and a manual session-restart menu were deliberately **not** added: the Windows tray is already the single control surface, ~72 Hz remains the validated low-load baseline, and normal watchdog/autostart recovery already covers the common restart case.

## Architecture direction

Current priorities are now:

1. Preserve and soak-test the stable Xbox/gamepad baseline.
2. Keep Angular-rate DS4 gyro as the production motion path and polish gameplay ergonomics such as the optional right-stick lock.
3. Soak-test the now-functional MR passthrough mode without compromising the zero-layer baseline.
4. Keep the mounted steering prototype isolated and experimental rather than expanding it.

## Stability target

A stable release should sustain long gameplay sessions without stuck controls, recurrent ADB reconnects, unexpected thermal escalation, or perceptible latency regression compared with a conventional gamepad. Optional motion/MR features must not degrade the no-motion, zero-layer Xbox baseline when disabled.
