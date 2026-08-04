cmake_minimum_required(VERSION 3.20)
project(dm_abi_smoke CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Reference integration for native consumers. The Qt client should be able to lift the
# target_include_directories / target_link_libraries lines below verbatim.

if(WIN32)
  set(DM_RID_DEFAULT "win-x64")
elseif(APPLE)
  set(DM_RID_DEFAULT "osx-x64")
else()
  set(DM_RID_DEFAULT "linux-x64")
endif()

# Building 32-bit needs the generator platform as well as the RID, since MSVC targets x64 by
# default and would otherwise link x64 objects against an x86 import library:
#
#   cmake -S tests/abi-smoke -B build/smoke32 -A Win32 -DDM_RID=win-x86
#
set(DM_RID "${DM_RID_DEFAULT}" CACHE STRING "Runtime identifier of the dm_core build to link")

set(DM_CORE_BUILD_DIR
    "${CMAKE_CURRENT_SOURCE_DIR}/../../src/Dm.Native/bin/Release/net9.0/${DM_RID}"
    CACHE PATH "Directory containing native/ and publish/ from the dm_core AOT publish")

set(DM_ABI_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../abi")

add_executable(dm_abi_smoke main.cpp)
target_include_directories(dm_abi_smoke PRIVATE "${DM_ABI_DIR}")

if(WIN32)
  # NativeAOT emits an import library alongside the DLL.
  target_link_libraries(dm_abi_smoke PRIVATE "${DM_CORE_BUILD_DIR}/native/dm_core.lib")

  add_custom_command(TARGET dm_abi_smoke POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${DM_CORE_BUILD_DIR}/publish/dm_core.dll"
            "$<TARGET_FILE_DIR:dm_abi_smoke>")
elseif(APPLE)
  target_link_libraries(dm_abi_smoke PRIVATE "${DM_CORE_BUILD_DIR}/publish/libdm_core.dylib")
else()
  target_link_libraries(dm_abi_smoke PRIVATE "${DM_CORE_BUILD_DIR}/publish/libdm_core.so")
  set_target_properties(dm_abi_smoke PROPERTIES
    BUILD_RPATH "${DM_CORE_BUILD_DIR}/publish")
endif()

enable_testing()
add_test(NAME abi_smoke COMMAND dm_abi_smoke)
