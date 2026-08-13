cmake_minimum_required(VERSION 3.20)
project(ShareXAvifBridge LANGUAGES C CXX)

if(NOT DEFINED AVIF_SOURCE_DIR)
  message(FATAL_ERROR "AVIF_SOURCE_DIR must point to the pinned libavif source tree")
endif()

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(AVIF_BUILD_APPS OFF CACHE BOOL "" FORCE)
set(AVIF_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(AVIF_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(AVIF_CODEC_AOM LOCAL CACHE STRING "" FORCE)
set(AVIF_LIBYUV OFF CACHE STRING "" FORCE)

if(MSVC)
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>" CACHE STRING "" FORCE)
endif()

add_subdirectory("${AVIF_SOURCE_DIR}" libavif EXCLUDE_FROM_ALL)

add_library(sharex_avif SHARED sharex_avif_bridge.cpp)
target_compile_features(sharex_avif PRIVATE cxx_std_17)
target_compile_definitions(sharex_avif PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(sharex_avif PRIVATE avif)

if(MSVC)
  set_property(TARGET sharex_avif PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  target_compile_options(sharex_avif PRIVATE /W4 /WX)
endif()

set_target_properties(sharex_avif PROPERTIES OUTPUT_NAME "ShareX.Avif")
