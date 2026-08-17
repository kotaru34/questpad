# QuestPad controller mapping

QuestPad maps Touch Plus input into a backend-independent logical gamepad. The logical state can currently be emitted as Xbox 360/XInput or DualShock 4. The recommended gyro path adds native DS4 motion without changing where the user's buttons, sticks or triggers are located.

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

Modifier actions must begin before the 0.50-second plain-Menu hold commits. Right-stick camera output is suppressed while the stick is being used as D-pad. Menu + R3 is a genuine held View/Share state rather than a short pulse.

## Native gyro

Native gyro is available only on the DS4 backend because XInput has no motion fields.

The physical source is always the **right Touch Plus controller**.

### Recommended: Angular-rate only

QuestPad consumes the OpenXR angular-velocity stream and sends controller-local angular rate to the DS4 motion report. Absolute controller orientation and XYZ position are not consumed by the Windows gyro processor.

This is not raw MEMS access: Horizon/OpenXR may still perform internal fusion.

### Diagnostic: Camera-assisted tracked pose

This path requires optical `POSITION_TRACKED=1` and derives angular rate from successive tracked orientation samples. Hardware A/B testing found it worse for aiming than Angular-rate-only, so it remains a diagnostic comparison mode rather than the recommended gameplay mapping.

### Smoothing

Optional adaptive smoothing is Off / Light / Medium / Strong and runs entirely on Windows. It does not change the physical control mapping. Hardware testing found it useful for hand tremor during precise micro-aim.

## Mounted steering experiment

QuestPad retains one limited steering experiment for two Touch controllers fixed to a rigid ring or plate. It is not intended as a replacement for a real multi-turn HID racing wheel and the old Free-air/Hybrid prototypes are no longer exposed in the normal UI.

When Mounted steering is enabled:

```text
estimated mounted-wheel angle -> logical Left Stick X
```

Everything else remains available: triggers, grips/shoulders, A/B/X/Y, right stick, stick clicks, Menu modifier controls and haptics.

### Arming and safety

1. Select `Mounted / rigid wheel (experimental)`.
2. Put the fixture at physical center.
3. Choose **Center + arm steering**.
4. Make the first deliberate learning turn to the **right** so the estimator establishes a deterministic positive axis.
5. Use **Invert steering direction** only if the particular fixture still behaves reversed.

Steering never keeps a stale non-zero LX after a tracking/geometry fault. Invalid, disarmed or clutch-open states explicitly output LX = 0.

A transient tracking problem enters a neutral safety hold. A persistent fault disarms the estimator and requires explicit Center + arm again. Large relative-controller geometry changes can disarm immediately.

### Optional light-grip clutch

The clutch requires both grip analog values to exceed a low threshold (~0.12) before steering is emitted. Releasing either hand immediately makes steering neutral without changing the rest of the logical gamepad.

The threshold is intentionally far below the LB/RB shoulder threshold, so the user can lightly hold the fixture without needing a full bumper-producing squeeze.

### Position validity

Mounted steering is primarily orientation-based. When optical position is used as an additional geometry check, XYZ is accepted only while `POSITION_TRACKED=1`; `POSITION_VALID=1` by itself is not treated as current optical tracking.

## Exiting QuestPad

Hold **LS + RS + LB + RB** for **3 seconds**. `LB/RB` here are grip squeezes.

While the exit gesture is armed:

- LS, RS, LB and RB are suppressed from the virtual controller;
- both Touch controllers pulse after 1 second;
- another cue occurs after 2 seconds;
- a stronger final confirmation occurs at 3 seconds;
- the virtual gamepad is neutralized before OpenXR exit is requested.

Releasing any part before 3 seconds cancels the sequence.

## Haptics

Virtual-controller rumble is bridged back to Touch Plus:

- large motor -> left controller
- small motor -> right controller

Exit countdown cues temporarily take priority. Rumble clears on pause, focus loss, disconnect and application exit.
