# QuestPad v0.1 build/validation status

## Verified

- Wire packet layout is fixed at 68 bytes and documented in `PROTOCOL.md`.
- Go transport diagnostic cross-compiles to Windows x64.
- The Windows diagnostic was exercised against a 72 Hz mock Quest TCP stream and correctly decoded sequence numbers, flags, sticks, triggers, grips, buttons, thermal status and packet cadence.
- Android manifest / Gradle / CMake project structure is aligned with Meta's NativeActivity OpenXR sample layout.
- ViGEm managed API calls used by `QuestPad.Host` were checked against the upstream `Nefarius.ViGEm.Client` interfaces: batched report mode, reset, axis/slider/button setters and explicit submit.
- GitHub Actions successfully compiled both `quest-debug.apk` and the self-contained `QuestPad.Host.exe` for win-x64.
- **Real Quest 3 transport/input test passed on 2026-08-16:** the Quest app connected through the ADB TCP forward, sustained ~71.9 Hz, reported `thermal=NONE`, and the host reported `drops=0` during the observed sample.
- The reconnect path was exercised: after the Quest closed one transport connection, the host watchdog detected it, returned to waiting state, and reconnected successfully when the Quest endpoint returned.
- **Real XInput/game test passed on 2026-08-16:** `QuestPad.Host` created the ViGEm Xbox 360 virtual controller successfully and a real Windows game accepted and worked with Quest Touch Plus input as a game controller rather than keyboard/mouse emulation.

## Still requires real hardware validation

- Verify all intended Touch Plus controls/mappings under active use, including both analog sticks, analog triggers, grips, A/B/X/Y, stick clicks, and Menu.
- Verify normal button input remains active when controller pose quality degrades while the headset rests on a desk.
- Run >= 60 minutes and record thermal state / subjective headset temperature with zero composition layers, 72 Hz and sustained-low performance hints.
- Verify whether the Android brightness request materially affects the physical panel under the Meta OpenXR runtime.
- Measure subjective and, if useful, instrumented end-to-end controller latency in a real game.
- Confirm focus-loss neutralization and the deliberate 3-second exit chord on hardware.

## Acceptance target before calling it stable

Run for >= 60 minutes with no input stalls, no stuck controls, no recurrent ADB reconnects, thermal state no worse than expected for an idle-ish immersive session, and no perceptible controller latency regression versus a normal wireless gamepad.
