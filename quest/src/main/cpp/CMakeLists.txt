cmake_minimum_required(VERSION 3.22.1)
project(questpad LANGUAGES C CXX)

find_package(OpenXR REQUIRED)

add_library(native_app_glue STATIC
    ${ANDROID_NDK}/sources/android/native_app_glue/android_native_app_glue.c)
target_include_directories(native_app_glue PUBLIC
    ${ANDROID_NDK}/sources/android/native_app_glue)
target_compile_definitions(native_app_glue PRIVATE ANDROID)

add_library(questpad SHARED questpad.cpp)
target_include_directories(questpad PRIVATE
    ${ANDROID_NDK}/sources/android/native_app_glue)
target_compile_definitions(questpad PRIVATE
    XR_USE_PLATFORM_ANDROID=1
    XR_USE_GRAPHICS_API_OPENGL_ES=1)

target_link_libraries(questpad PRIVATE
    native_app_glue
    OpenXR::openxr_loader
    android
    log
    EGL
    GLESv3)

# NativeActivity entry point is otherwise stripped by the linker.
target_link_options(questpad PRIVATE "-uANativeActivity_onCreate")
