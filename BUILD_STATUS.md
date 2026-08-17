# Project status

QuestPad is functional on real Quest 3 hardware and is in active public testing. The original Xbox/XInput gamepad path remains the stable baseline. Right-Touch DS4 gyro has now completed useful real-game A/B testing, while the new MR passthrough view needs integrated hardware validation.

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

Observed on hardware/gameplay:

- Both integrated gyro modes produce usable motion input.
- Angular-rate-only feels better/more accurate than the camera-assisted path and is now the recommended mode.
- Adaptive smoothing is clearly useful for real hand tremor during precise aiming and is being kept as-is.
- Angular-rate-only remains usable outside Quest camera FOV after Horizon's one-time post-reboot acquisition.

Still worth validating more broadly:

- DS4 gyro scale/sign behaviour across several native-motion PC games.
- Long-session latency and thermal comparison with gyro Off.
- Per-game interaction with games that already apply their own gyro filtering.

## Quest view modes

### Black / zero-layer

This is the established default and thermal baseline:

- zero submitted OpenXR composition layers;
- no eye swapchains or rendered scene;
- minimum Android window brightness override;
- motion acquisition only when the host requests it.

### Passthrough / MR — implemented, awaiting hardware test

v0.3.1 adds optional `XR_FB_passthrough` support:

- host-controlled `Black / zero-layer` vs `Passthrough / MR` toggle;
- lazy creation of a reconstruction passthrough feature/layer;
- one compositor passthrough layer when active, still with no eye swapchains or Quest-rendered scene;
- no raw camera-frame API or camera permission;
- normal/system display brightness while passthrough is active;
- passthrough paused and minimum brightness restored when the mode is disabled;
- host disconnect clears the passthrough request and returns QuestPad to zero-layer mode;
- availability/active state reported back to the Windows tray through unused protocol-v2 flag bits.

Hardware validation targets:

- confirm the full-room passthrough layer displays correctly on Quest 3;
- toggle repeatedly between Black and Passthrough without losing controller focus/transport;
- verify coexistence with the user's Mixed Reality Link / Windows App multitasking workflow;
- compare Android thermal state, battery temperature and practical headset warmth between modes;
- confirm Windows-host disconnect always returns the Quest app to black/zero-layer mode.

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

## Architecture direction

Current priorities are now:

1. Preserve and soak-test the stable Xbox/gamepad baseline.
2. Polish Angular-rate DS4 gyro and smoothing.
3. Validate the optional MR passthrough view without compromising the zero-layer baseline.
4. Keep the mounted steering prototype isolated and experimental rather than expanding it.

## Stability target

A stable release should sustain long gameplay sessions without stuck controls, recurrent ADB reconnects, unexpected thermal escalation, or perceptible latency regression compared with a conventional gamepad. Optional motion/MR features must not degrade the no-motion, zero-layer Xbox baseline when disabled.
