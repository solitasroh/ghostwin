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

# ─── Common (header-only) ───
add_library(ghostwin_common INTERFACE)
target_include_directories(ghostwin_common INTERFACE src/common)

# ─── stb_rect_pack (header-only, public domain) ───
add_library(stb_rect_pack INTERFACE)
target_include_directories(stb_rect_pack INTERFACE third_party)

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

# ─── Settings library (Phase 5-D) ───
add_library(settings STATIC
    src/settings/settings_manager.cpp
    src/settings/key_map.cpp
    src/settings/file_watcher.cpp
)
target_include_directories(settings PUBLIC src third_party)
target_link_libraries(settings PUBLIC ghostwin_common shell32)

# ─── Session library (Phase 5-A) ───
add_library(session STATIC
    src/session/session_manager.cpp
)
target_include_directories(session PUBLIC src)
target_link_libraries(session PUBLIC conpty renderer)

# ─── Renderer library (Phase 3) ───
add_library(renderer STATIC
    src/renderer/dx11_renderer.cpp
    src/renderer/glyph_atlas.cpp
    src/renderer/render_state.cpp
    src/renderer/quad_builder.cpp
    src/renderer/terminal_window.cpp
)
target_include_directories(renderer PUBLIC src/renderer src)
target_link_libraries(renderer PUBLIC
    vt_core ghostwin_common stb_rect_pack
    d3d11 dxgi dxguid d3dcompiler dwrite d2d1 dcomp
)

# ─── Engine DLL (WPF Hybrid PoC) ───
add_library(ghostwin_engine SHARED
    src/engine-api/ghostwin_engine.cpp
    src/engine-api/surface_manager.cpp
)
target_compile_definitions(ghostwin_engine PRIVATE GHOSTWIN_ENGINE_EXPORTS)
target_include_directories(ghostwin_engine PUBLIC
    src/engine-api
    src
)
# TSF static lib (for Engine DLL)
add_library(tsf STATIC src/tsf/tsf_implementation.cpp)
target_include_directories(tsf PUBLIC src)
target_link_libraries(tsf PUBLIC ole32 uuid)

target_link_libraries(ghostwin_engine PRIVATE
    session renderer settings tsf
    ole32 uuid imm32
    # CRT: Debug는 ucrtd/vcruntimed/msvcrtd, Release는 ucrt/vcruntime/msvcrt
    $<$<CONFIG:Debug>:ucrtd>   $<$<NOT:$<CONFIG:Debug>>:ucrt>
    $<$<CONFIG:Debug>:vcruntimed> $<$<NOT:$<CONFIG:Debug>>:vcruntime>
    $<$<CONFIG:Debug>:msvcrtd>  $<$<NOT:$<CONFIG:Debug>>:msvcrt>
)
add_dependencies(ghostwin_engine copy_ghostty_dll)

# Copy ghostty-vt.dll next to engine DLL
add_custom_command(TARGET ghostwin_engine POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${GHOSTTY_BIN_DIR}/ghostty-vt.dll"
        "$<TARGET_FILE_DIR:ghostwin_engine>"
    COMMENT "Copying ghostty-vt.dll next to engine DLL"
)

# ─── Main executable (Phase 3 PoC) ───
add_executable(ghostwin_terminal src/main.cpp)
target_include_directories(ghostwin_terminal PRIVATE src)
target_link_libraries(ghostwin_terminal PRIVATE renderer conpty)
add_dependencies(ghostwin_terminal copy_ghostty_dll)

# Phase 3: DX11 render test
add_executable(dx11_render_test tests/dx11_render_test.cpp)
target_include_directories(dx11_render_test PRIVATE src)
target_link_libraries(dx11_render_test PRIVATE renderer)

# Phase 3: Cell iteration test
add_executable(vt_bridge_cell_test tests/vt_bridge_cell_test.cpp)
target_link_libraries(vt_bridge_cell_test PRIVATE vt_core)
add_dependencies(vt_bridge_cell_test copy_ghostty_dll)

# TSF COM initialization test
add_executable(tsf_init_test tests/tsf_init_test.cpp)
target_link_libraries(tsf_init_test PRIVATE ole32.lib uuid.lib)

# Phase 3: RenderState test
add_executable(render_state_test tests/render_state_test.cpp)
target_include_directories(render_state_test PRIVATE src)
target_link_libraries(render_state_test PRIVATE renderer)
add_dependencies(render_state_test copy_ghostty_dll)

# Phase 4: QuadBuilder Korean glyph headless test (WARP device)
add_executable(quad_korean_test tests/quad_korean_test.cpp)
target_include_directories(quad_korean_test PRIVATE src)
target_link_libraries(quad_korean_test PRIVATE renderer)
add_dependencies(quad_korean_test copy_ghostty_dll)

# ─── WinUI3 Shell (removed in M-6, replaced by WPF) ───
# WinUI3 코드 및 의존성 제거됨. UI는 GhostWin.App (WPF C#)으로 대체.
# 복원 필요 시: git tag pre-winui3-removal 참조
