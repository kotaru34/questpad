from pathlib import Path

root = Path(__file__).resolve().parents[1]
p = root / "host/Program.cs"
text = p.read_text(encoding="utf-8")
old = "                    ushort control = HostControlBits.For(cfg);\n                    int rumble = outputs is null ? 0 : Volatile.Read(ref RumblePacked);\n"
new = "                    ushort control = HostControlBits.For(cfg);\n                    if (Volatile.Read(ref HostShutdownRequested) != 0)\n                        control |= ControlQuestShutdown;\n                    int rumble = outputs is null ? 0 : Volatile.Read(ref RumblePacked);\n"
if text.count(old) != 1:
    raise RuntimeError(f"expected one feedback-control site, got {text.count(old)}")
p.write_text(text.replace(old, new, 1), encoding="utf-8")

# Give the graceful XR/Android lifecycle a short head start before the ADB
# backstop. This is still fast for user-visible exit, but avoids racing normal
# NativeActivity teardown on a busy headset.
p = root / "host/Program.cs"
text = p.read_text(encoding="utf-8")
old = "                if (GracefulQuestShutdownSent)\n                    await Task.Delay(200);\n"
new = "                if (GracefulQuestShutdownSent)\n                    await Task.Delay(750);\n"
if text.count(old) != 1:
    raise RuntimeError(f"expected one graceful-delay site, got {text.count(old)}")
p.write_text(text.replace(old, new, 1), encoding="utf-8")

Path(__file__).unlink()
print("session polish race hardening applied")
