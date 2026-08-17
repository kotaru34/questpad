using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Numerics;
using System.Windows.Forms;

namespace QuestPad.Host;

internal static class Program
{
    private const int Port = 38888;
    private const uint Magic = 0x44415051;
    private const ushort Protocol = 2;
    private const int PacketSize = 152;
    private const uint FeedbackMagic = 0x31424651; // QFB1 little-endian
    private const int FeedbackSize = 8;
    private const float SteeringGripClutchThreshold = 0.12f;
    private static readonly TimeSpan PacketWatchdog = TimeSpan.FromMilliseconds(250);
    private static readonly CancellationTokenSource Cancel = new();
    private static readonly HostStatus Status = new();
    private static readonly RuntimeSettings Settings = new();
    private static volatile bool EmulationPaused;
    private static volatile bool CalibrateSteeringRequested;
    private static volatile bool DisarmSteeringRequested;
    private static int RumblePacked; // high byte = large motor, low byte = small motor

    private static async Task<int> Main(string[] args)
    {
        string? adbOverride = null;
        string? serial = null;
        bool noGamepad = false;
        bool noAdb = false;
        bool noTray = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--adb" when i + 1 < args.Length:
                    adbOverride = args[++i];
                    break;
                case "--serial" when i + 1 < args.Length:
                    serial = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    Settings.SetOutput(ParseOutput(args[++i]));
                    break;
                case "--gyro" when i + 1 < args.Length:
                    Settings.SetGyroSource(ParseGyro(args[++i]));
                    break;
                case "--gyro-smoothing" when i + 1 < args.Length:
                    Settings.SetGyroSmoothing(ParseSmoothing(args[++i]));
                    break;
                case "--steering" when i + 1 < args.Length:
                    Settings.SetSteering(ParseSteering(args[++i]));
                    break;
                case "--steering-smoothing" when i + 1 < args.Length:
                    Settings.SetSteeringSmoothing(ParseSmoothing(args[++i]));
                    break;
                case "--steering-range" when i + 1 < args.Length && float.TryParse(args[++i], out float range):
                    Settings.SetSteeringRange(range);
                    break;
                case "--steering-clutch" when i + 1 < args.Length:
                    Settings.SetSteeringGripClutch(ParseOnOff(args[++i]));
                    break;
                case "--steering-invert" when i + 1 < args.Length:
                    Settings.SetSteeringInverted(ParseOnOff(args[++i]));
                    break;
                case "--steering-arm":
                    CalibrateSteeringRequested = true;
                    break;
                case "--no-gamepad":
                    noGamepad = true;
                    break;
                case "--no-adb":
                    noAdb = true;
                    break;
                case "--no-tray":
                    noTray = true;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
            }
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Cancel.Cancel();
        };

        using TrayStatus? tray = noTray ? null : new TrayStatus(
            Status,
            Settings,
            () => CalibrateSteeringRequested = true,
            () => DisarmSteeringRequested = true,
            paused =>
            {
                EmulationPaused = paused;
                Status.SetPaused(paused);
                if (paused) Volatile.Write(ref RumblePacked, 0);
            },
            () => Cancel.Cancel());

        string? adb = null;
        if (!noAdb)
        {
            adb = FindAdb(adbOverride);
            if (adb is null)
            {
                FatalError("ADB not found. Put adb.exe in PATH or start QuestPad with --adb C:\\path\\to\\adb.exe");
                return 2;
            }

            Console.WriteLine($"ADB: {adb}");
            RunAdb(adb, serial, "forward", "--remove", $"tcp:{Port}");
            if (!RunAdb(adb, serial, "forward", $"tcp:{Port}", $"tcp:{Port}"))
            {
                FatalError("Could not create the ADB port forward. Check USB debugging/authorization or pass --serial if multiple Android devices are connected.");
                return 3;
            }
        }

        AdbControllerBatteryPoller? batteryPoller = adb is null
            ? null
            : new AdbControllerBatteryPoller(adb, serial, Status, Cancel.Token);

        OutputBackendManager? outputs = null;
        if (!noGamepad)
        {
            try
            {
                outputs = new OutputBackendManager((large, small) =>
                    Volatile.Write(ref RumblePacked, (large << 8) | small));
                IOutputBackend initial = outputs.Ensure(Settings.Snapshot().Output);
                Status.SetGamepadAvailable(true);
                Status.SetOutputBackend(initial.Name);
                Console.WriteLine($"Virtual controller: {initial.Name}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ViGEm unavailable: " + ex.Message);
                Console.Error.WriteLine("Install ViGEmBus, or use --no-gamepad for transport diagnostics.");
                outputs?.Dispose();
                outputs = null;
                Status.SetGamepadAvailable(false);
            }
        }

        try
        {
            await ReceiveLoopAsync(outputs, Cancel.Token);
        }
        finally
        {
            if (batteryPoller is not null)
                await batteryPoller.DisposeAsync();
            outputs?.Dispose();
            if (adb is not null)
                RunAdb(adb, serial, "forward", "--remove", $"tcp:{Port}");
            Console.WriteLine("\nQuestPad host stopped");
        }

        return 0;
    }

    private static async Task ReceiveLoopAsync(OutputBackendManager? outputs, CancellationToken ct)
    {
        var mapper = new ControllerMapper();
        var motionProcessor = new MotionProcessor();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                Status.SetConnection(false);
                Console.WriteLine("Waiting for QuestPad on Quest...");
                await tcp.ConnectAsync("127.0.0.1", Port, ct);
                Status.SetConnection(true);
                Console.WriteLine("QuestPad transport connected (protocol v2 motion-capable)");
                mapper.Reset();
                motionProcessor.Reset();

                using NetworkStream stream = tcp.GetStream();
                byte[] packetBytes = new byte[PacketSize];
                uint? previousSeq = null;
                int lastFeedbackKey = int.MinValue;
                long lastFeedbackTicks = 0;
                long lastPrintTicks = Stopwatch.GetTimestamp();
                long windowPackets = 0;
                long dropped = 0;

                while (!ct.IsCancellationRequested)
                {
                    await ReadExactlyWithTimeoutAsync(stream, packetBytes, PacketWatchdog, ct);
                    Packet p = Parse(packetBytes);
                    if (p.Magic != Magic || p.Version != Protocol || p.Size != PacketSize)
                        throw new IOException($"Protocol mismatch: magic=0x{p.Magic:X8} version={p.Version} size={p.Size}; install the matching QuestPad APK");

                    if (previousSeq.HasValue)
                    {
                        uint delta = unchecked(p.Sequence - previousSeq.Value);
                        if (delta > 1 && delta < 0x80000000u)
                            dropped += delta - 1;
                    }
                    previousSeq = p.Sequence;
                    windowPackets++;

                    RuntimeSettingsSnapshot cfg = Settings.Snapshot();
                    ushort control = HostControlBits.For(cfg);
                    int rumble = outputs is null ? 0 : Volatile.Read(ref RumblePacked);
                    int feedbackKey = (rumble << 16) | control;
                    long feedbackNow = Stopwatch.GetTimestamp();
                    if (feedbackKey != lastFeedbackKey || SecondsSince(lastFeedbackTicks, feedbackNow) >= 0.100)
                    {
                        await SendFeedbackAsync(stream, rumble, control, ct);
                        lastFeedbackKey = feedbackKey;
                        lastFeedbackTicks = feedbackNow;
                    }

                    MotionFrame motionFrame = ToMotionFrame(p);
                    if (DisarmSteeringRequested)
                    {
                        DisarmSteeringRequested = false;
                        motionProcessor.DisarmSteering();
                        Console.WriteLine("\nSteering manually disarmed; LX is forced neutral until Center + arm steering is used.");
                    }
                    if (CalibrateSteeringRequested)
                    {
                        CalibrateSteeringRequested = false;
                        motionProcessor.CalibrateSteering(motionFrame, cfg.Steering);
                        Console.WriteLine("\nSteering center/geometry captured. Turn RIGHT briefly first so QuestPad can learn a deterministic positive wheel axis.");
                    }

                    ProcessedMotion motion = motionProcessor.Process(motionFrame, cfg);
                    bool steeringClutchEngaged = !cfg.SteeringGripClutch ||
                        (p.LG >= SteeringGripClutchThreshold && p.RG >= SteeringGripClutchThreshold);

                    string gyroText = cfg.GyroSource switch
                    {
                        GyroSourceMode.CameraAssisted => $"camera EXP {(motion.GyroValid ? "valid" : "waiting for PT=1")}",
                        GyroSourceMode.AngularRate => $"angular-rate {(motion.GyroValid ? "valid" : "waiting for AV=1")}",
                        _ => "off"
                    };
                    string steeringText = cfg.Steering == SteeringMode.Off ? "off" : motion.SteeringState;
                    if (cfg.Steering != SteeringMode.Off && cfg.SteeringGripClutch && !steeringClutchEngaged)
                        steeringText += " | clutch open → LX=0";
                    Status.UpdateMotionStatus(motion.GyroValid, gyroText, steeringText);

                    IOutputBackend? backend = null;
                    if (outputs is not null)
                    {
                        try
                        {
                            backend = outputs.Ensure(cfg.Output);
                            Status.SetOutputBackend(backend.Name);
                        }
                        catch (Exception ex)
                        {
                            Status.SetGamepadAvailable(false);
                            throw new IOException("failed to switch virtual controller backend: " + ex.Message, ex);
                        }
                    }

                    if (backend is not null)
                    {
                        if (EmulationPaused || (p.Flags & 0x2u) == 0)
                        {
                            mapper.Reset();
                            Volatile.Write(ref RumblePacked, 0);
                            backend.Neutral();
                        }
                        else
                        {
                            LogicalGamepadState state = mapper.Map(
                                p.Buttons, p.LX, p.LY, p.RX, p.RY, p.LT, p.RT, p.LG, p.RG);

                            // Steering mode owns horizontal steering completely. Never
                            // leave a stale non-zero wheel value or fall back to physical
                            // LX while the experimental wheel mode is armed/configured:
                            // invalid/disarmed/clutch-open states are explicitly neutral.
                            if (cfg.Steering != SteeringMode.Off)
                            {
                                state.LX = motion.SteeringValid && steeringClutchEngaged
                                    ? motion.SteeringNormalized
                                    : 0.0f;
                            }

                            backend.Apply(state, motion);
                        }
                    }

                    long now = Stopwatch.GetTimestamp();
                    double printSeconds = SecondsSince(lastPrintTicks, now);
                    if (printSeconds >= 0.5)
                    {
                        double hz = windowPackets / printSeconds;
                        lastPrintTicks = now;
                        windowPackets = 0;
                        var (leftBattery, rightBattery) = DecodeBatteries(p.Reserved);
                        Status.UpdateTelemetry(hz, dropped, ThermalName(p.Thermal), leftBattery, rightBattery);
                        HostSnapshot snapshot = Status.Snapshot();
                        string batteryText = $"bat L {BatteryText(snapshot.LeftBattery),4} R {BatteryText(snapshot.RightBattery),4} [{snapshot.BatterySource}]";
                        Console.Write(
                            $"\r{hz,5:F1} Hz seq {p.Sequence,8} {snapshot.OutputBackend,-28} " +
                            $"gyro {gyroText,-31} steer {snapshot.SteeringStatus,-45} " +
                            $"{batteryText} therm {ThermalName(p.Thermal),8} drops {dropped}      ");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Status.SetConnection(false);
                mapper.Reset();
                motionProcessor.Reset();
                Volatile.Write(ref RumblePacked, 0);
                try { outputs?.Current?.Neutral(); } catch { }
                Console.WriteLine($"\ntransport lost/watchdog fired: {ex.Message}; reconnecting...");
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static MotionFrame ToMotionFrame(Packet p)
    {
        MotionValidity f = (MotionValidity)p.MotionFlags;
        ControllerMotion left = new(
            f.HasFlag(MotionValidity.LeftActive),
            f.HasFlag(MotionValidity.LeftOrientationValid),
            f.HasFlag(MotionValidity.LeftOrientationTracked),
            f.HasFlag(MotionValidity.LeftPositionValid),
            f.HasFlag(MotionValidity.LeftPositionTracked),
            f.HasFlag(MotionValidity.LeftAngularValid),
            p.LeftOrientation,
            p.LeftPosition,
            p.LeftAngularLocal);
        ControllerMotion right = new(
            f.HasFlag(MotionValidity.RightActive),
            f.HasFlag(MotionValidity.RightOrientationValid),
            f.HasFlag(MotionValidity.RightOrientationTracked),
            f.HasFlag(MotionValidity.RightPositionValid),
            f.HasFlag(MotionValidity.RightPositionTracked),
            f.HasFlag(MotionValidity.RightAngularValid),
            p.RightOrientation,
            p.RightPosition,
            p.RightAngularLocal);
        return new MotionFrame(p.MonotonicNs, left, right);
    }

    private static async Task SendFeedbackAsync(NetworkStream stream, int packed, ushort control, CancellationToken ct)
    {
        byte[] feedback = new byte[FeedbackSize];
        BinaryPrimitives.WriteUInt32LittleEndian(feedback.AsSpan(0, 4), FeedbackMagic);
        feedback[4] = (byte)((packed >> 8) & 0xFF);
        feedback[5] = (byte)(packed & 0xFF);
        BinaryPrimitives.WriteUInt16LittleEndian(feedback.AsSpan(6, 2), control);
        await stream.WriteAsync(feedback, ct);
    }

    private static async Task ReadExactlyWithTimeoutAsync(NetworkStream stream, byte[] buffer, TimeSpan timeout, CancellationToken outer)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        timeoutCts.CancelAfter(timeout);
        int off = 0;
        try
        {
            while (off < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(off), timeoutCts.Token);
                if (n == 0) throw new EndOfStreamException("Quest closed the transport");
                off += n;
            }
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            throw new TimeoutException($"no complete controller packet for {timeout.TotalMilliseconds:F0} ms");
        }
    }

    private static Packet Parse(ReadOnlySpan<byte> b)
    {
        static float F32(ReadOnlySpan<byte> s, int offset) =>
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s.Slice(offset, 4)));
        static Vector3 V3(ReadOnlySpan<byte> s, int offset) => new(F32(s, offset), F32(s, offset + 4), F32(s, offset + 8));
        static Quaternion Q(ReadOnlySpan<byte> s, int offset) => new(F32(s, offset), F32(s, offset + 4), F32(s, offset + 8), F32(s, offset + 12));

        return new Packet(
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(0, 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(4, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(6, 2)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(12, 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(16, 8)),
            BinaryPrimitives.ReadInt32LittleEndian(b.Slice(24, 4)),
            F32(b, 28), F32(b, 32), F32(b, 36), F32(b, 40),
            F32(b, 44), F32(b, 48), F32(b, 52), F32(b, 56),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(60, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(64, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(68, 4)),
            Q(b, 72), Q(b, 88), V3(b, 104), V3(b, 116), V3(b, 128), V3(b, 140));
    }

    private static string ThermalName(int t) => t switch
    {
        0 => "NONE", 1 => "LIGHT", 2 => "MODERATE", 3 => "SEVERE",
        4 => "CRITICAL", 5 => "EMERGENCY", 6 => "SHUTDOWN", _ => t.ToString()
    };

    private static (int? left, int? right) DecodeBatteries(uint packed)
    {
        int? left = (packed & (1u << 16)) != 0 ? (int)(packed & 0xFFu) : null;
        int? right = (packed & (1u << 17)) != 0 ? (int)((packed >> 8) & 0xFFu) : null;
        return (left, right);
    }

    private static string BatteryText(int? value) => value.HasValue ? $"{value.Value}%" : "n/a";
    private static double SecondsSince(long oldTicks, long newTicks) => (newTicks - oldTicks) / (double)Stopwatch.Frequency;

    private static OutputMode ParseOutput(string value) => value.ToLowerInvariant() switch
    {
        "ds4" or "dualshock4" or "playstation" => OutputMode.DualShock4,
        _ => OutputMode.Xbox360
    };

    private static GyroSourceMode ParseGyro(string value) => value.ToLowerInvariant() switch
    {
        "camera" or "tracked" or "camera-assisted" => GyroSourceMode.CameraAssisted,
        "rate" or "angular" or "gyro" => GyroSourceMode.AngularRate,
        _ => GyroSourceMode.Off
    };

    private static SteeringMode ParseSteering(string value) => value.ToLowerInvariant() switch
    {
        "mounted" or "rigid" => SteeringMode.Mounted,
        "freeair" or "free-air" or "optical" => SteeringMode.FreeAir,
        "hybrid" or "auto" => SteeringMode.Hybrid,
        _ => SteeringMode.Off
    };

    private static SmoothingLevel ParseSmoothing(string value) => value.ToLowerInvariant() switch
    {
        "light" => SmoothingLevel.Light,
        "medium" => SmoothingLevel.Medium,
        "strong" => SmoothingLevel.Strong,
        _ => SmoothingLevel.Off
    };

    private static bool ParseOnOff(string value) => value.ToLowerInvariant() switch
    {
        "1" or "on" or "true" or "yes" or "enabled" => true,
        _ => false
    };

    private static bool RunAdb(string adb, string? serial, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(adb)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (!string.IsNullOrWhiteSpace(serial))
            {
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add(serial);
            }
            foreach (string arg in arguments) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string? FindAdb(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string p = Path.Combine(dir.Trim(), "adb.exe");
                if (File.Exists(p)) return p;
            }
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string sdk = Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe");
        return File.Exists(sdk) ? sdk : null;
    }

    private static void FatalError(string message)
    {
#if QUESTPAD_GUI
        MessageBox.Show(message, "QuestPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
#else
        Console.Error.WriteLine(message);
#endif
    }

    private static void PrintHelp()
    {
        const string text =
            "QuestPad.Host [options]\n" +
            "  --adb PATH                 adb.exe path\n" +
            "  --serial SERIAL            select Android device\n" +
            "  --output xbox|ds4          virtual controller backend\n" +
            "  --gyro off|rate|camera     right-Touch native gyro source\n" +
            "  --gyro-smoothing off|light|medium|strong\n" +
            "  --steering off|mounted|freeair|hybrid\n" +
            "  --steering-range DEG       total lock-to-lock range (60..1080)\n" +
            "  --steering-smoothing off|light|medium|strong\n" +
            "  --steering-clutch on|off   require light grip on both Touch controllers\n" +
            "  --steering-invert on|off   reverse steering output direction\n" +
            "  --steering-arm             center + arm on the first received motion frame\n" +
            "  --no-gamepad               transport/motion diagnostic only\n" +
            "  --no-adb                   assume tcp:38888 is already forwarded\n" +
            "  --no-tray                  console-only mode\n\n" +
            "Gyro 'rate' is the recommended path and consumes controller-local OpenXR angular velocity.\n" +
            "Gyro 'camera' is an experimental A/B reference that derives rate from tracked pose and requires PT=1.\n" +
            "Neither mode is raw MEMS access; Horizon may still perform internal sensor fusion.\n" +
            "Steering is fail-safe: tracking/geometry faults force LX=0 and persistent faults disarm it.\n" +
            "After Center + arm steering, turn RIGHT briefly first to establish the positive wheel axis.\n" +
            "Selecting a non-off gyro source automatically selects the DS4 backend.\n" +
            "After a Quest reboot, Horizon currently needs to see the controller once before motion becomes valid.\n" +
            "For a real console window use QuestPad.Host.Console.exe.";
#if QUESTPAD_GUI
        MessageBox.Show(text, "QuestPad command-line options", MessageBoxButtons.OK, MessageBoxIcon.Information);
#else
        Console.WriteLine(text);
#endif
    }

    private readonly record struct Packet(
        uint Magic,
        ushort Version,
        ushort Size,
        uint Sequence,
        uint Flags,
        ulong MonotonicNs,
        int Thermal,
        float LX,
        float LY,
        float RX,
        float RY,
        float LT,
        float RT,
        float LG,
        float RG,
        uint Buttons,
        uint Reserved,
        uint MotionFlags,
        Quaternion LeftOrientation,
        Quaternion RightOrientation,
        Vector3 LeftPosition,
        Vector3 RightPosition,
        Vector3 LeftAngularLocal,
        Vector3 RightAngularLocal);
}
