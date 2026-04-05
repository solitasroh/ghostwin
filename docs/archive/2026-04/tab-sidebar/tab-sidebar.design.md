# tab-sidebar Design Document

> **Summary**: Phase 5-B — 좌측 수직 탭 사이드바. WinUI3 Code-only ListView로 탭 목록 표시, SessionManager API 연동, CWD 표시, 드래그 순서 변경, 키보드 단축키.
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-03
> **Status**: Draft (v1.4 — 16-agent 리서치 + cpp.md/common.md 심층 리뷰 합의 완료)
> **Planning Doc**: [multi-session-ui.plan.md](../../01-plan/features/multi-session-ui.plan.md)
> **Dependency**: Phase 5-A session-manager (95% 완료)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | SessionManager가 다중 세션을 관리하지만, 탭 UI가 없어 세션 전환·추가·닫기가 [TEMP] 단축키에만 의존. 사용자에게 세션 목록이 보이지 않음 |
| **Solution** | WinUI3 Code-only ListView 기반 좌측 수직 사이드바. SessionEvents 콜백으로 실시간 동기화, CanReorderItems로 드래그 순서 변경 |
| **Function/UX Effect** | 좌측 사이드바에 탭 목록(제목+CWD) 상시 표시, 클릭으로 전환, '+' 버튼으로 추가, 'x' 버튼으로 닫기, 드래그로 재배치. Ctrl+T/W/Tab/1~9 단축키 |
| **Core Value** | 시각적 세션 관리. WT/cmux 수준의 탭 UX. Phase 6 알림 배지·상태 표시의 사이드바 기반 |

---

## 1. Overview

### 1.1 Design Goals

1. **시각적 세션 목록**: 좌측 수직 사이드바에 모든 세션을 탭으로 표시
2. **실시간 동기화**: SessionManager 이벤트 기반으로 탭 추가/제거/제목 변경 자동 반영
3. **드래그 순서 변경**: ListView의 CanReorderItems으로 드래그 지원, SessionManager::move_session 연동
4. **키보드 단축키**: Ctrl+T(새 탭), Ctrl+W(닫기), Ctrl+Tab/Shift+Tab(순환), Ctrl+1~9(직접 선택)
5. **CWD 표시**: 탭 항목에 현재 작업 디렉토리 축약 표시
6. **픽셀 정렬 유지**: 사이드바 너비를 정수 픽셀로 고정하여 SwapChainPanel 블러 방지
7. **Phase 6 확장 준비**: 알림 배지, git branch, 포트 정보 추가 가능한 구조

### 1.2 Design Principles

- **SessionManager API만 사용**: 직접 Session 필드 접근 최소화. SessionEvents 콜백 기반
- **WinUI3 Code-only 패턴**: XAML 파일 없이 C++ 코드로 UI 생성 (ADR-009)
- **단일 책임**: TabSidebar 클래스가 사이드바 UI만 담당, 세션 관리는 SessionManager에 위임
- **WT 참조 구현**: Windows Terminal의 TabView/TabViewItem 패턴을 Code-only ListView로 축소 구현

### 1.2.1 코드 ���질 규칙 (rkit cpp.md + common.md 준수)

구현 시 rkit PostToolUse 훅이 `refs/code-quality/cpp.md`와 `common.md`를 참조하여 코드를 검증한다.
Design 단계에서 이 규칙들을 사전 반영:

**Modern C++17+ 필수 패턴 (cpp.md)**:
- **RAII**: `scoped_lock` 필수, 수동 `lock()`/`unlock()` 금지. guard flag도 RAII ScopeGuard로 관리
- **소유권 시그니처**: `TabSidebar`는 SessionManager를 관찰(`*` raw pointer, non-owning). WinUI3 요소는 값 타입(winrt::com_ptr 내부)
- **Lambda 캡처**: UI 이벤트 핸들러(저장됨) → `[this]` 또는 값 캡처. 알고리즘 콜백(로컬) → `[&]` 참조 OK
- **함수 포인터 통일**: SessionEvents, NewTabFn 모두 함수 포인터 + context 패턴 (heap 할당 회피). `std::function` 사용 금지 — `#include <functional>` 불필요
- **std::optional**: `id_at()`, `index_of()` 등 nullable 반환에 사용 (이미 SessionManager API에 적용)
- **std::string_view**: VT OSC 콜백의 title/CWD 전달 시 `const char*` + `size_t` → 내부에서 `std::wstring`으로 변환

**코드 구조 제한 (common.md — PostToolUse 훅 자동 검사)**:
- **함수 본문**: 40줄 경고 / 80줄 에러 → `initialize()`를 `setup_listview()`, `setup_add_button()` 등으로 분리
- **함수 매개변수**: 3개 경고 / 5개 에러 → `create_session`의 5개 파라미터는 기존 유지 (SessionManager API)
- **중첩 깊이**: 3단계 경고 / 5단계 에러 → 이벤트 핸들러 내부 조건문 최소화, early return 패턴
- **파일 길이**: 300줄 경��� / 500줄 에러 → `tab_sidebar.cpp` (~300 LOC) 범위 내. 초과 시 `cwd_query.cpp` 분리가 이미 계획됨

**Clean Architecture 준수 (common.md)**:
- TabSidebar = **Presentation** 레이어 (UI 렌더링, 입력 처리)
- SessionManager = **Application** 레이어 (세션 라이프사이클 조정)
- ConPtySession = **Infrastructure** 레이어 (외부 프로세스 통신)
- TabSidebar → SessionManager 방향 의존 (내부 방향). 역방향 의존 없음 (콜백으로 느슨한 결합)

### 1.3 Reference Implementation Comparison

| 항목 | cmux | WT | Alacritty | **GhostWin 결정** |
|------|------|----|-----------|--------------------|
| 탭 위치 | 좌측 수직 | 상단 수평 (TabView) | 없음 (OS 탭) | **좌측 수직** (cmux 패턴) |
| 탭 정보 | CWD+git+포트+알림 | 제목만 | — | **제목+CWD** (Phase 5), git+알림 (Phase 6) |
| 드래그 | 지원 | 지원 (TabView 내장) | — | **지원** (ListView CanReorderItems) |
| 새 탭 | `+` 버튼 + 단축키 | `+` 버튼 + 단축키 | — | **`+` 버튼 + Ctrl+T** |
| 닫기 | `x` 버튼 | `x` 버튼 | — | **`x` 버튼 + Ctrl+W** |
| 사이드바 크기 | 리사이즈 가능 | — (수평 탭) | — | **고정 너비** (Phase 5), 리사이즈 (Phase 6+) |

### 1.4 Plan과의 차이점

| Plan 기술 | Design 결정 | 변경 근거 |
|-----------|------------|-----------|
| "수직 탭 사이드바 (cmux 패턴)" | ListView + StackPanel Code-only 구현 | WinUI3 Code-only에서 TabView는 복잡도 과다. ListView가 충분 |
| CWD 표시 방법 미지정 | ConPTY title polling (2초) + 프로세스 CWD 쿼리 병행 | UX 완성도 우선. 실시간 title/CWD 반영으로 cmux 수준 달성 |
| "드래그로 탭 순서 변경" | CanReorderItems PoC 먼저 수행, 실패 시 DragStarting 수동 구현 | 우회 없이 정면 구현. Code-only 제약 있으면 수동 드래그 이벤트로 해결 |
| 사이드바 리사이즈 미언급 | Phase 5에서는 고정 너비 (220px) | 우선순위 낮음. 리사이즈 핸들은 Phase 6+로 연기 |

---

## 2. Architecture

### 2.1 현재 구조 (Before — [TEMP] 단축키만)

```
GhostWinApp::OnLaunched
├── Grid { col0: 0px, col1: star }
│   ├── ListView (col0) — 빈 사이드바, 너비 0
│   └── SwapChainPanel (col1) — 터미널 렌더링
├── HandleKeyDown
│   ├── [TEMP] Ctrl+T → create_session
│   ├── [TEMP] Ctrl+W → close_session
│   └── [TEMP] Ctrl+Tab → activate_next/prev
└── SessionEvents — on_child_exit만 연결
```

### 2.2 목표 구조 (After — TabSidebar 통합)

```
GhostWinApp::OnLaunched
├── Grid { col0: Auto, col1: star }
│   ├── TabSidebar (col0)
│   │   ├── StackPanel (vertical)
│   │   │   ├── ListView — 탭 항목 목록
│   │   │   │   └── DataTemplate: TabItemView { title, cwd, close_btn }
│   │   │   └── Button "+" — 새 탭 추가
│   │   └── 고정 너비: 220px (DPI 스케일링 적용)
│   └── SwapChainPanel (col1) — 터미널 렌더링 (불변)
├── HandleKeyDown
│   ├── Ctrl+T → TabSidebar::request_new_tab()
│   ├── Ctrl+W → TabSidebar::request_close_tab()
│   ├── Ctrl+Tab → SessionManager::activate_next/prev
│   └── Ctrl+1~9 → SessionManager::id_at(n) → activate
└── SessionEvents — 모든 콜백 연결 → TabSidebar 업데이트
```

### 2.3 Component Diagram

```
┌──────────────────────────────────────────────────────────┐
│                      GhostWinApp                          │
│                                                          │
│  ┌──────────────┐      ┌────────────────────────────┐   │
│  │  TabSidebar   │      │      SessionManager         │   │
│  │               │◄────►│                            │   │
│  │  m_listview   │ API  │  sessions_[]               │   │
│  │  m_add_btn    │      │  active_idx_               │   │
│  │  m_items      │      │  events_ ─────────────────►│   │
│  │ (ObsVector)   │      │    on_created    → AddTab  │   │
│  └──────┬───────┘      │    on_closed     → RemTab  │   │
│         │               │    on_activated  → Highlight│   │
│         │ Width=220px   │    on_title_changed → Text  │   │
│         │ (DPI-aware)   │    on_cwd_changed → SubText │   │
│  ┌──────┴───────────────┴────────────────────────────┐   │
│  │                Grid Layout                         │   │
│  │  [col0: Auto=220px] [col1: Star]                  │   │
│  │   TabSidebar          SwapChainPanel              │   │
│  └───────────────────────────────────────────────────┘   │
│                                                          │
│  ┌────────────────────────────────────────────────────┐   │
│  │              Shared Rendering Infra (불변)          │   │
│  │  DX11Renderer ← GlyphAtlas ← QuadBuilder          │   │
│  └────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────┘
```

### 2.4 Dependencies

| Component | Depends On | Purpose |
|-----------|-----------|---------|
| TabSidebar | SessionManager | 세션 목록 조회, 생성/닫기/전환 요청 |
| TabSidebar | GhostWinApp | 새 세션 생성 시 ConPTY 파라미터 계산 (cols/rows) |
| GhostWinApp | TabSidebar | Grid 레이아웃에 사이드바 삽입, 단축키 위임 |
| SessionEvents | TabSidebar | 세션 변경 알림 → UI 업데이트 |

### 2.5 Pixel Alignment Constraint (ADR-009 연관)

SwapChainPanel의 ActualOffset이 비정수이면 DWM이 bilinear filtering을 적용하여 텍스트가 흐려진다.

**리서치 결과 (에이전트 5)**: Grid는 정수 물리 픽셀을 자동 보장하지 않음. 125%/175% 같은 비정수 DPI에서 fractional offset 발생 가능.

**해결책**: `UseLayoutRounding(true)` + `round(width * scale) / scale` 보정.

```cpp
// Grid 생성 시
grid.UseLayoutRounding(true);  // 물리 픽셀 스냅 강제

// 사이드바 너비: round 보정으로 물리 픽셀 정수 보장
float scale = m_panel.CompositionScaleX();
double sidebar_logical = std::round(220.0 * scale) / scale;
col0.Width(winui::GridLengthHelper::FromPixels(sidebar_logical));

// 런타임 검증 (디버그 로그)
auto offset = m_panel.ActualOffset();
float physical_x = offset.x * scale;
LOG_I("winui", "SwapChainPanel physical offset: %.2f (integer=%s)",
      physical_x, std::abs(physical_x - std::round(physical_x)) < 0.01f ? "YES" : "NO");
```

**근거**: floor 대신 round를 사용하면 175% DPI (scale=1.75)에서 `floor(220*1.75)=385` vs `round(220*1.75)=385` (동일하지만), 125% (scale=1.25)에서 `floor(220*1.25)=275` vs `round(220*1.25)=275` (동일). 차이가 나는 것은 비표준 스케일(137% 등)에서 round가 더 가까운 물리 픽셀을 선택함.

---

## 3. Data Model

### 3.1 TabItemData + IObservableVector 데이터 소스

**리서치 결과 (에이전트 1)**: ListView의 `CanReorderItems(true)`는 `ItemsSource`가 `IObservableVector`에
바인딩되어야만 동작. `Items().Append()` 수동 추가로는 드래그 리오더 미동작.

**결정**: `winrt::single_threaded_observable_vector<IInspectable>()` + `ItemsSource()` 조합 사용.
각 탭 항목은 WinUI3 UIElement(Grid)를 직접 ItemsSource에 넣는 대신, lightweight IInspectable 래퍼를 사용.

```cpp
// src/ui/tab_sidebar.h

namespace ghostwin {

/// 탭 항목 표시 데이터 (UI thread only)
struct TabItemData {
    SessionId session_id = 0;
    std::wstring title;          // 세션 제목 (ConPTY title 또는 "Session N")
    std::wstring cwd_display;    // CWD 축약 표시 (예: "~/projects/ghostwin")
    bool is_active = false;      // 활성 탭 하이라이트
};

} // namespace ghostwin
```

ListView의 데이터 소스 구조:
```cpp
// TabSidebar 멤버
winrt::Windows::Foundation::Collections::IObservableVector<winrt::IInspectable> items_source_{nullptr};

// initialize() 내부
items_source_ = winrt::single_threaded_observable_vector<winrt::IInspectable>();
list_view_.ItemsSource(items_source_);
list_view_.CanReorderItems(true);
list_view_.AllowDrop(true);
```

`items_source_`에는 각 탭의 UIElement(Grid)를 IInspectable으로 추가.
`items_` 벡터(C++ 측)와 `items_source_`(WinRT 측)를 항상 1:1 동기화 유지.

### 3.2 CWD 축약 규칙

| CWD 원본 | 축약 표시 | 규칙 |
|----------|-----------|------|
| `C:\Users\Solit\Projects\ghostwin` | `ghostwin` | 마지막 디렉토리 이름 |
| `C:\Users\Solit` | `~` | 홈 디렉토리 = `~` |
| `C:\Users\Solit\Documents` | `~/Documents` | 홈 하위 = `~/...` |
| `C:\` | `C:\` | 루트는 그대로 |
| `(알 수 없음)` | 셸 이름 (예: `pwsh`) | CWD 쿼리 실패 시 |

구현: `ShortenCwd(const std::wstring& full_path)` 유틸리티 함수.

### 3.3 CWD 쿼리 방법

ConPTY 자식 프로세스의 CWD를 쿼리하는 방법:

```
ConPtySession → child_pid (CreateProcess에서 획득)
    → NtQueryInformationProcess(ProcessBasicInformation) → PEB → ProcessParameters → CurrentDirectory
    또는
    → GetFinalPathNameByHandle (프로세스 핸들 → CWD)
```

**선택: 프로세스 트리 워킹**

1. `ConPtySession::child_pid()` 접근자 추가 (이미 내부에 저장됨)
2. 자식 프로세스의 가장 깊은 자손의 CWD를 가져옴 (bash → git 등 포그라운드 프로세스)
3. 폴링 주기: 2초 (WT와 동일)
4. 실패 시: 이전 CWD 유지 또는 셸 이름 표시

**Phase 5-B 범위 (UX 완성도 우선 결정)**:

title과 CWD를 모두 Phase 5-B에서 실시간 반영한다. 우회하지 않고 정면 구현.

**3.3.1 Title 반영 — 2초 폴링 (VT 파서 OSC 결과 읽기)**

> **구현 결정 변경**: Design v1.3의 "이벤트 드리븐 콜백 (지연 ~0ms)" 대신,
> `poll_titles_and_cwd()` 2초 폴링으로 `VtCore::get_title()` 조회 방식 채택.
> 이유: vt_bridge의 Zig 측 이펙트 콜백 연결이 복잡하고, 2초 폴링이 WT와 동일한 수준.
> 향후 이벤트 드리븐 전환 가능 (vt_bridge에 API는 이미 준비됨).

**리서치 결과 (에이전트 2)**: ConPTY는 OSC 0/2를 출력 파이프로 그대로 전달한다.
`SetConsoleTitle()` 호출도 conhost가 OSC 2로 변환하여 파이프에 기록.
libghostty VT 파서가 이미 OSC 0/2를 파싱하므로, **콜백으로 즉시 title 변경을 받을 수 있다** (지연 ~0ms).

```
ConPTY 자식 프로세스
    → SetConsoleTitle() 또는 printf("\033]0;title\007")
    → conhost가 OSC 0/2로 변환 → pseudoconsole output pipe
    → ConPtySession I/O thread → libghostty VT 파서
    → osc_dispatch 콜백 (OSC 0/2 감지)
    → Session::title 업데이트
    → fire_event(events_.on_title_changed, id, new_title)  [UI thread 전환 필요]
    → TabSidebar::on_title_changed() → ListView 항목 텍스트 업데이트
```

**libghostty 내부 인프라 (에이전트 7 리서치 발견)**:

libghostty는 이미 OSC 0/2 title 파싱을 완전 구현하고 있다:
- `external/ghostty/src/terminal/osc/parsers/change_window_title.zig` — OSC 0/2 파서
- `external/ghostty/src/terminal/Terminal.zig` — `setTitle()` / `getTitle()` API
- `external/ghostty/src/terminal/stream_terminal.zig` — `title_changed` 이펙트 콜백
- UTF-8 검증 + 1024바이트 DoS 보호 내장

**현재 문제**: `src/vt-core/vt_bridge.h`가 OSC 콜백을 노출하지 않음.

구현 위치 — vt_bridge에 다음 API를 추가:
```c
// vt_bridge.h 추가
typedef void (*VtTitleChangedCallback)(void* userdata, const char* title, size_t len);
typedef void (*VtCwdChangedCallback)(void* userdata, const char* cwd, size_t len);

typedef struct {
    VtTitleChangedCallback on_title_changed;
    VtCwdChangedCallback   on_cwd_changed;   // OSC 7 파싱 결과
    void* userdata;
} VtOscCallbacks;

void vt_bridge_set_osc_callbacks(void* terminal, const VtOscCallbacks* callbacks);
const char* vt_bridge_get_title(void* terminal);  // 현재 title 조회
```

- `conpty_session.cpp`: VT 파싱 루프에서 title/CWD 변경 감지 시 콜백 호출
- **폴링 타이머 불필요** — 이벤트 드리븐 방식으로 즉시 반영
- ghostty의 `stream_terminal.zig` → `title_changed` 이펙트 → vt_bridge 콜백 체인

**3.3.2 CWD 실시간 반영 — 이중 전략 (OSC + PEB 폴링)**

**리서치 결과 (에이전트 3)**: 
- PEB ReadProcessMemory 3회로 CWD 쿼리 가능 (마이크로초 단위)
- **단 PowerShell은 SetCurrentDirectory()를 호출하지 않으므로 PEB CWD가 갱신되지 않음**
- WT는 **OSC 9;9** 시퀀스를 셸이 프롬프트마다 출력하는 방식에 의존

**3중 전략 결정 (에이전트 7+9 리서치 반영)**:

```
경로 A (최우선): libghostty VT 파서 OSC 7 — 이벤트 드리븐, 지연 ~0ms
    → libghostty가 이미 OSC 7 (report_pwd) 파서를 내장 (osc.zig)
    → vt_bridge에 on_cwd_changed 콜백 노출
    → bash/zsh의 PROMPT_COMMAND로 OSC 7 출력 시 즉시 감지
    → ConPTY는 OSC 7을 출력 파이프로 passthrough (에이전트 9 확인)

경로 B (보조): OSC 9;9 파싱 — WT 호환, 이벤트 드리븐
    → OSC 9;9 ("path") — ConEmu/WT 확장. ConPTY passthrough 확인
    → PowerShell prompt 함수에서 출력 시 즉시 감지
    → libghostty OSC 9 파서에 9;9 서브커맨드 처리 추가 필요

경로 C (폴백): PEB CWD 폴링 — 2초 주기, 셸 설정 불필요
    → 어떤 셸도 기본으로 OSC 출력 안 함 (에이전트 9 확인). cmd.exe는 OSC 불가
    → ConPtySession::child_pid() → GetDeepestChildPid() → GetProcessCwd()
    → NtQueryInformationProcess → PEB → ProcessParameters → CurrentDirectory
    → PowerShell 제한: SetCurrentDirectory 미호출 → PEB CWD 부정확
    → 이 경우 프로세스 이름(pwsh.exe) 표시로 폴백
```

**우선순위**: OSC 7/9;9 수신 시 즉시 반영. 미수신 시 PEB 2초 폴링 자동 활성화.
3개 경로가 겹쳐도 "마지막 업데이트 wins" — CWD는 덮어쓰기이므로 충돌 없음.

구현 위치: `src/ui/cwd_query.h/cpp` (PEB CWD 쿼리 + 자식 프로세스 열거).

```cpp
// cwd_query.h 핵심 API
namespace ghostwin {
    /// PID로 CWD 조회. ReadProcessMemory 3회. 실패 시 빈 문자열.
    std::wstring GetProcessCwd(DWORD pid);
    /// 자식 프로세스 체인에서 가장 깊은 프로세스 PID 반환.
    DWORD GetDeepestChildPid(DWORD root_pid);
    /// 통합: 셸 PID → 가장 깊은 자식의 CWD (폴백: 셸 자체 CWD).
    std::wstring GetShellCwd(DWORD shell_pid);
}
```

**3.3.3 SessionManager에 필요한 변경**

기존 `on_title_changed`, `on_cwd_changed`는 SessionEvents에 선언만 있고 발화 코드가 없다.
Phase 5-B에서 다음을 추가:

- VT 파서 OSC 콜백 → `fire_title_event()`, `fire_cwd_event()` (즉시 반영)
- `SessionManager::poll_cwd()` — 2초 폴링으로 PEB CWD 폴백 (OSC 미지원 셸 대응)
- `ConPtySession::child_pid()` 접근자 (이미 내부에 PID 저장 중, public 접근자만 추가)

이 변경으로 session_manager.h/cpp, conpty_session.h는 **수정 대상**이 된다.

---

## 4. TabSidebar Class

### 4.1 클래스 정의

```cpp
// src/ui/tab_sidebar.h
#pragma once

#include "session/session_manager.h"

#undef GetCurrentTime
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.UI.Xaml.Input.h>

#include <vector>

namespace ghostwin {

namespace winui = winrt::Microsoft::UI::Xaml;
namespace controls = winui::Controls;

/// 탭 사이드바 — WinUI3 Code-only 좌측 수직 패널
///
/// Thread ownership: 모든 public 메서드는 UI thread (main thread) only.
/// SessionManager 이벤트는 bind() 시 내부 자동 등록 — on_* 메서드는 private.
///
/// cpp.md 준수:
///   - Rule of Zero (WinRT 값타입 멤버 → 복사 삭제, 이동/소멸 컴파일러 생성)
///   - Public 메서드 7개 이하 (God Object 방지)
///   - Lambda [this] 캡처: TabSidebar가 WinUI3 요소를 소유 → 소멸 시 요소 소멸 → 안전
class TabSidebar {
public:
    using NewTabFn = void(*)(void* ctx);

    // ─── Public API (7개 — common.md 제한 준수) ───

    /// 초기화 + SessionManager 바인딩 + 콜백 등록을 한 번에 수행
    /// bind()와 set_new_tab_callback()을 initialize()에 통합하여 public 메서드 수 절감
    void initialize(float dpi_scale, SessionManager* mgr,
                    NewTabFn new_tab_fn, void* new_tab_ctx);

    [[nodiscard]] winui::FrameworkElement root() const;  // Grid::SetColumn requires FrameworkElement
    void request_new_tab();
    void request_close_active();
    void update_dpi(float new_scale);
    void toggle_visibility();
    [[nodiscard]] bool is_visible() const;

    // Non-copyable (WinRT com_ptr 멤버로 암묵적 복사 불가 → 명시 삭제)
    TabSidebar() = default;
    TabSidebar(const TabSidebar&) = delete;
    TabSidebar& operator=(const TabSidebar&) = delete;
    // Rule of Zero: move/dtor는 컴파일러 생성

private:
    SessionManager* mgr_ = nullptr;     // non-owning observer
    NewTabFn new_tab_fn_ = nullptr;
    void* new_tab_ctx_ = nullptr;       // non-owning

    // WinUI3 요소 (값 타입, com_ptr 내부 관리)
    controls::StackPanel root_panel_{nullptr};
    controls::ListView list_view_{nullptr};
    controls::Button add_button_{nullptr};

    // 데이터 소스 (CanReorderItems 필수: IObservableVector)
    winrt::Windows::Foundation::Collections::IObservableVector<winrt::IInspectable>
        items_source_{nullptr};

    std::vector<TabItemData> items_;     // C++ 측 1:1 동기화

    // RAII guard for SelectionChanged ↔ on_activated 무한 루프 방지
    bool updating_selection_ = false;
    struct SelectionGuard {
        bool& flag;
        SelectionGuard(bool& f) : flag(f) { flag = true; }
        ~SelectionGuard() { flag = false; }
    };

    float dpi_scale_ = 1.0f;
    bool visible_ = true;
    static constexpr double kBaseWidth = 220.0;

    // ─── SessionEvents 핸들러 (private — bind 시 내부 등록) ───

    void on_session_created(SessionId id);
    void on_session_closed(SessionId id);
    void on_session_activated(SessionId id);
    void on_title_changed(SessionId id, const std::wstring& title);
    void on_cwd_changed(SessionId id, const std::wstring& cwd);

    // ─── 내부 UI 생성 (cpp.md: 함수 40줄 제한 → 분리) ───

    void setup_listview();
    void setup_add_button();
    controls::StackPanel create_text_panel(const TabItemData& data);
    controls::Button create_close_button(SessionId sid);
    winui::UIElement create_tab_item_ui(const TabItemData& data);

    void update_active_highlight(SessionId active_id);
    void sync_items_from_listview();
    void rebuild_list();

    [[nodiscard]] double sidebar_width() const;
};

} // namespace ghostwin
```

### 4.2 Tab Item UI 구조 (Code-only DataTemplate)

```
┌─────────────────────────────────┐
│ [Grid: 2 cols]                  │
│  ┌──────────────────┐ ┌──────┐ │
│  │ StackPanel       │ │Button│ │
│  │  TextBlock title │ │ "×"  │ │
│  │  TextBlock cwd   │ │      │ │
│  └──────────────────┘ └──────┘ │
│  col0: Star           col1: Auto│
│                                 │
│  Background:                    │
│    active → SolidColorBrush     │
│    hover  → subtle highlight    │
│    normal → transparent         │
└─────────────────────────────────┘
```

각 탭 항목은 `create_tab_item_ui()`에서 Code-only로 생성.
**cpp.md 준수**: 함수 본문 40줄 제한 → `create_text_panel()`, `create_close_button()` 분리.

```cpp
// ─── 텍스트 영역 (title + cwd) ─── [~15줄]
controls::StackPanel TabSidebar::create_text_panel(const TabItemData& data) {
    auto panel = controls::StackPanel();
    panel.Margin({8, 4, 0, 4});

    auto title_block = controls::TextBlock();
    title_block.Text(winrt::hstring(data.title));
    title_block.FontSize(13);
    if (data.is_active) {
        title_block.FontWeight(winrt::Windows::UI::Text::FontWeights::SemiBold());
    }
    panel.Children().Append(title_block);

    auto cwd_block = controls::TextBlock();
    cwd_block.Text(winrt::hstring(data.cwd_display));
    cwd_block.FontSize(11);
    cwd_block.Opacity(0.6);
    panel.Children().Append(cwd_block);
    return panel;
}

// ─── 닫기 버튼 ─── [~12줄]
controls::Button TabSidebar::create_close_button(SessionId sid) {
    auto btn = controls::Button();
    btn.Content(winrt::box_value(L"\u00D7"));  // ×
    btn.Padding({4, 2, 4, 2});
    btn.Margin({0, 0, 4, 0});
    btn.VerticalAlignment(winui::VerticalAlignment::Center);
    btn.Click([this, sid](auto&&, auto&&) {
        if (mgr_) mgr_->close_session(sid);
    });
    return btn;
}

// ─── 탭 아이템 조립 ─── [~15줄]
winui::UIElement TabSidebar::create_tab_item_ui(const TabItemData& data) {
    auto grid = controls::Grid();
    auto col0 = controls::ColumnDefinition();
    col0.Width(winui::GridLength{1, winui::GridUnitType::Star});
    auto col1 = controls::ColumnDefinition();
    col1.Width(winui::GridLengthHelper::Auto());
    grid.ColumnDefinitions().Append(col0);
    grid.ColumnDefinitions().Append(col1);

    auto text = create_text_panel(data);
    controls::Grid::SetColumn(text, 0);
    grid.Children().Append(text);

    auto close = create_close_button(data.session_id);
    controls::Grid::SetColumn(close, 1);
    grid.Children().Append(close);
    return grid;
}
```

### 4.3 ListView 이벤트 연동

**cpp.md 준수**: `initialize()` 40줄 초과 방지 → `setup_listview()`, `setup_add_button()` 분리.

```cpp
// ─── ListView 초기화 ─── [~20줄]
void TabSidebar::setup_listview() {
    items_source_ = winrt::single_threaded_observable_vector<winrt::IInspectable>();

    list_view_ = controls::ListView();
    list_view_.ItemsSource(items_source_);
    list_view_.SelectionMode(controls::ListViewSelectionMode::Single);
    list_view_.CanReorderItems(true);
    list_view_.AllowDrop(true);
    list_view_.VerticalAlignment(winui::VerticalAlignment::Stretch);

    // guard flag로 무한 루프 방지 (WT 검증 패턴)
    list_view_.SelectionChanged([this](auto&&, auto&&) {
        if (updating_selection_) return;
        int idx = list_view_.SelectedIndex();
        if (idx < 0 || idx >= static_cast<int>(items_.size())) return;
        if (mgr_) mgr_->activate(items_[idx].session_id);
    });

    list_view_.DragItemsCompleted([this](auto&&, auto&&) {
        sync_items_from_listview();
    });
}

// ─── '+' 버튼 초기화 ─── [~8줄]
void TabSidebar::setup_add_button() {
    add_button_ = controls::Button();
    add_button_.Content(winrt::box_value(L"+"));
    add_button_.HorizontalAlignment(winui::HorizontalAlignment::Stretch);
    add_button_.Margin({4, 4, 4, 4});
    add_button_.Click([this](auto&&, auto&&) { request_new_tab(); });
}

// ─── 메인 초기화 ─── [~10줄]
void TabSidebar::initialize(float dpi_scale) {
    dpi_scale_ = dpi_scale;

    root_panel_ = controls::StackPanel();
    root_panel_.Orientation(controls::Orientation::Vertical);
    root_panel_.Width(sidebar_width());

    setup_listview();
    root_panel_.Children().Append(list_view_);

    setup_add_button();
    root_panel_.Children().Append(add_button_);
}

// on_activated 핸들러에서 guard flag 사용 예시
void TabSidebar::on_session_activated(SessionId id) {
    // RAII guard: 생성자에서 true 설정, 소멸자에서 false 리셋 (cpp.md RAII 준수)
    SelectionGuard guard{updating_selection_};

    for (size_t i = 0; i < items_.size(); ++i) {
        items_[i].is_active = (items_[i].session_id == id);
        if (items_[i].is_active) {
            list_view_.SelectedIndex(static_cast<int32_t>(i));
            // ↑ SelectionChanged 발생하지만 guard에 의해 무시됨
        }
    }
    update_active_highlight(id);
}
```

### 4.4 드래그 순서 변경 동기화

ListView의 CanReorderItems가 내부적으로 아이템 순서를 변경하면,
SessionManager의 sessions_ 벡터도 일치하도록 `move_session`을 호출해야 한다.

```
사용자 드래그 → ListView 내부 재배치 → DragItemsCompleted 이벤트
    → sync_items_from_listview()
        → items_ 벡터를 ListView의 현재 순서로 재구성
        → 변경된 인덱스마다 mgr_->move_session(old_idx, new_idx) 호출
```

**주의**: ListView의 CanReorderItems + Code-only 구성에서 ItemsSource를 ObservableCollection으로
바인딩하지 않으면 내부 재배치가 동작하지 않을 수 있다. 이 경우 단계적 해결:

**Phase 5-B 드래그 구현 전략 (우회 없이 정면 해결)**:

```
Step 1: CanReorderItems PoC (Implementation Step 2에서 수행)
   └── ListView에 CanReorderItems(true) + AllowDrop(true) 설정
   └── DragItemsCompleted 이벤트 핸들러 등록
   └── 동작 확인: 아이템이 실제로 드래그 가능한지 테스트

Step 2a: PoC 성공 시
   └── sync_items_from_listview()로 SessionManager 동기화
   └── 완료

Step 2b: PoC 실패 시 (CanReorderItems가 Code-only에서 미동작)
   └── 대안 B 구현: DragStarting + Drop 이벤트 수동 처리
   └── 각 탭 아이템에 CanDrag(true) 설정
   └── DragStarting: 드래그 중인 SessionId를 DataPackage에 저장
   └── DragOver: 드롭 위치 시각 표시 (InsertionMark)
   └── Drop: SessionManager::move_session() + rebuild_list()
   └── 이 방식은 WT의 TabViewItem 드래그와 동일 원리

Step 2c: 대안 B도 불가 시 (ListView 자체가 드래그 미지원)
   └── 키보드 이동(Ctrl+Shift+PgUp/Dn)은 이미 구현됨
   └── 원인 분석 → 이슈 리포트 → Phase 6에서 커스텀 Panel로 해결
```

→ **결정**: 포기하지 않는다. CanReorderItems → 수동 드래그 이벤트 → 키보드 이동 순서로
3단계 폴백. 각 단계에서 실패 원인을 분석한 후에만 다음 단계로 전환.

---

## 5. GhostWinApp Integration

### 5.1 OnLaunched 수정

```cpp
// 변경 전 (현재 코드)
auto col0 = controls::ColumnDefinition();
col0.Width(winui::GridLengthHelper::FromPixels(0));  // 사이드바 0px

// 변경 후
// initialize()에 bind + callback 통합 (God Object 방지: public 메서드 7개 제한)
m_tab_sidebar.initialize(
    m_current_dpi_scale.load(),
    &m_session_mgr,
    [](void* ctx) { static_cast<GhostWinApp*>(ctx)->create_new_session(); },
    this);

auto sidebar_element = m_tab_sidebar.root();
controls::Grid::SetColumn(sidebar_element, 0);
grid.Children().Append(sidebar_element);
```

### 5.2 SessionEvents 콜백 연결

**아키텍처 결정 (에이전트 13 리뷰 반영)**: on_* 핸들러는 TabSidebar private.
`TabSidebar::initialize()` 내부의 `bind()`에서 SessionEvents를 자동 등록할 수도 있으나,
I/O 스레드 콜백의 DispatcherQueue 전환은 GhostWinApp이 m_window를 소유하므로
GhostWinApp에서 이벤트를 등록하는 것이 적절 (레이어 경계 존중).
TabSidebar는 UI 업데이트만 담당, 스레드 전환은 GhostWinApp이 담당.

```cpp
// GhostWinApp에서 SessionEvents 설정 시 TabSidebar 핸들러 연결
SessionEvents events{};
events.context = this;

events.on_created = [](void* ctx, SessionId id) {
    auto* app = static_cast<GhostWinApp*>(ctx);
    app->m_tab_sidebar.on_session_created(id);
};
events.on_closed = [](void* ctx, SessionId id) {
    auto* app = static_cast<GhostWinApp*>(ctx);
    app->m_tab_sidebar.on_session_closed(id);
    // 마지막 세션이면 앱 종료
    if (app->m_session_mgr.count() == 0) {
        app->ShutdownRenderThread();
        app->m_window.Close();
    }
};
events.on_activated = [](void* ctx, SessionId id) {
    auto* app = static_cast<GhostWinApp*>(ctx);
    app->m_tab_sidebar.on_session_activated(id);
};
// *** CRITICAL: on_title_changed, on_cwd_changed는 I/O 스레드에서 호출됨 ***
// on_child_exit와 동일하게 DispatcherQueue.TryEnqueue로 UI 스레드 전환 필수.
// std::wstring 값 복사 후 move 캡처 — cpp.md "stored lambda: capture by value or move"
events.on_title_changed = [](void* ctx, SessionId id, const std::wstring& title) {
    auto* app = static_cast<GhostWinApp*>(ctx);
    std::wstring t = title;  // 값 복사 (I/O 스레드의 임시 데이터 수명 보장)
    app->m_window.DispatcherQueue().TryEnqueue([app, id, t = std::move(t)]() {
        app->m_tab_sidebar.on_title_changed(id, t);
    });
};
events.on_cwd_changed = [](void* ctx, SessionId id, const std::wstring& cwd) {
    auto* app = static_cast<GhostWinApp*>(ctx);
    std::wstring c = cwd;
    app->m_window.DispatcherQueue().TryEnqueue([app, id, c = std::move(c)]() {
        app->m_tab_sidebar.on_cwd_changed(id, c);
    });
};
events.on_child_exit = [](void* ctx, SessionId id, uint32_t exit_code) {
    auto* app = static_cast<GhostWinApp*>(ctx);
    app->m_window.DispatcherQueue().TryEnqueue([app, id]() {
        app->m_session_mgr.close_session(id);
    });
};

m_session_mgr.set_events(events);
```

### 5.3 [TEMP] 단축키 → 정식 전환

| 현재 ([TEMP]) | Phase 5-B 이후 | 변경점 |
|---------------|---------------|--------|
| `Ctrl+T` → 직접 create_session | `Ctrl+T` → `m_tab_sidebar.request_new_tab()` | 사이드바 경유 |
| `Ctrl+W` → 직접 close_session | `Ctrl+W` → `m_tab_sidebar.request_close_active()` | 사이드바 경유 |
| `Ctrl+Tab` → activate_next/prev | `Ctrl+Tab` → activate_next/prev (유지) | 변경 없음 |
| — | `Ctrl+1~9` → id_at(n-1) → activate | **신규** |
| — | `Ctrl+Shift+PageUp/Down` → 탭 이동 | **신규** |

### 5.4 winui_app.h 변경

```cpp
// 추가 include
#include "ui/tab_sidebar.h"

class GhostWinApp : ... {
    // ... 기존 멤버 ...

    TabSidebar m_tab_sidebar;  // Presentation 레이어

    void create_new_session();  // [TEMP] 추출

    // CWD PEB 폴링 (2초, OSC 미지원 셸 폴백). Title은 VT OSC 콜�� 즉시 반영.
    winrt::Microsoft::UI::Xaml::DispatcherTimer m_cwd_poll_timer{nullptr};
    void poll_cwd();
};
```

---

## 6. Keyboard Shortcuts

### 6.1 전체 단축키 맵

| 단축키 | 동작 | 구현 위치 |
|--------|------|-----------|
| `Ctrl+T` | 새 탭 생성 | HandleKeyDown → TabSidebar::request_new_tab |
| `Ctrl+W` | 현재 탭 닫기 | HandleKeyDown → TabSidebar::request_close_active |
| `Ctrl+Tab` | 다음 탭 | HandleKeyDown → SessionManager::activate_next |
| `Ctrl+Shift+Tab` | 이전 탭 | HandleKeyDown → SessionManager::activate_prev |
| `Ctrl+1~9` | N번째 탭 직접 선택 | HandleKeyDown → SessionManager::id_at(n-1) → activate |
| `Ctrl+Shift+PageUp` | 탭을 위로 이동 | HandleKeyDown → SessionManager::move_session |
| `Ctrl+Shift+PageDown` | 탭을 아래로 이동 | HandleKeyDown → SessionManager::move_session |
| `Ctrl+Shift+B` | 사이드바 표시/숨김 토글 | HandleKeyDown → TabSidebar::toggle_visibility |

### 6.2 충돌 방지

| 단축키 | 기존 용도 | 해결 |
|--------|----------|------|
| `Ctrl+T` | [TEMP] → 정식으로 승격 | 충돌 없음 |
| `Ctrl+W` | [TEMP] → 정식으로 승격 | 충돌 없음 |
| `Ctrl+Tab` | [TEMP] → 유지 | 충돌 없음 |
| `Ctrl+1~9` | 없음 | 충돌 없음 |
| `Ctrl+Shift+PgUp/Dn` | 없음 | 충돌 없음 |
| `Ctrl+B` | 없음 (Ctrl+B는 현재 ^B 제어 코드 전송) | **충돌 있음** — 아래 해결 |

**Ctrl+B 충돌 해결**: 현재 `Ctrl+B`는 `HandleKeyDown`의 `Ctrl+A~Z` 제어 코드 전송(^B = 0x02)으로
처리된다. 사이드바 토글이 우선하도록 `Ctrl+B`를 제어 코드 전송보다 먼저 가로챈다.
tmux의 `Ctrl+B` prefix와 충돌하므로, 대안으로 `Ctrl+Shift+B` 사용도 고려.
→ **결정**: `Ctrl+Shift+B`로 사이드바 토글. `Ctrl+B`는 기존 ^B 전송 유지.

**주의**: `Ctrl+Shift+T`(마지막 닫은 탭 복원)은 Phase 5 범위 밖. Phase 5-E에서 고려.

---

## 7. File Structure

### 7.1 신규 파일

| File | Purpose | LOC (예상) |
|------|---------|:----------:|
| `src/ui/tab_sidebar.h` | TabSidebar 클래스 정의 + TabItemData | ~90 |
| `src/ui/tab_sidebar.cpp` | TabSidebar 구현 (UI 생성, 이벤트, CWD 축약, 드래그) | ~300 |
| `src/platform/cwd_query.h` | 프로세스 CWD 쿼리 + ShortenCwd 유틸리티 선언. HandleGuard RAII 래퍼 포함 | ~30 |
| `src/platform/cwd_query.cpp` | GetProcessCwd (HandleGuard RAII), GetDeepestChildPid, ShortenCwd(wstring_view) | ~140 |

### 7.2 수정 파일

| File | Changes | LOC 변경 |
|------|---------|:--------:|
| `src/app/winui_app.h` | TabSidebar 멤버 + title_poll_timer + create_new_session 선언 | +10 |
| `src/app/winui_app.cpp` | OnLaunched 사이드바 통합, 단축키 정식화, create_new_session 추출, SessionEvents 전체 연결, poll 타이머 | +60, -30 (TEMP 제거) |
| `src/session/session_manager.h` | `poll_titles_and_cwd()` + `fire_title_event()` / `fire_cwd_event()` 선언 | +10 |
| `src/session/session_manager.cpp` | title/CWD 폴링 로직, 이벤트 발화 구현 | +40 |
| `src/vt-core/vt_bridge.h` | OSC 콜백 등록 API 추가 (VtOscCallbacks, vt_bridge_set_osc_callbacks, vt_bridge_get_title) | +15 |
| `src/vt-core/vt_bridge.cpp` | ghostty stream_terminal 이펙트 콜백 → vt_bridge 콜백 체인 연결 | +30 |
| `src/conpty/conpty_session.h` | `child_pid()` public 접근자 추가 | +2 |
| `src/conpty/conpty_session.cpp` | OSC 콜백 등록 → title/CWD 변경 시 SessionEvents 발화 | +20 |
| `CMakeLists.txt` | `src/ui/tab_sidebar.cpp` + `src/platform/cwd_query.cpp` 추가 | +2 |

### 7.3 디렉토리 구조

```
src/
├── app/
│   ├── winui_app.h          (수정)
│   └── winui_app.cpp        (수정)
├── vt-core/
│   ├── vt_bridge.h          (수정: OSC 콜백 API 추가)
│   └── vt_bridge.cpp        (수정: ghostty 이펙트 → 콜백 체인)
├── conpty/
│   ├── conpty_session.h     (수정: child_pid 접근자)
│   └── conpty_session.cpp   (수정: OSC 콜백 → SessionEvents 발화)
├── session/
│   ├── session.h            (불변)
│   ├── session_manager.h    (수정: poll + fire 메서드)
│   └── session_manager.cpp  (수정: title/CWD 폴링 구현)
├── platform/                (신규 — Infrastructure 레이어, common.md 레이어 규칙)
│   ├── cwd_query.h          (신규: GetProcessCwd, ShortenCwd, HandleGuard)
│   └── cwd_query.cpp        (신규: PEB 쿼리, 프로세스 열거, CWD 축약)
└── ui/                      (신규 — Presentation 레이어)
    ├── tab_sidebar.h        (신규)
    └── tab_sidebar.cpp      (신규)
```

---

## 8. Implementation Order

```
Step 1: 파일 구조 + 드래그 PoC
   └── src/ui/ 디렉토리, tab_sidebar.h/cpp + cwd_query.h/cpp 스켈레톤
   └── CMakeLists.txt 업데이트
   └── ListView CanReorderItems PoC (빈 사이드바에서 드래그 동작 테스트)
   └── PoC 결과에 따라 드래그 구현 방식 확정 (4.4절 Step 2a/2b/2c)

Step 2: TabSidebar 기본 UI (사이드바 출현)
   └── initialize(), root(), 고정 너비 StackPanel + ListView + '+' 버튼
   └── GhostWinApp::OnLaunched에서 Grid col0에 삽입
   └── 빌드 확인: 사이드바가 보이는지 시각 확인
   └── ActualOffset.x 정수 픽셀 확인 (ADR-009)

Step 3: SessionEvents → TabSidebar 연결
   └── on_session_created → ListView에 항목 추가
   └── on_session_closed → ListView에서 항목 제거
   └── on_session_activated → 활성 탭 하이라이트
   └── on_title_changed → 탭 제목 텍스트 업데이트
   └── on_cwd_changed → 탭 CWD 서브텍스트 업데이트
   └── SelectionChanged ↔ on_activated 무한 루프 가드 (Risk #3)
   └── 기존 [TEMP] Ctrl+T/W로 동작 확인

Step 4: Title/CWD 실시간 반영 (이중 전략)
   └── 4a: VT 파서 OSC 콜백 (title 즉시 반영)
   │   └── vt_bridge에 OSC 0/2 title 변경 콜백 등록 API 추가
   │   └── ConPtySession I/O thread → title 변경 감지 → UI thread 전환
   │   └── fire_title_event() → TabSidebar::on_title_changed()
   │   └── 지연 ~0ms (이벤트 드리븐, 폴링 아님)
   └── 4b: VT 파서 OSC 7/9;9 콜백 (CWD 즉시 반영, 경로 A)
   │   └── OSC 7 (file://host/path) + OSC 9;9 (WT 호환) 파싱
   │   └── fire_cwd_event() → TabSidebar::on_cwd_changed()
   └── 4c: PEB CWD 폴링 (OSC 미지원 셸 폴백, 경로 B)
       └── ConPtySession::child_pid() 접근자 추가
       └── cwd_query.cpp: GetDeepestChildPid + GetProcessCwd 구현
       └── GhostWinApp에 2초 DispatcherTimer → poll_cwd()
       └── PowerShell 제한 인지 (SetCurrentDirectory 미호출)

Step 5: 탭 클릭 + 드래그 동작
   └── ListView::SelectionChanged → mgr_->activate()
   └── 닫기(×) 버튼 → mgr_->close_session()
   └── '+' 버튼 → create_new_session()
   └── 드래그 구현 (Step 1 PoC 결과 기반)

Step 6: 단축키 정식화
   └── [TEMP] 코드 → TabSidebar 경유로 전환
   └── Ctrl+1~9 추가
   └── Ctrl+Shift+PageUp/Down 탭 이동 추가
   └── Ctrl+Shift+B 사이드바 토글 추가

Step 7: CWD 축약 + DPI 대응
   └── shorten_cwd() 유틸리티 구현 + 단위 테스트
   └── update_dpi() → 사이드바 너비 재조정

Step 8: [TEMP] 코드 정리 + 회귀 테스트
   └── HandleKeyDown의 [TEMP] 주석 및 직접 호출 제거
   └── 사이드바 0px → Auto로 전환 완료 확인
   └── Phase 4 테스트 10/10 PASS 확인
```

---

## 9. Test Cases

### 9.1 수동 검증 시나리오

| TC | 시나리오 | 예상 결과 | 우선순위 |
|----|---------|-----------|:--------:|
| TC-01 | 앱 시작 | 사이드바에 "Session 0" 탭 1개 표시, 활성 하이라이트 | P0 |
| TC-02 | Ctrl+T | 새 탭 추가, 사이드바에 "Session N" 표시, 새 탭 활성 | P0 |
| TC-03 | Ctrl+W | 현재 탭 닫기, 인접 탭 활성화 | P0 |
| TC-04 | 마지막 탭 Ctrl+W | 앱 종료 | P0 |
| TC-05 | 탭 클릭 | 클릭한 탭으로 세션 전환 | P0 |
| TC-06 | '+' 버튼 클릭 | TC-02와 동일 | P0 |
| TC-07 | '×' 버튼 클릭 | TC-03과 동일 | P0 |
| TC-08 | Ctrl+Tab | 다음 탭으로 순환 전환 | P0 |
| TC-09 | Ctrl+Shift+Tab | 이전 탭으로 순환 전환 | P0 |
| TC-10 | Ctrl+1 | 첫 번째 탭 선택 | P1 |
| TC-11 | Ctrl+9 (탭 3개) | 마지막 탭 선택 (9번이 없으면 마지막) | P1 |
| TC-12 | 세션 자동 종료 (exit) | 탭 자동 제거, 인접 탭 활성화 | P0 |
| TC-13 | 10개 탭 열기 | 스크롤 가능, 안정 동작 | P1 |
| TC-14 | 사이드바 픽셀 정렬 | SwapChainPanel ActualOffset.x가 정수 | P0 |
| TC-15 | DPI 150% 모니터 | 사이드바 너비 적절, 텍스트 선명 | P1 |
| TC-16 | 한글 IME + 탭 전환 | IME 조합 중 탭 전환 시 조합 취소, 전환 후 정상 IME | P0 |
| TC-17 | Ctrl+Shift+PageUp | 현재 탭이 위로 이동 | P1 |
| TC-18 | 기존 Phase 4 테스트 | 10/10 PASS 유지 | P0 |
| TC-19 | `cd /tmp` 후 2초 대기 | 탭 CWD가 "tmp" (또는 축약)으로 변경 | P0 |
| TC-20 | `title MyTitle` (cmd) 후 2초 대기 | 탭 제목이 "MyTitle"로 변경 | P0 |
| TC-21 | Ctrl+Shift+B | 사이드바 숨김 → SwapChainPanel 전체 폭 확장 | P1 |
| TC-22 | Ctrl+Shift+B 다시 | 사이드바 복원 → 이전 레이아웃 복원 | P1 |
| TC-23 | 드래그로 탭 순서 변경 | 탭 순서 변경, SessionManager 동기화 확인 | P1 |
| TC-24 | shorten_cwd 단위 테스트 | 홈→~, 홈하위→~/..., 루트→그대로, 알수없음→셸이름 | P1 |

### 9.2 Phase 4 회귀 테스트

기존 단일 세션 동작이 Phase 5-B 이후에도 동일하게 동작해야 한다:
- 영문 입력, 한글 IME, 서로게이트 쌍, 커서 이동, 붙여넣기
- DPI 변경 시 리사이즈 + 폰트 재생성
- Mica 배경 + MicaBackdrop

---

## 10. Risks and Mitigations

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| WinUI3 Code-only에서 ListView CanReorderItems 미동작 | 중 | 중 | 3단계 폴백: CanReorderItems → 수동 DragStarting/Drop → 키보드 이동. 각 단계 실패 원인 분석 후 전환 |
| 사이드바 너비가 SwapChainPanel 정렬을 깨뜨림 | 상 | 중 | `floor(220 * dpi_scale)` 정수 픽셀 고정 + 시작 시 ActualOffset 로그 검증 |
| ListView SelectionChanged → activate → on_activated → 다시 SelectionChanged 무한 루프 | 상 | 중 | on_activated 핸들러에서 ListView SelectedIndex 설정 시 SelectionChanged 해제/재등록 |
| SessionEvents 콜백이 I/O thread에서 호출되어 UI 접근 불가 | 상 | 높 | on_child_exit만 I/O thread. DispatcherQueue.TryEnqueue로 전환. 나머지는 main thread |
| NtQueryInformationProcess CWD 쿼리 권한 부족 | 중 | 중 | PROCESS_QUERY_LIMITED_INFORMATION으로 시도. 실패 시 ConPTY title 폴백 |
| Title/CWD 2초 폴링의 CPU 부하 | 하 | 하 | 세션 수 ≤ 10, 각 쿼리 < 1ms. 비활성 세션은 건너뛰기 옵션 |
| 많은 탭(20+)에서 ListView 렌더링 성능 | 하 | 하 | 탭 20개는 일반적이지 않음. 필요 시 VirtualizingLayout 적용 |

---

## 11. Out of Scope (Phase 6+)

| 항목 | Phase | 근거 |
|------|-------|------|
| 사이드바 리사이즈 드래그 | 6+ | 고정 너비로 충분 |
| ~~드래그 탭 재배치~~ | — | Phase 5-B 범위로 승격 (PoC + 3단계 폴백) |
| git branch 표시 | 6 | OSC 훅/프로세스 쿼리 연동 필요 |
| 알림 배지 (Notification Ring) | 6 | OSC 9/777/99 수신 구현 필요 |
| 포트 리스닝 표시 | 6 | ETW/netstat 폴링 필요 |
| 탭 오른쪽 클릭 컨텍스트 메뉴 | 6+ | MenuFlyout Code-only 구현 필요 |
| 탭별 색상/아이콘 | 6+ | 설정 시스템(Phase 5-C) 연동 필요 |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.0 | 2026-04-03 | Initial design (Plan FR-02 기반) | 노수장 |
| 1.1 | 2026-04-03 | design-validator 피드백 반영: C-01/C-02, W-01~W-08 해결 | 노수장 |
| 1.2 | 2026-04-03 | 5-agent 리서치 반영: observable vector, VT OSC callback, guard flag, pixel round | 노수장 |
| 1.3 | 2026-04-03 | 10-agent 리서치 최종 + rkit 코드 품질 규칙 사전 반영 | 노수장 |
| 1.4 | 2026-04-03 | 5-agent cpp.md/common.md 심층 리뷰 반영: (1) Critical: I/O→UI 스레드 전환 (on_title/cwd DispatcherQueue) (2) God Object 해결: public 14→7 (on_* private, initialize 통합) (3) 레이어 수정: cwd_query → src/platform/ (4) RAII SelectionGuard 완전화 (5) Rule of Zero 복사 삭제 명시 (6) shorten_cwd → ShortenCwd free function cwd_query.h 이동 (7) HandleGuard RAII 래퍼 명시 | 노수장 |
