from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected one match, got {count}")
    return text.replace(old, new, 1)

# ---------------- Windows host ----------------
host_path = Path("host/Program.cs")
host = host_path.read_text(encoding="utf-8")

host = replace_once(host,
    "    private const int PacketSize = 68;\n",
    "    private const int PacketSize = 68;\n"
    "    private const uint FeedbackMagic = 0x31424651; // QFB1 little-endian\n"
    "    private const int FeedbackSize = 8;\n"
    "    private static int RumblePacked; // high byte = large motor, low byte = small motor\n",
    "host constants")

host = replace_once(host,
    "                pad.AutoSubmitReport = false;\n                pad.Connect();\n",
    "                pad.AutoSubmitReport = false;\n"
    "                pad.FeedbackReceived += (_, e) =>\n"
    "                    Volatile.Write(ref RumblePacked, (e.LargeMotor << 8) | e.SmallMotor);\n"
    "                pad.Connect();\n",
    "host feedback subscription")

host = replace_once(host,
    "                Console.WriteLine(\"Full-gamepad layer: Menu tap=Start; Menu+RS=D-pad; Menu+R3=Back/View; Menu+LT+RT=Guide.\");\n",
    "                Console.WriteLine(\"Full-gamepad layer: Menu tap=Start; Menu+RS=D-pad; Menu+R3=Back/View; Menu+LT+RT=Guide.\");\n"
    "                Console.WriteLine(\"Rumble bridge: Xbox large/small motors -> left/right Touch Plus haptics.\");\n",
    "host rumble log")

host = replace_once(host,
    "                uint? previousSeq = null;\n                long lastPrintTicks = Stopwatch.GetTimestamp();\n",
    "                uint? previousSeq = null;\n"
    "                int lastSentRumble = -1;\n"
    "                long lastRumbleSendTicks = 0;\n"
    "                long lastPrintTicks = Stopwatch.GetTimestamp();\n",
    "host connection feedback state")

host = replace_once(host,
    "                    previousSeq = p.Sequence;\n                    windowPackets++;\n\n                    if (pad is not null)\n",
    "                    previousSeq = p.Sequence;\n"
    "                    windowPackets++;\n\n"
    "                    // ViGEm's feedback callback may run on another thread. Ship the\n"
    "                    // latest two motor amplitudes back over the same full-duplex TCP\n"
    "                    // connection. A 100 ms keepalive also guarantees that the Quest\n"
    "                    // eventually learns the current state after any transient loss.\n"
    "                    int rumble = pad is null ? 0 : Volatile.Read(ref RumblePacked);\n"
    "                    long feedbackNow = Stopwatch.GetTimestamp();\n"
    "                    if (rumble != lastSentRumble ||\n"
    "                        SecondsSince(lastRumbleSendTicks, feedbackNow) >= 0.100)\n"
    "                    {\n"
    "                        await SendFeedbackAsync(stream, rumble, ct);\n"
    "                        lastSentRumble = rumble;\n"
    "                        lastRumbleSendTicks = feedbackNow;\n"
    "                    }\n\n"
    "                    if (pad is not null)\n",
    "host feedback send")

host = replace_once(host,
    "                        if ((p.Flags & 0x2u) == 0)\n                        {\n                            mapper.Reset();\n                            Neutral(pad);\n",
    "                        if ((p.Flags & 0x2u) == 0)\n"
    "                        {\n"
    "                            mapper.Reset();\n"
    "                            Volatile.Write(ref RumblePacked, 0);\n"
    "                            Neutral(pad);\n",
    "host focus rumble safety")

host = replace_once(host,
    "            catch (Exception ex)\n            {\n                mapper.Reset();\n",
    "            catch (Exception ex)\n"
    "            {\n"
    "                mapper.Reset();\n"
    "                Volatile.Write(ref RumblePacked, 0);\n",
    "host disconnect rumble safety")

host = replace_once(host,
    "    private static async Task ReadExactlyWithTimeoutAsync(NetworkStream stream, byte[] buffer, TimeSpan timeout, CancellationToken outer)\n",
    "    private static async Task SendFeedbackAsync(NetworkStream stream, int packed, CancellationToken ct)\n"
    "    {\n"
    "        byte[] feedback = new byte[FeedbackSize];\n"
    "        BinaryPrimitives.WriteUInt32LittleEndian(feedback.AsSpan(0, 4), FeedbackMagic);\n"
    "        feedback[4] = (byte)((packed >> 8) & 0xFF);\n"
    "        feedback[5] = (byte)(packed & 0xFF);\n"
    "        // bytes 6..7 reserved\n"
    "        await stream.WriteAsync(feedback, ct);\n"
    "    }\n\n"
    "    private static async Task ReadExactlyWithTimeoutAsync(NetworkStream stream, byte[] buffer, TimeSpan timeout, CancellationToken outer)\n",
    "host feedback method")

host_path.write_text(host, encoding="utf-8")
print("patched host/Program.cs for rumble")

# ---------------- Quest native app ----------------
quest_path = Path("quest/src/main/cpp/questpad.cpp")
q = quest_path.read_text(encoding="utf-8")

q = replace_once(q,
    "constexpr uint16_t kPort = 38888;\nconstexpr uint32_t kMagic = 0x44415051u; // \"QPAD\" little-endian\n",
    "constexpr uint16_t kPort = 38888;\n"
    "constexpr uint32_t kMagic = 0x44415051u; // \"QPAD\" little-endian\n"
    "constexpr uint32_t kFeedbackMagic = 0x31424651u; // \"QFB1\" little-endian\n",
    "quest feedback magic")

q = replace_once(q,
    "static_assert(sizeof(PadPacket) == 68, \"PadPacket wire size changed\");\n\n",
    "static_assert(sizeof(PadPacket) == 68, \"PadPacket wire size changed\");\n\n"
    "struct __attribute__((packed)) RumblePacket {\n"
    "    uint32_t magic;\n"
    "    uint8_t largeMotor;\n"
    "    uint8_t smallMotor;\n"
    "    uint16_t reserved;\n"
    "};\n"
    "static_assert(sizeof(RumblePacket) == 8, \"RumblePacket wire size changed\");\n\n",
    "quest feedback packet")

q = replace_once(q,
    "    void sendPacket(const PadPacket& packet) {\n",
    "    uint16_t pollRumble() {\n"
    "        int fd = clientFd_.load(std::memory_order_relaxed);\n"
    "        if (fd < 0) {\n"
    "            rumblePacked_.store(0, std::memory_order_relaxed);\n"
    "            feedbackBytes_ = 0;\n"
    "            return 0;\n"
    "        }\n\n"
    "        for (;;) {\n"
    "            const ssize_t n = ::recv(\n"
    "                fd, feedbackBuf_ + feedbackBytes_, sizeof(feedbackBuf_) - feedbackBytes_, MSG_DONTWAIT);\n"
    "            if (n > 0) {\n"
    "                feedbackBytes_ += static_cast<size_t>(n);\n"
    "                if (feedbackBytes_ == sizeof(feedbackBuf_)) {\n"
    "                    RumblePacket feedback{};\n"
    "                    std::memcpy(&feedback, feedbackBuf_, sizeof(feedback));\n"
    "                    feedbackBytes_ = 0;\n"
    "                    if (feedback.magic == kFeedbackMagic) {\n"
    "                        const uint16_t packed =\n"
    "                            (static_cast<uint16_t>(feedback.largeMotor) << 8) | feedback.smallMotor;\n"
    "                        rumblePacked_.store(packed, std::memory_order_relaxed);\n"
    "                    } else {\n"
    "                        LOGW(\"invalid rumble packet magic: 0x%08x\", feedback.magic);\n"
    "                    }\n"
    "                }\n"
    "                continue;\n"
    "            }\n"
    "            if (n == 0) {\n"
    "                dropClient(fd);\n"
    "                return 0;\n"
    "            }\n"
    "            if (errno == EAGAIN || errno == EWOULDBLOCK)\n"
    "                return rumblePacked_.load(std::memory_order_relaxed);\n"
    "            if (errno == EINTR) continue;\n"
    "            dropClient(fd);\n"
    "            return 0;\n"
    "        }\n"
    "    }\n\n"
    "    void sendPacket(const PadPacket& packet) {\n",
    "quest poll rumble")

q = replace_once(q,
    "        if (clientFd_.compare_exchange_strong(expected, -1)) {\n            close(fd);\n            LOGW(\"host disconnected\");\n",
    "        if (clientFd_.compare_exchange_strong(expected, -1)) {\n"
    "            close(fd);\n"
    "            rumblePacked_.store(0, std::memory_order_relaxed);\n"
    "            feedbackBytes_ = 0;\n"
    "            LOGW(\"host disconnected\");\n",
    "quest disconnect rumble reset")

q = replace_once(q,
    "            const int old = clientFd_.exchange(c);\n            if (old >= 0) close(old);\n",
    "            const int old = clientFd_.exchange(c);\n"
    "            if (old >= 0) close(old);\n"
    "            rumblePacked_.store(0, std::memory_order_relaxed);\n"
    "            feedbackBytes_ = 0;\n",
    "quest accept rumble reset")

q = replace_once(q,
    "    std::atomic<int> clientFd_{-1};\n    std::thread thread_;\n",
    "    std::atomic<int> clientFd_{-1};\n"
    "    std::atomic<uint16_t> rumblePacked_{0};\n"
    "    uint8_t feedbackBuf_[sizeof(RumblePacket)]{};\n"
    "    size_t feedbackBytes_ = 0;\n"
    "    std::thread thread_;\n",
    "quest feedback fields")

q = replace_once(q,
    "    XrAction view = XR_NULL_HANDLE;\n};\n",
    "    XrAction view = XR_NULL_HANDLE;\n"
    "    XrAction lHaptic = XR_NULL_HANDLE;\n"
    "    XrAction rHaptic = XR_NULL_HANDLE;\n"
    "};\n",
    "quest haptic actions fields")

q = replace_once(q,
    "    a.view = makeAction(a.set, XR_ACTION_TYPE_BOOLEAN_INPUT, \"view\", \"View\");\n\n",
    "    a.view = makeAction(a.set, XR_ACTION_TYPE_BOOLEAN_INPUT, \"view\", \"View\");\n"
    "    a.lHaptic = makeAction(a.set, XR_ACTION_TYPE_VIBRATION_OUTPUT, \"left_haptic\", \"Left haptic\");\n"
    "    a.rHaptic = makeAction(a.set, XR_ACTION_TYPE_VIBRATION_OUTPUT, \"right_haptic\", \"Right haptic\");\n\n",
    "quest haptic action creation")

q = replace_once(q,
    "    bind(a.view, \"/user/hand/left/input/menu/click\");\n\n",
    "    bind(a.view, \"/user/hand/left/input/menu/click\");\n"
    "    bind(a.lHaptic, \"/user/hand/left/output/haptic\");\n"
    "    bind(a.rHaptic, \"/user/hand/right/output/haptic\");\n\n",
    "quest haptic bindings")

q = replace_once(q,
    "XrActionStateVector2f getVec2(XrSession s, XrAction a) {\n    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO}; gi.action = a;\n    XrActionStateVector2f st{XR_TYPE_ACTION_STATE_VECTOR2F};\n    xrGetActionStateVector2f(s, &gi, &st);\n    return st;\n}\n\n",
    "XrActionStateVector2f getVec2(XrSession s, XrAction a) {\n"
    "    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO}; gi.action = a;\n"
    "    XrActionStateVector2f st{XR_TYPE_ACTION_STATE_VECTOR2F};\n"
    "    xrGetActionStateVector2f(s, &gi, &st);\n"
    "    return st;\n"
    "}\n\n"
    "void setHaptic(XrSession session, XrAction action, uint8_t intensity) {\n"
    "    XrHapticActionInfo info{XR_TYPE_HAPTIC_ACTION_INFO};\n"
    "    info.action = action;\n"
    "    if (intensity == 0) {\n"
    "        xrStopHapticFeedback(session, &info);\n"
    "        return;\n"
    "    }\n"
    "    XrHapticVibration vibration{XR_TYPE_HAPTIC_VIBRATION};\n"
    "    vibration.duration = 100'000'000; // 100 ms; refreshed before expiry\n"
    "    vibration.frequency = XR_FREQUENCY_UNSPECIFIED;\n"
    "    vibration.amplitude = static_cast<float>(intensity) / 255.0f;\n"
    "    xrApplyHapticFeedback(\n"
    "        session, &info, reinterpret_cast<const XrHapticBaseHeader*>(&vibration));\n"
    "}\n\n",
    "quest haptic helper")

q = replace_once(q,
    "    uint64_t nextThermalPoll = 0;\n\n",
    "    uint64_t nextThermalPoll = 0;\n"
    "    uint16_t lastRumble = 0;\n"
    "    uint64_t nextHapticRefresh = 0;\n\n",
    "quest haptic state")

q = replace_once(q,
    "        // If the app loses XR focus, the packet stays neutral by construction.\n        bridge.sendPacket(packet);\n\n",
    "        // If the app loses XR focus, the packet stays neutral by construction.\n"
    "        bridge.sendPacket(packet);\n\n"
    "        // Reverse feedback path: preserve the Xbox 360 two-motor distinction by\n"
    "        // mapping the large/low-frequency motor to the left Touch controller and\n"
    "        // the small/high-frequency motor to the right. OpenXR runtimes choose the\n"
    "        // actual actuator frequency when XR_FREQUENCY_UNSPECIFIED is used.\n"
    "        const uint16_t rumble = bridge.pollRumble();\n"
    "        const uint8_t largeMotor = static_cast<uint8_t>((rumble >> 8) & 0xFF);\n"
    "        const uint8_t smallMotor = static_cast<uint8_t>(rumble & 0xFF);\n"
    "        if (!effectiveFocused || rumble == 0) {\n"
    "            if (lastRumble != 0) {\n"
    "                setHaptic(session, actions.lHaptic, 0);\n"
    "                setHaptic(session, actions.rHaptic, 0);\n"
    "            }\n"
    "            lastRumble = 0;\n"
    "            nextHapticRefresh = 0;\n"
    "        } else if (rumble != lastRumble || packet.monotonicNs >= nextHapticRefresh) {\n"
    "            setHaptic(session, actions.lHaptic, largeMotor);\n"
    "            setHaptic(session, actions.rHaptic, smallMotor);\n"
    "            lastRumble = rumble;\n"
    "            nextHapticRefresh = packet.monotonicNs + 75'000'000ULL;\n"
    "        }\n\n",
    "quest reverse feedback loop")

q = replace_once(q,
    "    bridge.stop();\n    if (sessionActive) xrEndSession(session);\n",
    "    setHaptic(session, actions.lHaptic, 0);\n"
    "    setHaptic(session, actions.rHaptic, 0);\n"
    "    bridge.stop();\n"
    "    if (sessionActive) xrEndSession(session);\n",
    "quest shutdown haptic stop")

quest_path.write_text(q, encoding="utf-8")
print("patched quest/src/main/cpp/questpad.cpp for rumble")
