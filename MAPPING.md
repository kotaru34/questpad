# QuestPad controller mapping

QuestPad exposes a complete Xbox 360/XInput control surface while keeping normal Touch Plus controls direct and synthetic controls ergonomic.

## Direct controls

| Touch Plus | Virtual Xbox 360 |
|---|---|
| Left thumbstick | Left Stick |
| Right thumbstick | Right Stick |
| Left trigger | LT (analog) |
| Right trigger | RT (analog) |
| Left grip squeeze | LB |
| Right grip squeeze | RB |
| A / B / X / Y | A / B / X / Y |
| Left stick click | LS / L3 |
| Right stick click | RS / R3 |

Grip-to-shoulder conversion uses hysteresis: press at 0.62, release below 0.45. This avoids button chatter without requiring an unnecessarily hard squeeze.

## Menu modifier layer

The physical **left Menu** button acts as both the Xbox Start/Menu control and a modifier because it is available to applications. The Meta/System button remains owned by Horizon OS.

| Gesture | Virtual Xbox 360 |
|---|---|
| Tap and release Menu | Start / Menu tap |
| Hold Menu by itself for 0.50 s | Start / Menu held continuously until release |
| Hold Menu + right stick ↑ | D-pad Up |
| Hold Menu + right stick ↓ | D-pad Down |
| Hold Menu + right stick ← | D-pad Left |
| Hold Menu + right stick → | D-pad Right |
| Hold Menu + right stick diagonally | matching D-pad diagonal |
| Menu + R3 | Back / View; stays held while R3 remains physically held |
| Hold Menu + LT + RT for 0.75 s | Guide |

### Why this layout

- Menu is on the left controller, leaving the right thumb free for the D-pad layer.
- A quick Menu press still behaves like an ordinary Start/Menu tap.
- A plain Menu hold commits to a real continuous Start/Menu hold after 0.50 s, which is required by games that distinguish press from hold.
- Modifier actions must begin before the plain Menu hold commits. Once the hold has committed, it stays Start/Menu until release instead of changing mode unexpectedly.
- Right-stick camera output is suppressed while Menu is held for D-pad use.
- Menu + R3 is a cross-hand chord and does not require one thumb to press two controls at once. Once activated, Back/View remains down until R3 is released, so games can detect a genuine long press and the user may release Menu after starting the chord.
- Guide is intentionally deliberate because it is rarely needed during moment-to-moment gameplay.
- D-pad directions use hysteresis to avoid chatter around direction boundaries.

## Exiting QuestPad

Hold **LS + RS + LB + RB** for **3 seconds**.

Here `LB/RB` mean the physical **left/right grip squeezes**. The exit detector uses the same comfortable logical shoulder thresholds as normal gameplay rather than requiring a >75% squeeze.

While the exit gesture is armed:

- LS, RS, LB and RB are suppressed from the virtual controller;
- both Touch Plus controllers pulse after 1 second;
- a stronger pulse is sent after 2 seconds;
- a final confirmation pulse is sent at 3 seconds;
- the virtual gamepad is neutralized before the OpenXR session is asked to exit.

Releasing any part of the gesture before 3 seconds cancels the exit sequence.

## Touch Plus inputs intentionally left unused

Touch Plus exposes additional capacitive/touch-style signals that do not have direct equivalents on a standard Xbox 360 controller. Mapping normal finger-rest states to gameplay buttons would increase accidental input, so these signals are reserved for future optional profiles rather than enabled by default.

## Motion data

Touch Plus also exposes tracked controller poses through OpenXR (`grip/pose` and `aim/pose`). OpenXR can return orientation, position, linear velocity and angular velocity for those tracked spaces. QuestPad does not currently forward motion data because the Xbox 360/XInput report has no gyroscope or accelerometer fields.

A future motion profile can therefore use one of three strategies without changing the basic controller transport:

- right Touch Plus motion as the default aiming gyro source;
- left Touch Plus motion as an alternative source;
- a synthesized two-hand virtual-gamepad frame derived from both controller poses for users who hold the controllers like two halves of one gamepad.

The first option is expected to be the most stable and ergonomic default. A synthesized two-hand frame is possible but inherently less deterministic because the two Touch Plus controllers are independent physical objects rather than one rigid controller shell.

## Haptics

Xbox 360 rumble is bridged back to Touch Plus through OpenXR haptics:

- large motor → left controller
- small motor → right controller

Exit countdown cues temporarily take priority over game rumble. Rumble is cleared on focus loss, disconnect, and application exit.
