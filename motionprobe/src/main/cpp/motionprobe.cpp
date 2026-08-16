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

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>

namespace {
constexpr const char* kTag = "QuestPadMotion";
constexpr uint64_t kLogPeriodNs = 250'000'000ULL;
constexpr uint64_t kThermalPeriodNs = 1'000'000'000ULL;

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, kTag, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, kTag, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, kTag, __VA_ARGS__)

uint64_t monoNs() {
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<uint64_t>(ts.tv_sec) * 1'000'000'000ULL + static_cast<uint64_t>(ts.tv_nsec);
}

const char* thermalName(int value) {
    switch (value) {
        case 0: return "NONE";
        case 1: return "LIGHT";
        case 2: return "MODERATE";
        case 3: return "SEVERE";
        case 4: return "CRITICAL";
        case 5: return "EMERGENCY";
        case 6: return "SHUTDOWN";
        default: return "N/A";
    }
}

const char* sessionStateName(XrSessionState state) {
    switch (state) {
        case XR_SESSION_STATE_IDLE: return "IDLE";
        case XR_SESSION_STATE_READY: return "READY";
        case XR_SESSION_STATE_SYNCHRONIZED: return "SYNC";
        case XR_SESSION_STATE_VISIBLE: return "VISIBLE";
        case XR_SESSION_STATE_FOCUSED: return "FOCUSED";
        case XR_SESSION_STATE_STOPPING: return "STOPPING";
        case XR_SESSION_STATE_LOSS_PENDING: return "LOSS_PENDING";
        case XR_SESSION_STATE_EXITING: return "EXITING";
        default: return "UNKNOWN";
    }
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
    std::vector<XrExtensionProperties> extensions(count);
    for (auto& extension : extensions) {
        extension.type = XR_TYPE_EXTENSION_PROPERTIES;
        extension.next = nullptr;
    }
    if (XR_FAILED(xrEnumerateInstanceExtensionProperties(nullptr, count, &count, extensions.data()))) return false;
    for (const auto& extension : extensions) {
        if (std::strcmp(extension.extensionName, name) == 0) return true;
    }
    return false;
}

struct EglState {
    EGLDisplay display = EGL_NO_DISPLAY;
    EGLConfig config = nullptr;
    EGLContext context = EGL_NO_CONTEXT;
    EGLSurface surface = EGL_NO_SURFACE;
};

bool createEgl(EglState& egl) {
    egl.display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    if (egl.display == EGL_NO_DISPLAY) return false;
    EGLint major = 0, minor = 0;
    if (!eglInitialize(egl.display, &major, &minor)) return false;

    const EGLint configAttributes[] = {
        EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT_KHR,
        EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,
        EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
        EGL_NONE
    };
    EGLint count = 0;
    if (!eglChooseConfig(egl.display, configAttributes, &egl.config, 1, &count) || count < 1) return false;
    const EGLint contextAttributes[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE};
    egl.context = eglCreateContext(egl.display, egl.config, EGL_NO_CONTEXT, contextAttributes);
    if (egl.context == EGL_NO_CONTEXT) return false;
    const EGLint surfaceAttributes[] = {EGL_WIDTH, 16, EGL_HEIGHT, 16, EGL_NONE};
    egl.surface = eglCreatePbufferSurface(egl.display, egl.config, surfaceAttributes);
    if (egl.surface == EGL_NO_SURFACE) return false;
    if (!eglMakeCurrent(egl.display, egl.surface, egl.surface, egl.context)) return false;
    LOGI("EGL %d.%d ready", major, minor);
    return true;
}

void destroyEgl(EglState& egl) {
    if (egl.display != EGL_NO_DISPLAY) {
        eglMakeCurrent(egl.display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        if (egl.surface != EGL_NO_SURFACE) eglDestroySurface(egl.display, egl.surface);
        if (egl.context != EGL_NO_CONTEXT) eglDestroyContext(egl.display, egl.context);
        eglTerminate(egl.display);
    }
    egl = {};
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
        env->CallVoidMethod(window, addFlags, 0x80); // FLAG_KEEP_SCREEN_ON
        jmethodID getAttributes = env->GetMethodID(
            windowClass, "getAttributes", "()Landroid/view/WindowManager$LayoutParams;");
        jobject attributes = env->CallObjectMethod(window, getAttributes);
        if (attributes) {
            jclass attributesClass = env->GetObjectClass(attributes);
            jfieldID brightness = env->GetFieldID(attributesClass, "screenBrightness", "F");
            env->SetFloatField(attributes, brightness, 0.0f);
            jmethodID setAttributes = env->GetMethodID(
                windowClass, "setAttributes", "(Landroid/view/WindowManager$LayoutParams;)V");
            env->CallVoidMethod(window, setAttributes, attributes);
            env->DeleteLocalRef(attributesClass);
            env->DeleteLocalRef(attributes);
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
    jmethodID getSystemService = env->GetMethodID(
        activityClass, "getSystemService", "(Ljava/lang/String;)Ljava/lang/Object;");
    jobject powerManager = env->CallObjectMethod(act, getSystemService, powerService);
    int value = -1;
    if (powerManager) {
        jclass powerManagerClass = env->GetObjectClass(powerManager);
        jmethodID getCurrent = env->GetMethodID(powerManagerClass, "getCurrentThermalStatus", "()I");
        if (getCurrent) value = env->CallIntMethod(powerManager, getCurrent);
        env->DeleteLocalRef(powerManagerClass);
        env->DeleteLocalRef(powerManager);
    }
    env->DeleteLocalRef(activityClass);
    env->DeleteLocalRef(contextClass);
    return value;
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
    float selected = rates.front();
    float distance = std::fabs(selected - 72.0f);
    for (float rate : rates) {
        const float candidate = std::fabs(rate - 72.0f);
        if (candidate < distance) { selected = rate; distance = candidate; }
    }
    if (XR_SUCCEEDED(requestRate(session, selected))) LOGI("requested display refresh %.1f Hz", selected);
}

XrAction makeAction(XrActionSet set, XrActionType type, const char* name, const char* prettyName) {
    XrActionCreateInfo info{XR_TYPE_ACTION_CREATE_INFO};
    info.actionType = type;
    std::strncpy(info.actionName, name, XR_MAX_ACTION_NAME_SIZE - 1);
    std::strncpy(info.localizedActionName, prettyName, XR_MAX_LOCALIZED_ACTION_NAME_SIZE - 1);
    XrAction action = XR_NULL_HANDLE;
    return XR_SUCCEEDED(xrCreateAction(set, &info, &action)) ? action : XR_NULL_HANDLE;
}

struct MotionActions {
    XrActionSet set = XR_NULL_HANDLE;
    XrAction leftGripPose = XR_NULL_HANDLE;
    XrAction rightGripPose = XR_NULL_HANDLE;
};

bool setupMotionActions(XrInstance instance, XrSession session, bool touchPlusExtension, MotionActions& actions) {
    XrActionSetCreateInfo setInfo{XR_TYPE_ACTION_SET_CREATE_INFO};
    std::strncpy(setInfo.actionSetName, "placement_probe", XR_MAX_ACTION_SET_NAME_SIZE - 1);
    std::strncpy(setInfo.localizedActionSetName, "QuestPad Placement Probe", XR_MAX_LOCALIZED_ACTION_SET_NAME_SIZE - 1);
    if (!xrOk(instance, xrCreateActionSet(instance, &setInfo, &actions.set), "xrCreateActionSet")) return false;

    actions.leftGripPose = makeAction(actions.set, XR_ACTION_TYPE_POSE_INPUT, "left_grip_pose", "Left controller grip pose");
    actions.rightGripPose = makeAction(actions.set, XR_ACTION_TYPE_POSE_INPUT, "right_grip_pose", "Right controller grip pose");
    if (actions.leftGripPose == XR_NULL_HANDLE || actions.rightGripPose == XR_NULL_HANDLE) return false;

    XrPath leftPath = XR_NULL_PATH, rightPath = XR_NULL_PATH;
    if (!xrOk(instance, xrStringToPath(instance, "/user/hand/left/input/grip/pose", &leftPath), "left grip path")) return false;
    if (!xrOk(instance, xrStringToPath(instance, "/user/hand/right/input/grip/pose", &rightPath), "right grip path")) return false;

    const XrActionSuggestedBinding bindings[] = {
        {actions.leftGripPose, leftPath},
        {actions.rightGripPose, rightPath}
    };
    auto suggest = [&](const char* profileText) {
        XrPath profile = XR_NULL_PATH;
        if (XR_FAILED(xrStringToPath(instance, profileText, &profile))) return false;
        XrInteractionProfileSuggestedBinding suggestion{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING};
        suggestion.interactionProfile = profile;
        suggestion.countSuggestedBindings = 2;
        suggestion.suggestedBindings = bindings;
        return XR_SUCCEEDED(xrSuggestInteractionProfileBindings(instance, &suggestion));
    };

    if (!suggest("/interaction_profiles/oculus/touch_controller")) {
        LOGE("failed to suggest Oculus Touch pose bindings");
        return false;
    }
    if (touchPlusExtension && !suggest("/interaction_profiles/meta/touch_plus_controller"))
        LOGW("Touch Plus pose binding suggestion failed; legacy Touch profile remains available");

    XrSessionActionSetsAttachInfo attach{XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO};
    attach.countActionSets = 1;
    attach.actionSets = &actions.set;
    return xrOk(instance, xrAttachSessionActionSets(session, &attach), "xrAttachSessionActionSets");
}

XrPosef identityPose() {
    XrPosef pose{};
    pose.orientation.w = 1.0f;
    return pose;
}

struct PoseSample {
    bool active = false;
    XrSpaceLocationFlags locationFlags = 0;
    XrSpaceVelocityFlags velocityFlags = 0;
    XrPosef pose = identityPose();
    XrVector3f angular{};
};

PoseSample sampleController(XrSession session, XrAction action, XrSpace space, XrSpace localSpace, XrTime time) {
    PoseSample sample{};
    XrActionStateGetInfo getInfo{XR_TYPE_ACTION_STATE_GET_INFO};
    getInfo.action = action;
    XrActionStatePose poseState{XR_TYPE_ACTION_STATE_POSE};
    if (XR_FAILED(xrGetActionStatePose(session, &getInfo, &poseState)) || !poseState.isActive) return sample;
    sample.active = true;

    XrSpaceVelocity velocity{XR_TYPE_SPACE_VELOCITY};
    XrSpaceLocation location{XR_TYPE_SPACE_LOCATION};
    location.next = &velocity;
    if (XR_FAILED(xrLocateSpace(space, localSpace, time, &location))) return sample;
    sample.locationFlags = location.locationFlags;
    sample.velocityFlags = velocity.velocityFlags;
    sample.pose = location.pose;
    if ((velocity.velocityFlags & XR_SPACE_VELOCITY_ANGULAR_VALID_BIT) != 0)
        sample.angular = velocity.angularVelocity;
    return sample;
}

bool flag(XrSpaceLocationFlags flags, XrSpaceLocationFlags bit) { return (flags & bit) != 0; }

float distance(const XrVector3f& a, const XrVector3f& b) {
    const float x = a.x - b.x, y = a.y - b.y, z = a.z - b.z;
    return std::sqrt(x*x + y*y + z*z);
}

} // namespace

void android_main(android_app* app) {
    app_dummy();
    LOGI("QuestPad Placement Probe starting");
    LOGI("Tracks HMD + BOTH Touch grip poses in LOCAL space. No network or virtual gamepad is active.");
    setLowBrightnessAndKeepAwake(app->activity);

    JNIEnv* env = nullptr;
    app->activity->vm->AttachCurrentThread(&env, nullptr);

    PFN_xrInitializeLoaderKHR initializeLoader = nullptr;
    xrGetInstanceProcAddr(XR_NULL_HANDLE, "xrInitializeLoaderKHR", reinterpret_cast<PFN_xrVoidFunction*>(&initializeLoader));
    if (initializeLoader) {
        XrLoaderInitInfoAndroidKHR loaderInfo{XR_TYPE_LOADER_INIT_INFO_ANDROID_KHR};
        loaderInfo.applicationVM = app->activity->vm;
        loaderInfo.applicationContext = app->activity->clazz;
        if (XR_FAILED(initializeLoader(reinterpret_cast<XrLoaderInitInfoBaseHeaderKHR*>(&loaderInfo)))) return;
    }

    if (!hasExtension(XR_KHR_OPENGL_ES_ENABLE_EXTENSION_NAME)) return;
    std::vector<const char*> extensions{XR_KHR_OPENGL_ES_ENABLE_EXTENSION_NAME};
    const bool performanceExtension = hasExtension(XR_EXT_PERFORMANCE_SETTINGS_EXTENSION_NAME);
    if (performanceExtension) extensions.push_back(XR_EXT_PERFORMANCE_SETTINGS_EXTENSION_NAME);
    const bool refreshExtension = hasExtension(XR_FB_DISPLAY_REFRESH_RATE_EXTENSION_NAME);
    if (refreshExtension) extensions.push_back(XR_FB_DISPLAY_REFRESH_RATE_EXTENSION_NAME);
    const bool touchPlusExtension = hasExtension(XR_META_TOUCH_CONTROLLER_PLUS_EXTENSION_NAME);
    if (touchPlusExtension) extensions.push_back(XR_META_TOUCH_CONTROLLER_PLUS_EXTENSION_NAME);

    XrInstanceCreateInfo instanceInfo{XR_TYPE_INSTANCE_CREATE_INFO};
    std::strncpy(instanceInfo.applicationInfo.applicationName, "QuestPad Placement Probe", XR_MAX_APPLICATION_NAME_SIZE - 1);
    instanceInfo.applicationInfo.applicationVersion = 2;
    std::strncpy(instanceInfo.applicationInfo.engineName, "QuestPadNative", XR_MAX_ENGINE_NAME_SIZE - 1);
    instanceInfo.applicationInfo.engineVersion = 1;
    instanceInfo.applicationInfo.apiVersion = XR_API_VERSION_1_0;
    instanceInfo.enabledExtensionCount = static_cast<uint32_t>(extensions.size());
    instanceInfo.enabledExtensionNames = extensions.data();

    XrInstance instance = XR_NULL_HANDLE;
    if (!xrOk(instance, xrCreateInstance(&instanceInfo, &instance), "xrCreateInstance")) return;
    XrSystemGetInfo systemInfo{XR_TYPE_SYSTEM_GET_INFO};
    systemInfo.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId systemId = XR_NULL_SYSTEM_ID;
    if (!xrOk(instance, xrGetSystem(instance, &systemInfo, &systemId), "xrGetSystem")) { xrDestroyInstance(instance); return; }

    PFN_xrGetOpenGLESGraphicsRequirementsKHR getGraphicsRequirements = nullptr;
    xrGetInstanceProcAddr(instance, "xrGetOpenGLESGraphicsRequirementsKHR", reinterpret_cast<PFN_xrVoidFunction*>(&getGraphicsRequirements));
    if (!getGraphicsRequirements) { xrDestroyInstance(instance); return; }
    XrGraphicsRequirementsOpenGLESKHR graphicsRequirements{XR_TYPE_GRAPHICS_REQUIREMENTS_OPENGL_ES_KHR};
    if (!xrOk(instance, getGraphicsRequirements(instance, systemId, &graphicsRequirements), "xrGetOpenGLESGraphicsRequirementsKHR")) {
        xrDestroyInstance(instance); return;
    }

    EglState egl;
    if (!createEgl(egl)) { xrDestroyInstance(instance); return; }
    XrGraphicsBindingOpenGLESAndroidKHR graphicsBinding{XR_TYPE_GRAPHICS_BINDING_OPENGL_ES_ANDROID_KHR};
    graphicsBinding.display = egl.display;
    graphicsBinding.config = egl.config;
    graphicsBinding.context = egl.context;
    XrSessionCreateInfo sessionInfo{XR_TYPE_SESSION_CREATE_INFO};
    sessionInfo.next = &graphicsBinding;
    sessionInfo.systemId = systemId;
    XrSession session = XR_NULL_HANDLE;
    if (!xrOk(instance, xrCreateSession(instance, &sessionInfo, &session), "xrCreateSession")) {
        destroyEgl(egl); xrDestroyInstance(instance); return;
    }

    MotionActions actions;
    if (!setupMotionActions(instance, session, touchPlusExtension, actions)) {
        if (actions.set != XR_NULL_HANDLE) xrDestroyActionSet(actions.set);
        xrDestroySession(session); destroyEgl(egl); xrDestroyInstance(instance); return;
    }

    XrReferenceSpaceCreateInfo localInfo{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    localInfo.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    localInfo.poseInReferenceSpace = identityPose();
    XrSpace localSpace = XR_NULL_HANDLE;
    if (!xrOk(instance, xrCreateReferenceSpace(session, &localInfo, &localSpace), "xrCreateReferenceSpace(LOCAL)")) return;

    XrReferenceSpaceCreateInfo viewInfo{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    viewInfo.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;
    viewInfo.poseInReferenceSpace = identityPose();
    XrSpace viewSpace = XR_NULL_HANDLE;
    if (!xrOk(instance, xrCreateReferenceSpace(session, &viewInfo, &viewSpace), "xrCreateReferenceSpace(VIEW)")) return;

    auto createActionSpace = [&](XrAction action, const char* name, XrSpace& out) {
        XrActionSpaceCreateInfo info{XR_TYPE_ACTION_SPACE_CREATE_INFO};
        info.action = action;
        info.poseInActionSpace = identityPose();
        return xrOk(instance, xrCreateActionSpace(session, &info, &out), name);
    };
    XrSpace leftSpace = XR_NULL_HANDLE, rightSpace = XR_NULL_HANDLE;
    if (!createActionSpace(actions.leftGripPose, "xrCreateActionSpace(left)", leftSpace) ||
        !createActionSpace(actions.rightGripPose, "xrCreateActionSpace(right)", rightSpace)) return;

    bool resumed = false, sessionActive = false, focused = false, quit = false;
    XrSessionState sessionState = XR_SESSION_STATE_UNKNOWN;
    int thermal = -1;
    uint64_t nextThermalPoll = 0, nextLog = 0;

    app->userData = &resumed;
    app->onAppCmd = [](android_app* state, int32_t command) {
        auto* value = static_cast<bool*>(state->userData);
        if (!value) return;
        if (command == APP_CMD_RESUME) { *value = true; LOGI("Android lifecycle -> RESUMED"); }
        if (command == APP_CMD_PAUSE || command == APP_CMD_STOP) { *value = false; LOGI("Android lifecycle -> PAUSED/STOPPED"); }
    };

    LOGI("Watch: adb logcat -s QuestPadMotion");
    LOGI("OT/PT mean actively tracked orientation/position. PV without PT may be inferred/last-known.");
    LOGI("For off-head wheel use we want state=FOCUSED and BOTH controllers PT=1 when visible to the headset cameras.");

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
                LOGI("XR session state -> %s", sessionStateName(changed->state));
                if (changed->state == XR_SESSION_STATE_STOPPING && sessionActive) {
                    xrEndSession(session); sessionActive = false; focused = false;
                } else if (changed->state == XR_SESSION_STATE_EXITING || changed->state == XR_SESSION_STATE_LOSS_PENDING) {
                    quit = true;
                }
            }
            event = {XR_TYPE_EVENT_DATA_BUFFER};
        }

        if (!sessionActive && resumed && sessionState == XR_SESSION_STATE_READY) {
            XrSessionBeginInfo beginInfo{XR_TYPE_SESSION_BEGIN_INFO};
            beginInfo.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
            if (XR_SUCCEEDED(xrBeginSession(session, &beginInfo))) {
                sessionActive = true;
                if (performanceExtension) {
                    PFN_xrPerfSettingsSetPerformanceLevelEXT setPerformance = nullptr;
                    xrGetInstanceProcAddr(instance, "xrPerfSettingsSetPerformanceLevelEXT", reinterpret_cast<PFN_xrVoidFunction*>(&setPerformance));
                    if (setPerformance) {
                        setPerformance(session, XR_PERF_SETTINGS_DOMAIN_CPU_EXT, XR_PERF_SETTINGS_LEVEL_SUSTAINED_LOW_EXT);
                        setPerformance(session, XR_PERF_SETTINGS_DOMAIN_GPU_EXT, XR_PERF_SETTINGS_LEVEL_SUSTAINED_LOW_EXT);
                    }
                }
                if (refreshExtension) requestLowRefreshRate(instance, session);
            }
        }
        if (!sessionActive) continue;

        XrFrameWaitInfo waitInfo{XR_TYPE_FRAME_WAIT_INFO};
        XrFrameState frameState{XR_TYPE_FRAME_STATE};
        if (XR_FAILED(xrWaitFrame(session, &waitInfo, &frameState))) continue;
        XrFrameBeginInfo frameBegin{XR_TYPE_FRAME_BEGIN_INFO};
        if (XR_FAILED(xrBeginFrame(session, &frameBegin))) continue;

        const uint64_t now = monoNs();
        if (now >= nextThermalPoll) { thermal = getThermalStatus(app->activity); nextThermalPoll = now + kThermalPeriodNs; }

        XrSpaceLocation head{XR_TYPE_SPACE_LOCATION};
        xrLocateSpace(viewSpace, localSpace, frameState.predictedDisplayTime, &head);

        PoseSample left{}, right{};
        if (focused && resumed) {
            XrActiveActionSet activeSet{actions.set, XR_NULL_PATH};
            XrActionsSyncInfo syncInfo{XR_TYPE_ACTIONS_SYNC_INFO};
            syncInfo.countActiveActionSets = 1;
            syncInfo.activeActionSets = &activeSet;
            if (XR_SUCCEEDED(xrSyncActions(session, &syncInfo))) {
                left = sampleController(session, actions.leftGripPose, leftSpace, localSpace, frameState.predictedDisplayTime);
                right = sampleController(session, actions.rightGripPose, rightSpace, localSpace, frameState.predictedDisplayTime);
            }
        }

        if (now >= nextLog) {
            const bool hOV = flag(head.locationFlags, XR_SPACE_LOCATION_ORIENTATION_VALID_BIT);
            const bool hOT = flag(head.locationFlags, XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT);
            const bool hPV = flag(head.locationFlags, XR_SPACE_LOCATION_POSITION_VALID_BIT);
            const bool hPT = flag(head.locationFlags, XR_SPACE_LOCATION_POSITION_TRACKED_BIT);
            auto logController = [](const char* side, const PoseSample& s) {
                LOGI("%s active=%d OV=%d OT=%d PV=%d PT=%d AV=%d p=(%+.3f,%+.3f,%+.3f) w=(%+.3f,%+.3f,%+.3f)",
                    side, s.active ? 1 : 0,
                    flag(s.locationFlags, XR_SPACE_LOCATION_ORIENTATION_VALID_BIT) ? 1 : 0,
                    flag(s.locationFlags, XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT) ? 1 : 0,
                    flag(s.locationFlags, XR_SPACE_LOCATION_POSITION_VALID_BIT) ? 1 : 0,
                    flag(s.locationFlags, XR_SPACE_LOCATION_POSITION_TRACKED_BIT) ? 1 : 0,
                    (s.velocityFlags & XR_SPACE_VELOCITY_ANGULAR_VALID_BIT) ? 1 : 0,
                    s.pose.position.x, s.pose.position.y, s.pose.position.z,
                    s.angular.x, s.angular.y, s.angular.z);
            };

            LOGI("STATE=%s resumed=%d HMD OV=%d OT=%d PV=%d PT=%d p=(%+.3f,%+.3f,%+.3f) thermal=%s",
                sessionStateName(sessionState), resumed ? 1 : 0,
                hOV ? 1 : 0, hOT ? 1 : 0, hPV ? 1 : 0, hPT ? 1 : 0,
                head.pose.position.x, head.pose.position.y, head.pose.position.z, thermalName(thermal));
            logController("LEFT ", left);
            logController("RIGHT", right);
            if (flag(left.locationFlags, XR_SPACE_LOCATION_POSITION_VALID_BIT) &&
                flag(right.locationFlags, XR_SPACE_LOCATION_POSITION_VALID_BIT)) {
                LOGI("PAIR span=%.4f m bothPT=%d", distance(left.pose.position, right.pose.position),
                    (flag(left.locationFlags, XR_SPACE_LOCATION_POSITION_TRACKED_BIT) &&
                     flag(right.locationFlags, XR_SPACE_LOCATION_POSITION_TRACKED_BIT)) ? 1 : 0);
            }
            nextLog = now + kLogPeriodNs;
        }

        XrFrameEndInfo frameEnd{XR_TYPE_FRAME_END_INFO};
        frameEnd.displayTime = frameState.predictedDisplayTime;
        frameEnd.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
        frameEnd.layerCount = 0;
        frameEnd.layers = nullptr;
        xrEndFrame(session, &frameEnd);
    }

    if (sessionActive) xrEndSession(session);
    if (rightSpace != XR_NULL_HANDLE) xrDestroySpace(rightSpace);
    if (leftSpace != XR_NULL_HANDLE) xrDestroySpace(leftSpace);
    if (viewSpace != XR_NULL_HANDLE) xrDestroySpace(viewSpace);
    if (localSpace != XR_NULL_HANDLE) xrDestroySpace(localSpace);
    if (actions.set != XR_NULL_HANDLE) xrDestroyActionSet(actions.set);
    xrDestroySession(session);
    destroyEgl(egl);
    xrDestroyInstance(instance);
    app->activity->vm->DetachCurrentThread();
    LOGI("QuestPad Placement Probe stopped");
}
