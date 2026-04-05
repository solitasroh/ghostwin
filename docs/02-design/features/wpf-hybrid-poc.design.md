# WPF Hybrid PoC Design Document

> **Summary**: Engine 7K lines를 CMake native DLL(C API)로 격리하고, WPF C# PoC 앱에서 HwndHost + P/Invoke로 터미널 1화면을 렌더링. 6개 검증 항목의 구현 명세.
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-05
> **Status**: Draft
> **Planning Doc**: [wpf-hybrid-poc.plan.md](../../01-plan/features/wpf-hybrid-poc.plan.md)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 10K+ UI 코드를 WinUI3 Code-only C++로 작성 시 생산성 2.5~3배 저하. WPF 전환이 합의되었으나 ClearType/P/Invoke/TSF 기술 리스크 미검증. |
| **Solution** | Engine을 `ghostwin_engine.dll` (C API, 예외 방어막)로 분리하고, WPF HwndHost에서 HWND SwapChain으로 DX11 렌더링. 3일 PoC로 6항목 검증. |
| **Function/UX Effect** | PoC 결과: 터미널 1화면이 WPF 창에서 ClearType 품질로 렌더링되고, 한글 입력이 동작하며, wpf-ui 테마가 적용된 상태. |
| **Core Value** | 추측이 아닌 실증 기반 아키텍처 결정. Go 시 10K+ UI 개발 속도 3배 향상의 토대. |

---

## 1. Overview

### 1.1 Design Goals

1. **Engine DLL 격리**: 엔진 코드를 변경 없이 SHARED DLL로 빌드, C API 래퍼로 노출
2. **HWND SwapChain ClearType**: Phase 3 HWND SwapChain 코드를 재활용하여 HwndHost에서 렌더링
3. **P/Invoke 왕복 검증**: 키입력 → Engine → ConPTY → 출력 → WPF 콜백 전체 경로
4. **TSF 유지**: Hidden HWND + TSF 패턴을 WPF HwndSource로 이식
5. **wpf-ui 테마**: Mica + DarkMode + NavigationView 동작 확인
6. **스루풋 벤치마크**: 대량 출력 시 WPF Dispatcher 병목 방지

### 1.2 Design Principles

- **기존 엔진 코드 변경 최소화**: 새 파일(`engine-api/`) 추가만, 기존 `.cpp` 수정 0건 목표
- **예외 방어막 필수**: C API 경계의 모든 진입점에 `try-catch(...)` — C++ 예외의 C# 누수 방지
- **Blittable 타입만 사용**: P/Invoke 마샬링 비용 0을 위해 `void*`, `uint32_t`, `wchar_t*`, 함수 포인터만
- **Phase 3 코드 재활용**: `DX11Renderer::create(RendererConfig)` HWND 경로가 이미 존재

### 1.3 Plan과의 차이점

| Plan 기술 | Design 결정 | 변경 근거 |
|-----------|------------|-----------|
| Engine DLL에 Settings 포함 | Settings 제외 (C# 이전 대상) | Plan에서 사용자가 "Settings는 C#으로 이동" 결정 |
| `gw_render_frame` API | 렌더 루프를 Engine 내부에서 관리 | 렌더 스레드를 C# 측에서 제어하면 GC 간섭 위험 |
| TSF를 별도 API로 노출 | Engine 내부에서 TSF 관리, attach HWND만 전달 | TSF COM 객체를 P/Invoke로 넘기는 것은 불필요한 복잡도 |

---

## 2. Architecture

### 2.1 PoC 전체 구조

```text
┌──────────────────────────────────────────────────────┐
│  GhostWinPoC.exe  (C# WPF, .NET 9)                  │
│                                                      │
│  ┌─────────────┐  ┌──────────────────────────────┐   │
│  │ wpf-ui      │  │ MainWindow.xaml               │   │
│  │ FluentWindow│  │  ┌─────────────────────────┐  │   │
│  │ Mica+Dark   │  │  │ TerminalHostControl     │  │   │
│  └─────────────┘  │  │ (HwndHost subclass)     │  │   │
│                   │  │  ┌───────────────────┐   │  │   │
│  ┌─────────────┐  │  │  │ Child HWND        │   │  │   │
│  │ NavView     │  │  │  │ HWND SwapChain    │   │  │   │
│  │ (테마확인용) │  │  │  │ DX11 ClearType    │   │  │   │
│  └─────────────┘  │  │  └───────────────────┘   │  │   │
│                   │  └─────────────────────────┘  │   │
│  ┌─────────────┐  └──────────────────────────────┘   │
│  │ HwndSource  │   ← Hidden HWND for TSF/IME         │
│  └──────┬──────┘                                     │
│         │                                            │
│  ┌──────┴──────────────────────────────────────┐     │
│  │ NativeEngine.cs  (LibraryImport P/Invoke)   │     │
│  │ EngineHandle.cs  (SafeHandle + IDisposable) │     │
│  │ CallbackBridge.cs (UnmanagedCallersOnly)    │     │
│  └──────┬──────────────────────────────────────┘     │
├─────────┼────────────────────────────────────────────┤
│         ▼  ghostwin_engine.dll  (C++ Native, CMake)  │
│  ┌──────────────────────────────────────────────┐    │
│  │ ghostwin_engine.h/cpp  (C API + 예외 방어막)  │    │
│  │  ┌──────────┐ ┌──────────┐ ┌──────────────┐  │    │
│  │  │DX11      │ │SessionMgr│ │ConPTY        │  │    │
│  │  │Renderer  │ │          │ │Session       │  │    │
│  │  │GlyphAtlas│ │SessionRef│ │(I/O thread)  │  │    │
│  │  │QuadBuild │ │Events CB │ │VtCore        │  │    │
│  │  └──────────┘ └──────────┘ └──────────────┘  │    │
│  │  ┌──────────┐ ┌──────────┐                    │    │
│  │  │TSF/IME   │ │RenderLoop│ ← 전용 native      │    │
│  │  │(COM)     │ │(thread)  │   스레드             │    │
│  │  └──────────┘ └──────────┘                    │    │
│  └──────────────────────────────────────────────┘    │
├──────────────────────────────────────────────────────┤
│  ghostty-vt.dll  (Zig, x86_64-windows-gnu)           │
└──────────────────────────────────────────────────────┘
```

### 2.2 Engine DLL 내부 소유권

```text
GwEngine (ghostwin_engine.cpp 내부)
├── SessionManager (unique_ptr)
│   └── Session[] (vector)
│       ├── ConPtySession (unique_ptr) — I/O thread 내장
│       │   └── VtCore (내부 소유)
│       │       └── vt_bridge → ghostty-vt.dll
│       ├── TerminalRenderState (unique_ptr)
│       └── TsfHandle (move-only)
│           └── TsfImplementation (COM)
│               └── SessionTsfAdapter (IDataProvider)
├── DX11Renderer (unique_ptr)
│   ├── GlyphAtlas (unique_ptr)
│   └── QuadBuilder (내부)
├── RenderThread (std::jthread)
│   └── RenderLoop: start_paint → upload_and_draw → Present
└── GwCallbacks (함수 포인터 구조체, C# 소유 콜백)
```

### 2.3 스레드 모델

| 스레드 | 소유자 | 역할 | GC 영향 |
|--------|--------|------|---------|
| **WPF UI Thread** | C# (.NET CLR) | WPF 이벤트, P/Invoke 호출, 콜백 수신 | GC 대상 |
| **Render Thread** | Engine DLL (std::jthread) | DX11 렌더 루프 (waitable swapchain) | **GC 없음** |
| **I/O Thread ×N** | Engine DLL (ConPtySession당 1개) | ConPTY pipe 읽기 → VtCore write | **GC 없음** |
| **Cleanup Thread** | Engine DLL (SessionManager) | 세션 종료 처리 (I/O thread join) | **GC 없음** |

**핵심**: 성능 크리티컬 경로(렌더링, VT 파싱, ConPTY I/O)는 모두 네이티브 스레드에서 실행되어 .NET GC의 영향을 받지 않음.

---

## 3. Engine C API 상세 설계

### 3.1 헤더 파일: `ghostwin_engine.h`

```c
// ghostwin_engine.h — WPF PoC용 최소 C API
#pragma once

#include <stdint.h>
#include <stdbool.h>

#ifdef _WIN32
#include <windows.h>
#endif

#ifdef GHOSTWIN_ENGINE_EXPORTS
#define GWAPI __declspec(dllexport)
#else
#define GWAPI __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

// ── Opaque handles ──
typedef void* GwEngine;
typedef uint32_t GwSessionId;

// ── Error codes ──
#define GW_OK           0
#define GW_ERR_INVALID  -1
#define GW_ERR_INTERNAL -2
#define GW_ERR_NOT_FOUND -3

// ── Callbacks (모두 blittable 함수 포인터) ──
// 주의: on_output은 I/O thread에서 호출됨. 나머지는 호출 스레드 불특정.
typedef void (*GwSessionFn)(void* ctx, GwSessionId id);
typedef void (*GwExitFn)(void* ctx, GwSessionId id, uint32_t exit_code);
typedef void (*GwTitleFn)(void* ctx, GwSessionId id, const wchar_t* title, uint32_t len);
typedef void (*GwCwdFn)(void* ctx, GwSessionId id, const wchar_t* cwd, uint32_t len);
typedef void (*GwRenderInvalidateFn)(void* ctx);  // 렌더 완료 시 WPF에 알림

typedef struct {
    void* context;           // GCHandle.ToIntPtr(this)
    GwSessionFn   on_created;
    GwSessionFn   on_closed;
    GwSessionFn   on_activated;
    GwTitleFn     on_title_changed;
    GwCwdFn       on_cwd_changed;
    GwExitFn      on_child_exit;
    GwRenderInvalidateFn on_render_done;  // 프레임 완료 시 호출 (렌더 스레드)
} GwCallbacks;

// ── Engine lifecycle ──
GWAPI GwEngine gw_engine_create(const GwCallbacks* callbacks);
GWAPI void     gw_engine_destroy(GwEngine engine);

// ── Render init (HWND 기반 — HwndHost가 제공) ──
GWAPI int gw_render_init(GwEngine engine, HWND hwnd,
                          uint32_t width_px, uint32_t height_px,
                          float font_size_pt, const wchar_t* font_family);
GWAPI int gw_render_resize(GwEngine engine, uint32_t width_px, uint32_t height_px);
GWAPI int gw_render_set_clear_color(GwEngine engine, uint32_t rgb);

// ── Session lifecycle ──
GWAPI GwSessionId gw_session_create(GwEngine engine,
                                     const wchar_t* shell_path,  // NULL = 자동감지
                                     const wchar_t* initial_dir, // NULL = 현재 디렉토리
                                     uint16_t cols, uint16_t rows);
GWAPI int  gw_session_close(GwEngine engine, GwSessionId id);
GWAPI void gw_session_activate(GwEngine engine, GwSessionId id);

// ── I/O ──
GWAPI int  gw_session_write(GwEngine engine, GwSessionId id,
                             const uint8_t* data, uint32_t len);
GWAPI int  gw_session_resize(GwEngine engine, GwSessionId id,
                              uint16_t cols, uint16_t rows);

// ── TSF/IME ──
GWAPI int  gw_tsf_attach(GwEngine engine, HWND hidden_hwnd);
GWAPI int  gw_tsf_focus(GwEngine engine, GwSessionId id);
GWAPI int  gw_tsf_unfocus(GwEngine engine);

// ── Query ──
GWAPI uint32_t gw_session_count(GwEngine engine);
GWAPI GwSessionId gw_active_session_id(GwEngine engine);
GWAPI void gw_poll_titles(GwEngine engine);

// ── Render loop control ──
GWAPI int  gw_render_start(GwEngine engine);   // 렌더 스레드 시작
GWAPI void gw_render_stop(GwEngine engine);    // 렌더 스레드 정지

// ── Benchmark ──
GWAPI int  gw_benchmark_throughput(GwEngine engine, const char* file_path,
                                    uint32_t* out_lines_per_sec);

#ifdef __cplusplus
}
#endif
```

### 3.2 예외 방어막 패턴

```cpp
// ghostwin_engine.cpp — 모든 API 함수의 공통 패턴
#define GW_TRY_BEGIN try {
#define GW_TRY_END \
    } catch (const std::exception& e) { \
        OutputDebugStringA(e.what()); \
        return GW_ERR_INTERNAL; \
    } catch (...) { \
        OutputDebugStringA("ghostwin_engine: unknown exception"); \
        return GW_ERR_INTERNAL; \
    }

GWAPI GwSessionId gw_session_create(GwEngine engine,
                                     const wchar_t* shell_path,
                                     const wchar_t* initial_dir,
                                     uint16_t cols, uint16_t rows) {
    GW_TRY_BEGIN
        auto* eng = static_cast<EngineImpl*>(engine);
        SessionCreateParams params;
        if (shell_path) params.shell_path = shell_path;
        if (initial_dir) params.initial_dir = initial_dir;
        params.cols = cols;
        params.rows = rows;
        return eng->session_manager->create_session(
            params, eng->input_hwnd,
            eng->viewport_fn, eng->cursor_fn, eng->fn_ctx);
    GW_TRY_END
    return 0; // unreachable, 컴파일러 경고 방지
}
```

### 3.3 렌더 루프 설계

렌더 루프는 **Engine DLL 내부**에서 관리합니다. C#은 `gw_render_start/stop`만 호출.

```cpp
// Engine 내부 렌더 스레드
void EngineImpl::render_loop() {
    while (render_running_.load(std::memory_order_acquire)) {
        WaitForSingleObject(frame_latency_waitable_, 16);

        auto* session = session_manager_->active_session();
        if (!session || !session->conpty) continue;

        auto& vt = session->conpty->vt_core();
        auto& state = *session->render_state;

        {
            std::lock_guard lock(session->vt_mutex);
            vt.update_render_state(state);
        }

        renderer_->start_paint(state, *atlas_, staging_);
        renderer_->upload_and_draw(staging_.data(),
                                    static_cast<uint32_t>(staging_.size()),
                                    bg_count_);

        // 프레임 완료 알림 (WPF에 Invalidate 힌트)
        if (callbacks_.on_render_done)
            callbacks_.on_render_done(callbacks_.context);
    }
}
```

---

## 4. WPF C# 설계

### 4.1 P/Invoke 레이어: `NativeEngine.cs`

```csharp
// Interop/NativeEngine.cs
using System.Runtime.InteropServices;

namespace GhostWinPoC.Interop;

internal static partial class NativeEngine
{
    private const string Dll = "ghostwin_engine";

    // Engine lifecycle
    [LibraryImport(Dll)]
    internal static partial nint gw_engine_create(in GwCallbacks callbacks);

    [LibraryImport(Dll)]
    internal static partial void gw_engine_destroy(nint engine);

    // Render
    [LibraryImport(Dll)]
    internal static partial int gw_render_init(nint engine, nint hwnd,
        uint width, uint height, float fontSize,
        [MarshalAs(UnmanagedType.LPWStr)] string fontFamily);

    [LibraryImport(Dll)]
    internal static partial int gw_render_resize(nint engine, uint width, uint height);

    [LibraryImport(Dll)]
    internal static partial int gw_render_start(nint engine);

    [LibraryImport(Dll)]
    internal static partial void gw_render_stop(nint engine);

    // Session
    [LibraryImport(Dll)]
    internal static partial uint gw_session_create(nint engine,
        [MarshalAs(UnmanagedType.LPWStr)] string? shellPath,
        [MarshalAs(UnmanagedType.LPWStr)] string? initialDir,
        ushort cols, ushort rows);

    [LibraryImport(Dll)]
    internal static partial int gw_session_close(nint engine, uint id);

    [LibraryImport(Dll)]
    internal static partial int gw_session_write(nint engine, uint id,
        nint data, uint len);

    // TSF
    [LibraryImport(Dll)]
    internal static partial int gw_tsf_attach(nint engine, nint hiddenHwnd);

    [LibraryImport(Dll)]
    internal static partial int gw_tsf_focus(nint engine, uint sessionId);
}
```

### 4.2 콜백 브릿지: `CallbackBridge.cs`

```csharp
// Interop/CallbackBridge.cs
using System.Runtime.InteropServices;

namespace GhostWinPoC.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct GwCallbacks
{
    public nint Context;
    public nint OnCreated;
    public nint OnClosed;
    public nint OnActivated;
    public nint OnTitleChanged;
    public nint OnCwdChanged;
    public nint OnChildExit;
    public nint OnRenderDone;
}

internal static class CallbackBridge
{
    // I/O thread → WPF UI thread 디스패치
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static void OnTitleChanged(nint ctx, uint sessionId, nint titlePtr, uint titleLen)
    {
        var title = Marshal.PtrToStringUni(titlePtr, (int)titleLen) ?? "";
        var app = (App)GCHandle.FromIntPtr(ctx).Target!;

        app.Dispatcher.BeginInvoke(() =>
        {
            // ViewModel 업데이트 (UI thread)
            app.OnSessionTitleChanged(sessionId, title);
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static void OnChildExit(nint ctx, uint sessionId, uint exitCode)
    {
        var app = (App)GCHandle.FromIntPtr(ctx).Target!;
        app.Dispatcher.BeginInvoke(() =>
        {
            app.OnSessionExited(sessionId, exitCode);
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static void OnRenderDone(nint ctx)
    {
        // 렌더 스레드에서 호출 — WPF Invalidate 불필요
        // HWND SwapChain은 Present()가 직접 화면에 출력하므로
        // WPF 측 작업 없음
    }
}
```

### 4.3 HwndHost 구현: `TerminalHostControl.cs`

```csharp
// Controls/TerminalHostControl.cs
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace GhostWinPoC.Controls;

public class TerminalHostControl : HwndHost
{
    private nint _childHwnd;
    private const string ChildClassName = "GhostWinTerminalChild";

    // WPF가 호출: child HWND 생성
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // Win32 자식 윈도우 생성
        var wc = new WNDCLASSEX {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate<WndProcDelegate>(WndProc),
            hInstance = Marshal.GetHINSTANCE(typeof(TerminalHostControl).Module),
            lpszClassName = ChildClassName,
        };
        RegisterClassEx(ref wc);

        _childHwnd = CreateWindowEx(
            0, ChildClassName, "",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, (int)ActualWidth, (int)ActualHeight,
            hwndParent.Handle, IntPtr.Zero,
            wc.hInstance, IntPtr.Zero);

        return new HandleRef(this, _childHwnd);
    }

    // WPF가 호출: child HWND 파괴
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
    }

    // Engine에 HWND 전달용
    public nint ChildHwnd => _childHwnd;

    // 리사이즈 시 Engine에 알림
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_childHwnd != IntPtr.Zero)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var widthPx = (uint)(sizeInfo.NewSize.Width * dpi.DpiScaleX);
            var heightPx = (uint)(sizeInfo.NewSize.Height * dpi.DpiScaleY);

            // Child HWND 리사이즈
            SetWindowPos(_childHwnd, IntPtr.Zero, 0, 0,
                (int)widthPx, (int)heightPx, SWP_NOZORDER | SWP_NOMOVE);

            // Engine에 SwapChain 리사이즈 요청
            RenderResizeRequested?.Invoke(widthPx, heightPx);
        }
    }

    public event Action<uint, uint>? RenderResizeRequested;

    // WndProc — 키보드 메시지를 Hidden HWND로 리다이렉트
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // P/Invoke declarations (Win32)
    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")] static extern nint CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll")] static extern bool SetWindowPos(
        nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern nint DefWindowProc(
        nint hwnd, uint msg, nint wParam, nint lParam);

    const uint WS_CHILD = 0x40000000;
    const uint WS_VISIBLE = 0x10000000;
    const uint WS_CLIPCHILDREN = 0x02000000;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOMOVE = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }
}
```

### 4.4 TSF Hidden HWND: `TsfBridge.cs`

```csharp
// Interop/TsfBridge.cs
using System.Windows.Interop;

namespace GhostWinPoC.Interop;

/// <summary>
/// WPF 내에서 Hidden HWND를 생성하여 Engine의 TSF에 연결.
/// 키보드 포커스 시 이 HWND로 SetFocus.
/// </summary>
public class TsfBridge : IDisposable
{
    private HwndSource? _hwndSource;

    public nint Hwnd => _hwndSource?.Handle ?? IntPtr.Zero;

    public void Initialize(nint parentHwnd)
    {
        var param = new HwndSourceParameters("GhostWinTsfInput")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000, // 화면 밖
            PositionY = -32000,
            WindowStyle = 0x00000000, // WS_OVERLAPPED only
            ParentWindow = parentHwnd,
        };
        _hwndSource = new HwndSource(param);
        _hwndSource.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // WM_KEYDOWN, WM_CHAR 등은 TSF가 처리
        // WPF로 전달하지 않음 (handled = false 유지)
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _hwndSource?.Dispose();
        _hwndSource = null;
    }
}
```

---

## 5. CMake 변경 사항

### 5.1 `ghostwin_engine` SHARED 타깃 추가

```cmake
# CMakeLists.txt 에 추가 (기존 타깃 아래)

# ── Engine DLL ──
add_library(ghostwin_engine SHARED
    src/engine-api/ghostwin_engine.cpp
)

target_compile_definitions(ghostwin_engine PRIVATE GHOSTWIN_ENGINE_EXPORTS)

target_include_directories(ghostwin_engine PUBLIC
    ${CMAKE_SOURCE_DIR}/src/engine-api
    ${CMAKE_SOURCE_DIR}/src
)

target_link_libraries(ghostwin_engine PRIVATE
    session          # SessionManager → ConPTY → VtCore
    renderer         # DX11Renderer, GlyphAtlas, QuadBuilder
    libghostty_vt    # ghostty-vt.dll import lib
)

# ghostty-vt.dll도 Engine DLL 옆에 복사
add_custom_command(TARGET ghostwin_engine POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${GHOSTTY_BIN_DIR}/ghostty-vt.dll"
        "$<TARGET_FILE_DIR:ghostwin_engine>"
)
```

### 5.2 WPF 프로젝트: `GhostWinPoC.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Wpf.Ui" Version="3.*" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
  </ItemGroup>

  <!-- Native DLL 복사 -->
  <ItemGroup>
    <None Include="..\build\ghostwin_engine.dll" CopyToOutputDirectory="PreserveNewest" />
    <None Include="..\build\ghostty-vt.dll" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

---

## 6. 검증 항목별 구현 명세

### V1: Engine DLL C API 빌드

| 항목 | 명세 |
|------|------|
| 파일 | `src/engine-api/ghostwin_engine.h`, `ghostwin_engine.cpp` |
| API 수 | 최소 15개 (위 헤더 참조) |
| 예외 방어 | `GW_TRY_BEGIN/END` 매크로, 모든 진입점 적용 |
| 빌드 확인 | `dumpbin /exports ghostwin_engine.dll`로 심볼 확인 |
| P/Invoke 확인 | C# 콘솔 앱에서 `gw_engine_create` → `gw_engine_destroy` 호출 |

### V2: HwndHost + ClearType

| 항목 | 명세 |
|------|------|
| SwapChain | `CreateSwapChainForHwnd` (HWND 기반, ALPHA_MODE 불필요) |
| 렌더러 | `DX11Renderer::create(RendererConfig)` — Phase 3 코드 그대로 |
| ClearType | Dual Source Blending 셰이더 동일. HWND는 기본 opaque → ClearType 자동 동작 |
| 리사이즈 | `OnRenderSizeChanged` → `SetWindowPos` → `gw_render_resize` 순서 |
| 검증 | 현행 WinUI3 스크린샷 vs PoC 스크린샷 시각 비교 |

### V3: P/Invoke 키입력 왕복

| 항목 | 명세 |
|------|------|
| 경로 | WPF KeyDown → UTF-8 변환 → `gw_session_write` P/Invoke → ConPTY → VT → 렌더 |
| 측정 | `Stopwatch.GetTimestamp()` 기반 왕복 시간 100회 측정, P99 < 1ms |
| 키입력 처리 | Hidden HWND의 `WM_CHAR` → TSF → Engine 콜백 → `gw_session_write` |

### V4: TSF 한글 IME

| 항목 | 명세 |
|------|------|
| 구현 | `TsfBridge.cs`로 HwndSource 생성, `gw_tsf_attach(engine, hwnd)` |
| 포커스 | TerminalHostControl 클릭 시 `SetFocus(tsfBridge.Hwnd)` |
| 검증 | "한글테스트" 입력 → 조합 중 백스페이스 → 확정 → VT 출력 확인 |

### V5: wpf-ui 테마

| 항목 | 명세 |
|------|------|
| 패키지 | `Wpf.Ui` NuGet |
| Mica | `FluentWindow` 베이스 클래스 + `WindowBackdropType.Mica` |
| 다크모드 | `ApplicationThemeManager.Apply(ApplicationTheme.Dark)` |
| NavigationView | 더미 3페이지 (General, Appearance, About) |
| Airspace 우회 | Popup/Flyout는 별도 `Window` + `AllowsTransparency`로 Z-order 확보 |

### V6: 스루풋 벤치마크

| 항목 | 명세 |
|------|------|
| 테스트 | 1MB 텍스트 파일 `cat` 출력, 처리 완료까지 시간 측정 |
| 기준 | 현행 WinUI3 `ghostwin_winui.exe`와 동일 파일 비교 |
| 쓰로틀링 | `on_render_done` 콜백에서 WPF 측 갱신 불필요 (HWND 직접 Present) |
| Go 기준 | PoC ≥ 현행의 90% |

---

## 7. 프로젝트 구조

```text
ghostwin/
├── CMakeLists.txt                    (+ghostwin_engine SHARED 추가)
├── src/
│   ├── engine-api/                   ★ 신규
│   │   ├── ghostwin_engine.h         (C API 헤더)
│   │   └── ghostwin_engine.cpp       (C API 구현 + 예외 방어막)
│   ├── renderer/                     (기존 유지, 변경 없음)
│   ├── vt-core/                      (기존 유지, 변경 없음)
│   ├── conpty/                       (기존 유지, 변경 없음)
│   ├── tsf/                          (기존 유지, 변경 없음)
│   ├── session/                      (기존 유지, 변경 없음)
│   ├── settings/                     (기존 유지, PoC 후 C# 이전 대상)
│   ├── ui/                           (기존 유지, WinUI3용)
│   └── app/                          (기존 유지, WinUI3용)
│
├── wpf-poc/                          ★ 신규
│   ├── GhostWinPoC.csproj
│   ├── app.manifest                  (DPI awareness: PerMonitorV2)
│   ├── App.xaml
│   ├── App.xaml.cs                   (Engine 초기화, GCHandle 관리)
│   ├── MainWindow.xaml               (FluentWindow + Grid 레이아웃)
│   ├── MainWindow.xaml.cs            (TerminalHost + TSF 연결)
│   ├── Controls/
│   │   └── TerminalHostControl.cs    (HwndHost 서브클래스)
│   ├── Interop/
│   │   ├── NativeEngine.cs           (LibraryImport P/Invoke)
│   │   ├── CallbackBridge.cs         (UnmanagedCallersOnly 콜백)
│   │   ├── EngineHandle.cs           (SafeHandle + IDisposable)
│   │   └── TsfBridge.cs              (HwndSource Hidden HWND)
│   └── Pages/
│       ├── GeneralPage.xaml          (wpf-ui NavigationView 테스트)
│       ├── AppearancePage.xaml
│       └── AboutPage.xaml
│
├── scripts/
│   ├── build_wpf_poc.ps1             ★ 신규 (CMake → dotnet 2단계)
│   ├── build_libghostty.ps1          (기존 유지)
│   ├── build_ghostwin.ps1            (기존 유지)
│   └── setup_winui.ps1               (기존 유지)
│
└── external/
    ├── ghostty/                      (기존 유지)
    └── winui/                        (기존 유지, PoC와 독립)
```

---

## 8. 빌드 파이프라인: `build_wpf_poc.ps1`

```powershell
# scripts/build_wpf_poc.ps1
param([string]$Config = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# Step 1: ghostty-vt.dll 빌드 (이미 있으면 스킵)
$vtDll = "$root/external/ghostty/zig-out/bin/ghostty-vt.dll"
if (-not (Test-Path $vtDll)) {
    Write-Host "Building libghostty-vt..."
    & "$root/scripts/build_libghostty.ps1"
}

# Step 2: Engine DLL 빌드 (CMake + Ninja)
Write-Host "Building ghostwin_engine.dll..."
& "$root/scripts/build_ghostwin.ps1" -Config $Config

# Step 3: WPF PoC 빌드 (dotnet)
Write-Host "Building WPF PoC..."
$engineDll = "$root/build/ghostwin_engine.dll"
$vtDllBuild = "$root/build/ghostty-vt.dll"

# DLL을 wpf-poc 출력 디렉토리에 복사
$wpfOut = "$root/wpf-poc/bin/$Config/net9.0-windows"
if (-not (Test-Path $wpfOut)) { New-Item -ItemType Directory -Path $wpfOut -Force }
Copy-Item $engineDll $wpfOut -Force
Copy-Item $vtDllBuild $wpfOut -Force

dotnet build "$root/wpf-poc/GhostWinPoC.csproj" -c $Config

Write-Host "Build complete: $wpfOut/GhostWinPoC.exe"
```

---

## 9. 구현 순서 (Day 1 → Day 3)

| Day | 순서 | 작업 | 파일 | 검증 |
|-----|------|------|------|------|
| **1** | 1 | CMake에 ghostwin_engine SHARED 추가 | `CMakeLists.txt` | `dumpbin /exports` |
| | 2 | `ghostwin_engine.h` 헤더 작성 | `src/engine-api/ghostwin_engine.h` | 컴파일 |
| | 3 | `ghostwin_engine.cpp` 구현 (예외 방어막 포함) | `src/engine-api/ghostwin_engine.cpp` | DLL 빌드 |
| | 4 | WPF .csproj 생성 + NuGet | `wpf-poc/GhostWinPoC.csproj` | `dotnet build` |
| | 5 | `NativeEngine.cs` P/Invoke 선언 | `wpf-poc/Interop/NativeEngine.cs` | create/destroy 호출 |
| | 6 | `build_wpf_poc.ps1` 빌드 스크립트 | `scripts/build_wpf_poc.ps1` | 전체 빌드 성공 |
| **2** | 7 | `TerminalHostControl.cs` HwndHost | `wpf-poc/Controls/TerminalHostControl.cs` | Child HWND 생성 |
| | 8 | Engine에서 HWND SwapChain 렌더링 연결 | `ghostwin_engine.cpp` (render_init) | 화면 출력 |
| | 9 | ClearType 시각 비교 | — | 스크린샷 비교 |
| | 10 | `TsfBridge.cs` + `gw_tsf_attach` | `wpf-poc/Interop/TsfBridge.cs` | 한글 입력 |
| **3** | 11 | `FluentWindow` + Mica + DarkMode | `wpf-poc/MainWindow.xaml` | 테마 확인 |
| | 12 | NavigationView 더미 페이지 | `wpf-poc/Pages/*.xaml` | 페이지 전환 |
| | 13 | Airspace 우회 팝업 테스트 | — | 오버레이 동작 |
| | 14 | P/Invoke 왕복 지연 측정 | — | P99 < 1ms |
| | 15 | 스루풋 벤치마크 | — | ≥ 90% |
| | 16 | Go/No-Go ADR 작성 | `docs/adr/` | 최종 판정 |

---

## 10. Airspace 문제 대응 설계

HwndHost의 가장 큰 제약 — WPF 요소가 Child HWND 위에 렌더링되지 않음.

### 영향받는 UI

| UI 요소 | 터미널 위 오버레이 필요 | 대응 |
|---------|:---:|------|
| Tab Sidebar | X (옆에 배치) | 영향 없음 |
| Title Bar | X (위에 배치) | 영향 없음 |
| Settings 패널 | X (별도 Window) | 영향 없음 |
| **Search Overlay** | **O** | 별도 Popup Window |
| **Command Palette** | **O** | 별도 Popup Window |
| **Context Menu** | **O** | WPF ContextMenu (자동 Popup) |
| **Tooltip** | **O** | WPF ToolTip (자동 Popup) |

### 오버레이 Popup 패턴 (PoC에서 검증)

```csharp
// 터미널 영역 위에 검색 바를 띄우는 예시
var popup = new Window {
    WindowStyle = WindowStyle.None,
    AllowsTransparency = true,
    Background = Brushes.Transparent,
    ShowInTaskbar = false,
    Topmost = true,  // Z-order 강제
    Owner = mainWindow,
};
// 위치를 TerminalHostControl 영역 기준으로 계산
var pos = terminalHost.PointToScreen(new Point(0, 0));
popup.Left = pos.X;
popup.Top = pos.Y;
popup.Show();
```

---

## 11. DPI 처리 설계

| 이벤트 | 처리 |
|--------|------|
| WPF Window DPI 변경 | `Window.DpiChanged` → `gw_render_resize` (물리 픽셀) |
| HwndHost 크기 변경 | `OnRenderSizeChanged` → DPI 스케일 적용 → `SetWindowPos` + `gw_render_resize` |
| GlyphAtlas 재생성 | Engine 내부에서 DPI 변경 감지 시 자동 처리 |

`app.manifest`에서 Per-Monitor V2 DPI awareness 선언:

```xml
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">
      PerMonitorV2
    </dpiAwareness>
  </windowsSettings>
</application>
```

---

*Design created for WPF Hybrid PoC — wpf-hybrid-poc feature*
