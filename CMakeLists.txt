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

# ─── WinUI3 Shell (Phase 4-A) ───
set(WINUI_DIR "${CMAKE_SOURCE_DIR}/external/winui")

if(EXISTS "${WINUI_DIR}/include")
    message(STATUS "WinUI3 SDK found at ${WINUI_DIR}")

    add_executable(ghostwin_winui WIN32
        src/app/main_winui.cpp
        src/app/winui_app.cpp
        src/tsf/tsf_implementation.cpp
    )
    target_include_directories(ghostwin_winui PRIVATE
        src
        "${WINUI_DIR}/include"
        "${WINUI_DIR}/cppwinrt"
        "${WINUI_DIR}/cppwinrt/winrt"
    )
    target_link_libraries(ghostwin_winui PRIVATE
        renderer conpty
        "${WINUI_DIR}/lib/Microsoft.WindowsAppRuntime.Bootstrap.lib"
        windowsapp.lib
        ole32.lib uuid.lib imm32.lib
    )
    target_compile_definitions(ghostwin_winui PRIVATE
        DISABLE_XAML_GENERATED_MAIN
    )
    # /bigobj: C++/WinRT generates many sections per TU
    target_compile_options(ghostwin_winui PRIVATE /bigobj)
    add_dependencies(ghostwin_winui copy_ghostty_dll)

    # Copy Bootstrap DLL to build output
    add_custom_command(TARGET ghostwin_winui POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${WINUI_DIR}/bin/Microsoft.WindowsAppRuntime.Bootstrap.dll"
            "$<TARGET_FILE_DIR:ghostwin_winui>"
        COMMENT "Copying WinUI3 Bootstrap DLL"
    )
else()
    message(WARNING "WinUI3 SDK not found. Run scripts/setup_winui.ps1 first.")
endif()
