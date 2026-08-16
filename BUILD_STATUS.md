# Project status

QuestPad is functional on real Quest 3 hardware and is in early public testing. The original Xbox/XInput gamepad path is the stable baseline; v0.3 motion and steering features are experimental until their integrated backends complete hardware/game validation.

## Verified on hardware — baseline

- Native Quest OpenXR application builds and runs on Quest 3.
- Quest -> USB/ADB -> Windows transport works at approximately 72 Hz.
- Initial observed transport run reported `drops=0` and `thermal=NONE`.
- Host watchdog detects transport loss and reconnects automatically.
- Windows host creates an Xbox 360-compatible virtual controller through ViGEmBus.
- A real Windows game recognizes and accepts QuestPad as a controller.
- Game rumble is forwarded back to Touch Plus haptics.
- Tray-first Windows host runs without a console window; a separate console build retains CLI/logging.
- Controller battery percentage works on the development Quest through the ADB fallback when the OpenXR battery extension returns no usable value.
- Held Start/Menu and held View/Back semantics work in the mapper rather than being pulse-only.

## Verified by dedicated motion/placement probes

- After a full Quest reboot, Horizon on the development headset needs to see the controller optically once before motion becomes valid.
- After that initial acquisition, controller angular velocity remains valid while Touch Plus is entirely outside camera FOV.
- Orientation/angular velocity can remain tracked while optical position tracking is lost (`PT=0`).
- `PV=1` may remain set while `PT=0`; therefore production steering code treats XYZ position as trustworthy **only** while `PT=1`.
- Motion tracking can become temporarily inactive while a controller is idle and reacquire when it moves again.
- The Quest can be placed off-head (for example near a desk/monitor) and still track visible controllers; the XR session remained focused in the observed tests.
- Guardian/boundary behaviour interfered with one deliberately out-of-boundary placement test. QuestPad itself does not depend on boundary data.
- Android thermal status remained `NONE` throughout the observed probe runs.

## Implemented in v0.3, awaiting integrated hardware validation

- Protocol v2 motion transport with host-controlled, on-demand motion acquisition.
- Xbox 360 and DualShock 4 output backends behind a logical controller layer.
- Native DS4 extended-report gyro fields sourced from the right Touch Plus controller.
- Gyro A/B selector:
  - camera-assisted tracked-pose derivative, deliberately requiring `PT=1`;
  - OpenXR angular-rate-only path with no optical pose data consumed by the Windows host.
- Optional adaptive gyro smoothing: Off / Light / Medium / Strong.
- Steering-wheel estimator using both controllers:
  - Mounted / rigid;
  - Free-air optical;
  - Hybrid optical/orientation fallback.
- Steering center calibration and learned physical rotation axis.
- Optical position accepted only while both relevant `POSITION_TRACKED` flags are valid.
- Short steering tracking dropouts hold the last reliable wheel state rather than auto-centering; longer invalid periods fall back to the physical left stick.
- Steering changes only Left Stick X, so buttons, triggers, right stick, modifier controls and haptics remain available.
- Output/backend separation deliberately leaves room for a future native HID steering-wheel driver.

## Next validation targets

- Confirm DS4 gyro is recognized by Strinova and other native-motion PC games.
- Validate and, if necessary, tune DS4 gyro axis signs and sensor scaling.
- Compare Camera-assisted vs Angular-rate-only gyro for precision, jitter, occlusion behaviour and subjective latency.
- A/B/C thermal soak: motion Off vs Camera-assisted vs Angular-rate-only under otherwise comparable conditions.
- Test gyro smoothing with deliberate micro-aim/tremor and measure the latency/steadiness tradeoff.
- Validate Mounted, Free-air and Hybrid steering with real two-hand movement and, later, a rigid ring/plate fixture.
- Tune mounting-slip/outlier thresholds from real wheel data rather than synthetic assumptions.
- Long-duration (60+ minute) transport/thermal soak.
- Broader Windows game compatibility and end-to-end latency measurements.

## Stability target

A stable release should sustain long gameplay sessions without stuck controls, recurrent ADB reconnects, unexpected thermal escalation, or perceptible latency regression compared with a conventional gamepad. Motion features should not degrade the no-motion Xbox baseline when disabled.
