# titlebar-customization Design Document

> **Summary**: 커스텀 타이틀바 — AppWindowTitleBar + InputNonClientPointerSource 기반. enum class WindowState + constexpr 조회 테이블. Hybrid OCP (span extra_passthrough).
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-04
> **Status**: Final (v1.2)
> **Planning Doc**: [titlebar-customization.plan.md](../../01-plan/features/titlebar-customization.plan.md)
> **Dependency**: Phase 5-B tab-sidebar 완료

---

## Executive Summary

| Perspective            | Content                                                                                                                                                                |
| ---------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Problem**            | ExtendsContentIntoTitleBar(true) 후 드래그 불가 + 캡션 버튼 겹침. 사이드바/터미널이 타이틀바 영역 침범                                                                 |
| **Solution**           | TitleBarManager 클래스: AppWindowTitleBar(Tall 48 DIP) + InputNonClientPointerSource(드래그/Passthrough). enum class WindowState + constexpr 테이블. Hybrid OCP (span) |
| **Function/UX Effect** | 드래그 정상, Snap Layout, Mica 사이드바+타이틀바 투과, 캡션 겹침 없음                                                                                                  |
| **Core Value**         | 제품 수준 윈도우 관리 + 확장 가능 구조 (Phase 6 요소 추가 시 TitleBarManager 수정 불필요)                                                                              |

---

## 1. Overview

### 1.1 Design Goals

1. **드래그 + Snap Layout**: 윈도우 이동, Windows 11 Snap
2. **캡션 버튼 겹침 방지**: RightInset 기반 동적 마진
3. **Mica 연속성**: 사이드바 + 타이틀바 전체 투과
4. **픽셀 정렬**: 48 DIP = 모든 표준 DPI에서 정수 물리 px (ADR-009)
5. **OCP 확장성**: 새 타이틀바 요소 추가 시 TitleBarManager 수정 불필요 (D안 7:3 합의)

### 1.2 cpp.md 준수 원칙

- `enum class WindowState` — 타입 안전
- `constexpr TitlebarParams[]` — 분기 0개 조회 테이블
- `TitleBarConfig` 구조체 — params ≤ 3
- Function pointer DI — TabSidebar 타입 의존 제거
- Public API ≤ 7개 — God Object 방지
- 함수 ≤ 40줄 — setup_titlebar_properties/apply_state 분리
- 명시적 소멸자 — AppWindow.Changed 이벤트 토큰 해지 (RAII)

### 1.3 패턴 적용 결정 (30-agent 합의)

| 패턴                            |   적용   | 근거                                  |
| ------------------------------- | :------: | ------------------------------------- |
| `enum class + constexpr 테이블` | **적용** | 상태별 데이터 차이, 분기 0개          |
| Function pointer DI             | **적용** | TabSidebar 타입 의존 제거             |
| Hybrid OCP (span)               | **적용** | 7:3 투표. 확장 시 Manager 수정 불필요 |
| State (variant+visit)           |   배제   | 과도 — 데이터 차이, 행동 분기 아님    |
| Observer                        |   배제   | 과도 — 리스너 3개 미만                |
| Policy-Based (Win10/11)         |   배제   | 과도 — API graceful degradation       |

---

## 2. Architecture

### 2.1 현재 구조 (Before)

```
GhostWinApp::OnLaunched
├── Grid (2 columns, 0 rows)
│   ├── Col 0: TabSidebar (Auto) ← 0,0에서 시작 → 타이틀바 영역 침범
│   └── Col 1: SwapChainPanel (Star) ← 0,0에서 시작 → 캡션 버튼 겹침
├── ExtendsContentIntoTitleBar(true) ← 드래그 영역 미정의
└── SetTitleBar() 미호출 ← 드래그 불가
```

### 2.2 목표 구조 (After)

```
GhostWinApp::OnLaunched
├── Grid (2 columns, 2 rows)
│   ├── Row 0 (48 DIP): 타이틀바 영역
│   │   ├── Col 0: TabSidebar 상단 (관통, Passthrough)
│   │   └── Col 1: 드래그 영역 + 캡션 버튼 (RightInset 마진)
│   ├── Row 1 (Star): 콘텐츠 영역
│   │   ├── Col 0: TabSidebar 하단 (탭 목록)
│   │   └── Col 1: SwapChainPanel (Y = 48 DIP, 정수 물리 px)
│   └── TabSidebar: RowSpan=2 (관통)
├── TitleBarManager.initialize(config)
│   ├── AppWindowTitleBar.ExtendsContentIntoTitleBar(true)
│   ├── PreferredHeightOption::Tall
│   ├── ButtonBackgroundColor = Transparent
│   └── InputNonClientPointerSource 초기화
├── SizeChanged / ScaleChanged → update_regions(extra_passthrough)
└── AppWindow.Changed → on_state_changed()
```

### 2.3 Component Diagram

```
┌──────────────────────────────────────────────────────┐
│                    GhostWinApp                       │
│                                                      │
│  ┌──────────────┐    ┌──────────────────────────┐    │
│  │ TitleBarMgr  │    │     TabSidebar           │    │
│  │              │◄───│ (sidebar_width_fn DI)    │    │
│  │ - app_window │    │                          │   │
│  │ - nonclient  │    │                          │   │
│  │ - state_     │    └──────────────────────────┘   │
│  │ - scale_     │                                    │
│  └──────┬───────┘                                    │
│         │ update_regions(extra_passthrough)           │
│         │ ← GhostWinApp가 rect 수집 + 전달           │
│         │                                            │
│  ┌──────┴───────────────────────────────────────┐   │
│  │              Grid Layout                      │   │
│  │  ┌──────────┬───────────────────────────┐    │   │
│  │  │ Col 0    │ Col 1                     │    │   │
│  │  │ Sidebar  ├───────────────────────────│    │   │
│  │  │ (RowSpan │ Row 0: 드래그 + 캡션      │    │   │
│  │  │  = 2)    ├───────────────────────────│    │   │
│  │  │          │ Row 1: SwapChainPanel     │    │   │
│  │  └──────────┴───────────────────────────┘    │   │
│  └──────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────┘
```

---

## 3. Data Model

### 3.1 WindowState + constexpr 테이블

```cpp
// src/ui/titlebar_manager.h

#include <cstdint>

namespace ghostwin {

/// cpp.md: enum class over enum
enum class WindowState : uint8_t { Normal, Maximized, Fullscreen };

/// 상태별 타이틀바 파라미터 (constexpr 조회 테이블, 분기 0개)
struct TitlebarParams {
    double height_dip;
    double top_padding_dip;
    bool   caption_visible;
    bool   drag_enabled;
    bool   sidebar_visible;
};

inline constexpr TitlebarParams kTitlebarParams[] = {
    /* Normal     */ { 48.0, 0.0, true,  true,  true  },
    /* Maximized  */ { 48.0, 7.0, true,  true,  true  },
    /* Fullscreen */ {  0.0, 0.0, false, false, false },
};

static constexpr double kTitleBarHeightDip = 48.0;

} // namespace ghostwin
```

### 3.2 TitleBarConfig

```cpp
/// Sidebar 폭 조회 — function pointer DI (cpp.md: no std::function)
using SidebarWidthFn = double(*)(void* ctx);

/// cpp.md: params ≤ 3 → Config 구조체
struct TitleBarConfig {
    HWND hwnd = nullptr;
    SidebarWidthFn sidebar_width_fn = nullptr;
    void* sidebar_ctx = nullptr;
};
```

---

## 4. TitleBarManager Class

### 4.1 클래스 정의

```cpp
// src/ui/titlebar_manager.h
#pragma once

#include <cstdint>
#include <span>

#include <winrt/Windows.Graphics.h>

namespace ghostwin {

// Forward declare — avoids heavy WinRT header includes
namespace detail {
    struct TitleBarState;  // pimpl for WinRT types
}

class TitleBarManager {
public:
    // ─── Public API (7개 — common.md ≤ 7) ───

    void initialize(const TitleBarConfig& config);

    /// OCP Hybrid: caller가 외부 컴포넌트의 passthrough rect를 수집하여 전달.
    /// TitleBarManager는 드래그 rect + 전달받은 passthrough를 합성하여 SetRegionRects 호출.
    /// 새 컴포넌트 추가 시 TitleBarManager 수정 불필요 (10-agent 7:3 합의).
    void update_regions(std::span<const winrt::Windows::Graphics::RectInt32>
                        extra_passthrough = {});

    void update_caption_colors(bool dark_theme);
    void on_state_changed(WindowState new_state);
    void update_dpi(double new_scale);
    [[nodiscard]] double height_dip() const;
    [[nodiscard]] WindowState state() const;

    TitleBarManager() = default;
    ~TitleBarManager();  // AppWindow.Changed 이벤트 토큰 해지
    TitleBarManager(const TitleBarManager&) = delete;
    TitleBarManager& operator=(const TitleBarManager&) = delete;

private:
    // WinRT 프로젝션 타입 (COM RAII 자동)
    winrt::Microsoft::UI::Windowing::AppWindow app_window_{nullptr};
    winrt::Microsoft::UI::Input::InputNonClientPointerSource nonclient_src_{nullptr};

    WindowState state_ = WindowState::Normal;
    double scale_ = 1.0;

    SidebarWidthFn sidebar_width_fn_ = nullptr;
    void* sidebar_ctx_ = nullptr;
    winrt::event_token changed_token_{};  // AppWindow.Changed 구독 토큰

    // ─── Internal helpers (cpp.md: ≤ 40줄) ───

    /// AppWindowTitleBar 속성 설정: Tall, 투명 캡션 버튼
    void setup_titlebar_properties();

    /// 상태 적용: constexpr 테이블 조회 → titlebar 높이/가시성 적용
    void apply_state();

    /// DIP → 물리 픽셀 변환 (cpp.md: inline, ≤ 3줄)
    [[nodiscard]] int32_t to_px(double dip) const {
        return static_cast<int32_t>(dip * scale_);
    }
};

} // namespace ghostwin
```

### 4.2 initialize 구현 (≤ 40줄)

```cpp
void TitleBarManager::initialize(const TitleBarConfig& config) {
    sidebar_width_fn_ = config.sidebar_width_fn;
    sidebar_ctx_ = config.sidebar_ctx;

    // HWND → AppWindow
    auto window_id = winrt::Microsoft::UI::GetWindowIdFromWindow(config.hwnd);
    app_window_ = winrt::Microsoft::UI::Windowing::AppWindow::GetFromWindowId(window_id);

    // AppWindowTitleBar 설정
    auto titlebar = app_window_.TitleBar();
    titlebar.ExtendsContentIntoTitleBar(true);
    titlebar.PreferredHeightOption(
        winrt::Microsoft::UI::Windowing::TitleBarHeightOption::Tall);

    // 캡션 버튼 배경 투명 (Mica 투과)
    titlebar.ButtonBackgroundColor(winrt::Windows::UI::Colors::Transparent());
    titlebar.ButtonInactiveBackgroundColor(winrt::Windows::UI::Colors::Transparent());

    // InputNonClientPointerSource
    nonclient_src_ = winrt::Microsoft::UI::Input::InputNonClientPointerSource
        ::GetForWindowId(window_id);

    // 초기 스케일
    scale_ = GetDpiForWindow(config.hwnd) / 96.0;

    // AppWindow.Changed: maximize/restore/fullscreen 자동 감지
    // DidPresenterChange: Normal ↔ FullScreen (presenter 교체)
    // DidSizeChange: Normal ↔ Maximized (동일 presenter 내 상태 변경)
    namespace MUW = winrt::Microsoft::UI::Windowing;
    changed_token_ = app_window_.Changed(
        [this](MUW::AppWindow const& sender, MUW::AppWindowChangedEventArgs const& args) {
            if (!args.DidPresenterChange() && !args.DidSizeChange()) return;
            auto kind = sender.Presenter().Kind();
            if (kind == MUW::AppWindowPresenterKind::FullScreen) {
                on_state_changed(WindowState::Fullscreen);
                return;
            }
            auto overlapped = sender.Presenter().try_as<MUW::OverlappedPresenter>();
            if (!overlapped) return;
            auto ps = overlapped.State();
            WindowState ws = (ps == MUW::OverlappedPresenterState::Maximized)
                ? WindowState::Maximized : WindowState::Normal;
            on_state_changed(ws);
        });

    LOG_I("titlebar", "Initialized (height=%g dip, scale=%.2f)", kTitleBarHeightDip, scale_);
}
```

### 4.3 update_regions 구현 (Hybrid OCP)

```cpp
void TitleBarManager::update_regions(
        std::span<const winrt::Windows::Graphics::RectInt32> extra_passthrough) {
    if (!app_window_ || !nonclient_src_) return;

    const auto& p = kTitlebarParams[static_cast<int>(state_)];
    if (!p.drag_enabled) {
        // Fullscreen: 드래그 영역 없음
        nonclient_src_.SetRegionRects(... , {})  // 빈 배열로 클리어 (ClearRegionRects 존재 미확인)(
            winrt::Microsoft::UI::Input::NonClientRegionKind::Caption);
        nonclient_src_.SetRegionRects(... , {})  // 빈 배열로 클리어 (ClearRegionRects 존재 미확인)(
            winrt::Microsoft::UI::Input::NonClientRegionKind::Passthrough);
        return;
    }

    auto titlebar = app_window_.TitleBar();
    int32_t right_inset = titlebar.RightInset();
    int32_t height = titlebar.Height();

    // 사이드바 폭 (DPI-aware)
    double sidebar_dip = sidebar_width_fn_ ? sidebar_width_fn_(sidebar_ctx_) : 0.0;
    int32_t sidebar_px = to_physical(sidebar_dip);

    // 드래그 영역: Col 1 상단 전체 (캡션 버튼 제외)
    auto size = app_window_.Size();
    winrt::Windows::Graphics::RectInt32 drag_rect{
        sidebar_px,   // X: 사이드바 오른쪽부터
        0,            // Y: 최상단
        size.Width - sidebar_px - right_inset,  // W: 캡션 버튼 제외
        height        // H: 타이틀바 높이
    };
    nonclient_src_.SetRegionRects(
        winrt::Microsoft::UI::Input::NonClientRegionKind::Caption, {&drag_rect, 1});

    // Passthrough: 사이드바 영역 + 외부 추가 영역 (OCP)
    std::vector<winrt::Windows::Graphics::RectInt32> passthrough;
    passthrough.reserve(1 + extra_passthrough.size());

    // 사이드바 전체 = passthrough (탭 클릭 가능)
    winrt::Windows::Graphics::RectInt32 sidebar_rect{0, 0, sidebar_px, size.Height};
    passthrough.push_back(sidebar_rect);

    // 외부 컴포넌트 rect 추가 (OCP: caller가 수집)
    for (const auto& r : extra_passthrough)
        passthrough.push_back(r);

    nonclient_src_.SetRegionRects(
        winrt::Microsoft::UI::Input::NonClientRegionKind::Passthrough, passthrough);

    LOG_I("titlebar", "Regions updated (drag=%dx%d, passthrough=%zu)",
          drag_rect.Width, drag_rect.Height, passthrough.size());
}
```

### 4.4 on_state_changed + apply_state

```cpp
void TitleBarManager::on_state_changed(WindowState new_state) {
    if (state_ == new_state) return;
    state_ = new_state;
    apply_state();
    LOG_I("titlebar", "State → %d", static_cast<int>(state_));
}

void TitleBarManager::apply_state() {
    const auto& p = kTitlebarParams[static_cast<int>(state_)];

    if (state_ == WindowState::Fullscreen) {
        // Fullscreen: FullScreenPresenter 적용 → 캡션 버튼 자동 숨김
        // 드래그/Passthrough 영역 제거
        nonclient_src_.SetRegionRects(
            winrt::Microsoft::UI::Input::NonClientRegionKind::Caption, {});
        nonclient_src_.SetRegionRects(
            winrt::Microsoft::UI::Input::NonClientRegionKind::Passthrough, {});
    } else {
        // Normal/Maximized: 드래그 영역 재계산
        update_regions();
    }
    // 사이드바 가시성은 GhostWinApp이 담당 (SRP — TitleBarManager는 타이틀바만)
}
```

---

## 5. GhostWinApp Integration

### 5.1 Grid Row 분리

```cpp
// OnLaunched — 기존 2-column Grid에 2-row 추가

// Row 0: 타이틀바 (48 DIP)
auto row0 = controls::RowDefinition();
row0.Height(winui::GridLengthHelper::FromPixels(kTitleBarHeightDip));
grid.RowDefinitions().Append(row0);

// Row 1: 콘텐츠 (Star)
auto row1 = controls::RowDefinition();
row1.Height(winui::GridLength{1, winui::GridUnitType::Star});
grid.RowDefinitions().Append(row1);

// TabSidebar: RowSpan=2 (관통)
controls::Grid::SetRowSpan(sidebar_root, 2);

// SwapChainPanel: Row 1 (Y = 48 DIP)
controls::Grid::SetRow(m_panel, 1);
```

### 5.2 TitleBarManager 초기화

```cpp
// winui_app.h 멤버 추가
TitleBarManager m_titlebar;

// OnLaunched — Loaded 콜백 내부 (HWND 확보 후)
TitleBarConfig tb_config{};
tb_config.hwnd = parentHwnd;
tb_config.sidebar_width_fn = [](void* ctx) -> double {
    auto* app = static_cast<GhostWinApp*>(ctx);
    return app->m_tab_sidebar.is_visible()
        ? app->m_tab_sidebar.root().ActualWidth() : 0.0;
};
tb_config.sidebar_ctx = this;
m_titlebar.initialize(tb_config);
m_titlebar.update_regions();
```

### 5.3 이벤트 연결

```cpp
// SizeChanged — 기존 resize_timer Tick 내부에 추가
m_titlebar.update_regions();

// CompositionScaleChanged — DPI 변경 시 직접 호출
m_titlebar.update_dpi(static_cast<double>(newScale));
// update_dpi() 내부에서 update_regions() 자동 호출

// Ctrl+Shift+B 사이드바 토글 — toggle_visibility() 직후 호출
m_tab_sidebar.toggle_visibility();
m_titlebar.update_regions();

// AppWindow.Changed — TitleBarManager::initialize() 내부에서 자동 등록
// DidPresenterChange + DidSizeChange → on_state_changed() 자동 호출
```

### 5.4 TabSidebar Background 투명화

```cpp
// tab_sidebar.cpp initialize() 내부
root_panel_.Background(nullptr);  // Transparent → Mica 투과
```

---

## 6. File Structure

### 6.1 신규 파일

| File                          | Purpose                                               | LOC (예상) |
| ----------------------------- | ----------------------------------------------------- | :--------: |
| `src/ui/titlebar_manager.h`   | TitleBarManager 클래스 + WindowState + TitleBarConfig |    ~80     |
| `src/ui/titlebar_manager.cpp` | initialize, update_regions, on_state_changed 구현     |    ~120    |

### 6.2 수정 파일

| File                     | Changes                                             |
| ------------------------ | --------------------------------------------------- |
| `src/app/winui_app.h`    | TitleBarManager 멤버 추가                           |
| `src/app/winui_app.cpp`  | Grid Row 분리, TitleBarManager 초기화 + 이벤트 연결 |
| `src/ui/tab_sidebar.cpp` | Background=Transparent 설정                         |
| `CMakeLists.txt`         | titlebar_manager.cpp 추가                           |

---

## 7. Implementation Order

```
Step 1: titlebar_manager.h/cpp 스켈레톤 + CMakeLists
Step 2: WindowState enum + constexpr 테이블
Step 3: initialize() — AppWindowTitleBar + InputNonClientPointerSource
Step 4: Grid Row 분리 (Row 0: 48 DIP, Row 1: Star, TabSidebar RowSpan=2)
Step 5: update_regions() — 드래그 + Passthrough + OCP span
Step 6: TabSidebar Background=Transparent (Mica 투과)
Step 7: 캡션 버튼 색상 (다크 테마)
Step 8: DPI/Resize 이벤트 연결
Step 9: 빌드 + 10/10 PASS
```

---

## 8. Test Cases

| TC    | 시나리오                   | 예상 결과                         |
| ----- | -------------------------- | --------------------------------- |
| TC-01 | 타이틀바 드래그            | 윈도우 이동                       |
| TC-02 | 최대화 호버                | Snap Layout 표시 (Win11)          |
| TC-03 | 캡션 버튼 클릭             | 최소/최대/닫기 정상               |
| TC-04 | 사이드바 탭 클릭           | 탭 전환 (Passthrough)             |
| TC-05 | SwapChainPanel Y 오프셋    | 정수 물리 px (블러 없음)          |
| TC-06 | DPI 150% → 100% 변경       | 드래그 영역 재계산, 레이아웃 유지 |
| TC-07 | Ctrl+Shift+B 사이드바 토글 | 드래그 영역 재계산 (사이드바 0px) |
| TC-08 | Mica 투과                  | 사이드바 + 타이틀바에 Mica 연속   |
| TC-09 | 기존 10/10 테스트          | PASS 유지                         |
| TC-10 | 최대화 → 복원              | 타이틀바 높이 유지, 드��그 정상   |
| TC-11 | F11 전체화면 → 복원        | 타이틀바 숨김→복원, 사이드바 복원 |
| TC-12 | 최소화 상태에서 닫기       | 크래시 없음 (#10103 방어)         |

---

## 9. Risks

| Risk                             | Impact | Mitigation                                           |
| -------------------------------- | ------ | ---------------------------------------------------- |
| DPI 변경 시 드래그 깨짐 (#10151) | HIGH   | SizeChanged + ScaleChanged에서 update_regions 재호출 |
| Win10 캡션 가시성 (#8899)        | MEDIUM | ButtonForegroundColor 수동 설정                      |
| 물리 픽셀 좌표계 불확실          | MEDIUM | PoC로 실측 검증                                      |
| RowSpan TabSidebar 배치 이슈     | LOW    | Grid Code-only 테스트                                |
| 최소화 닫기 크래시 (#10103)      | MEDIUM | 닫기 전 Restore 방어 코드                            |
| Maximize hit-test (#8805)        | MEDIUM | 모니터링, 필요시 WM_NCHITTEST                        |

---

## Version History

| Version | Date       | Changes                                                                                                                                                     | Author |
| ------- | ---------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- | ------ |
| 1.0     | 2026-04-04 | Plan v0.3 기반 초안. 30-agent 합의 (패턴 + OCP 7:3) 반영                                                                                                    | 노수장 |
| 1.1     | 2026-04-04 | design-validator 반영: C-01 .h 동기화, C-02 ClearRegionRects→빈 배열, W-01 apply_state 구현, W-02 TC-10~12, W-03 Risk #10103/#8805, W-04 sidebar_width 동적 | 노수장 |
| 1.2     | 2026-04-04 | 구현 후 동기화: helper 이름 (setup_titlebar_properties/apply_state/to_px), update_dpi() 추가 (7th API), AppWindow.Changed 이벤트 자동 등록, 소멸자 토큰 해지, TODO 제거 | 노수장 |
