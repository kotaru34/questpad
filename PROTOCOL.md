# QuestPad wire protocol v1

One full-duplex TCP connection is carried over the ADB forward on port 38888.

## Quest -> Windows input stream

Fixed-size 68-byte little-endian packets, one packet per XR frame (~72 Hz).

| Offset | Type | Field |
|---:|---|---|
| 0 | u32 | magic `0x44415051` (`QPAD`) |
| 4 | u16 | version = 1 |
| 6 | u16 | size = 68 |
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

Flags:

- bit 0: session active
- bit 1: OpenXR focused
- bit 2: left input active
- bit 3: right input active
- bit 4: exit chord armed

Buttons:

- bit 0 A
- bit 1 B
- bit 2 X
- bit 3 Y
- bit 4 left thumb click
- bit 5 right thumb click
- bit 6 raw left Menu input

The Windows mapping layer converts that raw control state into the complete Xbox 360 surface documented in `MAPPING.md`.

## Windows -> Quest feedback stream

Xbox rumble uses the reverse direction of the **same TCP connection**. There is no second socket or ADB forward.

Each feedback report is a fixed-size 8-byte little-endian packet:

| Offset | Type | Field |
|---:|---|---|
| 0 | u32 | magic `0x31424651` (`QFB1`) |
| 4 | u8 | Xbox large-motor amplitude, 0..255 |
| 5 | u8 | Xbox small-motor amplitude, 0..255 |
| 6 | u16 | reserved = 0 |

The Windows host sends a report when motor state changes and at least every 100 ms as a keepalive. The Quest side consumes feedback non-blockingly in the XR frame loop. The large motor drives left Touch Plus haptics and the small motor drives right Touch Plus haptics. Zero feedback, OpenXR focus loss, connection loss, and app exit all stop haptics.

TCP framing is intentionally fixed-size in both directions. A partial write/read is accumulated until a complete packet exists; malformed feedback magic is ignored rather than interpreted as rumble state.

## Controller battery telemetry (offset 64)

The existing 32-bit reserved field is used without changing protocol v1 packet size:

- bits 0..7: left controller battery percentage (0..100)
- bits 8..15: right controller battery percentage (0..100)
- bit 16: left percentage valid
- bit 17: right percentage valid
- bit 18: left controller charging
- bit 19: right controller charging

Battery data comes from the optional ratified `XR_EXT_interaction_profile_battery_state_display` extension. If the Quest OpenXR runtime does not expose the extension, validity bits remain clear and hosts must display battery state as unavailable.
