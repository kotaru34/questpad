# First real-hardware test

Do this only after the GitHub Actions workflow has produced `quest-debug.apk`.

1. Connect the Quest 3 over USB with USB debugging authorized.
2. Install the APK:
   ```powershell
   adb install -r .\quest-debug.apk
   ```
3. Launch **QuestPad** on the headset. There is intentionally no controller-driven UI.
4. Before installing ViGEm or running the full host, validate the raw transport:
   ```powershell
   .\QuestPad-Diagnostic-win64.exe --adb "C:\path\to\adb.exe"
   ```
5. Move both sticks and press A/B/X/Y, triggers, grips, stick clicks, and left Menu. Confirm values change immediately and return to neutral.
6. Leave it running for 30-60 minutes with the headset resting in the intended position. Watch `therm=` and packet sequence/drop counters.
7. Test the exit chord: hold both stick clicks + both grips for 3 seconds. The stream should neutralize and stop.

Only after this passes should `QuestPad.Host.exe` + ViGEmBus be introduced. That isolates Quest/OpenXR/USB issues from virtual-controller-driver issues.
