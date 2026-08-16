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

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
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
constexpr uint32_t kMagic = 0x44415051u; // "QPAD" little-endian
constexpr uint32_t kFeedbackMagic = 0x31424651u; // "QFB1" little-endian
constexpr uint16_t kProtocolVersion = 1;
constexpr uint64_t kExitHoldNs = 3'000'000'000ULL;

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
    uint32_t reserved;
};
static_assert(sizeof(PadPacket) == 68, "PadPacket wire size changed");

struct __attribute__((packed)) RumblePacket {
    uint32_t magic;
    uint8_t largeMotor;
    uint8_t smallMotor;
    uint16_t reserved;
};
static_assert(sizeof(RumblePacket) == 8, "RumblePacket wire size changed");

enum PacketFlags : uint32_t {
    FLAG_SESSION_ACTIVE = 1u << 0,
    FLAG_FOCUSED = 1u << 1,
    FLAG_LEFT_ACTIVE = 1u << 2,
    FLAG_RIGHT_ACTIVE = 1u << 3,
    FLAG_EXIT_ARMED = 1u << 4,
};

enum Buttons : uint32_t {
    BTN_A = 1u << 0,
    BTN_B = 1u << 1,
    BTN_X = 1u << 2,
    BTN_Y = 1u << 3,
    BTN_LTHUMB = 1u << 4,
    BTN_RTHUMB = 1u << 5,
    BTN_VIEW = 1u << 6, // left Menu button
};

class BridgeServer {
public:
    ~BridgeServer() { stop(); }

    bool start() {
        if (running_.exchange(true)) return true;
        thread_ = std::thread([this] { run(); });
        return true;
    }

    void stop() {
        if (!running_.exchange(false)) return;
        const int listen = listenFd_.exchange(-1);
        if (listen >= 0) close(listen);
        const int client = clientFd_.exchange(-1);
        if (client >= 0) close(client);
        if (thread_.joinable()) thread_.join();
    }

    uint16_t pollRumble() {
        int fd = clientFd_.load(std::memory_order_relaxed);
        if (fd < 0) {
            rumblePacked_.store(0, std::memory_order_relaxed);
            feedbackBytes_ = 0;
            return 0;
        }

        for (;;) {
            const ssize_t n = ::recv(
                fd, feedbackBuf_ + feedbackBytes_, sizeof(feedbackBuf_) - feedbackBytes_, MSG_DONTWAIT);
            if (n > 0) {
                feedbackBytes_ += static_cast<size_t>(n);
                if (feedbackBytes_ == sizeof(feedbackBuf_)) {
                    RumblePacket feedback{};
                    std::memcpy(&feedback, feedbackBuf_, sizeof(feedback));
                    feedbackBytes_ = 0;
                    if (feedback.magic == kFeedbackMagic) {
                        const uint16_t packed =
                            (static_cast<uint16_t>(feedback.largeMotor) << 8) | feedback.smallMotor;
                        rumblePacked_.store(packed, std::memory_order_relaxed);
                    } else {
                        LOGW("invalid rumble packet magic: 0x%08x", feedback.magic);
                    }
                }
                continue;
            }
            if (n == 0) {
                dropClient(fd);
                return 0;
            }
            if (errno == EAGAIN || errno == EWOULDBLOCK)
                return rumblePacked_.load(std::memory_order_relaxed);
            if (errno == EINTR) continue;
            dropClient(fd);
            return 0;
        }
    }

    void sendPacket(const PadPacket& packet) {
        int fd = clientFd_.load(std::memory_order_relaxed);
        if (fd < 0) return;
        const auto* p = reinterpret_cast<const uint8_t*>(&packet);
        size_t left = sizeof(packet);
        while (left > 0) {
            const ssize_t n = ::send(fd, p, left, MSG_NOSIGNAL | MSG_DONTWAIT);
            if (n > 0) {
                p += n;
                left -= static_cast<size_t>(n);
                continue;
            }
            if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
                // Dropping a whole stale sample is safe, but a partial TCP write would
                // destroy packet framing. If any bytes were already sent, reconnect.
                if (left == sizeof(packet)) return;
                dropClient(fd);
                return;
            }
            dropClient(fd);
            return;
        }
    }

private:
    void dropClient(int fd) {
        int expected = fd;
        if (clientFd_.compare_exchange_strong(expected, -1)) {
            close(fd);
            rumblePacked_.store(0, std::memory_order_relaxed);
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
        if (bind(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) < 0) {
            LOGE("bind(127.0.0.1:%u) failed: %d", kPort, errno);
            close(s);
            listenFd_ = -1;
            running_ = false;
            return;
        }
        if (listen(s, 1) < 0) {
            LOGE("listen() failed: %d", errno);
            close(s);
            listenFd_ = -1;
            running_ = false;
            return;
        }
        LOGI("bridge listening on 127.0.0.1:%u", kPort);

        while (running_.load()) {
            sockaddr_in peer{};
            socklen_t peerLen = sizeof(peer);
            int c = accept(s, reinterpret_cast<sockaddr*>(&peer), &peerLen);
            if (c < 0) {
                if (!running_.load()) break;
                if (errno == EINTR) continue;
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                continue;
            }
            const int old = clientFd_.exchange(c);
            if (old >= 0) close(old);
            rumblePacked_.store(0, std::memory_order_relaxed);
            feedbackBytes_ = 0;
            int snd = 4096;
            setsockopt(c, SOL_SOCKET, SO_SNDBUF, &snd, sizeof(snd));
            int noDelay = 1;
            setsockopt(c, IPPROTO_TCP, TCP_NODELAY, &noDelay, sizeof(noDelay));
            LOGI("host connected (TCP_NODELAY)");
        }
    }

    std::atomic<bool> running_{false};
    std::atomic<int> listenFd_{-1};
    std::atomic<int> clientFd_{-1};
    std::atomic<uint16_t> rumblePacked_{0};
    uint8_t feedbackBuf_[sizeof(RumblePacket)]{};
    size_t feedbackBytes_ = 0;
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

    const EGLint cfgAttrs[] = {
        EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT_KHR,
        EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,
        EGL_RED_SIZE, 8,
        EGL_GREEN_SIZE, 8,
        EGL_BLUE_SIZE, 8,
        EGL_ALPHA_SIZE, 8,
        EGL_NONE};
    EGLint n = 0;
    if (!eglChooseConfig(e.display, cfgAttrs, &e.config, 1, &n) || n < 1) return false;

    const EGLint ctxAttrs[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE};
    e.context = eglCreateContext(e.display, e.config, EGL_NO_CONTEXT, ctxAttrs);
    if (e.context == EGL_NO_CONTEXT) return false;

    const EGLint surfAttrs[] = {EGL_WIDTH, 16, EGL_HEIGHT, 16, EGL_NONE};
    e.surface = eglCreatePbufferSurface(e.display, e.config, surfAttrs);
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

struct Actions {
    XrActionSet set = XR_NULL_HANDLE;
    XrAction lStick = XR_NULL_HANDLE;
    XrAction rStick = XR_NULL_HANDLE;
    XrAction lTrigger = XR_NULL_HANDLE;
    XrAction rTrigger = XR_NULL_HANDLE;
    XrAction lGrip = XR_NULL_HANDLE;
    XrAction rGrip = XR_NULL_HANDLE;
    XrAction a = XR_NULL_HANDLE;
    XrAction b = XR_NULL_HANDLE;
    XrAction x = XR_NULL_HANDLE;
    XrAction y = XR_NULL_HANDLE;
    XrAction lThumb = XR_NULL_HANDLE;
    XrAction rThumb = XR_NULL_HANDLE;
    XrAction view = XR_NULL_HANDLE;
    XrAction lHaptic = XR_NULL_HANDLE;
    XrAction rHaptic = XR_NULL_HANDLE;
};

bool xrOk(XrInstance inst, XrResult r, const char* what) {
    if (XR_SUCCEEDED(r)) return true;
    char s[XR_MAX_RESULT_STRING_SIZE] = {};
    if (inst != XR_NULL_HANDLE) xrResultToString(inst, r, s);
    LOGE("%s failed: %d %s", what, r, s);
    return false;
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

bool setupActions(XrInstance inst, XrSession session, Actions& a, bool touchPlusExt) {
    XrActionSetCreateInfo setInfo{XR_TYPE_ACTION_SET_CREATE_INFO};
    std::strncpy(setInfo.actionSetName, "gamepad", XR_MAX_ACTION_SET_NAME_SIZE - 1);
    std::strncpy(setInfo.localizedActionSetName, "QuestPad Gamepad", XR_MAX_LOCALIZED_ACTION_SET_NAME_SIZE - 1);
    setInfo.priority = 0;
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

    auto suggestProfile = [&](const char* profileText) {
        XrPath profile = XR_NULL_PATH;
        if (XR_FAILED(xrStringToPath(inst, profileText, &profile))) return false;
        XrInteractionProfileSuggestedBinding suggestion{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING};
        suggestion.interactionProfile = profile;
        suggestion.countSuggestedBindings = static_cast<uint32_t>(bindings.size());
        suggestion.suggestedBindings = bindings.data();
        return XR_SUCCEEDED(xrSuggestInteractionProfileBindings(inst, &suggestion));
    };

    // Keep the core Oculus Touch profile as a compatibility fallback. With OpenXR 1.0,
    // Quest 3 Touch Plus has a device-specific profile behind XR_META_touch_controller_plus.
    if (!suggestProfile("/interaction_profiles/oculus/touch_controller")) {
        LOGE("failed to suggest Oculus Touch bindings");
        return false;
    }
    if (touchPlusExt && !suggestProfile("/interaction_profiles/meta/touch_plus_controller")) {
        LOGW("Touch Plus device-specific binding suggestion failed; legacy Touch profile remains available");
    }

    XrSessionActionSetsAttachInfo attach{XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO};
    attach.countActionSets = 1;
    attach.actionSets = &a.set;
    return xrOk(inst, xrAttachSessionActionSets(session, &attach), "xrAttachSessionActionSets");
}

XrActionStateBoolean getBool(XrSession s, XrAction a) {
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO}; gi.action = a;
    XrActionStateBoolean st{XR_TYPE_ACTION_STATE_BOOLEAN};
    xrGetActionStateBoolean(s, &gi, &st);
    return st;
}
XrActionStateFloat getFloat(XrSession s, XrAction a) {
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO}; gi.action = a;
    XrActionStateFloat st{XR_TYPE_ACTION_STATE_FLOAT};
    xrGetActionStateFloat(s, &gi, &st);
    return st;
}
XrActionStateVector2f getVec2(XrSession s, XrAction a) {
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO}; gi.action = a;
    XrActionStateVector2f st{XR_TYPE_ACTION_STATE_VECTOR2F};
    xrGetActionStateVector2f(s, &gi, &st);
    return st;
}

void setHaptic(XrSession session, XrAction action, uint8_t intensity) {
    XrHapticActionInfo info{XR_TYPE_HAPTIC_ACTION_INFO};
    info.action = action;
    if (intensity == 0) {
        xrStopHapticFeedback(session, &info);
        return;
    }
    XrHapticVibration vibration{XR_TYPE_HAPTIC_VIBRATION};
    vibration.duration = 100'000'000; // 100 ms; refreshed before expiry
    vibration.frequency = XR_FREQUENCY_UNSPECIFIED;
    vibration.amplitude = static_cast<float>(intensity) / 255.0f;
    xrApplyHapticFeedback(
        session, &info, reinterpret_cast<const XrHapticBaseHeader*>(&vibration));
}

void setLowBrightnessAndKeepAwake(ANativeActivity* activity) {
    JNIEnv* env = nullptr;
    activity->vm->AttachCurrentThread(&env, nullptr);
    jobject act = activity->clazz;
    jclass activityClass = env->GetObjectClass(act);
    jmethodID getWindow = env->GetMethodID(activityClass, "getWindow", "()Landroid/view/Window;");
    jobject window = env->CallObjectMethod(act, getWindow);
    if (window) {
        jclass windowClass = env->GetObjectClass(window);
        jmethodID addFlags = env->GetMethodID(windowClass, "addFlags", "(I)V");
        // WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON = 0x80
        env->CallVoidMethod(window, addFlags, 0x80);
        jmethodID getAttrs = env->GetMethodID(windowClass, "getAttributes", "()Landroid/view/WindowManager$LayoutParams;");
        jobject attrs = env->CallObjectMethod(window, getAttrs);
        if (attrs) {
            jclass attrsClass = env->GetObjectClass(attrs);
            jfieldID brightness = env->GetFieldID(attrsClass, "screenBrightness", "F");
            env->SetFloatField(attrs, brightness, 0.0f);
            jmethodID setAttrs = env->GetMethodID(windowClass, "setAttributes", "(Landroid/view/WindowManager$LayoutParams;)V");
            env->CallVoidMethod(window, setAttrs, attrs);
            env->DeleteLocalRef(attrsClass);
            env->DeleteLocalRef(attrs);
        }
        env->DeleteLocalRef(windowClass);
        env->DeleteLocalRef(window);
    }
    env->DeleteLocalRef(activityClass);
}

int getThermalStatus(ANativeActivity* activity) {
    JNIEnv* env = nullptr;
    activity->vm->AttachCurrentThread(&env, nullptr);
    jobject act = activity->clazz;
    jclass contextClass = env->FindClass("android/content/Context");
    jfieldID powerServiceField = env->GetStaticFieldID(contextClass, "POWER_SERVICE", "Ljava/lang/String;");
    jobject powerService = env->GetStaticObjectField(contextClass, powerServiceField);
    jclass activityClass = env->GetObjectClass(act);
    jmethodID getSystemService = env->GetMethodID(activityClass, "getSystemService", "(Ljava/lang/String;)Ljava/lang/Object;");
    jobject pm = env->CallObjectMethod(act, getSystemService, powerService);
    int value = -1;
    if (pm) {
        jclass pmClass = env->GetObjectClass(pm);
        jmethodID getCurrent = env->GetMethodID(pmClass, "getCurrentThermalStatus", "()I");
        if (getCurrent) value = env->CallIntMethod(pm, getCurrent);
        env->DeleteLocalRef(pmClass);
        env->DeleteLocalRef(pm);
    }
    env->DeleteLocalRef(activityClass);
    env->DeleteLocalRef(contextClass);
    return value;
}

bool hasExtension(const char* name) {
    uint32_t count = 0;
    if (XR_FAILED(xrEnumerateInstanceExtensionProperties(nullptr, 0, &count, nullptr))) return false;
    std::vector<XrExtensionProperties> exts(count);
    for (auto& e : exts) {
        e.type = XR_TYPE_EXTENSION_PROPERTIES;
        e.next = nullptr;
    }
    if (XR_FAILED(xrEnumerateInstanceExtensionProperties(nullptr, count, &count, exts.data()))) return false;
    for (const auto& e : exts) if (std::strcmp(e.extensionName, name) == 0) return true;
    return false;
}

void requestLowRefreshRate(XrInstance instance, XrSession session) {
    PFN_xrEnumerateDisplayRefreshRatesFB enumerateRates = nullptr;
    PFN_xrRequestDisplayRefreshRateFB requestRate = nullptr;
    xrGetInstanceProcAddr(instance, "xrEnumerateDisplayRefreshRatesFB", reinterpret_cast<PFN_xrVoidFunction*>(&enumerateRates));
    xrGetInstanceProcAddr(instance, "xrRequestDisplayRefreshRateFB", reinterpret_cast<PFN_xrVoidFunction*>(&requestRate));
    if (!enumerateRates || !requestRate) return;

    uint32_t count = 0;
    if (XR_FAILED(enumerateRates(session, 0, &count, nullptr)) || count == 0) return;
    std::vector<float> rates(count);
    if (XR_FAILED(enumerateRates(session, count, &count, rates.data()))) return;

    // Prefer 72 Hz exactly. If unavailable, use the supported rate closest to 72 Hz.
    float selected = rates.front();
    float bestDistance = std::fabs(selected - 72.0f);
    for (float rate : rates) {
        const float distance = std::fabs(rate - 72.0f);
        if (distance < bestDistance) {
            selected = rate;
            bestDistance = distance;
        }
    }
    const XrResult r = requestRate(session, selected);
    if (XR_SUCCEEDED(r)) LOGI("requested display refresh %.1f Hz", selected);
    else LOGW("display refresh request %.1f Hz failed: %d", selected, r);
}

} // namespace

void android_main(android_app* app) {
    app_dummy();
    LOGI("QuestPad v0.1 starting");
    setLowBrightnessAndKeepAwake(app->activity);

    JNIEnv* env = nullptr;
    app->activity->vm->AttachCurrentThread(&env, nullptr);

    PFN_xrInitializeLoaderKHR initializeLoader = nullptr;
    xrGetInstanceProcAddr(XR_NULL_HANDLE, "xrInitializeLoaderKHR", reinterpret_cast<PFN_xrVoidFunction*>(&initializeLoader));
    if (initializeLoader) {
        XrLoaderInitInfoAndroidKHR li{XR_TYPE_LOADER_INIT_INFO_ANDROID_KHR};
        li.applicationVM = app->activity->vm;
        li.applicationContext = app->activity->clazz;
        if (XR_FAILED(initializeLoader(reinterpret_cast<XrLoaderInitInfoBaseHeaderKHR*>(&li)))) {
            LOGE("xrInitializeLoaderKHR failed");
            return;
        }
    }

    std::vector<const char*> extensions;
    if (!hasExtension(XR_KHR_OPENGL_ES_ENABLE_EXTENSION_NAME)) {
        LOGE("runtime lacks %s", XR_KHR_OPENGL_ES_ENABLE_EXTENSION_NAME);
        return;
    }
    extensions.push_back(XR_KHR_OPENGL_ES_ENABLE_EXTENSION_NAME);
    const bool perfExt = hasExtension(XR_EXT_PERFORMANCE_SETTINGS_EXTENSION_NAME);
    if (perfExt) extensions.push_back(XR_EXT_PERFORMANCE_SETTINGS_EXTENSION_NAME);
    const bool refreshExt = hasExtension(XR_FB_DISPLAY_REFRESH_RATE_EXTENSION_NAME);
    if (refreshExt) extensions.push_back(XR_FB_DISPLAY_REFRESH_RATE_EXTENSION_NAME);
    const bool touchPlusExt = hasExtension(XR_META_TOUCH_CONTROLLER_PLUS_EXTENSION_NAME);
    if (touchPlusExt) extensions.push_back(XR_META_TOUCH_CONTROLLER_PLUS_EXTENSION_NAME);

    XrInstanceCreateInfo ici{XR_TYPE_INSTANCE_CREATE_INFO};
    std::strncpy(ici.applicationInfo.applicationName, "QuestPad", XR_MAX_APPLICATION_NAME_SIZE - 1);
    ici.applicationInfo.applicationVersion = 1;
    std::strncpy(ici.applicationInfo.engineName, "QuestPadNative", XR_MAX_ENGINE_NAME_SIZE - 1);
    ici.applicationInfo.engineVersion = 1;
    ici.applicationInfo.apiVersion = XR_API_VERSION_1_0;
    ici.enabledExtensionCount = static_cast<uint32_t>(extensions.size());
    ici.enabledExtensionNames = extensions.data();

    XrInstance instance = XR_NULL_HANDLE;
    if (!xrOk(instance, xrCreateInstance(&ici, &instance), "xrCreateInstance")) return;

    XrSystemGetInfo sgi{XR_TYPE_SYSTEM_GET_INFO};
    sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId systemId = XR_NULL_SYSTEM_ID;
    if (!xrOk(instance, xrGetSystem(instance, &sgi, &systemId), "xrGetSystem")) {
        xrDestroyInstance(instance); return;
    }

    PFN_xrGetOpenGLESGraphicsRequirementsKHR getGlesReq = nullptr;
    xrGetInstanceProcAddr(instance, "xrGetOpenGLESGraphicsRequirementsKHR", reinterpret_cast<PFN_xrVoidFunction*>(&getGlesReq));
    if (!getGlesReq) {
        LOGE("xrGetOpenGLESGraphicsRequirementsKHR unavailable");
        xrDestroyInstance(instance); return;
    }
    XrGraphicsRequirementsOpenGLESKHR req{XR_TYPE_GRAPHICS_REQUIREMENTS_OPENGL_ES_KHR};
    if (!xrOk(instance, getGlesReq(instance, systemId, &req), "xrGetOpenGLESGraphicsRequirementsKHR")) {
        xrDestroyInstance(instance); return;
    }

    EglState egl;
    if (!createEgl(egl)) {
        LOGE("EGL creation failed");
        xrDestroyInstance(instance); return;
    }

    XrGraphicsBindingOpenGLESAndroidKHR binding{XR_TYPE_GRAPHICS_BINDING_OPENGL_ES_ANDROID_KHR};
    binding.display = egl.display;
    binding.config = egl.config;
    binding.context = egl.context;
    XrSessionCreateInfo sci{XR_TYPE_SESSION_CREATE_INFO};
    sci.next = &binding;
    sci.systemId = systemId;
    XrSession session = XR_NULL_HANDLE;
    if (!xrOk(instance, xrCreateSession(instance, &sci, &session), "xrCreateSession")) {
        destroyEgl(egl); xrDestroyInstance(instance); return;
    }

    Actions actions;
    if (!setupActions(instance, session, actions, touchPlusExt)) {
        xrDestroySession(session); destroyEgl(egl); xrDestroyInstance(instance); return;
    }

    BridgeServer bridge;
    bridge.start();

    bool resumed = false;
    bool sessionActive = false;
    bool focused = false;
    XrSessionState sessionState = XR_SESSION_STATE_UNKNOWN;
    bool quit = false;
    bool exitRequested = false;
    uint32_t sequence = 0;
    uint64_t exitStartNs = 0;
    int thermal = -1;
    uint64_t nextThermalPoll = 0;
    uint16_t lastRumble = 0;
    uint64_t nextHapticRefresh = 0;

    app->userData = &resumed;
    app->onAppCmd = [](android_app* a, int32_t cmd) {
        auto* r = static_cast<bool*>(a->userData);
        if (!r) return;
        if (cmd == APP_CMD_RESUME) *r = true;
        if (cmd == APP_CMD_PAUSE || cmd == APP_CMD_STOP) *r = false;
    };

    while (!quit && !app->destroyRequested) {
        int events = 0;
        android_poll_source* source = nullptr;
        while (ALooper_pollOnce(sessionActive ? 0 : 10, nullptr, &events, reinterpret_cast<void**>(&source)) >= 0) {
            if (source) source->process(app, source);
            if (app->destroyRequested) { quit = true; break; }
            if (sessionActive) break;
        }

        XrEventDataBuffer event{XR_TYPE_EVENT_DATA_BUFFER};
        while (xrPollEvent(instance, &event) == XR_SUCCESS) {
            if (event.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
                const auto* changed = reinterpret_cast<XrEventDataSessionStateChanged*>(&event);
                sessionState = changed->state;
                focused = changed->state == XR_SESSION_STATE_FOCUSED;
                if (changed->state == XR_SESSION_STATE_STOPPING && sessionActive) {
                    xrEndSession(session);
                    sessionActive = false;
                    focused = false;
                    LOGI("XR session stopped");
                } else if (changed->state == XR_SESSION_STATE_EXITING || changed->state == XR_SESSION_STATE_LOSS_PENDING) {
                    quit = true;
                }
            }
            event = {XR_TYPE_EVENT_DATA_BUFFER};
        }

        // Match the NativeActivity lifecycle: do not begin an immersive session before
        // the Android activity is resumed. Remembering sessionState avoids relying on a
        // second READY event if RESUME arrives slightly later.
        if (!sessionActive && resumed && sessionState == XR_SESSION_STATE_READY) {
            XrSessionBeginInfo bi{XR_TYPE_SESSION_BEGIN_INFO};
            bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
            if (XR_SUCCEEDED(xrBeginSession(session, &bi))) {
                sessionActive = true;
                LOGI("XR session active");
                if (perfExt) {
                    PFN_xrPerfSettingsSetPerformanceLevelEXT setPerf = nullptr;
                    xrGetInstanceProcAddr(instance, "xrPerfSettingsSetPerformanceLevelEXT", reinterpret_cast<PFN_xrVoidFunction*>(&setPerf));
                    if (setPerf) {
                        // Keep both domains in a thermally sustainable low-complexity mode.
                        // POWER_SAVINGS is intentionally avoided because the extension allows
                        // runtimes to deprioritize low latency at that level.
                        setPerf(session, XR_PERF_SETTINGS_DOMAIN_CPU_EXT, XR_PERF_SETTINGS_LEVEL_SUSTAINED_LOW_EXT);
                        setPerf(session, XR_PERF_SETTINGS_DOMAIN_GPU_EXT, XR_PERF_SETTINGS_LEVEL_SUSTAINED_LOW_EXT);
                    }
                }
                if (refreshExt) requestLowRefreshRate(instance, session);
            }
        }

        if (!sessionActive) continue;

        XrFrameWaitInfo wi{XR_TYPE_FRAME_WAIT_INFO};
        XrFrameState fs{XR_TYPE_FRAME_STATE};
        if (XR_FAILED(xrWaitFrame(session, &wi, &fs))) continue;
        XrFrameBeginInfo fi{XR_TYPE_FRAME_BEGIN_INFO};
        if (XR_FAILED(xrBeginFrame(session, &fi))) continue;

        XrActiveActionSet active{actions.set, XR_NULL_PATH};
        XrActionsSyncInfo sync{XR_TYPE_ACTIONS_SYNC_INFO};
        sync.countActiveActionSets = 1;
        sync.activeActionSets = &active;

        PadPacket packet{};
        packet.magic = kMagic;
        packet.version = kProtocolVersion;
        packet.size = sizeof(PadPacket);
        packet.sequence = sequence++;
        packet.monotonicNs = monoNs();
        const bool effectiveFocused = focused && resumed && !exitRequested;
        if (sessionActive) packet.flags |= FLAG_SESSION_ACTIVE;
        if (effectiveFocused) packet.flags |= FLAG_FOCUSED;

        if (packet.monotonicNs >= nextThermalPoll) {
            thermal = getThermalStatus(app->activity);
            nextThermalPoll = packet.monotonicNs + 1'000'000'000ULL;
        }
        packet.thermalStatus = thermal;

        if (effectiveFocused && XR_SUCCEEDED(xrSyncActions(session, &sync))) {
            const auto ls = getVec2(session, actions.lStick);
            const auto rs = getVec2(session, actions.rStick);
            const auto lt = getFloat(session, actions.lTrigger);
            const auto rt = getFloat(session, actions.rTrigger);
            const auto lg = getFloat(session, actions.lGrip);
            const auto rg = getFloat(session, actions.rGrip);
            if (ls.isActive || lt.isActive || lg.isActive) packet.flags |= FLAG_LEFT_ACTIVE;
            if (rs.isActive || rt.isActive || rg.isActive) packet.flags |= FLAG_RIGHT_ACTIVE;
            if (ls.isActive) { packet.lx = ls.currentState.x; packet.ly = ls.currentState.y; }
            if (rs.isActive) { packet.rx = rs.currentState.x; packet.ry = rs.currentState.y; }
            if (lt.isActive) packet.lt = std::clamp(lt.currentState, 0.0f, 1.0f);
            if (rt.isActive) packet.rt = std::clamp(rt.currentState, 0.0f, 1.0f);
            if (lg.isActive) packet.lg = std::clamp(lg.currentState, 0.0f, 1.0f);
            if (rg.isActive) packet.rg = std::clamp(rg.currentState, 0.0f, 1.0f);

            auto pressed = [&](XrAction action) {
                const auto st = getBool(session, action);
                return st.isActive && st.currentState;
            };
            if (pressed(actions.a)) packet.buttons |= BTN_A;
            if (pressed(actions.b)) packet.buttons |= BTN_B;
            if (pressed(actions.x)) packet.buttons |= BTN_X;
            if (pressed(actions.y)) packet.buttons |= BTN_Y;
            if (pressed(actions.lThumb)) packet.buttons |= BTN_LTHUMB;
            if (pressed(actions.rThumb)) packet.buttons |= BTN_RTHUMB;
            if (pressed(actions.view)) packet.buttons |= BTN_VIEW;

            const bool exitChord =
                (packet.buttons & BTN_LTHUMB) && (packet.buttons & BTN_RTHUMB) &&
                packet.lg > 0.75f && packet.rg > 0.75f;
            if (exitChord) {
                if (exitStartNs == 0) exitStartNs = packet.monotonicNs;
                packet.flags |= FLAG_EXIT_ARMED;
                // Do not leak the exit chord into the emulated controller.
                packet.buttons &= ~(BTN_LTHUMB | BTN_RTHUMB);
                packet.lg = 0.0f;
                packet.rg = 0.0f;
                if (packet.monotonicNs - exitStartNs >= kExitHoldNs && !exitRequested) {
                    LOGI("exit chord held for 3 seconds; requesting exit");
                    // Make this and all subsequent packets neutral while the runtime
                    // transitions through STOPPING -> EXITING.
                    packet.lx = packet.ly = packet.rx = packet.ry = 0.0f;
                    packet.lt = packet.rt = packet.lg = packet.rg = 0.0f;
                    packet.buttons = 0;
                    packet.flags &= ~FLAG_FOCUSED;
                    exitRequested = true;
                    const XrResult exitResult = xrRequestExitSession(session);
                    if (XR_FAILED(exitResult)) {
                        LOGW("xrRequestExitSession failed: %d", exitResult);
                        quit = true;
                    }
                }
            } else {
                exitStartNs = 0;
            }
        }

        // If the app loses XR focus, the packet stays neutral by construction.
        bridge.sendPacket(packet);

        // Reverse feedback path: preserve the Xbox 360 two-motor distinction by
        // mapping the large/low-frequency motor to the left Touch controller and
        // the small/high-frequency motor to the right. OpenXR runtimes choose the
        // actual actuator frequency when XR_FREQUENCY_UNSPECIFIED is used.
        const uint16_t rumble = bridge.pollRumble();
        const uint8_t largeMotor = static_cast<uint8_t>((rumble >> 8) & 0xFF);
        const uint8_t smallMotor = static_cast<uint8_t>(rumble & 0xFF);
        if (!effectiveFocused || rumble == 0) {
            if (lastRumble != 0) {
                setHaptic(session, actions.lHaptic, 0);
                setHaptic(session, actions.rHaptic, 0);
            }
            lastRumble = 0;
            nextHapticRefresh = 0;
        } else if (rumble != lastRumble || packet.monotonicNs >= nextHapticRefresh) {
            setHaptic(session, actions.lHaptic, largeMotor);
            setHaptic(session, actions.rHaptic, smallMotor);
            lastRumble = rumble;
            nextHapticRefresh = packet.monotonicNs + 75'000'000ULL;
        }

        XrFrameEndInfo ei{XR_TYPE_FRAME_END_INFO};
        ei.displayTime = fs.predictedDisplayTime;
        ei.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
        ei.layerCount = 0;
        ei.layers = nullptr;
        xrEndFrame(session, &ei);
    }

    setHaptic(session, actions.lHaptic, 0);
    setHaptic(session, actions.rHaptic, 0);
    bridge.stop();
    if (sessionActive) xrEndSession(session);
    if (actions.set != XR_NULL_HANDLE) xrDestroyActionSet(actions.set);
    xrDestroySession(session);
    destroyEgl(egl);
    xrDestroyInstance(instance);
    app->activity->vm->DetachCurrentThread();
    LOGI("QuestPad stopped");
}
