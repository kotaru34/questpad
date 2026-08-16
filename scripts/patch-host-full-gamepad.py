from pathlib import Path

p = Path("host/Program.cs")
s = p.read_text(encoding="utf-8")

replacements = [
    (
        "IXbox360Controller? pad = null;\n        if (!noGamepad)",
        "IXbox360Controller? pad = null;\n        var mapper = new ControllerMapper();\n        if (!noGamepad)",
    ),
    (
        "Console.WriteLine(\"Virtual Xbox 360 controller: connected\");",
        "Console.WriteLine(\"Virtual Xbox 360 controller: connected\");\n                Console.WriteLine(\"Full-gamepad layer: Menu tap=Start; Menu+RS=D-pad; Menu+R3=Back/View; Menu+LT+RT=Guide.\");",
    ),
    (
        "await ReceiveLoopAsync(pad, Cancel.Token);",
        "await ReceiveLoopAsync(pad, mapper, Cancel.Token);",
    ),
    (
        "private static async Task ReceiveLoopAsync(IXbox360Controller? pad, CancellationToken ct)",
        "private static async Task ReceiveLoopAsync(IXbox360Controller? pad, ControllerMapper mapper, CancellationToken ct)",
    ),
    (
        "Console.WriteLine(\"QuestPad transport connected\");\n\n                using NetworkStream stream",
        "Console.WriteLine(\"QuestPad transport connected\");\n                mapper.Reset();\n\n                using NetworkStream stream",
    ),
    (
        "if ((p.Flags & 0x2u) == 0) Neutral(pad);\n                        else Apply(pad, p);",
        "if ((p.Flags & 0x2u) == 0)\n                        {\n                            mapper.Reset();\n                            Neutral(pad);\n                        }\n                        else\n                        {\n                            mapper.Apply(pad, p.Buttons, p.LX, p.LY, p.RX, p.RY, p.LT, p.RT, p.LG, p.RG);\n                        }",
    ),
    (
        "catch (Exception ex)\n            {\n                if (pad is not null)",
        "catch (Exception ex)\n            {\n                mapper.Reset();\n                if (pad is not null)",
    ),
    (
        "Console.WriteLine(\"  --no-adb      assume tcp:38888 is already reachable (developer testing)\");",
        "Console.WriteLine(\"  --no-adb      assume tcp:38888 is already reachable (developer testing)\");\n        Console.WriteLine();\n        Console.WriteLine(\"Full-gamepad layer:\");\n        Console.WriteLine(\"  Menu tap                -> Start/Menu\");\n        Console.WriteLine(\"  hold Menu + right stick -> D-pad (diagonals supported)\");\n        Console.WriteLine(\"  hold Menu + R3          -> Back/View\");\n        Console.WriteLine(\"  hold Menu + LT + RT     -> Guide after 0.75 s\");\n        Console.WriteLine(\"  both stick clicks + both grips for 3 s -> exit QuestPad\");",
    ),
]

for old, new in replacements:
    count = s.count(old)
    if count != 1:
        raise SystemExit(f"expected exactly one match, got {count}: {old[:80]!r}")
    s = s.replace(old, new, 1)

p.write_text(s, encoding="utf-8")
print("patched host/Program.cs")
