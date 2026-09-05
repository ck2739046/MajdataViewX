cmake_minimum_required(VERSION 3.21)

project(FFmpegLauncher LANGUAGES C)

if(NOT WIN32)
    message(FATAL_ERROR "FFmpegLauncher requires Windows.")
endif()

add_library(FFmpegLauncher SHARED
    "src/FFmpegLauncher.c"
    "src/FFmpegLauncher.h")

target_compile_features(FFmpegLauncher PRIVATE c_std_11)
target_compile_definitions(FFmpegLauncher PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS)
target_include_directories(FFmpegLauncher PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src")

if(MSVC)
    target_compile_options(FFmpegLauncher PRIVATE /utf-8)
endif()
