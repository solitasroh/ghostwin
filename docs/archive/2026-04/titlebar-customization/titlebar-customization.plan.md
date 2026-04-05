# titlebar-customization Plan

> **Summary**: 커스텀 타이틀바 — 사이드바 타이틀바 관통, Mica 투과, 드래그 영역, 캡션 버튼 겹침 방지. cmux 수준 UX.
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-04
> **Status**: Draft
> **Dependency**: Phase 5-B tab-sidebar 완료

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | `ExtendsContentIntoTitleBar(true)` 설정 후 `SetTitleBar()` 미호출로 드래그 불가 + 캡션 버튼과 콘텐츠 겹침. 사이드바/터미널이 0,0에서 시작하여 타이틀바 영역 침범 |
| **Solution** | `AppWindowTitleBar` + `InputNonClientPointerSource` 조합으로 커스텀 타이틀바 구현. 사이드바는 타이틀바 관통 (cmux 패턴), 터미널 영역은 48 DIP 아래에서 시작 |
| **Function/UX Effect** | 윈도우 드래그 정상 동작, Snap Layout 지원, Mica가 사이드바+타이틀바에 투과, 캡션 버튼 겹침 없음. cmux/WT 수준의 모던 윈도우 크롬 |
| **Core Value** | 제품 수준 윈도우 관리. 시스템 통합(드래그, Snap, 최대화/최소화) + 시각적 완성도(Mica 연속성) |

---

## 1. 현재 문제 분석

### 1.1 증상
- 윈도우 드래그로 이동 불가 (드래그 영역 미정의)
- 사이드바 + 터미널이 캡션 버튼(최소/최대/닫기)과 겹침
- 타이틀바 영역에 빈 공간 — Mica만 보이고 기능 없음

### 1.2 근본 원인
```cpp
// winui_app.cpp:524
m_window.ExtendsContentIntoTitleBar(true);  // ← 시스템 타이틀바 숨김
// SetTitleBar() 또는 InputNonClientPointerSource 미호출 ← 드래그 영역 없음
```

### 1.3 기술 근거 (15-agent 리서치)

| 사실 | 출처 |
|------|------|
| `SetTitleBar()`는 자식 요소 포인터 입력을 모두 무시 (드래그 전용) | MS 문서 |
| Unpackaged 앱에서 SetTitleBar 드래그 불가 버그 (Issue #6185) | GitHub |
| `AppWindowTitleBar` + `InputNonClientPointerSource`가 Code-only 정석 | MS 문서 + 에이전트 합의 |
| Tall 타이틀바 = 48 DIP (인터랙티브 콘텐츠용) | MS 문서 |
| 캡션 버튼 폭 = 138px @100% DPI, `RightInset`로 런타임 조회 | MS 문서 |
| Height/RightInset 단위 = 물리 픽셀 → `/ RasterizationScale` 변환 필요 | MS 공식 패턴 |
| 48 DIP = 모든 표준 DPI에서 정수 물리 픽셀 (4의 배수 규칙) | 에이전트 계산 검증 |
| Mica 투과 조건: 요소 Background = Transparent | MS 문서 |
| DPI 변경 시 드래그 영역 자동 갱신 안 됨 → 수동 재호출 필수 | Issue #10151 |

---

## 2. 목표 레이아웃

```
┌──────────────────────────────────────────────────┐
│ [Tab1  ] │  (드래그 영역)           [─][□][×]    │ ← Tall 48 DIP
│ [Tab2  ] ├───────────────────────────────────────│
│ [Tab3  ] │                                       │
│          │  터미널 콘텐츠 (SwapChainPanel)         │ ← Y = 48 DIP
│          │                                       │
│ [+ New ] │                                       │
└──────────┴───────────────────────────────────────┘
  Col 0          Col 1
  사이드바        콘텐츠
  (관통)         Row 0: 타이틀바 드래그 (48 DIP)
  Background=    Row 1: SwapChainPanel (Star)
  Transparent
```

### 2.1 핵심 설계 결정

| 결정 | 선택 | 근거 |
|------|------|------|
| API | `AppWindowTitleBar` + `InputNonClientPointerSource` | Code-only 정석, SetTitleBar Unpackaged 버그 회피 |
| 타이틀바 높이 | `PreferredHeightOption::Tall` (48 DIP) | 인터랙티브 콘텐츠 표준, 4의 배수 → 정수 물리 px |
| 사이드바 관통 | Col 0 = ColumnSpan 전체 높이, 타이틀바 영역 포함 | cmux 패턴, Mica 연속성 |
| 터미널 시작점 | Col 1 Row 1 (Y = 48 DIP) | ADR-009 픽셀 정렬 보장 |
| 드래그 영역 | Col 1 상단 48px (캡션 버튼 제외) | `InputNonClientPointerSource.SetRegionRects(Caption)` |
| 캡션 버튼 겹침 방지 | `RightInset` → Col 1 상단 우측 마진 | 런타임 DPI-aware |
| Mica 투과 | 사이드바 + 타이틀바 요소 Background = Transparent | MS 문서 확인 |

---

## 3. Functional Requirements

### FR-01: 윈도우 드래그
- Col 1 상단 48 DIP 영역에서 드래그로 윈도우 이동
- Windows 11 Snap Layout 지원 (최대화 hover)
- 우클릭 시 시스템 메뉴 표시

### FR-02: 캡션 버튼
- 시스템 캡션 버튼(최소/최대/닫기) 자동 렌더링 유지
- 캡션 버튼 배경색 = Transparent (Mica 투과)
- 다크 테마 대응: `ButtonForegroundColor` 수동 설정

### FR-03: 사이드바 타이틀바 관통
- TabSidebar가 윈도우 최상단(0,0)부터 시작
- 사이드바 영역은 드래그 불가 (탭 클릭 우선)
- Passthrough 영역으로 지정 → 탭 클릭 동작

### FR-04: Mica 연속성
- 타이틀바 + 사이드바 + 콘텐츠 영역에 Mica가 연속적으로 투과
- 모든 UI 요소 Background = Transparent
- 비활성 윈도우 시 자동 Mica 비활성화 (OS 처리)

### FR-05: DPI 대응
- DPI 변경 시 드래그 영역 + Passthrough 영역 재계산
- `RasterizationScaleChanged` 이벤트에서 `SetRegionRects` 재호출
- 48 DIP × scale = 정수 물리 픽셀 (4의 배수 규칙)

### FR-06: 윈도우 상태 전환
- 최대화: 상단 패딩 자동 적용, `AppWindowTitleBar.Height` 재조회
- 전체화면 (F11): 타이틀바 숨김 + 사이드바 선택적 숨김
- `AppWindow.Changed` + `DidPresenterChange`로 상태 감지

---

## 4. Non-Functional Requirements

| NFR | 목표 | 측정 |
|-----|------|------|
| NFR-01 | SwapChainPanel Y 오프셋 정수 물리 px | ActualOffset.y × scale = 정수 |
| NFR-02 | 드래그 응답 < 16ms (60fps) | 체감 지연 없음 |
| NFR-03 | 기존 10/10 테스트 PASS 유지 | 빌드 테스트 |
| NFR-04 | Phase 5-B TabSidebar 기능 무파괴 | 탭 클릭/드래그/단축키 정상 |

---

## 5. Architecture (cpp.md 디자인 패턴 합의 기반)

> 5-agent 패턴 타당성 평가 결과:
> - State 패턴 (variant+visit): **과도** → `enum class + constexpr 테이블`
> - Observer 패턴: **과도** → 직접 호출 (리스너 2~3개)
> - Policy-Based (Win10/11): **과도** → API graceful degradation
> - **적용**: `enum class WindowState`, `constexpr` 조회 테이블, function pointer DI, Config 구조체

### 5.1 윈도우 상태 모델 (enum class + constexpr 테이블)

```cpp
// src/ui/titlebar_manager.h

/// cpp.md: enum class over enum. constexpr over #define.
enum class WindowState : uint8_t { Normal, Maximized, Fullscreen };

/// 상태별 타이틀바 파라미터 — 행동 분기 아닌 데이터 차이이므로 조회 테이블이 최적.
/// cpp.md: "2 or fewer branches → Keep the if" — 3개지만 순수 데이터, 분기 0개.
struct TitlebarParams {
    double height_dip;       // 타이틀바 높이 (DIP)
    double top_padding_dip;  // 최대화 시 추가 패딩
    bool   caption_visible;  // 캡션 버튼 표시
    bool   drag_enabled;     // 드래그 영역 활성
    bool   sidebar_visible;  // 사이드바 표시
};

inline constexpr TitlebarParams kTitlebarParams[] = {
    /* Normal     */ { 48.0, 0.0, true,  true,  true  },
    /* Maximized  */ { 48.0, 7.0, true,  true,  true  },
    /* Fullscreen */ {  0.0, 0.0, false, false, false },
};

// 사용: 분기 없이 인덱싱 (cpp.md: constexpr 테이블)
// const auto& p = kTitlebarParams[static_cast<int>(state)];
```

### 5.2 TitleBarManager 클래스

```cpp
// src/ui/titlebar_manager.h

/// 사이드바 폭 조회 — function pointer DI (cpp.md: no std::function, TabSidebar 타입 의존 제거)
using SidebarWidthFn = double(*)(void* ctx);

/// 초기화 설정 (cpp.md: parameters ≤ 3 → Config 구조체)
struct TitleBarConfig {
    HWND hwnd = nullptr;                      // Window handle → AppWindow 획득
    SidebarWidthFn sidebar_width_fn = nullptr; // 사이드바 폭 간접 조회
    void* sidebar_ctx = nullptr;              // non-owning context
};

/// 커스텀 타이틀바 관리 — Presentation 레이어 (SRP: 타이틀바만)
///
/// cpp.md compliance:
///   - Public API 6개 (common.md ≤ 7)
///   - Rule of Zero (WinRT 값타입 멤버, copy deleted)
///   - constexpr 상태 테이블 (분기 0개)
///   - function pointer DI (TabSidebar 의존 제거)
///   - 함수 ≤ 40줄 (compute_* 분리)
///
/// Thread ownership: 모든 메서드 UI thread only.
class TitleBarManager {
public:
    void initialize(const TitleBarConfig& config);
    /// 드래그 + Passthrough 재계산.
    /// extra_passthrough: 외부 컴포넌트(탭, 검색바 등)의 클릭 가능 영역.
    /// OCP: 새 컴포넌트 추가 시 TitleBarManager 수정 불필요 — caller가 rect 수집.
    /// 5-agent 합의: D (Hybrid) 3:2 채택. span 1개 비용 ≈ 0, 반복 수정 제거.
    void update_regions(std::span<const winrt::Windows::Graphics::RectInt32>
                        extra_passthrough = {});
    void update_caption_colors(bool dark);    // 테마 대응
    void on_state_changed(WindowState state); // 상태 전환
    [[nodiscard]] double height_dip() const;  // 현재 타이틀바 DIP 높이
    [[nodiscard]] WindowState state() const;

    TitleBarManager() = default;
    TitleBarManager(const TitleBarManager&) = delete;
    TitleBarManager& operator=(const TitleBarManager&) = delete;

private:
    // WinRT 프로젝션 타입 (COM RAII 자동, 별도 래퍼 불필요)
    winrt::Microsoft::UI::Windowing::AppWindow app_window_{nullptr};
    winrt::Microsoft::UI::Input::InputNonClientPointerSource nonclient_src_{nullptr};

    WindowState state_ = WindowState::Normal;
    double scale_ = 1.0;

    SidebarWidthFn sidebar_width_fn_ = nullptr;
    void* sidebar_ctx_ = nullptr;

    static constexpr double kTitleBarHeightDip = 48.0;  // Tall (인터랙티브 콘텐츠)

    void compute_drag_rects();       // ≤ 40줄
    void compute_passthrough_rects(); // ≤ 40줄
    void apply_state();              // constexpr 테이블 조회 → 적용
};
```

### 5.3 GhostWinApp 통합

```
GhostWinApp::OnLaunched
├── Grid (UseLayoutRounding=true)
│   ├── Col 0: TabSidebar (Auto, 관통, Background=Transparent)
│   └── Col 1: 2-Row Grid
│       ├── Row 0 (kTitleBarHeightDip): 빈 영역 (Mica 투과)
│       └── Row 1 (Star): SwapChainPanel
├── TitleBarManager.initialize(config)
│   ├── AppWindowTitleBar.ExtendsContentIntoTitleBar(true)
│   ├── PreferredHeightOption::Tall
│   ├── ButtonBackgroundColor = Transparent
│   └── InputNonClientPointerSource 초기화 + 초기 영역 계산
├── SizeChanged / RasterizationScaleChanged:
│   ├── passthrough_rects = TabSidebar.collect_rects() + 기타 컴포넌트
│   └── TitleBarManager.update_regions(passthrough_rects)
└── AppWindow.Changed (DidPresenterChange) → TitleBarManager.on_state_changed()
```

**이벤트 디스패치**: 직접 호출 (Observer 불필요 — 리스너 3개 미만).
**확장성 (OCP)**: GhostWinApp이 rect collector 역할 — 새 컴포넌트 추가 시 TitleBarManager 수정 불필요.
**TabSidebar 연동**: function pointer로 사이드바 폭 간접 조회. 타입 의존 없음.

### 5.4 Clean Architecture 레이어 (common.md)

| 클래스 | 레이어 | 책임 | 의존 방향 |
|--------|--------|------|----------|
| `TitleBarManager` | Presentation | 타이틀바 드래그/캡션/상태 | → AppWindow API (Platform) |
| `TabSidebar` | Presentation | 탭 UI 관리 | → SessionManager (Application) |
| `GhostWinApp` | Presentation | 윈도우 조정, 이벤트 디스패치 | → TitleBarManager, TabSidebar |

### 5.5 패턴 적용 근거표

| 코드 위치 | 패턴 | 근거 | cpp.md 참조 |
|-----------|------|------|------------|
| `WindowState` | `enum class` | 타입 안전 상태 열거 | "enum class over enum" |
| `kTitlebarParams[]` | `constexpr` 조회 테이블 | 상태별 데이터 차이, 분기 0개 | "constexpr over #define" |
| `TitleBarConfig` | Parameter Object | 초기화 파라미터 4개 → 구조체 | "Constructor has 4+ params → Builder" |
| `SidebarWidthFn` | Function pointer DI | TabSidebar 타입 의존 제거 | "함수 포인터 + context" |
| `app_window_` | WinRT 값타입 | COM RAII 자동, 별도 래퍼 불필요 | "unique_ptr for sole ownership" |
| `compute_*()` 분리 | Extract Method | 함수 40줄 제한 | "Function body: 40 lines" |
| `update_regions(span)` | **Hybrid OCP** | 새 컴포넌트 추가 시 내부 수정 불필요 | self-check Q2 "수정 없이 확장" — 5-agent 3:2 합의 |

---

## 6. Implementation Order

```
Step 1: TitleBarManager 스켈레톤 + CMakeLists
Step 2: AppWindowTitleBar 초기화 (Tall, Transparent 캡션)
Step 3: Grid Row 분리 (Row 0: 48 DIP, Row 1: Star)
Step 4: InputNonClientPointerSource 드래그 영역
Step 5: 사이드바 Passthrough 영역 + Background=Transparent
Step 6: DPI/Resize 이벤트 → update_regions
Step 7: 전체화면 + 최대화 상태 전환
Step 8: 빌드 + 10/10 PASS 확인
```

---

## 7. Risks

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| DPI 변경 시 드래그 깨짐 (#10151) | HIGH | 높음 | RasterizationScaleChanged + SizeChanged에서 재호출 |
| Win10 캡션 버튼 가시성 (#8899) | MEDIUM | 중간 | ButtonForegroundColor 수동 설정 |
| 최소화 상태 닫기 크래시 (#10103) | MEDIUM | 중간 | 닫기 전 Restore 방어 코드 |
| Maximize hit-test (#8805) | MEDIUM | 낮음 | 모니터링, 필요시 WM_NCHITTEST |
| SwapChainPanel Y 오프셋 비정수 | HIGH | 낮음 | 48 DIP = 4의 배수, UseLayoutRounding |
| InputNonClientPointerSource 좌표계 불확실 | MEDIUM | 중간 | PoC로 물리/논리 px 실측 검증 |

---

## 8. Out of Scope

| 항목 | 근거 |
|------|------|
| 탭을 타이틀바 안에 배치 (WT 패턴) | 현재 수직 사이드바 패턴으로 충분 |
| 타이틀바 자동 숨김 (auto-hide) | Phase 6 고급 UX |
| 커스텀 캡션 버튼 드로잉 | 시스템 캡션 유지 |
| Win32 WM_NCCALCSIZE 직접 처리 | WinUI3 API로 충분 |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-04 | 15-agent 기술 리서치 기반 초안 | 노수장 |
| 0.2 | 2026-04-04 | 5-agent 디자인 패턴 합의: enum class + constexpr, fn pointer DI, Observer/State/Policy 배제 | 노수장 |
| 0.3 | 2026-04-04 | 5-agent OCP 확장성 투표 (D 3:2): update_regions(span) Hybrid 패턴 채택. Phase 6 확장 시 TitleBarManager 수정 불필요 | 노수장 |
