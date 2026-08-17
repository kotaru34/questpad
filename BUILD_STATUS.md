# Project status

QuestPad is functional on real Quest 3 hardware and is in early public testing. The original Xbox/XInput path remains the stable baseline. Native gyro is now hardware-tested but still needs broader game validation; steering remains explicitly experimental and is not release-ready yet.

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

## Verified by motion/placement testing

- After a full Quest reboot, Horizon on the development headset needs to see the controller optically once before motion becomes valid.
- After that initial acquisition, controller angular velocity remains valid while Touch Plus is entirely outside camera FOV.
- Orientation/angular velocity can remain tracked while optical position tracking is lost (`PT=0`).
- `PV=1` may remain set while `PT=0`; production steering therefore trusts XYZ position **only** while `PT=1`.
- Motion tracking can become temporarily inactive while a controller is idle and reacquire when it moves again.
- The Quest can be placed off-head (for example near a desk/monitor) and still track visible controllers; the XR session remained focused in the observed tests.
- Guardian/boundary behaviour interfered with one deliberately out-of-boundary placement test. QuestPad itself does not depend on boundary data.
- Android thermal status remained `NONE` throughout the observed probe runs.

## Integrated gyro hardware results

- Virtual DS4 native gyro is accepted by the tested game path.
- Both gyro acquisition modes function on the development Quest.
- **Angular-rate-only is the preferred/default recommendation:** in direct A/B use it was subjectively more accurate/useful than the camera-assisted derivative path.
- Camera-assisted gyro remains available only as an experimental diagnostic/A-B reference.
- Optional adaptive gyro smoothing (Off / Light / Medium / Strong) works well in real aiming and materially helps hand tremor without changing Quest-side tracking workload.
- Broader native-motion game compatibility and final DS4 axis/scale validation are still pending.

## Steering-wheel implementation

Implemented:

- Mounted / rigid, Free-air optical, and Hybrid tracking modes.
- Both-controller rigid-body fusion with loose-mount/outlier handling.
- Position data consumed only while `POSITION_TRACKED=1`.
- Learned physical wheel axis after center calibration.
- Gamepad steering currently maps to Left Stick X while all other controls stay available.
- Output separation leaves room for a future native HID steering-wheel backend.

Current safety patch, awaiting hardware re-test:

- Steering is explicitly **disarmed** until `Center + arm steering` is used.
- A tracking/geometry fault immediately outputs neutral `LX=0`; a fault lasting more than 250 ms permanently disarms steering until the user re-centers/re-arms it.
- Large relative-controller orientation changes or rigid-wheel spacing changes disarm immediately instead of trying to continue from a broken physical configuration.
- Steering no longer falls back to a potentially non-zero physical LX while wheel mode is enabled: invalid/disarmed state is always neutral.
- Optional light-grip clutch gates steering output to zero when either controller is not lightly held. The clutch threshold is intentionally below the LB/RB shoulder threshold.
- Steering direction now has an explicit invert toggle, and calibration instructs the user to make the first deliberate learning turn to the **right** so the learned axis sign is deterministic.

Steering is still **experimental and should not be presented as a stable release feature** until this fail-safe behaviour is validated with controller removal, putting the wheel down, tracking loss, and desktop/Steam Input scenarios.

## Next validation targets

- Re-test steering safety: remove one/both controllers, put the wheel down, leave/re-enter tracking, and verify LX immediately returns to zero and persistent faults disarm.
- Validate optional light-grip clutch ergonomics and threshold without accidental LB/RB presses.
- Confirm steering direction after `Center + arm` + first turn RIGHT; use the invert toggle if a particular mounting still needs reversal.
- Compare motion Off vs Camera-assisted vs Angular-rate-only for battery temperature, Android thermal status, precision and latency over longer sessions.
- Validate native gyro in additional PC games and tune DS4 scale/axis mapping only if real-game evidence requires it.
- Long-duration (60+ minute) transport/thermal soak.
- Design/implement an optional native HID steering-wheel backend rather than treating gamepad LX steering as the final wheel output.

## Stability target

A stable release should sustain long gameplay sessions without stuck controls, recurrent ADB reconnects, unexpected thermal escalation, or perceptible latency regression compared with a conventional gamepad. Motion features must not degrade the no-motion Xbox baseline when disabled, and experimental steering must fail neutral rather than emit stale non-zero input.
