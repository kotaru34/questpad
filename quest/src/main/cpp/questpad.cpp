#include <android/log.h>
#include <android/native_activity.h>
#include <android_native_app_glue.h>
#include <jni.h>

#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>

#define XR_USE_PLATFORM_ANDROID 1
#define XR_USE_GRAPHICS_API_OPENGL_ES 1
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

#include "passthrough_support.h"

#include <arpa/inet.h>
#include <errno.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <thread>
#include <vector>

namespace {
constexpr const char* kTag = "QuestPad";
constexpr uint16_t kPort = 38888;
constexpr uint32_t kMagic = 0x44415051u; // QPAD little-endian
constexpr uint32_t kFeedbackMagic = 0x31424651u; // QFB1 little-endian
constexpr uint16_t kProtocolVersion = 2;
constexpr uint16_t kControlPassthrough = 1u << 8;
constexpr uint64_t kExitHoldNs = 3'000'000'000ULL;
constexpr uint64_t kExitPulseNs = 125'000'000ULL;
constexpr float kShoulderPressThreshold = 0.62f;
constexpr float kShoulderReleaseThreshold = 0.45f;

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, kTag, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, kTag, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, kTag, __VA_ARGS__)

uint64_t monoNs() {
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<uint64_t>(ts.tv_sec) * 1'000'000'000ULL + static_cast<uint64_t>(ts.tv_nsec);
}

struct __attribute__((packed)) PadPacket {
    uint32_t magic;
    uint16_t version;
    uint16_t size;
    uint32_t sequence;
    uint32_t flags;
    uint64_t monotonicNs;
    int32_t thermalStatus;
    float lx;
    float ly;
    float rx;
    float ry;
    float lt;
    float rt;
    float lg;
    float rg;
    uint32_t buttons;
    uint32_t reserved; // battery telemetry, unchanged from protocol v1

    uint32_t motionFlags;
    XrQuaternionf leftOrientation;
    XrQuaternionf rightOrientation;
    XrVector3f leftPosition;
    XrVector3f rightPosition;
    XrVector3f leftAngularLocal;
    XrVector3f rightAngularLocal;
};
static_assert(sizeof(PadPacket) == 152, "PadPacket protocol v2 wire size changed");

struct __attribute__((packed)) RumblePacket {
    uint32_t magic;
    uint8_t largeMotor;
    uint8_t smallMotor;
    uint16_t control; // host -> Quest motion + view control word
};
static_assert(sizeof(RumblePacket) == 8, "RumblePacket wire size changed");

enum PacketFlags : uint32_t {
    FLAG_SESSION_ACTIVE = 1u << 0,
    FLAG_FOCUSED = 1u << 1,
    FLAG_LEFT_ACTIVE = 1u << 2,
    FLAG_RIGHT_ACTIVE = 1u << 3,
    FLAG_EXIT_ARMED = 1u << 4,
    FLAG_PASSTHROUGH_AVAILABLE = 1u << 5,
    FLAG_PASSTHROUGH_ACTIVE = 1u << 6,
};

enum Buttons : uint32_t {
    BTN_A = 1u << 0,
    BTN_B = 1u << 1,
    BTN_X = 1u << 2,
    BTN_Y = 1u << 3,
    BTN_LTHUMB = 1u << 4,
    BTN_RTHUMB = 1u << 5,
    BTN_VIEW = 1u << 6,
};

enum MotionFlags : uint32_t {
    MOTION_LEFT_ACTIVE = 1u << 0,
    MOTION_LEFT_OV = 1u << 1,
    MOTION_LEFT_OT = 1u << 2,
    MOTION_LEFT_PV = 1u << 3,
    MOTION_LEFT_PT = 1u << 4,
    MOTION_LEFT_AV = 1u << 5,
    MOTION_RIGHT_ACTIVE = 1u << 8,
    MOTION_RIGHT_OV = 1u << 9,
    MOTION_RIGHT_OT = 1u << 10,
    MOTION_RIGHT_PV = 1u << 11,
    MOTION_RIGHT_PT = 1u << 12,
    MOTION_RIGHT_AV = 1u << 13,
    MOTION_QUERIED = 1u << 16,
};

enum MotionRequest : uint16_t {
    MOTION_REQUEST_NONE = 0,
    MOTION_REQUEST_RIGHT_ANGULAR = 1,
    MOTION_REQUEST_RIGHT_TRACKED = 2,
    MOTION_REQUEST_BOTH_TRACKED = 3,
};

struct FeedbackState {
    uint8_t large = 0;
    uint8_t small = 0;
    uint16_t control = 0;
};

class BridgeServer {
public:
    ~BridgeServer() { stop(); }

    void start() {
        if (running_.exchange(true)) return;
        thread_ = std::thread([this] { run(); });
    }

    void stop() {
        if (!running_.exchange(false)) return;
        int listen = listenFd_.exchange(-1);
        if (listen >= 0) close(listen);
        int client = clientFd_.exchange(-1);
        if (client >= 0) close(client);
        if (thread_.joinable()) thread_.join();
    }

    FeedbackState pollFeedback() {
        int fd = clientFd_.load(std::memory_order_relaxed);
        if (fd != feedbackFd_) {
            feedbackFd_ = fd;
            feedbackBytes_ = 0;
            feedbackWord_.store(0, std::memory_order_relaxed);
        }
        if (fd < 0) return {};

        for (;;) {
            ssize_t n = ::recv(fd, feedbackBuf_ + feedbackBytes_, sizeof(feedbackBuf_) - feedbackBytes_, MSG_DONTWAIT);
            if (n > 0) {
                feedbackBytes_ += static_cast<size_t>(n);
                if (feedbackBytes_ == sizeof(feedbackBuf_)) {
                    RumblePacket p{};
                    std::memcpy(&p, feedbackBuf_, sizeof(p));
                    feedbackBytes_ = 0;
                    if (p.magic == kFeedbackMagic) {
                        uint32_t word = static_cast<uint32_t>(p.largeMotor) |
                            (static_cast<uint32_t>(p.smallMotor) << 8) |
                            (static_cast<uint32_t>(p.control) << 16);
                        feedbackWord_.store(word, std::memory_order_relaxed);
                    } else {
                        LOGW("invalid feedback magic: 0x%08x", p.magic);
                    }
                }
                continue;
            }
            if (n == 0) {
                dropClient(fd);
                return {};
            }
            if (errno == EAGAIN || errno == EWOULDBLOCK) break;
            if (errno == EINTR) continue;
            dropClient(fd);
            return {};
        }

        uint32_t word = feedbackWord_.load(std::memory_order_relaxed);
        return {
            static_cast<uint8_t>(word & 0xff),
            static_cast<uint8_t>((word >> 8) & 0xff),
            static_cast<uint16_t>((word >> 16) & 0xffff)
        };
    }

    void sendPacket(const PadPacket& packet) {
        int fd = clientFd_.load(std::memory_order_relaxed);
        if (fd < 0) return;
        const uint8_t* p = reinterpret_cast<const uint8_t*>(&packet);
        size_t left = sizeof(packet);
        while (left > 0) {
            ssize_t n = ::send(fd, p, left, MSG_NOSIGNAL | MSG_DONTWAIT);
            if (n > 0) {
                p += n;
                left -= static_cast<size_t>(n);
                continue;
            }
            if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
                if (left != sizeof(packet)) dropClient(fd);
                return;
            }
            if (n < 0 && errno == EINTR) continue;
            dropClient(fd);
            return;
        }
    }

private:
    void dropClient(int fd) {
        int expected = fd;
        if (clientFd_.compare_exchange_strong(expected, -1)) {
            close(fd);
            feedbackWord_.store(0, std::memory_order_relaxed);
            feedbackBytes_ = 0;
            LOGW("host disconnected");
        }
    }

    void run() {
        int s = socket(AF_INET, SOCK_STREAM, 0);
        if (s < 0) {
            LOGE("socket() failed: %d", errno);
            running_ = false;
            return;
        }
        listenFd_ = s;
        int one = 1;
        setsockopt(s, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));

        sockaddr_in addr{};
        addr.sin_family = AF_INET;
        addr.sin_port = htons(kPort);
        addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        if (bind(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) < 0 || listen(s, 1) < 0) {
            LOGE("bind/listen failed: %d", errno);
            close(s);
            listenFd_ = -1;
            running_ = false;
            return;
        }
        LOGI("bridge listening on 127.0.0.1:%u", kPort);

        while (running_.load()) {
            sockaddr_in peer{};
            socklen_t len = sizeof(peer);
            int c = accept(s, reinterpret_cast<sockaddr*>(&peer), &len);
            if (c < 0) {
                if (!running_.load()) break;
                if (errno == EINTR) continue;
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                continue;
            }
            int old = clientFd_.exchange(c);
            if (old >= 0) close(old);
            feedbackWord_.store(0, std::memory_order_relaxed);
            int snd = 8192;
            setsockopt(c, SOL_SOCKET, SO_SNDBUF, &snd, sizeof(snd));
            int noDelay = 1;
            setsockopt(c, IPPROTO_TCP, TCP_NODELAY, &noDelay, sizeof(noDelay));
            LOGI("host connected (TCP_NODELAY)");
        }
    }

    std::atomic<bool> running_{false};
    std::atomic<int> listenFd_{-1};
    std::atomic<int> clientFd_{-1};
    std::atomic<uint32_t> feedbackWord_{0};
    uint8_t feedbackBuf_[sizeof(RumblePacket)]{};
    size_t feedbackBytes_ = 0;
    int feedbackFd_ = -1;
    std::thread thread_;
};

struct EglState {
    EGLDisplay display = EGL_NO_DISPLAY;
    EGLConfig config = nullptr;
    EGLContext context = EGL_NO_CONTEXT;
    EGLSurface surface = EGL_NO_SURFACE;
};

bool createEgl(EglState& e) {
    e.display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    if (e.display == EGL_NO_DISPLAY) return false;
    EGLint major = 0, minor = 0;
    if (!eglInitialize(e.display, &major, &minor)) return false;
    const EGLint cfg[] = {
        EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT_KHR,
        EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,
        EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
        EGL_NONE};
    EGLint count = 0;
    if (!eglChooseConfig(e.display, cfg, &e.config, 1, &count) || count < 1) return false;
    const EGLint ctx[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE};
    e.context = eglCreateContext(e.display, e.config, EGL_NO_CONTEXT, ctx);
    if (e.context == EGL_NO_CONTEXT) return false;
    const EGLint surf[] = {EGL_WIDTH, 16, EGL_HEIGHT, 16, EGL_NONE};
    e.surface = eglCreatePbufferSurface(e.display, e.config, surf);
    if (e.surface == EGL_NO_SURFACE) return false;
    if (!eglMakeCurrent(e.display, e.surface, e.surface, e.context)) return false;
    LOGI("EGL %d.%d ready", major, minor);
    return true;
}

void destroyEgl(EglState& e) {
    if (e.display != EGL_NO_DISPLAY) {
        eglMakeCurrent(e.display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        if (e.surface != EGL_NO_SURFACE) eglDestroySurface(e.display, e.surface);
        if (e.context != EGL_NO_CONTEXT) eglDestroyContext(e.display, e.context);
        eglTerminate(e.display);
    }
    e = {};
}

bool xrOk(XrInstance instance, XrResult result, const char* what) {
    if (XR_SUCCEEDED(result)) return true;
    char text[XR_MAX_RESULT_STRING_SIZE]{};
    if (instance != XR_NULL_HANDLE) xrResultToString(instance, result, text);
    LOGE("%s failed: %d %s", what, result, text);
    return false;
}

bool hasExtension(const char* name) {
    uint32_t count = 0;
    if (XR_FAILED(xrEnumerateInstanceExtensionProperties(nullptr, 0, &count, nullptr))) return false;
    std::vector<XrExtensionProperties> props(count);
    for (auto& p : props) { p.type = XR_TYPE_EXTENSION_PROPERTIES; p.next = nullptr; }
    if (XR_FAILED(xrEnumerateInstanceExtensionProperties(nullptr, count, &count, props.data()))) return false;
    for (const auto& p : props) if (std::strcmp(p.extensionName, name) == 0) return true;
    return false;
}

XrPosef identityPose() {
    XrPosef p{};
    p.orientation.w = 1.0f;
    return p;
}

XrAction makeAction(XrActionSet set, XrActionType type, const char* name, const char* pretty) {
    XrActionCreateInfo ci{XR_TYPE_ACTION_CREATE_INFO};
    ci.actionType = type;
    std::strncpy(ci.actionName, name, XR_MAX_ACTION_NAME_SIZE - 1);
    std::strncpy(ci.localizedActionName, pretty, XR_MAX_LOCALIZED_ACTION_NAME_SIZE - 1);
    XrAction action = XR_NULL_HANDLE;
    if (XR_FAILED(xrCreateAction(set, &ci, &action))) return XR_NULL_HANDLE;
    return action;
}

struct Actions {
    XrActionSet set = XR_NULL_HANDLE;
    XrAction lStick = XR_NULL_HANDLE, rStick = XR_NULL_HANDLE;
    XrAction lTrigger = XR_NULL_HANDLE, rTrigger = XR_NULL_HANDLE;
    XrAction lGrip = XR_NULL_HANDLE, rGrip = XR_NULL_HANDLE;
    XrAction a = XR_NULL_HANDLE, b = XR_NULL_HANDLE, x = XR_NULL_HANDLE, y = XR_NULL_HANDLE;
    XrAction lThumb = XR_NULL_HANDLE, rThumb = XR_NULL_HANDLE, view = XR_NULL_HANDLE;
    XrAction lHaptic = XR_NULL_HANDLE, rHaptic = XR_NULL_HANDLE;
    XrAction lPose = XR_NULL_HANDLE, rPose = XR_NULL_HANDLE;
};

bool setupActions(XrInstance inst, XrSession session, Actions& a, bool touchPlusExt) {
    XrActionSetCreateInfo setInfo{XR_TYPE_ACTION_SET_CREATE_INFO};
    std::strncpy(setInfo.actionSetName, "gamepad", XR_MAX_ACTION_SET_NAME_SIZE - 1);
    std::strncpy(setInfo.localizedActionSetName, "QuestPad Gamepad", XR_MAX_LOCALIZED_ACTION_SET_NAME_SIZE - 1);
    if (!xrOk(inst, xrCreateActionSet(inst, &setInfo, &a.set), "xrCreateActionSet")) return false;

    a.lStick = makeAction(a.set, XR_ACTION_TYPE_VECTOR2F_INPUT, "left_stick", "Left stick");
    a.rStick = makeAction(a.set, XR_ACTION_TYPE_VECTOR2F_INPUT, "right_stick", "Right stick");
    a.lTrigger = makeAction(a.set, XR_ACTION_TYPE_FLOAT_INPUT, "left_trigger", "Left trigger");
    a.rTrigger = makeAction(a.set, XR_ACTION_TYPE_FLOAT_INPUT, "right_trigger", "Right trigger");
    a.lGrip = makeAction(a.set, XR_ACTION_TYPE_FLOAT_INPUT, "left_grip", "Left grip");
    a.rGrip = makeAction(a.set, XR_ACTION_TYPE_FLOAT_INPUT, "right_grip", "Right grip");
    a.a = makeAction(a.set, XR_ACTION_TYPE_BOOLEAN_INPUT, "button_a", "A");
    a.b = makeAction(a.set, XR_ACTION_TYPE_BOOLEAN_INPUT, "button_b", "B");
    a.x = makeAction(a.set, XR_ACTION_TYPE_BOOLEAN_INPUT, "button_x", "X");
    a.y = makeAction(a.set, XR_ACTION_TYPE_BOOLEAN_INPUT, "button_y", "Y");
    a.lThumb = makeAction(a.set, XR_ACTION_TYPE_BOOLEAN_INPUT, "left_thumb_click", "Left thumb click");
    a.rThumb = makeAction(a.set, XR_ACTION_TYPE_BOOLEAN_INPUT, "right_thumb_click", "Right thumb click");
    a.view = makeAction(a.set, XR_ACTION_TYPE_BOOLEAN_INPUT, "view", "View");
    a.lHaptic = makeAction(a.set, XR_ACTION_TYPE_VIBRATION_OUTPUT, "left_haptic", "Left haptic");
    a.rHaptic = makeAction(a.set, XR_ACTION_TYPE_VIBRATION_OUTPUT, "right_haptic", "Right haptic");
    a.lPose = makeAction(a.set, XR_ACTION_TYPE_POSE_INPUT, "left_grip_pose", "Left grip pose");
    a.rPose = makeAction(a.set, XR_ACTION_TYPE_POSE_INPUT, "right_grip_pose", "Right grip pose");

    std::vector<XrActionSuggestedBinding> bindings;
    auto bind = [&](XrAction action, const char* pathText) {
        XrPath p = XR_NULL_PATH;
        if (XR_SUCCEEDED(xrStringToPath(inst, pathText, &p))) bindings.push_back({action, p});
    };
    bind(a.lStick, "/user/hand/left/input/thumbstick");
    bind(a.rStick, "/user/hand/right/input/thumbstick");
    bind(a.lTrigger, "/user/hand/left/input/trigger/value");
    bind(a.rTrigger, "/user/hand/right/input/trigger/value");
    bind(a.lGrip, "/user/hand/left/input/squeeze/value");
    bind(a.rGrip, "/user/hand/right/input/squeeze/value");
    bind(a.a, "/user/hand/right/input/a/click");
    bind(a.b, "/user/hand/right/input/b/click");
    bind(a.x, "/user/hand/left/input/x/click");
    bind(a.y, "/user/hand/left/input/y/click");
    bind(a.lThumb, "/user/hand/left/input/thumbstick/click");
    bind(a.rThumb, "/user/hand/right/input/thumbstick/click");
    bind(a.view, "/user/hand/left/input/menu/click");
    bind(a.lHaptic, "/user/hand/left/output/haptic");
    bind(a.rHaptic, "/user/hand/right/output/haptic");
    bind(a.lPose, "/user/hand/left/input/grip/pose");
    bind(a.rPose, "/user/hand/right/input/grip/pose");

    auto suggest = [&](const char* profileText) {
        XrPath profile = XR_NULL_PATH;
        if (XR_FAILED(xrStringToPath(inst, profileText, &profile))) return false;
        XrInteractionProfileSuggestedBinding s{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING};
        s.interactionProfile = profile;
        s.countSuggestedBindings = static_cast<uint32_t>(bindings.size());
        s.suggestedBindings = bindings.data();
        return XR_SUCCEEDED(xrSuggestInteractionProfileBindings(inst, &s));
    };

    if (!suggest("/interaction_profiles/oculus/touch_controller")) return false;
    if (touchPlusExt && !suggest("/interaction_profiles/meta/touch_plus_controller"))
        LOGW("Touch Plus binding suggestion failed; using legacy Touch profile");

    XrSessionActionSetsAttachInfo attach{XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO};
    attach.countActionSets = 1;
    attach.actionSets = &a.set;
    return xrOk(inst, xrAttachSessionActionSets(session, &attach), "xrAttachSessionActionSets");
}

XrActionStateBoolean getBool(XrSession s, XrAction a) {
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO}; gi.action = a;
    XrActionStateBoolean st{XR_TYPE_ACTION_STATE_BOOLEAN}; xrGetActionStateBoolean(s, &gi, &st); return st;
}
XrActionStateFloat getFloat(XrSession s, XrAction a) {
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO}; gi.action = a;
    XrActionStateFloat st{XR_TYPE_ACTION_STATE_FLOAT}; xrGetActionStateFloat(s, &gi, &st); return st;
}
XrActionStateVector2f getVec2(XrSession s, XrAction a) {
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO}; gi.action = a;
    XrActionStateVector2f st{XR_TYPE_ACTION_STATE_VECTOR2F}; xrGetActionStateVector2f(s, &gi, &st); return st;
}
bool poseActive(XrSession s, XrAction a) {
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO}; gi.action = a;
    XrActionStatePose st{XR_TYPE_ACTION_STATE_POSE};
    return XR_SUCCEEDED(xrGetActionStatePose(s, &gi, &st)) && st.isActive;
}

struct BatteryReading { bool valid = false; bool charging = false; float level = 0; };

BatteryReading getBatteryState(XrSession session, XrPath userPath) {
    BatteryReading result{};
    XrBatteryStateDisplayEXT battery{XR_TYPE_BATTERY_STATE_DISPLAY_EXT};
    XrInteractionProfileState profile{XR_TYPE_INTERACTION_PROFILE_STATE};
    profile.next = &battery;
    if (XR_FAILED(xrGetCurrentInteractionProfile(session, userPath, &profile))) return result;
    if ((battery.stateFlags & XR_BATTERY_STATE_DISPLAY_STATE_VALID_BIT_EXT) == 0) return result;
    result.valid = true;
    result.charging = (battery.stateFlags & XR_BATTERY_STATE_DISPLAY_STATE_CHARGING_BIT_EXT) != 0;
    result.level = std::clamp(battery.batteryLevel, 0.0f, 1.0f);
    return result;
}

uint32_t packBatteryState(const BatteryReading& left, const BatteryReading& right) {
    uint32_t packed = 0;
    if (left.valid) {
        packed |= std::min(static_cast<uint32_t>(std::lround(left.level * 100.0f)), 100u);
        packed |= 1u << 16;
        if (left.charging) packed |= 1u << 18;
    }
    if (right.valid) {
        packed |= std::min(static_cast<uint32_t>(std::lround(right.level * 100.0f)), 100u) << 8;
        packed |= 1u << 17;
        if (right.charging) packed |= 1u << 19;
    }
    return packed;
}

void setHaptic(XrSession session, XrAction action, uint8_t intensity) {
    XrHapticActionInfo info{XR_TYPE_HAPTIC_ACTION_INFO}; info.action = action;
    if (intensity == 0) { xrStopHapticFeedback(session, &info); return; }
    XrHapticVibration v{XR_TYPE_HAPTIC_VIBRATION};
    v.duration = 100'000'000; v.frequency = XR_FREQUENCY_UNSPECIFIED; v.amplitude = intensity / 255.0f;
    xrApplyHapticFeedback(session, &info, reinterpret_cast<const XrHapticBaseHeader*>(&v));
}

void setLowBrightnessAndKeepAwake(ANativeActivity* activity) {
    JNIEnv* env = nullptr;
    activity->vm->AttachCurrentThread(&env, nullptr);
    jobject act = activity->clazz;
    jclass ac = env->GetObjectClass(act);
    jmethodID getWindow = env->GetMethodID(ac, "getWindow", "()Landroid/view/Window;");
    jobject window = env->CallObjectMethod(act, getWindow);
    if (window) {
        jclass wc = env->GetObjectClass(window);
        env->CallVoidMethod(window, env->GetMethodID(wc, "addFlags", "(I)V"), 0x80);
        jmethodID getAttrs = env->GetMethodID(wc, "getAttributes", "()Landroid/view/WindowManager$LayoutParams;");
        jobject attrs = env->CallObjectMethod(window, getAttrs);
        if (attrs) {
            jclass alc = env->GetObjectClass(attrs);
            env->SetFloatField(attrs, env->GetFieldID(alc, "screenBrightness", "F"), 0.0f);
            env->CallVoidMethod(window, env->GetMethodID(wc, "setAttributes", "(Landroid/view/WindowManager$LayoutParams;)V"), attrs);
            env->DeleteLocalRef(alc); env->DeleteLocalRef(attrs);
        }
        env->DeleteLocalRef(wc); env->DeleteLocalRef(window);
    }
    env->DeleteLocalRef(ac);
}

int getThermalStatus(ANativeActivity* activity) {
    JNIEnv* env = nullptr;
    activity->vm->AttachCurrentThread(&env, nullptr);
    jclass contextClass = env->FindClass("android/content/Context");
    jfieldID powerField = env->GetStaticFieldID(contextClass, "POWER_SERVICE", "Ljava/lang/String;");
    jobject powerName = env->GetStaticObjectField(contextClass, powerField);
    jclass ac = env->GetObjectClass(activity->clazz);
    jmethodID getService = env->GetMethodID(ac, "getSystemService", "(Ljava/lang/String;)Ljava/lang/Object;");
    jobject pm = env->CallObjectMethod(activity->clazz, getService, powerName);
    int value = -1;
    if (pm) {
        jclass pc = env->GetObjectClass(pm);
        jmethodID getCurrent = env->GetMethodID(pc, "getCurrentThermalStatus", "()I");
        if (getCurrent) value = env->CallIntMethod(pm, getCurrent);
        env->DeleteLocalRef(pc); env->DeleteLocalRef(pm);
    }
    env->DeleteLocalRef(ac); env->DeleteLocalRef(contextClass);
    return value;
}

void requestLowRefreshRate(XrInstance instance, XrSession session) {
    PFN_xrEnumerateDisplayRefreshRatesFB enumerate = nullptr;
    PFN_xrRequestDisplayRefreshRateFB request = nullptr;
    xrGetInstanceProcAddr(instance, "xrEnumerateDisplayRefreshRatesFB", reinterpret_cast<PFN_xrVoidFunction*>(&enumerate));
    xrGetInstanceProcAddr(instance, "xrRequestDisplayRefreshRateFB", reinterpret_cast<PFN_xrVoidFunction*>(&request));
    if (!enumerate || !request) return;
    uint32_t count = 0;
    if (XR_FAILED(enumerate(session, 0, &count, nullptr)) || count == 0) return;
    std::vector<float> rates(count);
    if (XR_FAILED(enumerate(session, count, &count, rates.data()))) return;
    float selected = rates.front();
    for (float r : rates) if (std::fabs(r - 72.0f) < std::fabs(selected - 72.0f)) selected = r;
    if (XR_SUCCEEDED(request(session, selected))) LOGI("requested display refresh %.1f Hz", selected);
}

struct MotionOutput {
    uint32_t flags = 0;
    XrQuaternionf orientation{};
    XrVector3f position{};
    XrVector3f angularLocal{};
};

void locateTracked(
    XrSpace controllerSpace,
    XrSpace localSpace,
    XrTime time,
    bool active,
    bool left,
    MotionOutput& out)
{
    const uint32_t activeBit = left ? MOTION_LEFT_ACTIVE : MOTION_RIGHT_ACTIVE;
    const uint32_t ovBit = left ? MOTION_LEFT_OV : MOTION_RIGHT_OV;
    const uint32_t otBit = left ? MOTION_LEFT_OT : MOTION_RIGHT_OT;
    const uint32_t pvBit = left ? MOTION_LEFT_PV : MOTION_RIGHT_PV;
    const uint32_t ptBit = left ? MOTION_LEFT_PT : MOTION_RIGHT_PT;
    const uint32_t avBit = left ? MOTION_LEFT_AV : MOTION_RIGHT_AV;
    if (!active) return;
    out.flags |= activeBit;

    XrSpaceLocation location{XR_TYPE_SPACE_LOCATION};
    if (XR_SUCCEEDED(xrLocateSpace(controllerSpace, localSpace, time, &location))) {
        if (location.locationFlags & XR_SPACE_LOCATION_ORIENTATION_VALID_BIT) { out.flags |= ovBit; out.orientation = location.pose.orientation; }
        if (location.locationFlags & XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT) out.flags |= otBit;
        if (location.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT) { out.flags |= pvBit; out.position = location.pose.position; }
        if (location.locationFlags & XR_SPACE_LOCATION_POSITION_TRACKED_BIT) out.flags |= ptBit;
    }

    // Inverse locate makes the returned velocity expressed in controller space.
    // Negating it yields controller angular velocity relative to LOCAL, still in
    // controller-local axes. This is the closest public OpenXR path to a gyro-only
    // stream without the host consuming orientation/position data.
    XrSpaceVelocity inverseVelocity{XR_TYPE_SPACE_VELOCITY};
    XrSpaceLocation inverseLocation{XR_TYPE_SPACE_LOCATION};
    inverseLocation.next = &inverseVelocity;
    if (XR_SUCCEEDED(xrLocateSpace(localSpace, controllerSpace, time, &inverseLocation)) &&
        (inverseVelocity.velocityFlags & XR_SPACE_VELOCITY_ANGULAR_VALID_BIT)) {
        out.flags |= avBit;
        out.angularLocal = {
            -inverseVelocity.angularVelocity.x,
            -inverseVelocity.angularVelocity.y,
            -inverseVelocity.angularVelocity.z};
    }
}

void locateAngularOnly(XrSpace controllerSpace, XrSpace localSpace, XrTime time, bool active, bool left, MotionOutput& out) {
    const uint32_t activeBit = left ? MOTION_LEFT_ACTIVE : MOTION_RIGHT_ACTIVE;
    const uint32_t avBit = left ? MOTION_LEFT_AV : MOTION_RIGHT_AV;
    if (!active) return;
    out.flags |= activeBit;
    XrSpaceVelocity velocity{XR_TYPE_SPACE_VELOCITY};
    XrSpaceLocation location{XR_TYPE_SPACE_LOCATION};
    location.next = &velocity;
    if (XR_SUCCEEDED(xrLocateSpace(localSpace, controllerSpace, time, &location)) &&
        (velocity.velocityFlags & XR_SPACE_VELOCITY_ANGULAR_VALID_BIT)) {
        out.flags |= avBit;
        out.angularLocal = {-velocity.angularVelocity.x, -velocity.angularVelocity.y, -velocity.angularVelocity.z};
    }
}

} // namespace

void android_main(android_app* app) {
    app_dummy();
    LOGI("QuestPad protocol v2 starting");
    setLowBrightnessAndKeepAwake(app->activity);

    JNIEnv* env = nullptr;
    app->activity->vm->AttachCurrentThread(&env, nullptr);

    PFN_xrInitializeLoaderKHR initializeLoader = nullptr;
    xrGetInstanceProcAddr(XR_NULL_HANDLE, "xrInitializeLoaderKHR", reinterpret_cast<PFN_xrVoidFunction*>(&initializeLoader));
    if (initializeLoader) {
        XrLoaderInitInfoAndroidKHR li{XR_TYPE_LOADER_INIT_INFO_ANDROID_KHR};
        li.applicationVM = app->activity->vm;
        li.applicationContext = app->activity->clazz;
        if (XR_FAILED(initializeLoader(reinterpret_cast<XrLoaderInitInfoBaseHeaderKHR*>(&li)))) return;
    }

    if (!hasExtension(XR_KHR_OPENGL_ES_ENABLE_EXTENSION_NAME)) return;
    std::vector<const char*> extensions{XR_KHR_OPENGL_ES_ENABLE_EXTENSION_NAME};
    bool perfExt = hasExtension(XR_EXT_PERFORMANCE_SETTINGS_EXTENSION_NAME);
    bool refreshExt = hasExtension(XR_FB_DISPLAY_REFRESH_RATE_EXTENSION_NAME);
    bool touchPlusExt = hasExtension(XR_META_TOUCH_CONTROLLER_PLUS_EXTENSION_NAME);
    bool batteryExt = hasExtension(XR_EXT_INTERACTION_PROFILE_BATTERY_STATE_DISPLAY_EXTENSION_NAME);
    bool passthroughExt = hasExtension(XR_FB_PASSTHROUGH_EXTENSION_NAME);
    if (perfExt) extensions.push_back(XR_EXT_PERFORMANCE_SETTINGS_EXTENSION_NAME);
    if (refreshExt) extensions.push_back(XR_FB_DISPLAY_REFRESH_RATE_EXTENSION_NAME);
    if (touchPlusExt) extensions.push_back(XR_META_TOUCH_CONTROLLER_PLUS_EXTENSION_NAME);
    if (batteryExt) extensions.push_back(XR_EXT_INTERACTION_PROFILE_BATTERY_STATE_DISPLAY_EXTENSION_NAME);
    if (passthroughExt) extensions.push_back(XR_FB_PASSTHROUGH_EXTENSION_NAME);

    XrInstanceCreateInfo ici{XR_TYPE_INSTANCE_CREATE_INFO};
    std::strncpy(ici.applicationInfo.applicationName, "QuestPad", XR_MAX_APPLICATION_NAME_SIZE - 1);
    ici.applicationInfo.applicationVersion = 4;
    std::strncpy(ici.applicationInfo.engineName, "QuestPadNative", XR_MAX_ENGINE_NAME_SIZE - 1);
    ici.applicationInfo.engineVersion = 1;
    ici.applicationInfo.apiVersion = XR_API_VERSION_1_0;
    ici.enabledExtensionCount = static_cast<uint32_t>(extensions.size());
    ici.enabledExtensionNames = extensions.data();

    XrInstance instance = XR_NULL_HANDLE;
    if (!xrOk(instance, xrCreateInstance(&ici, &instance), "xrCreateInstance")) return;
    XrSystemGetInfo sgi{XR_TYPE_SYSTEM_GET_INFO}; sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId systemId = XR_NULL_SYSTEM_ID;
    if (!xrOk(instance, xrGetSystem(instance, &sgi, &systemId), "xrGetSystem")) { xrDestroyInstance(instance); return; }

    PFN_xrGetOpenGLESGraphicsRequirementsKHR getReq = nullptr;
    xrGetInstanceProcAddr(instance, "xrGetOpenGLESGraphicsRequirementsKHR", reinterpret_cast<PFN_xrVoidFunction*>(&getReq));
    XrGraphicsRequirementsOpenGLESKHR req{XR_TYPE_GRAPHICS_REQUIREMENTS_OPENGL_ES_KHR};
    if (!getReq || !xrOk(instance, getReq(instance, systemId, &req), "xrGetOpenGLESGraphicsRequirementsKHR")) { xrDestroyInstance(instance); return; }

    EglState egl;
    if (!createEgl(egl)) { xrDestroyInstance(instance); return; }
    XrGraphicsBindingOpenGLESAndroidKHR gb{XR_TYPE_GRAPHICS_BINDING_OPENGL_ES_ANDROID_KHR};
    gb.display = egl.display; gb.config = egl.config; gb.context = egl.context;
    XrSessionCreateInfo sci{XR_TYPE_SESSION_CREATE_INFO}; sci.next = &gb; sci.systemId = systemId;
    XrSession session = XR_NULL_HANDLE;
    if (!xrOk(instance, xrCreateSession(instance, &sci, &session), "xrCreateSession")) { destroyEgl(egl); xrDestroyInstance(instance); return; }

    questpad::PassthroughSupport passthrough;
    bool passthroughAvailable = passthrough.initialize(instance, systemId, session, passthroughExt);
    LOGI("XR_FB_passthrough %s", passthroughAvailable ? "available" : "unavailable");

    Actions actions;
    if (!setupActions(instance, session, actions, touchPlusExt)) { passthrough.destroy(app->activity); xrDestroySession(session); destroyEgl(egl); xrDestroyInstance(instance); return; }

    XrReferenceSpaceCreateInfo localInfo{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    localInfo.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    localInfo.poseInReferenceSpace = identityPose();
    XrSpace localSpace = XR_NULL_HANDLE;
    if (!xrOk(instance, xrCreateReferenceSpace(session, &localInfo, &localSpace), "xrCreateReferenceSpace")) return;

    auto makeActionSpace = [&](XrAction action, XrSpace& space) {
        XrActionSpaceCreateInfo ci{XR_TYPE_ACTION_SPACE_CREATE_INFO};
        ci.action = action; ci.poseInActionSpace = identityPose();
        return xrOk(instance, xrCreateActionSpace(session, &ci, &space), "xrCreateActionSpace");
    };
    XrSpace leftSpace = XR_NULL_HANDLE, rightSpace = XR_NULL_HANDLE;
    if (!makeActionSpace(actions.lPose, leftSpace) || !makeActionSpace(actions.rPose, rightSpace)) return;

    XrPath leftUserPath = XR_NULL_PATH, rightUserPath = XR_NULL_PATH;
    xrStringToPath(instance, "/user/hand/left", &leftUserPath);
    xrStringToPath(instance, "/user/hand/right", &rightUserPath);

    BridgeServer bridge; bridge.start();
    bool resumed = false, sessionActive = false, focused = false, quit = false, exitRequested = false;
    XrSessionState sessionState = XR_SESSION_STATE_UNKNOWN;
    uint32_t sequence = 0;
    uint64_t exitStartNs = 0, nextThermalPoll = 0, nextBatteryPoll = 0, exitPulseUntilNs = 0, nextHapticRefresh = 0;
    bool leftShoulder = false, rightShoulder = false;
    uint8_t exitPulseStage = 0, exitPulseStrength = 0;
    int thermal = -1;
    uint32_t batteryPacked = 0;
    uint16_t lastRumble = 0;

    app->userData = &resumed;
    app->onAppCmd = [](android_app* a, int32_t cmd) {
        auto* r = static_cast<bool*>(a->userData);
        if (!r) return;
        if (cmd == APP_CMD_RESUME) *r = true;
        if (cmd == APP_CMD_PAUSE || cmd == APP_CMD_STOP) *r = false;
    };

    while (!quit && !app->destroyRequested) {
        int events = 0; android_poll_source* source = nullptr;
        while (ALooper_pollOnce(sessionActive ? 0 : 10, nullptr, &events, reinterpret_cast<void**>(&source)) >= 0) {
            if (source) source->process(app, source);
            if (app->destroyRequested) { quit = true; break; }
            if (sessionActive) break;
        }

        XrEventDataBuffer event{XR_TYPE_EVENT_DATA_BUFFER};
        while (xrPollEvent(instance, &event) == XR_SUCCESS) {
            if (event.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
                auto* changed = reinterpret_cast<XrEventDataSessionStateChanged*>(&event);
                sessionState = changed->state;
                focused = changed->state == XR_SESSION_STATE_FOCUSED;
                if (changed->state == XR_SESSION_STATE_STOPPING && sessionActive) {
                    passthrough.setEnabled(false, app->activity);
                    xrEndSession(session); sessionActive = false; focused = false;
                } else if (changed->state == XR_SESSION_STATE_EXITING || changed->state == XR_SESSION_STATE_LOSS_PENDING) quit = true;
            }
            event = {XR_TYPE_EVENT_DATA_BUFFER};
        }

        if (!sessionActive && resumed && sessionState == XR_SESSION_STATE_READY) {
            XrSessionBeginInfo bi{XR_TYPE_SESSION_BEGIN_INFO}; bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
            if (XR_SUCCEEDED(xrBeginSession(session, &bi))) {
                sessionActive = true;
                LOGI("XR session active");
                if (perfExt) {
                    PFN_xrPerfSettingsSetPerformanceLevelEXT setPerf = nullptr;
                    xrGetInstanceProcAddr(instance, "xrPerfSettingsSetPerformanceLevelEXT", reinterpret_cast<PFN_xrVoidFunction*>(&setPerf));
                    if (setPerf) {
                        setPerf(session, XR_PERF_SETTINGS_DOMAIN_CPU_EXT, XR_PERF_SETTINGS_LEVEL_SUSTAINED_LOW_EXT);
                        setPerf(session, XR_PERF_SETTINGS_DOMAIN_GPU_EXT, XR_PERF_SETTINGS_LEVEL_SUSTAINED_LOW_EXT);
                    }
                }
                if (refreshExt) requestLowRefreshRate(instance, session);
            }
        }
        if (!sessionActive) continue;

        XrFrameWaitInfo wi{XR_TYPE_FRAME_WAIT_INFO}; XrFrameState fs{XR_TYPE_FRAME_STATE};
        if (XR_FAILED(xrWaitFrame(session, &wi, &fs))) continue;
        XrFrameBeginInfo fi{XR_TYPE_FRAME_BEGIN_INFO}; if (XR_FAILED(xrBeginFrame(session, &fi))) continue;

        FeedbackState feedback = bridge.pollFeedback();
        uint16_t motionRequest = feedback.control & 0x3u;
        bool wantPassthrough = (feedback.control & kControlPassthrough) != 0;
        passthrough.setEnabled(wantPassthrough, app->activity);

        PadPacket packet{};
        packet.magic = kMagic; packet.version = kProtocolVersion; packet.size = sizeof(PadPacket);
        packet.sequence = sequence++; packet.monotonicNs = monoNs();
        bool effectiveFocused = focused && resumed && !exitRequested;
        if (sessionActive) packet.flags |= FLAG_SESSION_ACTIVE;
        if (effectiveFocused) packet.flags |= FLAG_FOCUSED;
        if (passthrough.available()) packet.flags |= FLAG_PASSTHROUGH_AVAILABLE;
        if (passthrough.active()) packet.flags |= FLAG_PASSTHROUGH_ACTIVE;

        if (packet.monotonicNs >= nextThermalPoll) {
            thermal = getThermalStatus(app->activity);
            nextThermalPoll = packet.monotonicNs + 1'000'000'000ULL;
        }
        packet.thermalStatus = thermal;

        XrActiveActionSet active{actions.set, XR_NULL_PATH};
        XrActionsSyncInfo sync{XR_TYPE_ACTIONS_SYNC_INFO}; sync.countActiveActionSets = 1; sync.activeActionSets = &active;

        if (!effectiveFocused) {
            leftShoulder = rightShoulder = false;
            exitStartNs = 0; exitPulseStage = 0;
        }

        if (effectiveFocused && XR_SUCCEEDED(xrSyncActions(session, &sync))) {
            auto ls = getVec2(session, actions.lStick); auto rs = getVec2(session, actions.rStick);
            auto lt = getFloat(session, actions.lTrigger); auto rt = getFloat(session, actions.rTrigger);
            auto lg = getFloat(session, actions.lGrip); auto rg = getFloat(session, actions.rGrip);
            if (ls.isActive || lt.isActive || lg.isActive) packet.flags |= FLAG_LEFT_ACTIVE;
            if (rs.isActive || rt.isActive || rg.isActive) packet.flags |= FLAG_RIGHT_ACTIVE;
            if (ls.isActive) { packet.lx = ls.currentState.x; packet.ly = ls.currentState.y; }
            if (rs.isActive) { packet.rx = rs.currentState.x; packet.ry = rs.currentState.y; }
            if (lt.isActive) packet.lt = std::clamp(lt.currentState, 0.0f, 1.0f);
            if (rt.isActive) packet.rt = std::clamp(rt.currentState, 0.0f, 1.0f);
            if (lg.isActive) packet.lg = std::clamp(lg.currentState, 0.0f, 1.0f);
            if (rg.isActive) packet.rg = std::clamp(rg.currentState, 0.0f, 1.0f);

            auto pressed = [&](XrAction action) { auto st = getBool(session, action); return st.isActive && st.currentState; };
            if (pressed(actions.a)) packet.buttons |= BTN_A;
            if (pressed(actions.b)) packet.buttons |= BTN_B;
            if (pressed(actions.x)) packet.buttons |= BTN_X;
            if (pressed(actions.y)) packet.buttons |= BTN_Y;
            if (pressed(actions.lThumb)) packet.buttons |= BTN_LTHUMB;
            if (pressed(actions.rThumb)) packet.buttons |= BTN_RTHUMB;
            if (pressed(actions.view)) packet.buttons |= BTN_VIEW;

            auto shoulder = [](bool& latched, float value) {
                if (latched) { if (value <= kShoulderReleaseThreshold) latched = false; }
                else if (value >= kShoulderPressThreshold) latched = true;
            };
            shoulder(leftShoulder, packet.lg); shoulder(rightShoulder, packet.rg);

            if (batteryExt && packet.monotonicNs >= nextBatteryPoll) {
                batteryPacked = packBatteryState(getBatteryState(session, leftUserPath), getBatteryState(session, rightUserPath));
                nextBatteryPoll = packet.monotonicNs + 5'000'000'000ULL;
            }
            packet.reserved = batteryPacked;

            bool lPoseActive = poseActive(session, actions.lPose);
            bool rPoseActive = poseActive(session, actions.rPose);
            if (motionRequest != MOTION_REQUEST_NONE) packet.motionFlags |= MOTION_QUERIED;

            if (motionRequest == MOTION_REQUEST_RIGHT_ANGULAR) {
                MotionOutput right{};
                locateAngularOnly(rightSpace, localSpace, fs.predictedDisplayTime, rPoseActive, false, right);
                packet.motionFlags |= right.flags;
                packet.rightAngularLocal = right.angularLocal;
            } else if (motionRequest == MOTION_REQUEST_RIGHT_TRACKED) {
                MotionOutput right{};
                locateTracked(rightSpace, localSpace, fs.predictedDisplayTime, rPoseActive, false, right);
                packet.motionFlags |= right.flags;
                packet.rightOrientation = right.orientation; packet.rightPosition = right.position;
                packet.rightAngularLocal = right.angularLocal;
            } else if (motionRequest == MOTION_REQUEST_BOTH_TRACKED) {
                MotionOutput left{}, right{};
                locateTracked(leftSpace, localSpace, fs.predictedDisplayTime, lPoseActive, true, left);
                locateTracked(rightSpace, localSpace, fs.predictedDisplayTime, rPoseActive, false, right);
                packet.motionFlags |= left.flags | right.flags;
                packet.leftOrientation = left.orientation; packet.rightOrientation = right.orientation;
                packet.leftPosition = left.position; packet.rightPosition = right.position;
                packet.leftAngularLocal = left.angularLocal; packet.rightAngularLocal = right.angularLocal;
            }

            bool exitChord = (packet.buttons & BTN_LTHUMB) && (packet.buttons & BTN_RTHUMB) && leftShoulder && rightShoulder;
            if (exitChord) {
                if (exitStartNs == 0) exitStartNs = packet.monotonicNs;
                packet.flags |= FLAG_EXIT_ARMED;
                packet.buttons &= ~(BTN_LTHUMB | BTN_RTHUMB); packet.lg = packet.rg = 0;
                uint64_t held = packet.monotonicNs - exitStartNs;
                auto cue = [&](uint8_t stage, uint8_t strength) {
                    if (exitPulseStage < stage) { exitPulseStage = stage; exitPulseStrength = strength; exitPulseUntilNs = packet.monotonicNs + kExitPulseNs; }
                };
                if (held >= 1'000'000'000ULL) cue(1, 80);
                if (held >= 2'000'000'000ULL) cue(2, 150);
                if (held >= kExitHoldNs && !exitRequested) {
                    cue(3, 255); packet = PadPacket{}; packet.magic = kMagic; packet.version = kProtocolVersion;
                    packet.size = sizeof(PadPacket); packet.sequence = sequence++; packet.monotonicNs = monoNs(); packet.thermalStatus = thermal;
                    if (passthrough.available()) packet.flags |= FLAG_PASSTHROUGH_AVAILABLE;
                    if (passthrough.active()) packet.flags |= FLAG_PASSTHROUGH_ACTIVE;
                    exitRequested = true;
                    if (XR_FAILED(xrRequestExitSession(session))) quit = true;
                }
            } else { exitStartNs = 0; exitPulseStage = 0; }
        }

        bridge.sendPacket(packet);

        uint16_t rumble = (static_cast<uint16_t>(feedback.large) << 8) | feedback.small;
        bool exitCue = exitPulseStrength != 0 && packet.monotonicNs < exitPulseUntilNs;
        if (exitCue) {
            setHaptic(session, actions.lHaptic, exitPulseStrength); setHaptic(session, actions.rHaptic, exitPulseStrength);
            lastRumble = 0; nextHapticRefresh = 0;
        } else if (!effectiveFocused || rumble == 0) {
            if (lastRumble != 0 || exitPulseStrength != 0) { setHaptic(session, actions.lHaptic, 0); setHaptic(session, actions.rHaptic, 0); }
            exitPulseStrength = 0; lastRumble = 0; nextHapticRefresh = 0;
        } else if (rumble != lastRumble || packet.monotonicNs >= nextHapticRefresh) {
            setHaptic(session, actions.lHaptic, feedback.large); setHaptic(session, actions.rHaptic, feedback.small);
            lastRumble = rumble; nextHapticRefresh = packet.monotonicNs + 75'000'000ULL;
        }

        XrFrameEndInfo ei{XR_TYPE_FRAME_END_INFO};
        ei.displayTime = fs.predictedDisplayTime;
        ei.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
        const XrCompositionLayerBaseHeader* passthroughLayer = passthrough.compositionLayer();
        if (passthroughLayer) {
            ei.layerCount = 1;
            ei.layers = &passthroughLayer;
        } else {
            ei.layerCount = 0;
            ei.layers = nullptr;
        }
        xrEndFrame(session, &ei);
    }

    setHaptic(session, actions.lHaptic, 0); setHaptic(session, actions.rHaptic, 0);
    bridge.stop();
    passthrough.destroy(app->activity);
    if (sessionActive) xrEndSession(session);
    if (leftSpace != XR_NULL_HANDLE) xrDestroySpace(leftSpace);
    if (rightSpace != XR_NULL_HANDLE) xrDestroySpace(rightSpace);
    if (localSpace != XR_NULL_HANDLE) xrDestroySpace(localSpace);
    if (actions.set != XR_NULL_HANDLE) xrDestroyActionSet(actions.set);
    xrDestroySession(session); destroyEgl(egl); xrDestroyInstance(instance);
    app->activity->vm->DetachCurrentThread();
    LOGI("QuestPad stopped");
}
