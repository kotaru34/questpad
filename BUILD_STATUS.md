# Project status

QuestPad is functional on real Quest 3 hardware and is currently in early public testing.

## Verified on hardware

- Native Quest OpenXR application builds and runs on Quest 3.
- Quest → USB/ADB → Windows transport works at approximately 72 Hz.
- Initial observed transport run reported `drops=0` and `thermal=NONE`.
- Host watchdog detects a closed transport and reconnects automatically.
- Windows host successfully creates an Xbox 360-compatible virtual controller through ViGEmBus.
- A real Windows game recognizes and accepts QuestPad as a game controller rather than keyboard/mouse emulation.
- Xbox rumble is successfully forwarded back to the Touch Plus controllers.
- Windows tray status/control UI is implemented; hardware validation of the tray and controller-battery telemetry is pending.

## Still being validated

- Long-duration (60+ minute) thermal and transport soak tests.
- Broad compatibility across different Windows games.
- Controller input behavior when headset/controller pose tracking quality degrades.
- Exit gesture and haptic countdown behavior across Horizon/OpenXR runtime states.
- Whether the current Quest OpenXR runtime exposes `XR_EXT_interaction_profile_battery_state_display` for Touch Plus, and the granularity/accuracy of the reported percentages.
- Subjective and instrumented end-to-end input latency.

## Stability target

A stable release should sustain long gameplay sessions without stuck controls, recurrent ADB reconnects, unexpected thermal escalation, or perceptible latency regression compared with a conventional wireless gamepad.
