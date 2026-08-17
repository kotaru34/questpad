# QuestPad controller mapping

QuestPad maps Touch Plus into a backend-independent logical gamepad state. That logical state can currently be emitted as Xbox 360/XInput or DualShock 4. Experimental steering can replace only logical Left Stick X while preserving every other control.

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

Native gyro is available only on the DS4 backend because XInput has no motion fields. The physical source is always the **right Touch Plus controller**.

- **Angular-rate only — recommended:** consumes the OpenXR angular-velocity stream and does not consume absolute controller pose on Windows. Real A/B testing on the development Quest found this path better for aiming than the camera-assisted derivative path.
- **Camera-assisted tracked pose — experimental A/B:** requires optical `POSITION_TRACKED=1` and derives controller-local angular rate from successive tracked orientation samples.

Angular-rate-only must not be described as raw MEMS access: Horizon/OpenXR can still perform internal fusion. The switch compares application-visible optical-pose dependence, accuracy, jitter and thermal behaviour.

Optional Windows-side adaptive gyro smoothing is Off/Light/Medium/Strong. It has been useful in real aiming for hand tremor/jitter and adds no Quest-side tracking workload.

## Steering-wheel mapping — experimental

Steering mode consumes both Touch Plus controllers and writes one logical field:

```text
estimated wheel angle -> Left Stick X
```

Everything else remains active: triggers, grips/shoulders, A/B/X/Y, right stick, stick clicks, Menu modifier controls and haptics.

The current output is intentionally still a gamepad axis, not a native HID wheel. Steering is therefore treated as experimental until the fail-safe behaviour and a future HID option are mature enough for release use.

### Center + arm

Steering begins **disarmed**. With the physical wheel/hands centered:

1. choose the steering mode;
2. select **Center + arm steering**;
3. make the first deliberate learning turn **to the right**.

The first significant post-calibration rotation defines the positive wheel-axis sign, so explicitly turning right first avoids an arbitrary quaternion-axis sign deciding left/right. An **Invert steering direction** toggle remains available for unusual fixtures.

Changing steering mode, reconnecting the transport, a persistent tracking failure, or a large rigid-geometry failure requires a new Center + arm operation.

### Fail-safe neutral rule

While steering mode is enabled, the estimator owns horizontal steering. It does **not** fall back to a possibly non-zero physical left-stick X when the wheel is unavailable.

```text
valid + armed + clutch satisfied -> estimated steering
anything else                    -> LX = 0
```

A tracking/source fault immediately enters a neutral safety hold. If the fault lasts longer than 250 ms, steering becomes permanently **DISARMED** until Center + arm is used again.

Large calibrated-geometry changes in rigid modes can disarm immediately rather than trying to guess through a controller being removed or a fixture changing shape.

### Optional light-grip clutch

The optional light-grip clutch requires both grip analog values to be at least about `0.12` before steering output is allowed. This threshold is deliberately far below the normal LB/RB press threshold (`0.62`), so a light hold can act as a clutch without requiring a full shoulder-button squeeze.

Releasing either grip gates steering to LX=0 but does not disable the rest of the gamepad. The feature is optional because different mounts and hand positions may not make it comfortable.

### Mounted / rigid

For controllers fixed to a ring, plate, cardboard wheel or similar fixture. Calibration records each controller's arbitrary mounting orientation and the pair's relative geometry.

Both orientations are treated as redundant observations of one rigid body. Small/medium disagreement can suppress the controller whose motion looks like the outlier; a large relative-orientation change disarms steering.

If both controllers are optically position-tracked during Mounted/Hybrid use, the calibrated controller spacing is also checked. A large spacing change can immediately disarm the wheel and helps detect a controller being removed while still visible to Quest cameras.

Mounted steering does not require position data to continue operating outside camera FOV.

### Free-air optical

Uses the line joining the two tracked controller XYZ positions as an imaginary wheel. It is valid only when **both controllers have `POSITION_TRACKED=1`**.

Loss of required optical tracking immediately outputs neutral steering; a persistent loss disarms the mode.

### Hybrid

Uses optical two-hand geometry while both positions are tracked and falls back to orientation-based rigid steering when optical position disappears. Hybrid keeps rigid-geometry checks because it is intended as an optical-assisted mounted wheel rather than unrestricted free-air hand motion.

### Position validity rule

`POSITION_VALID` by itself is not sufficient. A runtime can retain a valid-but-not-currently-tracked position after optical loss. QuestPad therefore consumes controller XYZ **only while `POSITION_TRACKED=1`**; PT=0 position values are ignored completely.

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
