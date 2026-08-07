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
else()
  # NativeAOT names the Unix outputs dm_core.so and dm_core.dylib - NO `lib` prefix. That breaks
  # the obvious `target_link_libraries(tgt "/path/to/dm_core.so")`, because CMake only treats a
  # full path as a library FILE when the name matches the platform convention (libfoo.so). It
  # does not here, so CMake falls back to treating it as a link-item NAME and emits -ldm_core,
  # and the link fails with "cannot find -ldm_core" while the file sits right there.
  #
  # An IMPORTED target is the way to say "this exact file is the library". Copy this block rather
  # than the one-liner - INTEGRATION 2 carries the same recipe for the same reason.
  if(APPLE)
    set(DM_CORE_LIB "${DM_CORE_BUILD_DIR}/publish/dm_core.dylib")

    # macOS records the DEPENDENCY by the dylib's own install name, at link time. NativeAOT bakes
    # a RELATIVE one - "bin/Release/net9.0/<rid>/native/dm_core.dylib" - so the executable asks
    # dyld for that literal relative path and the load fails wherever the working directory is not
    # the publish root. An rpath cannot rescue it, because the recorded name is not @rpath/...
    #
    # So the install name is rewritten BEFORE the link, which is the only moment that matters:
    # afterwards the string is already inside the executable. Idempotent, so re-running cmake is
    # harmless.
    execute_process(
      COMMAND install_name_tool -id "@rpath/dm_core.dylib" "${DM_CORE_LIB}"
      RESULT_VARIABLE DM_INSTALL_NAME_RESULT
      ERROR_VARIABLE DM_INSTALL_NAME_ERROR)

    if(NOT DM_INSTALL_NAME_RESULT EQUAL 0)
      message(WARNING "install_name_tool failed: ${DM_INSTALL_NAME_ERROR}")
    endif()
  else()
    set(DM_CORE_LIB "${DM_CORE_BUILD_DIR}/publish/dm_core.so")
  endif()

  add_library(dm_core SHARED IMPORTED)
  set_target_properties(dm_core PROPERTIES IMPORTED_LOCATION "${DM_CORE_LIB}")
  target_link_libraries(dm_abi_smoke PRIVATE dm_core)

  # The loader needs to find it at RUN time too, and it is not on any default search path.
  set_target_properties(dm_abi_smoke PROPERTIES
    BUILD_RPATH "${DM_CORE_BUILD_DIR}/publish")
endif()

enable_testing()
add_test(NAME abi_smoke COMMAND dm_abi_smoke)
