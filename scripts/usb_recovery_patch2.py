from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    s = p.read_text(encoding="utf-8")
    n = s.count(old)
    if n != 1:
        raise RuntimeError(f"{path}: expected one match, got {n}: {old[:120]!r}")
    p.write_text(s.replace(old, new, 1), encoding="utf-8")


p = Path("host/Program.cs")
s = p.read_text(encoding="utf-8")

s = s.replace(
    "        string? adb = null;\n        if (!noAdb)\n",
    "        string? adb = null;\n        AdbQuestDevice? questTarget = null;\n        if (!noAdb)\n",
    1,
)
s = s.replace(
    "            serial = quest.Serial;\n            string questName = string.Join(\" \", new[] { quest.Manufacturer, quest.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));\n",
    "            questTarget = quest;\n            serial = quest.Serial;\n            string questName = string.Join(\" \", new[] { quest.Manufacturer, quest.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));\n",
    1,
)
s = s.replace(
    "            await ReceiveLoopAsync(outputs, Cancel.Token);\n",
    "            await ReceiveLoopAsync(outputs, adb, questTarget, autoStartQuest, Cancel.Token);\n",
    1,
)

head_start = s.index("    private static async Task ReceiveLoopAsync(OutputBackendManager? outputs, CancellationToken ct)\n")
head_end_marker = "                mapper.Reset();\n                motionProcessor.Reset();\n                bool gyroStickLocked = false;\n"
head_end = s.index(head_end_marker, head_start)
old_head = s[head_start:head_end]
new_head = '''    private static async Task ReceiveLoopAsync(
        OutputBackendManager? outputs,
        string? adb,
        AdbQuestDevice? questTarget,
        bool autoStartQuest,
        CancellationToken ct)
    {
        var mapper = new ControllerMapper();
        var motionProcessor = new MotionProcessor();
        bool adbRecoveryNeeded = false;
        bool adbForwardPrepared = false;
        bool recoveryLaunchAttempted = false;
        bool adbWaitLogged = false;
        bool bridgeWaitLogged = false;

        while (!ct.IsCancellationRequested)
        {
            if (adbRecoveryNeeded && adb is not null && questTarget is not null)
            {
                if (!RunAdb(adb, questTarget.Serial, "get-state"))
                {
                    if (!adbWaitLogged)
                    {
                        Console.WriteLine($"Waiting for Quest USB/ADB device [{questTarget.Serial}] to return...");
                        adbWaitLogged = true;
                    }
                    adbForwardPrepared = false;
                    recoveryLaunchAttempted = false;
                    bridgeWaitLogged = false;
                    try { await Task.Delay(750, ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                adbWaitLogged = false;
                if (!adbForwardPrepared)
                {
                    RunAdb(adb, questTarget.Serial, "forward", "--remove", $"tcp:{Port}");
                    if (!RunAdb(adb, questTarget.Serial, "forward", $"tcp:{Port}", $"tcp:{Port}"))
                    {
                        Console.WriteLine("Quest ADB is back but the port forward is not ready yet; retrying...");
                        try { await Task.Delay(750, ct); }
                        catch (OperationCanceledException) { break; }
                        continue;
                    }
                    adbForwardPrepared = true;
                    bridgeWaitLogged = false;
                    Console.WriteLine("Quest USB/ADB restored; tcp:38888 forward recreated.");
                }
            }

            bool connectedThisAttempt = false;
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                Status.SetConnection(false);
                Console.WriteLine(adbRecoveryNeeded ? "Restoring QuestPad transport..." : "Waiting for QuestPad on Quest...");
                await tcp.ConnectAsync("127.0.0.1", Port, ct);
                connectedThisAttempt = true;
                bool recoveredFromAdbLoss = adbRecoveryNeeded;
                adbRecoveryNeeded = false;
                adbForwardPrepared = false;
                recoveryLaunchAttempted = false;
                adbWaitLogged = false;
                bridgeWaitLogged = false;
                Status.SetConnection(true);
                Console.WriteLine(recoveredFromAdbLoss
                    ? "QuestPad transport recovered after USB/ADB loss."
                    : "QuestPad transport connected (protocol v2 motion/MR-capable)");
'''
s = s[:head_start] + new_head + s[head_end:]

catch_start_marker = '                Console.WriteLine($"\\ntransport lost/watchdog fired: {ex.Message}; reconnecting...");\n'
catch_start = s.index(catch_start_marker)
catch_end_marker = "                catch (OperationCanceledException) { break; }\n"
catch_end = s.index(catch_end_marker, catch_start) + len(catch_end_marker)
new_catch_tail = '''                if (adb is not null && questTarget is not null)
                {
                    if (connectedThisAttempt || !adbRecoveryNeeded)
                    {
                        adbRecoveryNeeded = true;
                        adbForwardPrepared = false;
                        recoveryLaunchAttempted = false;
                        adbWaitLogged = false;
                        bridgeWaitLogged = false;
                        Console.WriteLine($"\\ntransport lost/watchdog fired: {ex.Message}; rebuilding ADB transport for [{questTarget.Serial}]...");
                    }
                    else
                    {
                        if (autoStartQuest && !recoveryLaunchAttempted &&
                            !IsQuestPadProcessRunning(adb, questTarget.Serial))
                        {
                            if (AdbQuestDeviceSelector.TryStartQuestPad(adb, questTarget, out string startError))
                            {
                                recoveryLaunchAttempted = true;
                                bridgeWaitLogged = false;
                                Console.WriteLine("QuestPad APK was not running; recovery launch requested over ADB.");
                            }
                            else
                            {
                                Console.WriteLine($"QuestPad recovery launch failed: {startError}");
                                adbForwardPrepared = false;
                            }
                        }
                        else if (!bridgeWaitLogged)
                        {
                            Console.WriteLine("Quest ADB forward is restored; waiting for the QuestPad bridge listener...");
                            bridgeWaitLogged = true;
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"\\ntransport lost/watchdog fired: {ex.Message}; reconnecting...");
                }

                try { await Task.Delay(750, ct); }
                catch (OperationCanceledException) { break; }
'''
s = s[:catch_start] + new_catch_tail + s[catch_end:]

run_adb_marker = "    private static bool RunAdb(string adb, string? serial, params string[] arguments)\n"
idx = s.index(run_adb_marker)
s = s[:idx] + '''    private static bool IsQuestPadProcessRunning(string adb, string serial) =>
        RunAdb(adb, serial, "shell", "pidof", AdbQuestDeviceSelector.QuestPadPackage);

''' + s[idx:]

p.write_text(s, encoding="utf-8")

for project in ("host/QuestPad.Host.csproj", "host/QuestPad.Host.Console.csproj"):
    replace_once(project, "<Version>0.3.6-test</Version>", "<Version>0.3.7-test</Version>")
    replace_once(project, "<FileVersion>0.3.6.0</FileVersion>", "<FileVersion>0.3.7.0</FileVersion>")

replace_once(
    "quest/build.gradle",
    "        versionCode 8\n        versionName '0.3.6-test'",
    "        versionCode 9\n        versionName '0.3.7-test'",
)
replace_once(
    "quest/src/main/cpp/questpad.cpp",
    "    ici.applicationInfo.applicationVersion = 5;",
    "    ici.applicationInfo.applicationVersion = 6;",
)

replace_once(
    "README.md",
    "- Unexpected USB/TCP loss neutralizes the controller and leaves the Windows host alive to reconnect.\n",
    "- Unexpected USB/TCP loss neutralizes the controller and leaves the Windows host alive. With normal ADB ownership enabled, the host waits for the **same selected Quest serial**, recreates the lost `tcp:38888` forward when USB/ADB returns, and reconnects automatically. If the QuestPad process was actually killed while disconnected, normal autostart may launch it again; a still-running Quest app is not restarted.\n",
)

replace_once(
    "BUILD_STATUS.md",
    """Implemented and build-gated, with the new bidirectional lifecycle still awaiting the next Quest 3 hardware pass:

- Windows Exit / Ctrl+C sends an explicit protocol shutdown request to the Quest app; ADB force-stop is retained only as the final lifecycle backstop.
- Quest exit-chord completion carries an explicit final status flag that closes the Windows host, while accidental transport loss still reconnects.
""",
    """Hardware-verified on Quest 3:

- Windows Exit / Ctrl+C sends an explicit protocol shutdown request and closes the Quest app cleanly; ADB force-stop remains only the final lifecycle backstop.
- Quest exit-chord completion carries an explicit final status flag and closes the Windows host too.
- The same bidirectional shutdown behaviour works while MR passthrough is active.
- the named Windows single-instance guard correctly rejects a second host instance without disturbing the active bridge;

New v0.3.7 recovery candidate, build-gated and awaiting the focused unplug/replug hardware retest:

- unexpected USB/ADB loss still neutralizes output and **does not** quit either side;
- the host now waits for the same selected Quest ADB serial instead of blindly retrying a stale localhost forward;
- when that Quest returns, the host removes/recreates `tcp:38888` and retries the existing Quest bridge first;
- the Quest app is relaunched only when autostart is enabled **and** its process is no longer running, avoiding unnecessary OpenXR-session restarts on ordinary cable reconnects.
""",
)

p = Path("BUILD_STATUS.md")
s = p.read_text(encoding="utf-8")
s = s.replace(
    "- a named Windows single-instance guard prevents two hosts from fighting over ADB forwarding, ViGEm and brightness ownership;\n",
    "",
)
p.write_text(s, encoding="utf-8")

for name in ("scripts/usb_recovery_patch.py", "scripts/usb_recovery_patch2.py"):
    q = Path(name)
    if q.exists():
        q.unlink()

print("USB reconnect recovery patch applied")
