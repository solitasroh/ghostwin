# winui3-shell Design

> **Feature**: WinUI3 UI Shell + SwapChainPanel DX11 연결
> **Project**: GhostWin Terminal
> **Phase**: 4-A (Master: winui3-integration FR-01~07)
> **Date**: 2026-03-30
> **Author**: Solit
> **Plan**: `docs/01-plan/features/winui3-shell.plan.md`
> **Revision**: 3.0 (구현 후 반영 — ADR-009 + gap-analysis 94%)

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | Phase 3 DX11 렌더러가 Win32 HWND에 종속. XAML UI (탭, 사이드바) 추가 불가 |
| **Solution** | Code-only WinUI3 (XAML 파일 없음) + NuGet 수동 관리로 기존 CMake 빌드 유지. SwapChainPanel에 DX11 스왑체인 연결 |
| **Function/UX** | WinUI3 창에서 커스텀 타이틀바 + 사이드바 스켈레톤 + SwapChainPanel 터미널 동작 |
| **Core Value** | Win32 HWND 종속 제거. CMake 빌드 시스템 유지하면서 XAML 기반 확보 |

---

## 1. Build System Decision

### 1.1 결정: Code-only WinUI3 + NuGet 수동 관리

Plan FR-06에서 `MainWindow.xaml` 기반 설계를 기술했으나, **CMake + Ninja 빌드 유지를 위해 Code-only 방식으로 변경**한다. XAML 컴파일러(`midl.exe`, XBF 생성) 의존성 제거가 핵심 이유.

1. CMake + Ninja 빌드 체인 유지 (XAML 컴파일러 불필요)
2. 터미널 UI는 단순 (SwapChainPanel + Grid + ListView) — XAML 디자이너 불필요
3. Windows Terminal도 core rendering은 코드에서 직접 구성

### 1.2 필요 NuGet 패키지

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.WindowsAppSDK | 1.6.250205002 | WinUI3 런타임 + 헤더 |
| Microsoft.Windows.CppWinRT | 2.0.240405.15 | C++/WinRT projection 헤더 생성 (`cppwinrt.exe` 포함) |
| Microsoft.Web.WebView2 | (reference only) | WinAppSDK winmd 의존성 해결용 (빌드 시 미사용) |

`setup_winui.ps1`에 **버전 호환성 매트릭스를 하드코딩**하고, 패키지 간 버전 불일치 시 에러를 출력한다. 생성된 projection 헤더는 `.gitignore`에 추가하고, CMake에서 `setup_winui.ps1` 미실행 시 경고를 출력한다.

### 1.3 Bootstrap API (Unpackaged)

```cpp
#include <MddBootstrap.h>
#include <WindowsAppSDK-VersionInfo.h>

namespace MddBootstrap = ::Microsoft::Windows::ApplicationModel::DynamicDependency::Bootstrap;

int WINAPI wWinMain(...) {
    winrt::init_apartment(winrt::apartment_type::single_threaded);

    // Bootstrap 초기화 — C++ wrapper (RAII shutdown guard)
    auto hr = MddBootstrap::InitializeNoThrow(
        WINDOWSAPPSDK_RELEASE_MAJORMINOR,
        WINDOWSAPPSDK_RELEASE_VERSION_TAG_W,
        WINDOWSAPPSDK_RUNTIME_VERSION_UINT64,
        MddBootstrap::InitializeOptions::OnNoMatch_ShowUI);
    if (FAILED(hr)) { /* MessageBox + return 1 */ }

    MddBootstrap::unique_mddbootstrapshutdown bootstrapGuard(...);

    // Undocked RegFree WinRT — CMake 빌드 필수 (ADR-009)
    HMODULE hRuntime = LoadLibraryW(L"Microsoft.WindowsAppRuntime.dll");
    if (hRuntime) {
        auto fn = GetProcAddress(hRuntime, "WindowsAppRuntime_EnsureIsLoaded");
        if (fn) ((HRESULT(STDAPICALLTYPE*)())fn)();
    }

    // Application 시작 (DispatcherQueue는 내부 자동 생성)
    winrt::Microsoft::UI::Xaml::Application::Start([](auto&&) {
        winrt::make<GhostWinApp>();
    });

    return 0;
}
```

---

## 2. Architecture

### 2.1 4-스레드 모델 (Phase 3 유지)

```
┌─────────────────────────────────────────────────────────┐
│ UI Thread (ASTA)     — WinUI3 DispatcherQueue            │
│   KeyDown/CharacterReceived → ConPTY send_input          │
│   SizeChanged → atomic resize_requested flag             │
│   CompositionScaleChanged → atomic dpi_changed flag      │
│   DispatcherTimer → cursor blink toggle                  │
├─────────────────────────────────────────────────────────┤
│ Render Thread (ThreadPool)                               │
│   check resize_requested → pause → resize → resume       │
│   start_paint(vt_mutex) → QuadBuilder → upload_and_draw  │
│   WaitForSingleObject(waitable) when idle                │
├─────────────────────────────────────────────────────────┤
│ Parse Thread (ConPtySession 내부)                         │
│   I/O read → VtCore::write() (vt_mutex 보호)             │
├─────────────────────────────────────────────────────────┤
│ I/O Thread (IOCP)                                        │
│   ConPTY async read → Parse Thread feed                  │
│   on_exit → DispatcherQueue.TryEnqueue(종료 요청)        │
└─────────────────────────────────────────────────────────┘
```

**공유 자원 및 잠금 전략** (ADR-006 vt_mutex 유지):

| 공유 자원 | 접근 스레드 | 잠금 |
|-----------|-----------|------|
| VtCore | Parse + Render | `vt_mutex` (std::mutex) |
| TerminalRenderState | Render (write) + UI (resize) | Render Thread pause protocol |
| DX11 Device Context | Render (draw) + UI (resize) | Render Thread pause protocol |
| ConPtySession::send_input | UI + I/O | ConPTY 내부 동기화 |

### 2.2 Render Thread Pause Protocol (리사이즈/DPI)

WinUI3에서는 UI 스레드에서 `join()`하면 ASTA 데드락이 발생한다. 대신 atomic flag 기반 pause를 사용:

```cpp
// UI Thread (SizeChanged 디바운스 타이머 콜백)
m_resize_requested.store(true);
// width/height를 atomic으로 저장
m_pending_width.store(new_w);
m_pending_height.store(new_h);

// Render Thread (매 프레임 시작)
if (m_resize_requested.load()) {
    // 렌더 스레드 내부에서 안전하게 리사이즈
    uint32_t w = m_pending_width.load();
    uint32_t h = m_pending_height.load();
    m_renderer->resize_swapchain(w, h);
    { std::lock_guard lock(m_vt_mutex);
      m_session->resize(new_cols, new_rows);
      m_state->resize(new_cols, new_rows);
    }
    m_resize_requested.store(false);
}
// 이후 정상 렌더 루프 진행
```

이 패턴은 **D3D11 Device Context 동시 접근을 방지**하면서 UI 스레드를 블록하지 않는다.

### 2.3 파일 구조

```
src/
├── app/                          ← NEW (WinUI3 shell)
│   ├── winui_app.h               ← Application + Window + 렌더 스레드 관리
│   ├── winui_app.cpp             ← UI 이벤트 핸들러 + D3D11 초기화
│   └── main_winui.cpp            ← Bootstrap + DispatcherQueue + Application::Start
├── renderer/
│   ├── dx11_renderer.h/cpp       ← + create_for_composition() 별도 팩토리
│   ├── terminal_window.h/cpp     ← 유지 (Phase 3 PoC 폴백)
│   └── ...
├── conpty/                       ← 변경 없음
├── vt-core/                      ← 변경 없음
└── main.cpp                      ← 유지 (Phase 3 PoC 폴백)
```

### 2.4 DX11Renderer 팩토리 분리

기존 `create(RendererConfig)` (HWND 필수)를 유지하고, **별도 팩토리 메서드**를 추가한다. HWND/Composition 분기를 내부 Impl에 캡슐화하여 코드 분기 폭발을 방지:

```cpp
// dx11_renderer.h

struct CompositionConfig {
    uint32_t width = 800;
    uint32_t height = 600;
    float font_size_pt = constants::kDefaultFontSizePt;
    const wchar_t* font_family = L"Cascadia Mono";
};

class DX11Renderer {
public:
    // Phase 3 PoC (HWND 기반)
    [[nodiscard]] static std::unique_ptr<DX11Renderer> create(
        const RendererConfig& config, Error* out_error = nullptr);

    // Phase 4 WinUI3 (Composition 기반)
    [[nodiscard]] static std::unique_ptr<DX11Renderer> create_for_composition(
        const CompositionConfig& config, Error* out_error = nullptr);

    /// Composition 스왑체인 raw pointer (SetSwapChain()에 전달)
    /// DX11Renderer가 소유권 보유. caller는 Release 금지.
    [[nodiscard]] IDXGISwapChain1* composition_swapchain() const;

    // ... 기존 API 유지 ...
};
```

**내부 Impl 구조:**
- `create_swapchain_hwnd(HWND)` — 기존 `CreateSwapChainForHwnd` 경로
- `create_swapchain_composition(w, h)` — `CreateSwapChainForComposition` 경로
- `resize_swapchain()`, `draw_instances()` 등은 **공통** — 스왑체인 타입과 무관

Composition 스왑체인에서도 `DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT`를 사용한다 (DoD-6 GPU 유휴 < 1% 충족). `IDXGISwapChain2`로 QI하여 waitable object를 획득하는 코드는 HWND 경로와 동일.

### 2.5 WinUI3 Application + Window

```cpp
// src/app/winui_app.h
namespace ghostwin {

// #undef GetCurrentTime 필수 — windows.h 매크로 충돌 (ADR-009)

class GhostWinApp : public winrt::Microsoft::UI::Xaml::ApplicationT<
        GhostWinApp, winrt::Microsoft::UI::Xaml::Markup::IXamlMetadataProvider> {
public:
    void OnLaunched(winrt::Microsoft::UI::Xaml::LaunchActivatedEventArgs const&);

    // IXamlMetadataProvider — Code-only WinUI3 필수 (ADR-009)
    // XamlControlsXamlMetaDataProvider에 위임
    markup::IXamlType GetXamlType(winrt::Windows::UI::Xaml::Interop::TypeName const& type);
    markup::IXamlType GetXamlType(winrt::hstring const& fullName);
    winrt::com_array<markup::XmlnsDefinition> GetXmlnsDefinitions();

private:
    winrt::Microsoft::UI::Xaml::XamlTypeInfo::XamlControlsXamlMetaDataProvider m_provider;
    winrt::Microsoft::UI::Xaml::Window m_window{nullptr};
    winrt::Microsoft::UI::Xaml::Controls::SwapChainPanel m_panel{nullptr};

    // Terminal components
    std::unique_ptr<DX11Renderer> m_renderer;
    std::unique_ptr<GlyphAtlas> m_atlas;
    std::unique_ptr<ConPtySession> m_session;
    std::unique_ptr<TerminalRenderState> m_state;

    // Render thread control (joinable, not detached — ADR-009 C1/C2 fix)
    std::thread m_render_thread;
    std::atomic<bool> m_render_running{false};
    std::atomic<bool> m_resize_requested{false};
    std::atomic<uint32_t> m_pending_width{800};   // 초기값 0 방지
    std::atomic<uint32_t> m_pending_height{600};

    // Mica backdrop (멤버로 유지해야 해제되지 않음)
    winrt::Microsoft::UI::Composition::SystemBackdrops::MicaController m_mica_controller{nullptr};
    winrt::Microsoft::UI::Composition::SystemBackdrops::SystemBackdropConfiguration m_backdrop_config{nullptr};
    std::mutex m_vt_mutex;
    std::vector<QuadInstance> m_staging;

    // Timers
    winrt::Microsoft::UI::Xaml::DispatcherTimer m_resize_timer{nullptr};
    winrt::Microsoft::UI::Xaml::DispatcherTimer m_blink_timer{nullptr};
    std::atomic<bool> m_cursor_blink_visible{true};  // UI/렌더 스레드 간 atomic

    // Surrogate pair buffering (emoji input)
    wchar_t m_high_surrogate = 0;

    void InitializeD3D11(winrt::Microsoft::UI::Xaml::Controls::SwapChainPanel const& panel);
    void StartTerminal(uint32_t width_px, uint32_t height_px);
    void RenderLoop();
    void OnResizeTimerTick(winrt::Windows::Foundation::IInspectable const&, winrt::Windows::Foundation::IInspectable const&);
};

} // namespace ghostwin
```

### 2.6 OnLaunched — UI 코드 구성

```cpp
void GhostWinApp::OnLaunched(LaunchActivatedEventArgs const&) {
    // XamlControlsResources 등록 (Phase 5 NavigationView/TabView 대비)
    auto resources = winrt::Microsoft::UI::Xaml::Controls::XamlControlsResources();
    Application::Current().Resources().MergedDictionaries().Append(resources);

    m_window = Window();
    m_window.ExtendsContentIntoTitleBar(true);

    // Root Grid
    auto grid = Grid();
    auto col0 = ColumnDefinition(); col0.Width(GridLengthHelper::FromPixels(220));
    auto col1 = ColumnDefinition(); col1.Width(GridLength{1, GridUnitType::Star});
    grid.ColumnDefinitions().Append(col0);
    grid.ColumnDefinitions().Append(col1);

    // Sidebar placeholder
    auto sidebar = ListView();
    Grid::SetColumn(sidebar, 0);
    grid.Children().Append(sidebar);

    // SwapChainPanel
    m_panel = SwapChainPanel();
    m_panel.IsTabStop(true);
    Grid::SetColumn(m_panel, 1);
    grid.Children().Append(m_panel);

    m_window.Content(grid);

    // Events (get_strong()로 weak reference 보호)
    m_panel.Loaded([self = get_strong()](auto&&, auto&&) {
        self->InitializeD3D11(self->m_panel);
    });

    m_panel.SizeChanged([self = get_strong()](auto&&, SizeChangedEventArgs const&) {
        self->m_resize_timer.Stop();
        self->m_resize_timer.Interval(std::chrono::milliseconds(100));
        self->m_resize_timer.Start();
    });

    m_panel.CompositionScaleChanged([self = get_strong()](SwapChainPanel const& p, auto&&) {
        // DPI 변경 → 렌더 스레드에서 처리 (UI 스레드 블록 방지)
        float scaleX = p.CompositionScaleX();
        uint32_t phys_w = (uint32_t)(p.ActualWidth() * scaleX);
        uint32_t phys_h = (uint32_t)(p.ActualHeight() * p.CompositionScaleY());
        self->m_pending_width.store(phys_w > 0 ? phys_w : 1);
        self->m_pending_height.store(phys_h > 0 ? phys_h : 1);
        self->m_resize_requested.store(true);
    });

    // Keyboard
    m_panel.KeyDown([self = get_strong()](auto&&, KeyRoutedEventArgs const& e) {
        // Phase 3 WM_KEYDOWN 이식
        const char* seq = nullptr;
        switch (e.Key()) {
        case VirtualKey::Up:     seq = "\033[A"; break;
        case VirtualKey::Down:   seq = "\033[B"; break;
        case VirtualKey::Right:  seq = "\033[C"; break;
        case VirtualKey::Left:   seq = "\033[D"; break;
        case VirtualKey::Home:   seq = "\033[H"; break;
        case VirtualKey::End:    seq = "\033[F"; break;
        case VirtualKey::Delete: seq = "\033[3~"; break;
        }
        if (seq) {
            self->m_session->send_input({(const uint8_t*)seq, strlen(seq)});
            e.Handled(true);
        }
    });

    m_panel.CharacterReceived([self = get_strong()](auto&&, CharacterReceivedRoutedEventArgs const& e) {
        wchar_t ch = e.Character();

        // Surrogate pair buffering (이모지 입력)
        if (ch >= 0xD800 && ch <= 0xDBFF) {
            self->m_high_surrogate = ch;
            e.Handled(true);
            return;
        }
        if (ch >= 0xDC00 && ch <= 0xDFFF && self->m_high_surrogate != 0) {
            wchar_t pair[2] = { self->m_high_surrogate, ch };
            self->m_high_surrogate = 0;
            char utf8[8];
            int len = WideCharToMultiByte(CP_UTF8, 0, pair, 2, utf8, sizeof(utf8), nullptr, nullptr);
            if (len > 0) self->m_session->send_input({(uint8_t*)utf8, (size_t)len});
            e.Handled(true);
            return;
        }
        self->m_high_surrogate = 0;

        // Control characters
        if (ch < 0x20) {
            uint8_t byte = (ch == 0x08) ? 0x7F : (uint8_t)ch;
            self->m_session->send_input({&byte, 1});
        } else {
            char utf8[4];
            int len = WideCharToMultiByte(CP_UTF8, 0, &ch, 1, utf8, sizeof(utf8), nullptr, nullptr);
            if (len > 0) self->m_session->send_input({(uint8_t*)utf8, (size_t)len});
        }
        e.Handled(true);
    });

    // Cursor blink timer
    m_blink_timer = DispatcherTimer();
    m_blink_timer.Interval(std::chrono::milliseconds(530));
    m_blink_timer.Tick([self = get_strong()](auto&&, auto&&) {
        self->m_cursor_blink_visible = !self->m_cursor_blink_visible;
    });
    m_blink_timer.Start();

    // Resize debounce timer (DPI-aware: 물리 픽셀 단위)
    m_resize_timer = DispatcherTimer();
    m_resize_timer.Tick([self = get_strong()](auto&&, auto&&) {
        self->m_resize_timer.Stop();
        float scaleX = self->m_panel.CompositionScaleX();
        float scaleY = self->m_panel.CompositionScaleY();
        uint32_t w = (uint32_t)(self->m_panel.ActualWidth() * scaleX);
        uint32_t h = (uint32_t)(self->m_panel.ActualHeight() * scaleY);
        self->m_pending_width.store(w > 0 ? w : 1, std::memory_order_release);
        self->m_pending_height.store(h > 0 ? h : 1, std::memory_order_release);
        self->m_resize_requested.store(true, std::memory_order_release);
    });

    // Mica backdrop (선택적 — 멤버 변수로 유지해야 해제되지 않음)
    if (winrt::Microsoft::UI::Composition::SystemBackdrops::MicaController::IsSupported()) {
        m_backdrop_config = winrt::Microsoft::UI::Composition::SystemBackdrops::SystemBackdropConfiguration();
        m_mica_controller = winrt::Microsoft::UI::Composition::SystemBackdrops::MicaController();
        m_mica_controller.AddSystemBackdropTarget(m_window.as<winrt::Microsoft::UI::Composition::ICompositionSupportsSystemBackdrop>());
        m_mica_controller.SetSystemBackdropConfiguration(m_backdrop_config);
    }

    m_window.Activate();
    m_panel.Focus(winrt::Microsoft::UI::Xaml::FocusState::Programmatic);
}
```

### 2.7 SwapChainPanel + D3D11 연결

```cpp
#include <microsoft.ui.xaml.media.dxinterop.h>  // ISwapChainPanelNative

void GhostWinApp::InitializeD3D11(SwapChainPanel const& panel) {
    float w = (float)panel.ActualWidth();
    float h = (float)panel.ActualHeight();
    // ActualWidth/Height=0 방어
    if (w < 1.0f) w = 1.0f;
    if (h < 1.0f) h = 1.0f;

    // DX11 Renderer (Composition 모드)
    CompositionConfig cfg;
    cfg.width = (uint32_t)w;
    cfg.height = (uint32_t)h;
    Error err{};
    m_renderer = DX11Renderer::create_for_composition(cfg, &err);
    if (!m_renderer) { /* error dialog */ return; }

    // Connect swapchain to SwapChainPanel
    auto panelNative = panel.as<ISwapChainPanelNative>();
    winrt::check_hresult(panelNative->SetSwapChain(m_renderer->composition_swapchain()));

    StartTerminal(cfg.width, cfg.height);
}

void GhostWinApp::StartTerminal(uint32_t width_px, uint32_t height_px) {
    // GlyphAtlas, ConPtySession, RenderState 생성 (Phase 3 main.cpp 이식)
    // ... (생략, S6에서 구현)

    // Staging buffer 초기 할당
    uint16_t cols = (uint16_t)(width_px / m_atlas->cell_width());
    uint16_t rows = (uint16_t)(height_px / m_atlas->cell_height());
    m_staging.resize((size_t)cols * rows * constants::kInstanceMultiplier + 1);

    // 렌더 스레드 시작 (staging 할당 이후, joinable — C1/C2 fix)
    m_render_running.store(true, std::memory_order_release);
    m_render_thread = std::thread([this] { RenderLoop(); });
}
```

### 2.8 렌더 스레드

```cpp
void GhostWinApp::RenderLoop() {
    QuadBuilder builder(m_atlas->cell_width(), m_atlas->cell_height(), m_atlas->baseline());

    while (m_render_running.load(std::memory_order_acquire)) {
        // Resize check (Render Thread Pause Protocol — acquire/release ordering)
        if (m_resize_requested.load(std::memory_order_acquire)) {
            uint32_t w = m_pending_width.load(std::memory_order_acquire);
            uint32_t h = m_pending_height.load(std::memory_order_acquire);
            if (w == 0) w = 1;
            if (h == 0) h = 1;
            m_renderer->resize_swapchain(w, h);
            uint16_t cols = (uint16_t)(w / m_atlas->cell_width());
            uint16_t rows = (uint16_t)(h / m_atlas->cell_height());
            if (cols < 1) cols = 1;
            if (rows < 1) rows = 1;
            {
                std::lock_guard lock(m_vt_mutex);
                m_session->resize(cols, rows);
                m_state->resize(cols, rows);
            }
            m_staging.resize((size_t)cols * rows * constants::kInstanceMultiplier + 1);
            builder.update_cell_size(m_atlas->cell_width(), m_atlas->cell_height());
            m_resize_requested.store(false, std::memory_order_release);
        }

        // Normal render
        bool dirty = m_state->start_paint(m_vt_mutex, m_session->vt_core());
        if (!dirty) {
            // Waitable swapchain idle (GPU < 1% — DoD-6)
            // WaitForSingleObject(m_renderer->frame_latency_waitable(), 1);
            Sleep(1);  // waitable 미지원 시 폴백
            continue;
        }

        const auto& frame = m_state->frame();
        uint32_t count = builder.build(frame, *m_atlas, m_renderer->context(),
                                        std::span<QuadInstance>(m_staging));
        if (count > 0) {
            m_renderer->upload_and_draw(m_staging.data(), count);
        }
    }
}
```

### 2.9 ConPTY 종료 처리

```cpp
// ConPTY exit callback — I/O 스레드에서 호출됨
scfg.on_exit = [self = get_strong()](uint32_t code) {
    LOG_I("main", "Child process exited with code %u", code);
    // UI 스레드로 마샬링 — 렌더 스레드 정지 후 Close (C4 fix)
    self->m_window.DispatcherQueue().TryEnqueue([self]() {
        self->ShutdownRenderThread();  // join 대기
        self->m_window.Close();
    });
};
```

---

## 3. CMakeLists.txt 변경

```cmake
# ─── WinUI3 (Phase 4) ───
set(WINUI_DIR "${CMAKE_SOURCE_DIR}/external/winui")

if(EXISTS "${WINUI_DIR}/include")
    message(STATUS "WinUI3 SDK found at ${WINUI_DIR}")

    add_executable(ghostwin_winui WIN32
        src/app/main_winui.cpp
        src/app/winui_app.cpp
    )
    target_include_directories(ghostwin_winui PRIVATE
        src
        "${WINUI_DIR}/include"
        "${WINUI_DIR}/cppwinrt"
    )
    target_link_libraries(ghostwin_winui PRIVATE
        renderer conpty
        "${WINUI_DIR}/lib/Microsoft.WindowsAppRuntime.Bootstrap.lib"
        windowsapp.lib
    )
    target_compile_definitions(ghostwin_winui PRIVATE
        DISABLE_XAML_GENERATED_MAIN  # Code-only WinUI3
    )
    add_dependencies(ghostwin_winui copy_ghostty_dll)
else()
    message(WARNING "WinUI3 SDK not found. Run scripts/setup_winui.ps1 first.")
endif()
```

기존 `ghostwin_terminal` (Phase 3 PoC) + 모든 테스트 타겟은 유지.

---

## 4. Error Handling

| Error | Location | Handling |
|-------|----------|----------|
| `MddBootstrapInitialize2` 실패 | `main_winui.cpp` | MessageBox + 설치 URL 안내 + return 1 |
| `CreateSwapChainForComposition` 실패 | `dx11_renderer.cpp` | Error 구조체 반환 → InitializeD3D11에서 에러 다이얼로그 |
| `panel.as<ISwapChainPanelNative>()` QI 실패 | `winui_app.cpp` | `winrt::check_hresult` → C++ exception |
| `SwapChainPanel ActualWidth/Height = 0` | `winui_app.cpp` | 최소 1px 보장 |
| ConPTY 생성 실패 | `winui_app.cpp` | 에러 로그 + 창 닫기 |
| NuGet 미설치 (external/winui 없음) | `CMakeLists.txt` | WARNING 메시지 + ghostwin_winui 빌드 스킵 |

---

## 5. Implementation Order

| Step | Task | Files | DoD |
|------|------|-------|-----|
| S1 | `setup_winui.ps1` — NuGet 다운로드 + cppwinrt 헤더 생성 + 버전 검증 | `scripts/setup_winui.ps1` | `external/winui/` 에 헤더+lib 배치 |
| S2 | CMakeLists.txt에 `ghostwin_winui` 타겟 추가 | `CMakeLists.txt` | 빈 WinUI3 앱 빌드 성공 |
| S3 | `main_winui.cpp` — Bootstrap + DispatcherQueue + Application::Start | `src/app/main_winui.cpp` | 빈 WinUI3 창 실행 |
| S4 | `winui_app.cpp` — OnLaunched + XamlControlsResources + 빈 Grid | `src/app/winui_app.h/cpp` | 빈 WinUI3 창 + Grid 표시 |
| S5 | `DX11Renderer::create_for_composition()` 팩토리 추가 | `dx11_renderer.h/cpp` | Composition 스왑체인 생성 성공 |
| S6 | SwapChainPanel.Loaded → SetSwapChain + 렌더 루프 | `winui_app.cpp` | cmd.exe 출력 화면 표시 |
| S7 | KeyDown + CharacterReceived → ConPTY (surrogate pair 포함) | `winui_app.cpp` | 키보드 + 특수키 + 이모지 |
| S8 | Render Thread Pause Protocol + 디바운스 리사이즈 | `winui_app.cpp` | 창 크기 변경 정상 |
| S9 | CompositionScaleChanged → atomic DPI | `winui_app.cpp` | 모니터 이동 시 선명도 |
| S10 | Grid 2-컬럼 + ListView 사이드바 + 커스텀 타이틀바 | `winui_app.cpp` | 사이드바 + 타이틀바 표시 |
| S11 | Mica 배경 (조건부) + 커서 블링크 DispatcherTimer | `winui_app.cpp` | Mica + 커서 블링크 |
| S12 | GPU 유휴 < 1% 검증 (waitable swapchain) | 테스트 | GPU-Z 측정 |

---

## 6. Test Plan

| # | Test | Expected |
|---|------|----------|
| T1 | 기존 23/23 테스트 (ghostwin_terminal 빌드 유지) | PASS |
| T2 | `ghostwin_winui.exe` 빈 창 실행 | WinUI3 창 표시 |
| T3 | SwapChainPanel clear 색상 | 렌더 확인 |
| T4 | cmd.exe/pwsh.exe 렌더링 | 기존과 동일 |
| T5 | 키보드 입력 + 특수키 | echo, Backspace, 화살표 |
| T6 | 리사이즈 | 100ms 디바운스, 깨짐 없음 |
| T7 | 16색/256색/TrueColor ANSI + Nerd Font + ClearType | Starship 프롬프트 포함 |
| T8 | DPI 변경 (모니터 이동/배율 변경) | 선명도 유지, 깜박임 없음 |
| T9 | 유휴 시 GPU < 1% | GPU-Z 측정 |
| T10 | Unpackaged 실행 (MSIX 없이) | exe 직접 실행 |

---

## 7. QC Criteria

| # | Criteria | Target |
|---|----------|--------|
| QC-01 | WinUI3 창에서 렌더링 | cmd/pwsh 실시간 |
| QC-02 | 키보드 입력 (surrogate pair 포함) | 셸 대화 + 이모지 |
| QC-03 | 리사이즈 (ASTA 데드락 없음) | Render Thread Pause Protocol |
| QC-04 | DPI 변경 | 선명도 유지 |
| QC-05 | 커스텀 타이틀바 + 사이드바 | XAML 레이아웃 |
| QC-06 | GPU 유휴 < 1% | waitable swapchain |
| QC-07 | Phase 3 빌드 유지 | ghostwin_terminal 23/23 PASS |
| QC-08 | Unpackaged 실행 | MSIX 없이 exe 실행 |

---

## 8. Risks

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| R1 | CMake + CppWinRT 헤더 생성 복잡도 | 상 | `setup_winui.ps1` 사전 생성 + 버전 매트릭스 검증 |
| R2 | Unpackaged Bootstrap 실패 | 중 | MessageBox + 설치 URL 안내 |
| R3 | SwapChainPanel 키보드 포커스 | 중 | `IsTabStop=true` + `Focus(Programmatic)` |
| R4 | Render Thread ↔ UI Thread 교착 | 상 | Render Thread Pause Protocol (atomic flag, join 금지) |
| R5 | Composition 스왑체인 성능 차이 | 하 | FLIP_SEQUENTIAL + waitable object 유지 |
| R6 | DPI 변경 시 깜박임(flicker) | 중 | 렌더 스레드에서 리사이즈 처리. Windows Terminal 코드 참고 |
| R7 | Code-only WinUI3 테마 리소스 로딩 | 중 | XamlControlsResources 등록 필수 |

---

## 9. Phase 5 Extension Points

Phase 4-A는 1 Window = 1 Session 구조이나, Phase 5 탭/Pane 확장을 위해:

- **TerminalPane 추상화 예정**: 각 Pane이 SwapChainPanel + ConPtySession + RenderState를 소유
- **D3D11 Device 공유**: DX11Renderer에서 device 소유권을 분리하여 multi-pane에서 단일 device 공유
- **사이드바 220px 하드코딩**: Phase 5에서 GridSplitter 또는 사용자 드래그 가능 폭으로 전환

이 확장 포인트들은 Phase 4-A 구현에 영향을 주지 않으나, 코드 구조 결정 시 참고한다.

---

## 10. Related Documents

| Document | Path |
|----------|------|
| Plan | `docs/01-plan/features/winui3-shell.plan.md` |
| WinUI3 + DX11 리서치 | `docs/00-research/research-winui3-dx11.md` |
| cmux AI UX 리서치 | `docs/00-research/cmux-ai-agent-ux-research.md` |
| Phase 3 보고서 | `docs/archive/2026-03/dx11-rendering/dx11-rendering.report.md` |
| ADR-006 vt_mutex 스레드 안전성 | `docs/adr/006-vt-mutex-thread-safety.md` |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-03-30 | Solit | Initial design |
| 2.0 | 2026-03-30 | Solit | 1차 검증 반영 — design-validator(3C+7W+4R+2T) + code-analyzer(6C+11W) 전수 해결 |
| 2.1 | 2026-03-30 | Solit | 2차 검증 반영 — N-C1(DPI resize), N-C2(memory ordering), N-W1(staging 초기화), N-W2(waitable idle), N-W4(atomic 초기값), N-W6(Mica lifetime) 해결 |
| 3.0 | 2026-03-30 | Solit | 구현 후 반영 — ADR-009(IXamlMetadataProvider, RegFree WinRT, GetCurrentTime), E1(Character API), E2(NuGet 1.6), C1-C5 수정사항, joinable thread |
