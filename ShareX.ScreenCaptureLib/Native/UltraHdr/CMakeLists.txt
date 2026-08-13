cmake_minimum_required(VERSION 3.20)
project(ShareXUltraHdrBridge LANGUAGES CXX)

if(NOT DEFINED UHDR_SOURCE_DIR)
  message(FATAL_ERROR "UHDR_SOURCE_DIR must point to the pinned libultrahdr source tree")
endif()

if(NOT DEFINED UHDR_STATIC_LIBRARY)
  message(FATAL_ERROR "UHDR_STATIC_LIBRARY must point to the pinned uhdr-static library")
endif()

add_library(sharex_ultrahdr SHARED sharex_ultrahdr_bridge.cpp)
target_compile_features(sharex_ultrahdr PRIVATE cxx_std_17)
target_compile_definitions(sharex_ultrahdr PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_include_directories(sharex_ultrahdr PRIVATE "${UHDR_SOURCE_DIR}")
target_link_libraries(sharex_ultrahdr PRIVATE "${UHDR_STATIC_LIBRARY}")

foreach(dependency IN ITEMS UHDR_JPEG_LIBRARY UHDR_HEIF_LIBRARY)
  if(DEFINED ${dependency})
    target_link_libraries(sharex_ultrahdr PRIVATE "${${dependency}}")
  endif()
endforeach()

if(MSVC)
  set_property(TARGET sharex_ultrahdr PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  target_compile_options(sharex_ultrahdr PRIVATE /W4 /WX)
endif()

set_target_properties(sharex_ultrahdr PROPERTIES OUTPUT_NAME "ShareX.UltraHdr")
