using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace QuestPad.Host;

internal static class Program
{
    private const int Port = 38888;
    private const uint Magic = 0x44415051;
    private const ushort Protocol = 1;
    private const int PacketSize = 68;
    private const uint FeedbackMagic = 0x31424651; // QFB1 little-endian
    private const int FeedbackSize = 8;
    private static int RumblePacked; // high byte = large motor, low byte = small motor
    private const double Deadzone = 0.08;
    private static readonly TimeSpan PacketWatchdog = TimeSpan.FromMilliseconds(250);
    private static readonly CancellationTokenSource Cancel = new();

    private static async Task<int> Main(string[] args)
    {
        string? adbOverride = null;
        string? serial = null;
        bool noGamepad = false;
        bool noAdb = false;

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
                case "--no-gamepad":
                    noGamepad = true;
                    break;
                case "--no-adb":
                    noAdb = true;
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

        string? adb = null;
        if (!noAdb)
        {
            adb = FindAdb(adbOverride);
            if (adb is null)
            {
                Console.Error.WriteLine("ADB not found. Put adb.exe in PATH or use --adb C:\\path\\to\\adb.exe");
                return 2;
            }

            Console.WriteLine($"ADB: {adb}");
            // Remove a stale local binding first. Failure is harmless if it didn't exist.
            RunAdb(adb, serial, "forward", "--remove", $"tcp:{Port}");
            if (!RunAdb(adb, serial, "forward", $"tcp:{Port}", $"tcp:{Port}"))
            {
                Console.Error.WriteLine("Could not create the ADB port forward. Check USB debugging/authorization or pass --serial if multiple Android devices are connected.");
                return 3;
            }
        }

        ViGEmClient? vigem = null;
        IXbox360Controller? pad = null;
        var mapper = new ControllerMapper();
        if (!noGamepad)
        {
            try
            {
                vigem = new ViGEmClient();
                pad = vigem.CreateXbox360Controller();
                // We update the entire XInput report once per Quest sample. Leaving this at
                // the library default would submit once for every individual axis/button setter.
                pad.AutoSubmitReport = false;
                pad.FeedbackReceived += (_, e) =>
                    Volatile.Write(ref RumblePacked, (e.LargeMotor << 8) | e.SmallMotor);
                pad.Connect();
                Neutral(pad);
                Console.WriteLine("Virtual Xbox 360 controller: connected");
                Console.WriteLine("Full-gamepad layer: Menu tap=Start; Menu+RS=D-pad; Menu+R3=Back/View; Menu+LT+RT=Guide.");
                Console.WriteLine("Rumble bridge: Xbox large/small motors -> left/right Touch Plus haptics.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ViGEm unavailable: " + ex.Message);
                Console.Error.WriteLine("Install ViGEmBus, or use --no-gamepad for transport diagnostics.");
                vigem?.Dispose();
                vigem = null;
                pad = null;
            }
        }

        try
        {
            await ReceiveLoopAsync(pad, mapper, Cancel.Token);
        }
        finally
        {
            if (pad is not null)
            {
                try { Neutral(pad); } catch { }
                try { pad.Disconnect(); } catch { }
            }
            vigem?.Dispose();
            if (adb is not null)
                RunAdb(adb, serial, "forward", "--remove", $"tcp:{Port}");
            Console.WriteLine("\nQuestPad host stopped");
        }

        return 0;
    }

    private static async Task ReceiveLoopAsync(IXbox360Controller? pad, ControllerMapper mapper, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                Console.WriteLine("Waiting for QuestPad on Quest...");
                await tcp.ConnectAsync("127.0.0.1", Port, ct);
                Console.WriteLine("QuestPad transport connected");
                mapper.Reset();

                using NetworkStream stream = tcp.GetStream();
                byte[] packet = new byte[PacketSize];
                uint? previousSeq = null;
                int lastSentRumble = -1;
                long lastRumbleSendTicks = 0;
                long lastPrintTicks = Stopwatch.GetTimestamp();
                long windowPackets = 0;
                long dropped = 0;

                while (!ct.IsCancellationRequested)
                {
                    await ReadExactlyWithTimeoutAsync(stream, packet, PacketWatchdog, ct);
                    var p = Parse(packet);
                    if (p.Magic != Magic || p.Version != Protocol || p.Size != PacketSize)
                        throw new IOException($"Protocol mismatch: magic=0x{p.Magic:X8} version={p.Version} size={p.Size}");

                    if (previousSeq.HasValue)
                    {
                        uint delta = unchecked(p.Sequence - previousSeq.Value);
                        if (delta > 1 && delta < 0x80000000u)
                            dropped += delta - 1;
                    }
                    previousSeq = p.Sequence;
                    windowPackets++;

                    // ViGEm's feedback callback may run on another thread. Ship the
                    // latest two motor amplitudes back over the same full-duplex TCP
                    // connection. A 100 ms keepalive also guarantees that the Quest
                    // eventually learns the current state after any transient loss.
                    int rumble = pad is null ? 0 : Volatile.Read(ref RumblePacked);
                    long feedbackNow = Stopwatch.GetTimestamp();
                    if (rumble != lastSentRumble ||
                        SecondsSince(lastRumbleSendTicks, feedbackNow) >= 0.100)
                    {
                        await SendFeedbackAsync(stream, rumble, ct);
                        lastSentRumble = rumble;
                        lastRumbleSendTicks = feedbackNow;
                    }

                    if (pad is not null)
                    {
                        // FLAG_FOCUSED = bit 1. Quest sends a neutral packet on focus loss,
                        // and the host independently enforces neutral state as a safety net.
                        if ((p.Flags & 0x2u) == 0)
                        {
                            mapper.Reset();
                            Volatile.Write(ref RumblePacked, 0);
                            Neutral(pad);
                        }
                        else
                        {
                            mapper.Apply(pad, p.Buttons, p.LX, p.LY, p.RX, p.RY, p.LT, p.RT, p.LG, p.RG);
                        }
                    }

                    long now = Stopwatch.GetTimestamp();
                    double printSeconds = SecondsSince(lastPrintTicks, now);
                    if (printSeconds >= 0.5)
                    {
                        double hz = windowPackets / printSeconds;
                        lastPrintTicks = now;
                        windowPackets = 0;
                        Console.Write(
                            $"\r{hz,5:F1} Hz  seq {p.Sequence,8}  L {p.LX,6:F2},{p.LY,6:F2}  R {p.RX,6:F2},{p.RY,6:F2}  " +
                            $"LT {p.LT:F2} RT {p.RT:F2}  grip {p.LG:F2}/{p.RG:F2}  " +
                            $"therm {ThermalName(p.Thermal),8}  drops {dropped}      ");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                mapper.Reset();
                Volatile.Write(ref RumblePacked, 0);
                if (pad is not null)
                {
                    try { Neutral(pad); } catch { }
                }
                Console.WriteLine($"\ntransport lost/watchdog fired: {ex.Message}; reconnecting...");
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static async Task SendFeedbackAsync(NetworkStream stream, int packed, CancellationToken ct)
    {
        byte[] feedback = new byte[FeedbackSize];
        BinaryPrimitives.WriteUInt32LittleEndian(feedback.AsSpan(0, 4), FeedbackMagic);
        feedback[4] = (byte)((packed >> 8) & 0xFF);
        feedback[5] = (byte)(packed & 0xFF);
        // bytes 6..7 reserved
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
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(64, 4)));
    }

    private static void Apply(IXbox360Controller pad, Packet p)
    {
        var (lx, ly) = Radial(p.LX, p.LY);
        var (rx, ry) = Radial(p.RX, p.RY);

        pad.ResetReport();
        pad.SetAxisValue(Xbox360Axis.LeftThumbX, ToShort(lx));
        pad.SetAxisValue(Xbox360Axis.LeftThumbY, ToShort(ly));
        pad.SetAxisValue(Xbox360Axis.RightThumbX, ToShort(rx));
        pad.SetAxisValue(Xbox360Axis.RightThumbY, ToShort(ry));
        pad.SetSliderValue(Xbox360Slider.LeftTrigger, ToByte(p.LT));
        pad.SetSliderValue(Xbox360Slider.RightTrigger, ToByte(p.RT));

        Set(pad, Xbox360Button.LeftShoulder, p.LG > 0.55f);
        Set(pad, Xbox360Button.RightShoulder, p.RG > 0.55f);
        Set(pad, Xbox360Button.A, (p.Buttons & (1u << 0)) != 0);
        Set(pad, Xbox360Button.B, (p.Buttons & (1u << 1)) != 0);
        Set(pad, Xbox360Button.X, (p.Buttons & (1u << 2)) != 0);
        Set(pad, Xbox360Button.Y, (p.Buttons & (1u << 3)) != 0);
        Set(pad, Xbox360Button.LeftThumb, (p.Buttons & (1u << 4)) != 0);
        Set(pad, Xbox360Button.RightThumb, (p.Buttons & (1u << 5)) != 0);
        Set(pad, Xbox360Button.Start, (p.Buttons & (1u << 6)) != 0);
        pad.SubmitReport();
    }

    private static void Neutral(IXbox360Controller pad)
    {
        pad.ResetReport();
        pad.SubmitReport();
    }

    private static void Set(IXbox360Controller p, Xbox360Button b, bool on) => p.SetButtonState(b, on);

    private static short ToShort(float v)
    {
        double x = Math.Clamp(v, -1.0f, 1.0f);
        if (x <= -1.0) return short.MinValue;
        return (short)Math.Round(x * short.MaxValue);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(Math.Round(Math.Clamp(v, 0.0f, 1.0f) * 255.0), 0, 255);

    private static (float x, float y) Radial(float x, float y)
    {
        double m = Math.Sqrt(x * x + y * y);
        if (m <= Deadzone) return (0, 0);
        double scaled = Math.Min(1.0, (m - Deadzone) / (1.0 - Deadzone));
        double k = scaled / m;
        return ((float)(x * k), (float)(y * k));
    }

    private static string ThermalName(int t) => t switch
    {
        0 => "NONE", 1 => "LIGHT", 2 => "MODERATE", 3 => "SEVERE",
        4 => "CRITICAL", 5 => "EMERGENCY", 6 => "SHUTDOWN", _ => t.ToString()
    };

    private static double SecondsSince(long oldTicks, long newTicks) =>
        (newTicks - oldTicks) / (double)Stopwatch.Frequency;

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
        catch
        {
            return false;
        }
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

    private static void PrintHelp()
    {
        Console.WriteLine("QuestPad.Host [--adb PATH] [--serial SERIAL] [--no-gamepad] [--no-adb]");
        Console.WriteLine("  --no-gamepad  transport/input diagnostic only; don't create XInput device");
        Console.WriteLine("  --no-adb      assume tcp:38888 is already reachable (developer testing)");
        Console.WriteLine();
        Console.WriteLine("Full-gamepad layer:");
        Console.WriteLine("  Menu tap                -> Start/Menu");
        Console.WriteLine("  hold Menu + right stick -> D-pad (diagonals supported)");
        Console.WriteLine("  hold Menu + R3          -> Back/View");
        Console.WriteLine("  hold Menu + LT + RT     -> Guide after 0.75 s");
        Console.WriteLine("  both stick clicks + both grips for 3 s -> exit QuestPad");
    }

    private readonly record struct Packet(
        uint Magic, ushort Version, ushort Size, uint Sequence, uint Flags, ulong MonotonicNs, int Thermal,
        float LX, float LY, float RX, float RY, float LT, float RT, float LG, float RG, uint Buttons, uint Reserved);
}
