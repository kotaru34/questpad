# QuestPad ergonomic Xbox mapping

The goal is to expose a complete Xbox 360/XInput control surface while keeping the normal Touch Plus controls direct and making every synthetic control reachable without awkward same-thumb chords.

## Direct controls

| Meta Touch Plus | Virtual Xbox 360 |
|---|---|
| left thumbstick | left stick |
| right thumbstick | right stick |
| left trigger | LT (analog) |
| right trigger | RT (analog) |
| left grip squeeze | LB |
| right grip squeeze | RB |
| A / B / X / Y | A / B / X / Y |
| left stick click | L3 |
| right stick click | R3 |

Grips use hysteresis (press at 0.62, release below 0.45) so a slightly variable squeeze does not chatter LB/RB.

## Menu modifier layer

The physical **left Menu** button is the modifier because it is application-accessible. The Meta/System button stays owned by Horizon OS.

| Gesture | Virtual Xbox 360 |
|---|---|
| tap and release Menu | Start / Menu |
| hold Menu + right stick ↑ | D-pad Up |
| hold Menu + right stick ↓ | D-pad Down |
| hold Menu + right stick ← | D-pad Left |
| hold Menu + right stick → | D-pad Right |
| hold Menu + right stick diagonal | matching D-pad diagonal (two directions) |
| hold Menu + R3 | Back / View |
| hold Menu + LT + RT for 0.75 s | Guide |

### Why this layout

- Menu is on the left controller, so the right thumb remains completely free for the D-pad layer.
- Right-stick motion is suppressed while Menu is held, so using the synthetic D-pad cannot move the camera.
- Menu + R3 is a cross-hand chord and does not require the left thumb to press two controls at once.
- Guide is deliberately slower and harder to trigger accidentally; both trigger values are suppressed while the Guide chord is armed.
- A plain Menu press becomes Start only on release, and only if no modifier action was used. This prevents a D-pad/View/Guide gesture from also leaking a Start press.
- D-pad directions use hysteresis (0.55 engage / 0.35 release) to avoid direction chatter.

## QuestPad exit

Hold **both stick clicks + both grips** for 3 seconds. This remains separate from the Xbox mapping. The Quest app suppresses the exit chord from the virtual gamepad and sends neutral state before requesting OpenXR session exit.

## Touch Plus capabilities intentionally not turned into extra gamepad buttons

Touch Plus also exposes capacitive touch/proximity-style inputs and additional trigger sensing through OpenXR. They are useful signals, but a standard Xbox 360 gamepad has no direct equivalents. Mapping normal finger resting/touch state to gameplay buttons would make accidental input much more likely, so these are intentionally reserved for future optional profiles rather than enabled in the default mapping.

Controller haptics are the next natural capability to bridge: ViGEm can report Xbox rumble requests and Touch Plus exposes OpenXR haptic output. This is separate from the input mapping so it can be added without changing the ergonomic control surface above.
