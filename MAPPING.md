# QuestPad controller mapping

QuestPad maps Touch Plus into a backend-independent logical gamepad state. That logical state can currently be emitted as Xbox 360/XInput or DualShock 4; steering mode can replace only the logical Left Stick X value while preserving every other control.

## Direct controls

| Touch Plus | Logical / Xbox | DualShock 4 |
|---|---|---|
| Left thumbstick | Left Stick | Left Stick |
| Right thumbstick | Right Stick | Right Stick |
| Left trigger | LT analog | L2 analog |
| Right trigger | RT analog | R2 analog |
| Left grip squeeze | LB | L1 |
| Right grip squeeze | RB | R1 |
| A | A | Cross |
| B | B | Circle |
| X | X | Square |
| Y | Y | Triangle |
| Left stick click | LS / L3 | L3 |
| Right stick click | RS / R3 | R3 |

Grip-to-shoulder conversion uses hysteresis: press at 0.62, release below 0.45.

## Menu modifier layer

The physical **left Menu** button is available to applications and acts as Start/Menu plus a modifier. The Meta/System button remains Horizon-owned.

| Gesture | Xbox 360 | DualShock 4 |
|---|---|---|
| Tap and release Menu | Start / Menu tap | Options tap |
| Hold Menu by itself for 0.50 s | Start / Menu held | Options held |
| Hold Menu + right stick | D-pad, including diagonals | D-pad, including diagonals |
| Menu + R3 | Back / View held while R3 remains held | Share held while R3 remains held |
| Hold Menu + LT + RT for 0.75 s | Guide | PS |

Modifier actions must begin before the 0.50-second plain-Menu hold commits. Right-stick camera output is suppressed while the stick is being used as D-pad. Menu + R3 becomes a genuine held View/Share state rather than a short pulse.

## Native gyro

Native gyro is available only on the DS4 backend because XInput has no motion fields.

The physical source is always the **right Touch Plus controller**. Two experimental acquisition paths are selectable:

- **Camera-assisted tracked pose**: requires optical `POSITION_TRACKED=1` and derives controller-local angular rate from successive tracked orientation samples.
- **Angular-rate only**: consumes the OpenXR angular-velocity stream and does not consume absolute controller pose on Windows.

The second mode must not be described as raw MEMS access: Horizon/OpenXR can still perform internal fusion. The purpose of the switch is to compare application-visible optical-pose dependence, accuracy, jitter and thermal behaviour.

Optional Windows-side adaptive smoothing is Off/Light/Medium/Strong. Off is the default because game-native motion filtering should be preferred when it is good enough.

## Steering-wheel mapping

Steering mode consumes both Touch Plus controllers and writes one logical field:

```text
estimated wheel angle -> Left Stick X
```

Everything else remains active: triggers, grips/shoulders, A/B/X/Y, right stick, stick clicks, Menu modifier controls and haptics.

### Calibration

With the wheel physically centered, choose **Calibrate steering center**. QuestPad records the relative controller orientations and then asks the user to turn left/right briefly so the estimator can learn the actual physical rotation axis instead of assuming a particular mounting angle.

### Mounted / rigid

For controllers fixed to a ring, plate, cardboard wheel or similar fixture. Both orientations are treated as redundant observations of one rigid body. The estimator checks whether the controllers preserve their calibrated relative orientation and rejects implausible sudden disagreement/tracking spikes.

Position data is not required for mounted steering.

### Free-air optical

Uses the line joining the two tracked controller XYZ positions as an imaginary wheel. It is valid only when **both controllers have `POSITION_TRACKED=1`**.

### Hybrid

Uses free-air optical geometry while both positions are tracked and falls back to orientation-based mounted steering when optical position disappears.

### Position validity rule

`POSITION_VALID` by itself is not sufficient. A runtime can retain a valid-but-not-currently-tracked position after optical loss. QuestPad therefore consumes controller XYZ **only while `POSITION_TRACKED=1`**; PT=0 position values are ignored completely.

### Dropouts

A short tracking dropout preserves the last reliable steering value instead of centering the car. If the estimator cannot recover within the short hold window, QuestPad falls back to the physical left stick.

The current wheel backend is intentionally a gamepad steering axis, not a native HID wheel. The estimator outputs a backend-independent logical steering value so a future HID wheel driver can reuse it without redesigning the tracking/fusion layer.

## Exiting QuestPad

Hold **LS + RS + LB + RB** for **3 seconds**. `LB/RB` here are the grip squeezes.

While the exit gesture is armed:

- LS, RS, LB and RB are suppressed from the virtual controller;
- both Touch Plus controllers pulse after 1 second;
- another cue occurs after 2 seconds;
- a stronger final confirmation occurs at 3 seconds;
- the virtual gamepad is neutralized before OpenXR exit is requested.

Releasing any part before 3 seconds cancels the sequence.

## Haptics

Virtual-controller rumble is bridged back to Touch Plus:

- large motor -> left controller
- small motor -> right controller

Exit countdown cues temporarily take priority. Rumble clears on pause, focus loss, disconnect and application exit.
