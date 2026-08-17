#pragma once

#include <android/log.h>
#include <android/native_activity.h>
#include <jni.h>
#include <openxr/openxr.h>

namespace questpad {

constexpr const char* kPassthroughLogTag = "QuestPad";

inline bool clearJniException(JNIEnv* env, const char* step) {
    if (!env || !env->ExceptionCheck()) return false;
    __android_log_print(ANDROID_LOG_ERROR, kPassthroughLogTag, "JNI exception during %s", step);
    env->ExceptionDescribe();
    env->ExceptionClear();
    return true;
}

inline void setWindowBrightness(ANativeActivity* activity, float brightness) {
    if (!activity || !activity->vm || !activity->clazz) return;

    JNIEnv* env = nullptr;
    bool detach = false;
    jint envStatus = activity->vm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6);
    if (envStatus == JNI_EDETACHED) {
        if (activity->vm->AttachCurrentThread(&env, nullptr) != JNI_OK) return;
        detach = true;
    } else if (envStatus != JNI_OK || !env) {
        return;
    }

    auto finish = [&]() {
        clearJniException(env, "brightness cleanup");
        if (detach) activity->vm->DetachCurrentThread();
    };

    jobject act = activity->clazz;
    jclass ac = env->GetObjectClass(act);
    if (!ac || clearJniException(env, "GetObjectClass(activity)")) {
        finish();
        return;
    }

    jmethodID getWindow = env->GetMethodID(ac, "getWindow", "()Landroid/view/Window;");
    if (!getWindow || clearJniException(env, "Window getWindow method lookup")) {
        env->DeleteLocalRef(ac);
        finish();
        return;
    }

    jobject window = env->CallObjectMethod(act, getWindow);
    if (clearJniException(env, "Window getWindow call") || !window) {
        env->DeleteLocalRef(ac);
        finish();
        return;
    }

    jclass wc = env->GetObjectClass(window);
    if (!wc || clearJniException(env, "GetObjectClass(window)")) {
        env->DeleteLocalRef(window);
        env->DeleteLocalRef(ac);
        finish();
        return;
    }

    jmethodID getAttrs = env->GetMethodID(wc, "getAttributes", "()Landroid/view/WindowManager$LayoutParams;");
    if (!getAttrs || clearJniException(env, "Window getAttributes lookup")) {
        env->DeleteLocalRef(wc);
        env->DeleteLocalRef(window);
        env->DeleteLocalRef(ac);
        finish();
        return;
    }

    jobject attrs = env->CallObjectMethod(window, getAttrs);
    if (clearJniException(env, "Window getAttributes call") || !attrs) {
        env->DeleteLocalRef(wc);
        env->DeleteLocalRef(window);
        env->DeleteLocalRef(ac);
        finish();
        return;
    }

    jclass alc = env->GetObjectClass(attrs);
    if (!alc || clearJniException(env, "GetObjectClass(LayoutParams)")) {
        env->DeleteLocalRef(attrs);
        env->DeleteLocalRef(wc);
        env->DeleteLocalRef(window);
        env->DeleteLocalRef(ac);
        finish();
        return;
    }

    jfieldID brightnessField = env->GetFieldID(alc, "screenBrightness", "F");
    if (!brightnessField || clearJniException(env, "LayoutParams.screenBrightness lookup")) {
        env->DeleteLocalRef(alc);
        env->DeleteLocalRef(attrs);
        env->DeleteLocalRef(wc);
        env->DeleteLocalRef(window);
        env->DeleteLocalRef(ac);
        finish();
        return;
    }

    env->SetFloatField(attrs, brightnessField, brightness);
    if (clearJniException(env, "LayoutParams.screenBrightness set")) {
        env->DeleteLocalRef(alc);
        env->DeleteLocalRef(attrs);
        env->DeleteLocalRef(wc);
        env->DeleteLocalRef(window);
        env->DeleteLocalRef(ac);
        finish();
        return;
    }

    jmethodID setAttrs = env->GetMethodID(
        wc,
        "setAttributes",
        "(Landroid/view/WindowManager$LayoutParams;)V");
    if (!setAttrs || clearJniException(env, "Window setAttributes lookup")) {
        env->DeleteLocalRef(alc);
        env->DeleteLocalRef(attrs);
        env->DeleteLocalRef(wc);
        env->DeleteLocalRef(window);
        env->DeleteLocalRef(ac);
        finish();
        return;
    }

    env->CallVoidMethod(window, setAttrs, attrs);
    clearJniException(env, "Window setAttributes call");

    __android_log_print(
        ANDROID_LOG_INFO,
        kPassthroughLogTag,
        "Quest view brightness override -> %.2f",
        brightness);

    env->DeleteLocalRef(alc);
    env->DeleteLocalRef(attrs);
    env->DeleteLocalRef(wc);
    env->DeleteLocalRef(window);
    env->DeleteLocalRef(ac);
    finish();
}

class PassthroughSupport {
public:
    bool initialize(XrInstance instance, XrSystemId systemId, XrSession session, bool extensionEnabled) {
        instance_ = instance;
        session_ = session;
        if (!extensionEnabled) {
            logInfo("XR_FB_passthrough extension not enabled");
            return false;
        }

        XrSystemPassthroughPropertiesFB passthroughProperties{XR_TYPE_SYSTEM_PASSTHROUGH_PROPERTIES_FB};
        XrSystemProperties systemProperties{XR_TYPE_SYSTEM_PROPERTIES};
        systemProperties.next = &passthroughProperties;
        XrResult propsResult = xrGetSystemProperties(instance_, systemId, &systemProperties);
        if (XR_FAILED(propsResult)) {
            logResult("xrGetSystemProperties(passthrough)", propsResult);
            return false;
        }
        if (passthroughProperties.supportsPassthrough != XR_TRUE) {
            logInfo("runtime reports supportsPassthrough=XR_FALSE");
            return false;
        }

        load("xrCreatePassthroughFB", createPassthrough_);
        load("xrDestroyPassthroughFB", destroyPassthrough_);
        load("xrPassthroughStartFB", startPassthrough_);
        load("xrPassthroughPauseFB", pausePassthrough_);
        load("xrCreatePassthroughLayerFB", createLayer_);
        load("xrDestroyPassthroughLayerFB", destroyLayer_);
        load("xrPassthroughLayerResumeFB", resumeLayer_);
        load("xrPassthroughLayerPauseFB", pauseLayer_);
        load("xrPassthroughLayerSetStyleFB", setStyle_);

        available_ = createPassthrough_ && destroyPassthrough_ && startPassthrough_ && pausePassthrough_ &&
            createLayer_ && destroyLayer_ && resumeLayer_ && pauseLayer_;
        if (!available_) {
            logInfo("one or more XR_FB_passthrough entry points are missing");
            return false;
        }

        // Meta's native sample creates the passthrough feature/layer before entering
        // the frame loop and leaves them paused until needed. Do the same here instead
        // of allocating compositor objects in the first MR frame.
        if (!ensureCreated()) {
            available_ = false;
            return false;
        }

        logInfo("passthrough feature/layer created and paused");
        return true;
    }

    bool available() const { return available_; }
    bool active() const { return active_; }

    const XrCompositionLayerBaseHeader* compositionLayer() const {
        return active_ ? reinterpret_cast<const XrCompositionLayerBaseHeader*>(&compositionLayer_) : nullptr;
    }

    void setEnabled(bool enabled, ANativeActivity* activity) {
        if (!enabled) {
            enableBlockedUntilToggle_ = false;
            if (!active_) return;

            logInfo("passthrough disable requested");
            if (layer_ != XR_NULL_HANDLE && pauseLayer_) {
                XrResult r = pauseLayer_(layer_);
                if (XR_FAILED(r)) logResult("xrPassthroughLayerPauseFB", r);
            }
            if (passthrough_ != XR_NULL_HANDLE && pausePassthrough_) {
                XrResult r = pausePassthrough_(passthrough_);
                if (XR_FAILED(r)) logResult("xrPassthroughPauseFB", r);
            }
            active_ = false;
            setWindowBrightness(activity, 0.0f);
            logInfo("passthrough disabled");
            return;
        }

        if (active_ || !available_ || enableBlockedUntilToggle_) return;
        logInfo("passthrough enable requested");

        if (!ensureCreated()) {
            available_ = false;
            enableBlockedUntilToggle_ = true;
            setWindowBrightness(activity, 0.0f);
            return;
        }

        XrResult startResult = startPassthrough_(passthrough_);
        logResult("xrPassthroughStartFB", startResult);
        if (XR_FAILED(startResult)) {
            active_ = false;
            enableBlockedUntilToggle_ = true;
            setWindowBrightness(activity, 0.0f);
            return;
        }

        XrResult resumeResult = resumeLayer_(layer_);
        logResult("xrPassthroughLayerResumeFB", resumeResult);
        if (XR_FAILED(resumeResult)) {
            if (pausePassthrough_) pausePassthrough_(passthrough_);
            active_ = false;
            enableBlockedUntilToggle_ = true;
            setWindowBrightness(activity, 0.0f);
            return;
        }

        if (setStyle_) {
            XrPassthroughStyleFB style{XR_TYPE_PASSTHROUGH_STYLE_FB};
            style.textureOpacityFactor = 1.0f;
            style.edgeColor = {0.0f, 0.0f, 0.0f, 0.0f};
            XrResult styleResult = setStyle_(layer_, &style);
            logResult("xrPassthroughLayerSetStyleFB", styleResult);
        }

        compositionLayer_ = {XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_FB};
        compositionLayer_.layerHandle = layer_;
        compositionLayer_.flags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT;
        compositionLayer_.space = XR_NULL_HANDLE;
        active_ = true;

        // Android's documented override sentinel. JNI failures are now explicitly
        // cleared so a Quest-specific WindowManager exception cannot poison the next
        // periodic JNI call (thermal/battery diagnostics).
        setWindowBrightness(activity, -1.0f);
        logInfo("passthrough active");
    }

    void disableAfterFrameError(ANativeActivity* activity) {
        if (!active_) return;
        logInfo("disabling passthrough after xrEndFrame failure");
        setEnabled(false, activity);
        enableBlockedUntilToggle_ = true;
    }

    void destroy(ANativeActivity* activity) {
        enableBlockedUntilToggle_ = false;
        if (active_) setEnabled(false, activity);
        if (layer_ != XR_NULL_HANDLE && destroyLayer_) {
            XrResult r = destroyLayer_(layer_);
            if (XR_FAILED(r)) logResult("xrDestroyPassthroughLayerFB", r);
            layer_ = XR_NULL_HANDLE;
        }
        if (passthrough_ != XR_NULL_HANDLE && destroyPassthrough_) {
            XrResult r = destroyPassthrough_(passthrough_);
            if (XR_FAILED(r)) logResult("xrDestroyPassthroughFB", r);
            passthrough_ = XR_NULL_HANDLE;
        }
        created_ = false;
        active_ = false;
    }

private:
    template <typename T>
    void load(const char* name, T& fn) {
        fn = nullptr;
        if (instance_ == XR_NULL_HANDLE) return;
        XrResult r = xrGetInstanceProcAddr(instance_, name, reinterpret_cast<PFN_xrVoidFunction*>(&fn));
        if (XR_FAILED(r)) logResult(name, r);
    }

    void logInfo(const char* text) const {
        __android_log_print(ANDROID_LOG_INFO, kPassthroughLogTag, "%s", text);
    }

    void logResult(const char* what, XrResult result) const {
        char text[XR_MAX_RESULT_STRING_SIZE]{};
        if (instance_ != XR_NULL_HANDLE) xrResultToString(instance_, result, text);
        __android_log_print(
            XR_FAILED(result) ? ANDROID_LOG_ERROR : ANDROID_LOG_INFO,
            kPassthroughLogTag,
            "%s -> %d %s",
            what,
            result,
            text);
    }

    bool ensureCreated() {
        if (!available_) return false;
        if (created_) return true;

        XrPassthroughCreateInfoFB passthroughInfo{XR_TYPE_PASSTHROUGH_CREATE_INFO_FB};
        XrResult createFeature = createPassthrough_(session_, &passthroughInfo, &passthrough_);
        logResult("xrCreatePassthroughFB", createFeature);
        if (XR_FAILED(createFeature)) {
            passthrough_ = XR_NULL_HANDLE;
            return false;
        }

        XrPassthroughLayerCreateInfoFB layerInfo{XR_TYPE_PASSTHROUGH_LAYER_CREATE_INFO_FB};
        layerInfo.passthrough = passthrough_;
        layerInfo.purpose = XR_PASSTHROUGH_LAYER_PURPOSE_RECONSTRUCTION_FB;
        XrResult createLayerResult = createLayer_(session_, &layerInfo, &layer_);
        logResult("xrCreatePassthroughLayerFB", createLayerResult);
        if (XR_FAILED(createLayerResult)) {
            destroyPassthrough_(passthrough_);
            passthrough_ = XR_NULL_HANDLE;
            layer_ = XR_NULL_HANDLE;
            return false;
        }

        created_ = true;
        return true;
    }

    XrInstance instance_ = XR_NULL_HANDLE;
    XrSession session_ = XR_NULL_HANDLE;
    bool available_ = false;
    bool created_ = false;
    bool active_ = false;
    bool enableBlockedUntilToggle_ = false;
    XrPassthroughFB passthrough_ = XR_NULL_HANDLE;
    XrPassthroughLayerFB layer_ = XR_NULL_HANDLE;
    XrCompositionLayerPassthroughFB compositionLayer_{XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_FB};

    PFN_xrCreatePassthroughFB createPassthrough_ = nullptr;
    PFN_xrDestroyPassthroughFB destroyPassthrough_ = nullptr;
    PFN_xrPassthroughStartFB startPassthrough_ = nullptr;
    PFN_xrPassthroughPauseFB pausePassthrough_ = nullptr;
    PFN_xrCreatePassthroughLayerFB createLayer_ = nullptr;
    PFN_xrDestroyPassthroughLayerFB destroyLayer_ = nullptr;
    PFN_xrPassthroughLayerResumeFB resumeLayer_ = nullptr;
    PFN_xrPassthroughLayerPauseFB pauseLayer_ = nullptr;
    PFN_xrPassthroughLayerSetStyleFB setStyle_ = nullptr;
};

} // namespace questpad
