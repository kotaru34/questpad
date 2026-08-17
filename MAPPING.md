# QuestPad controller mapping

QuestPad maps Touch Plus input into a backend-independent logical gamepad. The logical state can currently be emitted as Xbox 360/XInput or DualShock 4. The recommended gyro path adds native DS4 motion without changing the logical Xbox↔DS4 correspondence.

## Direct controls

The v0.4 layout intentionally swaps the earlier shoulder/trigger placement after real-game testing: the **Touch index triggers** now act as the target controller's bumpers, while the **Touch grip analogs** act as its analog triggers.

| Touch Plus | Logical / Xbox | DualShock 4 |
|---|---|---|
| Left thumbstick | Left Stick | Left Stick |
| Right thumbstick | Right Stick | Right Stick |
| Left index trigger | LB | L1 |
| Right index trigger | RB | R1 |
| Left grip squeeze | LT analog | L2 analog |
| Right grip squeeze | RT analog | R2 analog |
| A | A | Cross |
| B | B | Circle |
| X | X | Square |
| Y | Y | Triangle |
| Left stick click | LS / L3 | L3 |
| Right stick click | RS / R3 | R3 |

The source index triggers are analog but the target bumpers are digital, so trigger-to-shoulder conversion uses hysteresis: press at 0.62, release below 0.45. Grip values remain analog all the way to Xbox LT/RT or DS4 L2/R2.

## Menu modifier layer

The physical **left Menu** button is available to applications and acts as Start/Menu plus a modifier. The Meta/System button remains Horizon-owned.

| Gesture | Xbox 360 | DualShock 4 |
|---|---|---|
| Tap and release Menu | Start / Menu tap | Options tap |
| Hold Menu by itself for 0.50 s | Start / Menu held | Options held |
| Hold Menu + right stick | D-pad, including diagonals | D-pad, including diagonals |
| Menu + L3 | Back / View held while L3 remains held | Share held while L3 remains held |
| Menu + R3 | R3 remains available; no extra target button | Touchpad click held while R3 remains held |
| Hold Menu + both grips (logical LT + RT) for 0.75 s | Guide | PS Home |

D-pad/L3/R3 modifier actions should begin before the 0.50-second plain-Menu hold commits. **Guide/PS is deliberately different:** once both logical triggers are squeezed, the Guide chord takes priority over an already-eligible Start/Options hold and starts its own 0.75-second timer. This avoids the old timing race where a slightly sequential `Menu + LT + RT` press could never reach Guide/PS.

Right-stick camera output is suppressed while the stick is being used as D-pad. `Menu + L3` is a genuine held View/Share state rather than a short pulse. In DS4 mode `Menu + R3` mirrors that held behavior for the physical touchpad click; Xbox mode has no touchpad target, so R3 itself remains available and the chord only suppresses an accidental Start/Menu pulse.

### Xbox ↔ DualShock 4 spatial equivalence

QuestPad follows the conventional positional mapping: `A ↔ Cross`, `B ↔ Circle`, `X ↔ Square`, `Y ↔ Triangle`, `LB/RB ↔ L1/R1`, `LT/RT ↔ L2/R2`, `View/Back ↔ Share`, `Menu/Start ↔ Options`, and `Guide/Xbox ↔ PS Home`. D-pad directions, sticks and L3/R3 map directly. Counting the four D-pad directions separately and L2/R2 as pressable triggers, this exposes all 18 pressable DS4 controls; only touch-surface coordinates are intentionally not emulated.

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

The clutch requires both physical grip analog values to exceed a low threshold (~0.12) before steering is emitted. Releasing either hand immediately makes steering neutral without changing the rest of the logical gamepad.

In the v0.4 gamepad layout those same grip analogs are also the normal LT/RT (L2/R2) sources. The steering clutch is only an additional safety gate for LX; it does not quantize or otherwise alter the analog trigger values.

### Position validity

Mounted steering is primarily orientation-based. When optical position is used as an additional geometry check, XYZ is accepted only while `POSITION_TRACKED=1`; `POSITION_VALID=1` by itself is not treated as current optical tracking.

## Exiting QuestPad

Hold **both stick clicks + both physical grip squeezes** for **3 seconds**. In the v0.4 gamepad layout those grips are the controls mapped to logical **LT + RT / L2 + R2**.

While the exit gesture is armed:

- L3, R3 and both grip/trigger outputs are suppressed from the virtual controller;
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
