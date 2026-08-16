cmake_minimum_required(VERSION 3.22.1)
project(questpad_motionprobe LANGUAGES C CXX)

find_package(OpenXR REQUIRED)

add_library(native_app_glue_motion STATIC
    ${ANDROID_NDK}/sources/android/native_app_glue/android_native_app_glue.c)
target_include_directories(native_app_glue_motion PUBLIC
    ${ANDROID_NDK}/sources/android/native_app_glue)
target_compile_definitions(native_app_glue_motion PRIVATE ANDROID)

add_library(questpad_motionprobe SHARED motionprobe.cpp)
target_include_directories(questpad_motionprobe PRIVATE
    ${ANDROID_NDK}/sources/android/native_app_glue)
target_compile_definitions(questpad_motionprobe PRIVATE
    XR_USE_PLATFORM_ANDROID=1
    XR_USE_GRAPHICS_API_OPENGL_ES=1)

target_link_libraries(questpad_motionprobe PRIVATE
    native_app_glue_motion
    OpenXR::openxr_loader
    android
    log
    EGL
    GLESv3)

target_link_options(questpad_motionprobe PRIVATE "-uANativeActivity_onCreate")
