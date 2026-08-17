#pragma once

#include <android/log.h>
#include <android/native_activity.h>
#include <jni.h>
#include <openxr/openxr.h>

namespace questpad {

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

    jobject act = activity->clazz;
    jclass ac = env->GetObjectClass(act);
    if (ac) {
        jmethodID getWindow = env->GetMethodID(ac, "getWindow", "()Landroid/view/Window;");
        jobject window = getWindow ? env->CallObjectMethod(act, getWindow) : nullptr;
        if (window) {
            jclass wc = env->GetObjectClass(window);
            if (wc) {
                jmethodID getAttrs = env->GetMethodID(wc, "getAttributes", "()Landroid/view/WindowManager$LayoutParams;");
                jobject attrs = getAttrs ? env->CallObjectMethod(window, getAttrs) : nullptr;
                if (attrs) {
                    jclass alc = env->GetObjectClass(attrs);
                    if (alc) {
                        jfieldID brightnessField = env->GetFieldID(alc, "screenBrightness", "F");
                        if (brightnessField) env->SetFloatField(attrs, brightnessField, brightness);
                        jmethodID setAttrs = env->GetMethodID(wc, "setAttributes", "(Landroid/view/WindowManager$LayoutParams;)V");
                        if (setAttrs) env->CallVoidMethod(window, setAttrs, attrs);
                        env->DeleteLocalRef(alc);
                    }
                    env->DeleteLocalRef(attrs);
                }
                env->DeleteLocalRef(wc);
            }
            env->DeleteLocalRef(window);
        }
        env->DeleteLocalRef(ac);
    }

    if (detach) activity->vm->DetachCurrentThread();
}

class PassthroughSupport {
public:
    bool initialize(XrInstance instance, XrSystemId systemId, XrSession session, bool extensionEnabled) {
        instance_ = instance;
        session_ = session;
        extensionEnabled_ = extensionEnabled;
        if (!extensionEnabled_) return false;

        XrSystemPassthroughPropertiesFB passthroughProperties{XR_TYPE_SYSTEM_PASSTHROUGH_PROPERTIES_FB};
        XrSystemProperties systemProperties{XR_TYPE_SYSTEM_PROPERTIES};
        systemProperties.next = &passthroughProperties;
        if (XR_FAILED(xrGetSystemProperties(instance_, systemId, &systemProperties)) ||
            passthroughProperties.supportsPassthrough != XR_TRUE) {
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
        return available_;
    }

    bool available() const { return available_; }
    bool active() const { return active_; }

    const XrCompositionLayerBaseHeader* compositionLayer() const {
        return active_ ? reinterpret_cast<const XrCompositionLayerBaseHeader*>(&compositionLayer_) : nullptr;
    }

    void setEnabled(bool enabled, ANativeActivity* activity) {
        if (enabled == active_) return;
        if (enabled) {
            if (!ensureCreated()) {
                available_ = false;
                setWindowBrightness(activity, 0.0f);
                return;
            }

            if (XR_FAILED(startPassthrough_(passthrough_)) || XR_FAILED(resumeLayer_(layer_))) {
                available_ = false;
                active_ = false;
                setWindowBrightness(activity, 0.0f);
                return;
            }

            if (setStyle_) {
                XrPassthroughStyleFB style{XR_TYPE_PASSTHROUGH_STYLE_FB};
                style.textureOpacityFactor = 1.0f;
                style.edgeColor = {0.0f, 0.0f, 0.0f, 0.0f};
                setStyle_(layer_, &style);
            }

            compositionLayer_ = {XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_FB};
            compositionLayer_.layerHandle = layer_;
            compositionLayer_.flags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT;
            compositionLayer_.space = XR_NULL_HANDLE;
            active_ = true;
            // -1 tells Android to use the system/default brightness instead of the
            // zero-layer power-saving override.
            setWindowBrightness(activity, -1.0f);
        } else {
            if (layer_ != XR_NULL_HANDLE && pauseLayer_) pauseLayer_(layer_);
            if (passthrough_ != XR_NULL_HANDLE && pausePassthrough_) pausePassthrough_(passthrough_);
            active_ = false;
            setWindowBrightness(activity, 0.0f);
        }
    }

    void destroy(ANativeActivity* activity) {
        setEnabled(false, activity);
        if (layer_ != XR_NULL_HANDLE && destroyLayer_) {
            destroyLayer_(layer_);
            layer_ = XR_NULL_HANDLE;
        }
        if (passthrough_ != XR_NULL_HANDLE && destroyPassthrough_) {
            destroyPassthrough_(passthrough_);
            passthrough_ = XR_NULL_HANDLE;
        }
        created_ = false;
    }

private:
    template <typename T>
    void load(const char* name, T& fn) {
        fn = nullptr;
        if (instance_ == XR_NULL_HANDLE) return;
        xrGetInstanceProcAddr(instance_, name, reinterpret_cast<PFN_xrVoidFunction*>(&fn));
    }

    bool ensureCreated() {
        if (!available_) return false;
        if (created_) return true;

        XrPassthroughCreateInfoFB passthroughInfo{XR_TYPE_PASSTHROUGH_CREATE_INFO_FB};
        if (XR_FAILED(createPassthrough_(session_, &passthroughInfo, &passthrough_))) return false;

        XrPassthroughLayerCreateInfoFB layerInfo{XR_TYPE_PASSTHROUGH_LAYER_CREATE_INFO_FB};
        layerInfo.passthrough = passthrough_;
        layerInfo.purpose = XR_PASSTHROUGH_LAYER_PURPOSE_RECONSTRUCTION_FB;
        if (XR_FAILED(createLayer_(session_, &layerInfo, &layer_))) {
            destroyPassthrough_(passthrough_);
            passthrough_ = XR_NULL_HANDLE;
            return false;
        }

        created_ = true;
        return true;
    }

    XrInstance instance_ = XR_NULL_HANDLE;
    XrSession session_ = XR_NULL_HANDLE;
    bool extensionEnabled_ = false;
    bool available_ = false;
    bool created_ = false;
    bool active_ = false;
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
