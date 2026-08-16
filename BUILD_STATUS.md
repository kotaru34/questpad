# QuestPad v0.1 build/validation status

## Verified in this workspace

- Wire packet layout is fixed at 68 bytes and documented in `PROTOCOL.md`.
- Go transport diagnostic cross-compiles to Windows x64.
- The Windows diagnostic was exercised against a 72 Hz mock Quest TCP stream and correctly decoded sequence numbers, flags, sticks, triggers, grips, buttons, thermal status and packet cadence.
- Android manifest / Gradle / CMake project structure is aligned with Meta's NativeActivity OpenXR sample layout.
- ViGEm managed API calls used by `QuestPad.Host` were checked against the upstream `Nefarius.ViGEm.Client` interfaces: batched report mode, reset, axis/slider/button setters and explicit submit.

## Requires CI build

This execution environment does not contain an Android SDK/NDK or .NET SDK, so these two binaries are deliberately **not** represented as locally compiled/tested:

- `quest-debug.apk`
- `QuestPad.Host.exe`

`.github/workflows/build.yml` builds both without Visual Studio or Android Studio on the user's PC.

## Requires real Quest 3 hardware test

- Touch Plus interaction profile/bindings on the installed Horizon OS version.
- Whether normal button input remains active when controller pose quality degrades while the headset rests on a desk.
- Long-run thermal state and actual headset temperature with zero composition layers, 72 Hz and sustained-low performance hints.
- Whether the Android brightness request affects the physical panel under the Meta OpenXR runtime.
- End-to-end controller latency in a real game.
- ViGEm/XInput compatibility on the target Windows installation.

## Acceptance target before calling it stable

Run for >= 60 minutes with no input stalls, no stuck controls, no recurrent ADB reconnects, thermal state no worse than expected for an idle-ish immersive session, and no perceptible controller latency regression versus a normal wireless gamepad.
