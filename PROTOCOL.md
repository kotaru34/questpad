# QuestPad wire protocol v2

QuestPad uses one full-duplex TCP connection over the ADB forward on port 38888.

Protocol v2 carries the normal gamepad stream plus optional controller motion. v0.3.1 also uses previously-unused control/status bits for the Quest view mode. Packet sizes and the protocol version remain unchanged.

## Quest -> Windows input stream

Fixed-size **152-byte little-endian packets**, normally one per XR frame (~72 Hz).

| Offset | Type | Field |
|---:|---|---|
| 0 | u32 | magic `0x44415051` (`QPAD`) |
| 4 | u16 | version = 2 |
| 6 | u16 | size = 152 |
| 8 | u32 | sequence |
| 12 | u32 | base/status flags |
| 16 | u64 | Quest `CLOCK_MONOTONIC` nanoseconds |
| 24 | i32 | Android thermal status |
| 28 | f32 | LX |
| 32 | f32 | LY |
| 36 | f32 | RX |
| 40 | f32 | RY |
| 44 | f32 | LT |
| 48 | f32 | RT |
| 52 | f32 | left grip |
| 56 | f32 | right grip |
| 60 | u32 | button mask |
| 64 | u32 | controller battery telemetry |
| 68 | u32 | motion validity/tracking flags |
| 72 | 4 x f32 | left orientation quaternion `(x,y,z,w)` |
| 88 | 4 x f32 | right orientation quaternion `(x,y,z,w)` |
| 104 | 3 x f32 | left position `(x,y,z)` in LOCAL space, metres |
| 116 | 3 x f32 | right position `(x,y,z)` in LOCAL space, metres |
| 128 | 3 x f32 | left controller-local angular velocity `(x,y,z)`, rad/s |
| 140 | 3 x f32 | right controller-local angular velocity `(x,y,z)`, rad/s |

### Base/status flags

- bit 0: XR session active
- bit 1: OpenXR focused
- bit 2: left input active
- bit 3: right input active
- bit 4: exit chord armed
- bit 5: `XR_FB_passthrough` is available and usable on this Quest/runtime
- bit 6: QuestPad passthrough layer is currently active

Bits 5/6 let the host distinguish “requested but unavailable” from “requested and actually composited”.

### Buttons

- bit 0 A
- bit 1 B
- bit 2 X
- bit 3 Y
- bit 4 left thumb click
- bit 5 right thumb click
- bit 6 raw left Menu input

The Windows mapper converts this raw state into the backend-independent logical gamepad surface documented in `MAPPING.md`.

### Motion flags

Left controller:

- bit 0: pose action active
- bit 1: orientation valid
- bit 2: orientation tracked
- bit 3: position valid
- bit 4: position tracked
- bit 5: angular velocity valid

Right controller uses the same meanings at bits 8..13.

- bit 16: QuestPad performed motion acquisition for this frame

For the retained mounted-steering experiment, XYZ position is accepted only while `POSITION_TRACKED=1`; `POSITION_VALID=1` alone is not considered current optical tracking.

## Windows -> Quest feedback/control stream

Rumble, motion-acquisition control and Quest view control share the reverse direction of the **same TCP connection**. There is no second socket or ADB forward.

Each report is a fixed-size **8-byte little-endian packet**:

| Offset | Type | Field |
|---:|---|---|
| 0 | u32 | magic `0x31424651` (`QFB1`) |
| 4 | u8 | large-motor amplitude, 0..255 |
| 5 | u8 | small-motor amplitude, 0..255 |
| 6 | u16 | host control word |

### Control word

Low two bits are the motion request:

- `0`: no controller motion acquisition
- `1`: right-controller angular-rate path only
- `2`: right-controller tracked-pose path for the camera-assisted diagnostic gyro
- `3`: both controllers tracked for the mounted-steering experiment

Independent feature bits:

- bit 8 (`0x0100`): request Quest compositor passthrough

The host may combine bit 8 with any motion request. For example, angular-rate gyro + MR passthrough uses motion request `1` plus `0x0100`.

The host sends feedback/control whenever state changes and at least every 100 ms as a keepalive. If the Windows connection disappears, QuestPad sees a zero control word, disables passthrough and returns to its zero-layer/low-brightness baseline.

## Quest view modes

### Black / zero-layer

Control bit 8 is clear. QuestPad submits **zero composition layers** and keeps its existing minimum-brightness override. This is the default PC-only / lowest-workload display mode.

### Passthrough / MR

Control bit 8 is set. When `XR_FB_passthrough` is supported, QuestPad lazily creates a reconstruction passthrough feature/layer, starts/resumes it, restores normal/system display brightness, and submits exactly one `XrCompositionLayerPassthroughFB` as the backmost/only QuestPad composition layer.

QuestPad does **not** request raw camera frames. Passthrough is owned by the OpenXR runtime/compositor.

When passthrough is turned off, the layer and feature are paused and QuestPad returns to zero layers and minimum brightness. Objects are retained for quick toggling and destroyed on app shutdown.

## Gyro semantics

### Recommended: angular-rate only

Motion request `1` is used. The Quest side asks OpenXR for the right controller angular velocity and sends only the controller-local rate/validity needed by the host. The Windows side does not consume absolute controller orientation or XYZ position for aiming.

This is **not raw MEMS access**. Horizon may still perform internal sensor fusion.

### Diagnostic: camera-assisted tracked pose

Motion request `2` is used. The Quest side sends tracked right-controller orientation/position. Windows deliberately requires `POSITION_TRACKED=1` and derives angular rate from successive orientations. Real hardware A/B testing found this path less useful for aiming than angular-rate-only, so it is retained for diagnostics rather than recommended gameplay.

## Controller battery telemetry (offset 64)

- bits 0..7: left controller battery percentage (0..100)
- bits 8..15: right controller battery percentage (0..100)
- bit 16: left percentage valid
- bit 17: right percentage valid
- bit 18: left controller charging
- bit 19: right controller charging

The Quest-side OpenXR battery extension remains optional. The Windows host can independently use its slow ADB fallback when Horizon does not expose usable OpenXR battery telemetry.

## Haptics

The large virtual motor drives left Touch Plus haptics and the small motor drives right Touch Plus haptics. Zero feedback, OpenXR focus loss, transport loss and app exit stop haptics.

TCP framing is fixed-size in both directions. Reads accumulate until a complete packet exists; a partial non-blocking Quest write is treated conservatively so the stream cannot silently lose framing.
