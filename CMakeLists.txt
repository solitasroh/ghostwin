cmake_minimum_required(VERSION 3.25)

# Force Windows SDK 10.0.22621.0 — 26100 is missing specstrings_strict.h
# Must be set BEFORE project() to take effect on SDK selection
set(CMAKE_SYSTEM_VERSION 10.0.22621.0)
set(CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION 10.0.22621.0)

project(ghostwin VERSION 0.1.0 LANGUAGES C CXX)

set(CMAKE_C_STANDARD 17)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# /utf-8: treat source files as UTF-8 (fixes C4819 on Korean Windows)
add_compile_options("$<$<C_COMPILER_ID:MSVC>:/utf-8>")
add_compile_options("$<$<CXX_COMPILER_ID:MSVC>:/utf-8>")

# /MD (dynamic CRT) — CMake default
# Requires MSVC 14.51+ (vcvarsall -vcvars_ver=14.51)

# ─── libghostty-vt (pre-built DLL + import lib) ───
# GNU 타겟 static lib은 MSVC 링커와 COMDAT 불호환 (LNK1143)
# DLL이 GNU/MSVC 격리 계층 역할 — ADR-003 참조
set(GHOSTTY_DIR "${CMAKE_SOURCE_DIR}/external/ghostty")
set(GHOSTTY_LIB_DIR "${GHOSTTY_DIR}/zig-out/lib")
set(GHOSTTY_BIN_DIR "${GHOSTTY_DIR}/zig-out/bin")
set(GHOSTTY_INCLUDE_DIR "${GHOSTTY_DIR}/include")

add_library(libghostty_vt SHARED IMPORTED)
set_target_properties(libghostty_vt PROPERTIES
    IMPORTED_IMPLIB "${GHOSTTY_LIB_DIR}/ghostty-vt.lib"
    IMPORTED_LOCATION "${GHOSTTY_BIN_DIR}/ghostty-vt.dll"
    INTERFACE_INCLUDE_DIRECTORIES "${GHOSTTY_INCLUDE_DIR}"
)

# Copy DLL to build output
add_custom_target(copy_ghostty_dll ALL
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${GHOSTTY_BIN_DIR}/ghostty-vt.dll"
        "${CMAKE_BINARY_DIR}/ghostty-vt.dll"
    COMMENT "Copying ghostty-vt.dll"
)

# ─── VtCore wrapper library ───
add_library(vt_core STATIC
    src/vt-core/vt_bridge.c
    src/vt-core/vt_core.cpp
)
target_include_directories(vt_core PUBLIC
    src/vt-core
    ${GHOSTTY_INCLUDE_DIR}
)
target_link_libraries(vt_core PUBLIC libghostty_vt ntdll)

# ─── ConPTY session library ───
add_library(conpty STATIC
    src/conpty/conpty_session.cpp
)
target_include_directories(conpty PUBLIC src/conpty)
target_link_libraries(conpty PUBLIC vt_core)

# ─── Test executables ───
add_executable(vt_core_test tests/vt_core_test.cpp)
target_link_libraries(vt_core_test PRIVATE vt_core)
add_dependencies(vt_core_test copy_ghostty_dll)

add_executable(vt_minimal_test tests/vt_minimal_test.cpp)
target_link_libraries(vt_minimal_test PRIVATE vt_core)
add_dependencies(vt_minimal_test copy_ghostty_dll)

# Raw C test — no wrapper, direct ghostty API call
add_executable(ghostty_raw_test tests/ghostty_raw_test.c)
target_include_directories(ghostty_raw_test PRIVATE ${GHOSTTY_INCLUDE_DIR})
target_link_libraries(ghostty_raw_test PRIVATE libghostty_vt ntdll)
add_dependencies(ghostty_raw_test copy_ghostty_dll)

add_executable(conpty_integration_test tests/conpty_integration_test.cpp)
target_link_libraries(conpty_integration_test PRIVATE conpty)
add_dependencies(conpty_integration_test copy_ghostty_dll)

add_executable(conpty_benchmark tests/conpty_benchmark.cpp)
target_link_libraries(conpty_benchmark PRIVATE conpty)
add_dependencies(conpty_benchmark copy_ghostty_dll)
