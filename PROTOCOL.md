# QuestPad wire protocol v1

TCP, fixed-size 68-byte little-endian packets, one packet per XR frame.

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
| 64 | u32 | reserved |

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
- bit 6 View / left Menu
