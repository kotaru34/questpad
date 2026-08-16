# QuestPad wire protocol v2

One full-duplex TCP connection is carried over the ADB forward on port 38888.

Protocol v2 keeps the original gamepad/battery fields intact and appends optional motion telemetry. Motion acquisition is controlled by the Windows host and is **off by default**, so normal Xbox/XInput use does not continuously query controller poses.

## Quest -> Windows input stream

Fixed-size **152-byte little-endian packets**, one packet per XR frame (~72 Hz).

| Offset | Type | Field |
|---:|---|---|
| 0 | u32 | magic `0x44415051` (`QPAD`) |
| 4 | u16 | version = 2 |
| 6 | u16 | size = 152 |
| 8 | u32 | sequence |
| 12 | u32 | flags |
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
| 64 | u32 | controller battery display telemetry |
| 68 | u32 | motion validity/tracking flags |
| 72 | 4 × f32 | left orientation quaternion `(x,y,z,w)` |
| 88 | 4 × f32 | right orientation quaternion `(x,y,z,w)` |
| 104 | 3 × f32 | left position `(x,y,z)` in LOCAL space, metres |
| 116 | 3 × f32 | right position `(x,y,z)` in LOCAL space, metres |
| 128 | 3 × f32 | left controller-local angular velocity `(x,y,z)`, rad/s |
| 140 | 3 × f32 | right controller-local angular velocity `(x,y,z)`, rad/s |

### Base flags

- bit 0: session active
- bit 1: OpenXR focused
- bit 2: left input active
- bit 3: right input active
- bit 4: exit chord armed

### Buttons

- bit 0 A
- bit 1 B
- bit 2 X
- bit 3 Y
- bit 4 left thumb click
- bit 5 right thumb click
- bit 6 raw left Menu input

The Windows mapping layer converts the raw control state into the logical gamepad surface documented in `MAPPING.md` and then feeds the selected output backend.

### Motion flags

Left controller:

- bit 0: pose action active
- bit 1: orientation valid
- bit 2: orientation tracked
- bit 3: position valid
- bit 4: position tracked
- bit 5: angular velocity valid

Right controller uses the same meanings at bits 8..13.

- bit 16: the Quest app performed motion acquisition for this frame

**Position is accepted by the Windows steering estimator only when the corresponding `POSITION_TRACKED` bit is set.** A runtime may continue returning a position with `POSITION_VALID=1` after optical tracking is lost; QuestPad deliberately ignores that stale/predicted position when `POSITION_TRACKED=0`.

## Windows -> Quest feedback/control stream

Rumble and motion-acquisition control share the reverse direction of the **same TCP connection**. There is no second socket or ADB forward.

Each report remains a fixed-size **8-byte little-endian packet**:

| Offset | Type | Field |
|---:|---|---|
| 0 | u32 | magic `0x31424651` (`QFB1`) |
| 4 | u8 | large-motor amplitude, 0..255 |
| 5 | u8 | small-motor amplitude, 0..255 |
| 6 | u16 | host motion request |

Motion requests:

- `0`: no motion acquisition; standard gamepad baseline
- `1`: right-controller angular-rate path only
- `2`: right-controller tracked-pose path for camera-assisted gyro comparison
- `3`: both controllers tracked for steering-wheel modes

The host sends feedback/control when state changes and at least every 100 ms as a keepalive. The Quest side consumes it non-blockingly inside the XR frame loop.

When request `0` is active, QuestPad does not call `xrLocateSpace()` for controller motion. This preserves the original low-workload baseline as closely as possible.

## Gyro experiment semantics

QuestPad exposes two right-controller gyro sources for A/B testing:

### Camera-assisted tracked pose

Motion request `2` is used. QuestPad sends right-controller orientation/position/tracking flags. The Windows host deliberately requires `POSITION_TRACKED=1` and derives angular rate from successive tracked orientation quaternions.

This mode is intended to compare the camera-assisted tracked-pose path against the angular-rate-only path for accuracy, availability and thermal behaviour.

### Angular-rate only

Motion request `1` is used. The Quest app asks OpenXR for the controller angular velocity and sends only the controller-local angular-rate vector/validity required by the host. The Windows side does not consume controller position or absolute orientation for gyro aiming.

This is **not raw MEMS access**. Public OpenXR does not expose the Touch Plus physical gyroscope directly, so Horizon may still perform internal sensor fusion. The distinction is that QuestPad itself does not request or consume optical position/orientation data in this mode.

## Controller battery telemetry (offset 64)

The battery field retains its protocol-v1 layout:

- bits 0..7: left controller battery percentage (0..100)
- bits 8..15: right controller battery percentage (0..100)
- bit 16: left percentage valid
- bit 17: right percentage valid
- bit 18: left controller charging
- bit 19: right controller charging

The Quest-side OpenXR battery extension remains optional. The Windows host can independently use its slow ADB fallback when Horizon does not expose valid OpenXR battery data.

## Haptics

The large motor drives left Touch Plus haptics and the small motor drives right Touch Plus haptics. Zero feedback, OpenXR focus loss, connection loss and app exit all stop haptics.

TCP framing is fixed-size in both directions. Reads accumulate until a complete packet exists; a partial non-blocking Quest write is treated conservatively to avoid corrupting the packet stream.
