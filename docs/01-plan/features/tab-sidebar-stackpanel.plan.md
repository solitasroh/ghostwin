# tab-sidebar-stackpanel Plan Document

> **Summary**: Tab Sidebar ListView → StackPanel 전환. 프레임워크 우회 제거, 자체 Active Visual, Passive View 패턴, Single Source of Truth.
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-04
> **Status**: Draft (v1.1)
> **Dependency**: Phase 5-B tab-sidebar 완료, titlebar-customization 완료

---

## Executive Summary

| Perspective | Content |
|------------|---------|
| **Problem** | ListView의 Selection/Drag/Virtualization 5대 기능 중 0개 사용, 6곳 SelectionGuard 우회. SetAt 시 selection visual 파괴. 프레임워크와 싸우는 구조. hover/pressed/theme visual 미정의 |
| **Solution** | ListView → StackPanel 전환. IObservableVector 제거, SelectionGuard 제거. Passive View 패턴 + TabItemUIRefs in-place 갱신. 자체 active/hover/pressed visual (WinUI3 ThemeResource). Triple-sync atomic 헬퍼 |
| **Function/UX Effect** | 탭 클릭/생성/title 변경 시 active highlight 안정적 유지. hover/pressed 시각 피드백. 드래그 DPI-safe. TextTrimming으로 긴 제목 처리. 동일 UX + 코드 복잡도 대폭 감소 |
| **Core Value** | behavior.md "우회 금지" 준수. Single Source of Truth (`items_`). Passive View 3계층 분리. Phase 6 확장 시 workaround 재발 방지 |

---

## 1. Problem Statement

### 1.1 현재 아키텍처의 구조적 문제

ListView의 5대 핵심 기능 사용 현황:

| ListView 기능 | 상태 | 비고 |
|---|---|---|
| Selection visual | **Broken** | SetAt → selection 파괴, 6곳 SelectionGuard |
| CanReorderItems (OLE drag) | **Disabled** | DPI offset 버그 #5520/#9717 |
| Virtualization | **Unused** | 탭 <20개, rebuild_list가 Clear+Append |
| Keyboard nav | **Unused** | Ctrl+Tab/1~9 자체 핸들링 |
| Data binding | **Unused** | UIElement 직접 Append, DataTemplate 없음 |

### 1.2 SelectionGuard 산재 (6곳)

| # | 위치 | 이유 |
|---|---|---|
| 1 | on_session_created | 새 탭 active 설정 |
| 2 | on_session_activated | 탭 전환 |
| 3 | update_item (SetAt) | title/cwd 변경 → selection 파괴 복구 |
| 4 | rebuild_list | 전체 재구성 후 selection 복구 |
| 5 | PointerReleased | 클릭 시 이벤트 순서 보장 |
| 6 | SelectionChanged | drag 중 재진입 방지 |

### 1.3 10-agent 100% 합의 (2026-04-04)

> "ListView가 제공하는 모든 기능을 비활성화하거나 우회하면서 사용하는 것은 abstraction mismatch. StackPanel이 현재 요구사항에 정확히 맞는 컨테이너."

---

## 2. Goals

### 2.1 제거 목표

- [ ] `ListView list_view_` 멤버 제거
- [ ] `IObservableVector items_source_` 제거
- [ ] `SelectionGuard` 구조체 + `updating_selection_` 플래그 제거
- [ ] `SelectionChanged` 핸들러 제거
- [ ] `SelectedIndex` 수동 관리 코드 전부 제거 (6곳)
- [ ] `setup_listview()` 제거 → `setup_tabs_panel()` 대체
- [ ] `update_item` 템플릿 제거 (in-place 직접 갱신으로 대체)

### 2.2 추가 목표

- [ ] `StackPanel tabs_panel_` + `ScrollViewer scroll_viewer_` 멤버 추가
- [ ] `TabItemUIRefs` 구조체 + `item_refs_` 벡터 (items_와 1:1 병렬)
- [ ] 자체 active visual: accent bar (3 DIP) + Background (ThemeResource) + SemiBold
- [ ] Hover/Pressed visual: WinUI3 ThemeResource 기반 배경색 전환
- [ ] In-place TextBlock 갱신 (title/cwd → TextBlock.Text() 직접 설정)
- [ ] TextTrimming(CharacterEllipsis) + MaxWidth 적용
- [ ] Triple-sync atomic 헬퍼 (`append_tab`, `remove_tab_at`)
- [ ] `find_index(SessionId)` 단일 경로 헬퍼
- [ ] `apply_active_visual` / `apply_inactive_visual` 헬퍼
- [ ] Custom pointer drag 유지 (tabs_panel_.Children() 기반)
- [ ] ScrollViewer.BringIntoView() 신규 탭 자동 스크롤

### 2.3 유지 항목 (변경 없음)

- Public API 7개 (initialize, root, request_new_tab, request_close_active, update_dpi, toggle_visibility, is_visible)
- Private on_* handlers (friend GhostWinApp)
- TabSidebarConfig 구조체 (function pointer DI 유지)
- TabItemData 구조체
- DragState 구조체 + custom pointer drag
- winui_app.cpp 연동 코드 (public API + friend only, list_view_ 직접 참조 0건)
- TitleBarManager sidebar_width_fn → root_panel_.Width() 경로 불변

---

## 3. Architecture

### 3.1 Before (현재)

```
root_panel_ (StackPanel, Vertical)
├── list_view_ (ListView)
│   ├── items_source_ (IObservableVector<IInspectable>)
│   │   ├── Grid (tab 0)    ← SetAt → selection 파괴
│   │   ├── Grid (tab 1)
│   │   └── ...
│   ├── SelectionMode::Single   ← 6곳 SelectionGuard
│   ├── CanReorderItems(false)  ← OLE drag 비활성
│   └── SelectionChanged        ← guard 필요
└── add_button_ (Button "+")
```

### 3.2 After (목표)

```
root_panel_ (StackPanel, Vertical)
├── scroll_viewer_ (ScrollViewer, VerticalOnly)
│   └── tabs_panel_ (StackPanel, Vertical)
│       ├── Grid (tab 0) ← active: accent bar + Background + SemiBold
│       ├── Grid (tab 1) ← inactive: transparent + Normal
│       └── ...           ← Children.Append/RemoveAt 직접 관리
└── add_button_ (Button "+")

items_ (vector<TabItemData>)       ← Single Source of Truth (data)
item_refs_ (vector<TabItemUIRefs>) ← View references (derived, 1:1)
tabs_panel_.Children()             ← Rendered UI (derived, 1:1)
```

### 3.3 Passive View 3계층 분리

```
┌─────────────────────────────────────────────────────┐
│  SessionManager (Domain Layer)                       │
│  - 세션 생명주기, 순서, activate/close/move          │
└──────────────┬──────────────────────────────────────┘
               │ SessionEvents (function pointer callbacks)
               ▼
┌─────────────────────────────────────────────────────┐
│  GhostWinApp (Mediator / Controller)                 │
│  - 이벤트 라우팅, I/O→UI thread 전환                 │
│  - DispatcherQueue.TryEnqueue (title/cwd/child_exit) │
│  - 동기 호출 (created/closed/activated)              │
└──────────────┬──────────────────────────────────────┘
               │ Private on_* handlers (friend)
               ▼
┌─────────────────────────────────────────────────────┐
│  TabSidebar (Passive View)                           │
│  - UI 렌더링, 사용자 입력, active visual             │
│  - items_ = data truth, item_refs_ = view cache      │
│  - binding 없음 → 명시적 setter (Passive View 패턴)  │
└─────────────────────────────────────────────────────┘
```

### 3.4 이벤트 흐름

```
Session Created (UI thread, 동기):
  SessionManager.create_session()
  → events_.on_created(ctx, id)
  → GhostWinApp: m_tab_sidebar.on_session_created(id)
  → TabSidebar: append_tab(data, refs, ui) + apply_active_visual + BringIntoView

Title Changed (I/O thread → UI thread):
  ConPtySession I/O: write() before/after → fire_title_event(id, title)
  → events_.on_title_changed(ctx, id, title)
  → GhostWinApp: DispatcherQueue.TryEnqueue → m_tab_sidebar.on_title_changed(id, title)
  → TabSidebar: items_[idx].title = title + item_refs_[idx].title_block.Text(title)

Tab Click (UI thread):
  PointerReleased (not drag) → mgr_->activate(sid)
  → SessionManager: fire_event(on_activated, id)
  → GhostWinApp: m_tab_sidebar.on_session_activated(id)
  → TabSidebar: apply_inactive_visual(old) + apply_active_visual(new)

Drag Reorder (UI thread):
  PointerPressed → DragState.pending
  PointerMoved (>5 DIP) → DragState.active, TranslateTransform.Y
  PointerReleased → apply_drag_reorder(): items_ + item_refs_ 재배열, rebuild_list()

Session Closed (UI thread):
  mgr_->close_session(id) → events_.on_closed(ctx, id)
  → GhostWinApp: m_tab_sidebar.on_session_closed(id)
  → TabSidebar: remove_tab_at(idx)
  → drag 진행 중이면 drag 강제 취소 (drag_ = {})
```

### 3.5 데이터 일관성 보장 (Sync Invariant)

**불변식**: `items_.size() == item_refs_.size() == tabs_panel_.Children().Size()`

모든 구조 변경은 atomic 헬퍼를 통해서만 수행:

| 헬퍼 | 역할 | 조작 순서 |
|------|------|-----------|
| `append_tab(data, refs)` | 탭 추가 | items_ push_back → item_refs_ push_back → Children.Append |
| `remove_tab_at(idx)` | 탭 제거 | Children.RemoveAt → item_refs_ erase → items_ erase |
| `rebuild_list()` | 전체 재구성 (드래그 후 전용) | Children.Clear → item_refs_.root 재활용 Append |

**제거 순서**: UI → refs → data (UI 먼저 제거하면, 실패 시 데이터 무변경)

**Debug assert**: 모든 헬퍼 끝에 `assert(items_.size() == item_refs_.size())` 삽입

| 헬퍼 | 역할 |
|------|------|
| `find_index(SessionId) → optional<size_t>` | index 변환 단일 경로. 모든 on_* 핸들러에서 사용 |
| `apply_active_visual(TabItemUIRefs&)` | Background=accent, SemiBold, accent bar Visible |
| `apply_inactive_visual(TabItemUIRefs&)` | Background=transparent, Normal, accent bar Collapsed |

---

## 4. Visual Design Spec

### 4.1 Tab Item Layout

```
┌─────────────────────────────────────┐
│ [3px accent bar] [title      ] [×] │  ← active 탭
│                  [~/project  ]     │
└─────────────────────────────────────┘
┌─────────────────────────────────────┐
│                  [title      ] [×] │  ← inactive 탭
│                  [~/project  ]     │
└─────────────────────────────────────┘
```

| 속성 | 값 |
|------|-----|
| Grid MinHeight | 40 DIP |
| Grid 3-column | Col 0: Auto (accent bar), Col 1: Star (text), Col 2: Auto (close) |
| Accent Bar | Border, Width=3 DIP, CornerRadius={2,0,0,2} |
| Text Panel Margin | {8, 4, 0, 4} |
| Title FontSize | 13 |
| CWD FontSize | 11, Opacity=0.6 |
| Close Button Padding | {4, 2, 4, 2} |
| Close Button | 항상 표시 (hover-only는 복잡도 과다) |
| TextTrimming | CharacterEllipsis (title_block + cwd_block) |

### 4.2 상태별 Visual

| 상태 | Background | FontWeight | Accent Bar | 구현 |
|------|-----------|------------|------------|------|
| **Active** | `SubtleFillColorSecondaryBrush` | SemiBold | Visible (SystemAccentColor) | apply_active_visual() |
| **Inactive** | Transparent (nullptr) | Normal | Collapsed | apply_inactive_visual() |
| **Hover** | `SubtleFillColorSecondaryBrush` | 현재 유지 | 현재 유지 | PointerEntered/Exited |
| **Pressed** | `SubtleFillColorTertiaryBrush` | 현재 유지 | 현재 유지 | PointerPressed (drag threshold 전) |
| **Dragging** | Opacity=0.85, Scale=1.03 | 현재 유지 | 현재 유지 | 기존 drag visual 유지 |

### 4.3 Theme Support

| 요소 | 리소스 | Dark/Light 자동 |
|------|--------|:-:|
| Active Background | `SubtleFillColorSecondaryBrush` | O |
| Hover Background | `SubtleFillColorSecondaryBrush` | O |
| Pressed Background | `SubtleFillColorTertiaryBrush` | O |
| Accent Bar Color | `SystemAccentColor` | O |
| Text Foreground | TextBlock 기본값 (테마 자동) | O |

모든 색상은 WinUI3 ThemeResource 사용 — 하드코딩 RGB 금지.

### 4.4 ScrollViewer

| 속성 | 값 | 근거 |
|------|-----|------|
| VerticalScrollBarVisibility | Auto | 탭 <10이면 숨김 |
| HorizontalScrollBarVisibility | Disabled | 수직 목록 전용 |
| 신규 탭 auto-scroll | `BringIntoView()` on new tab Grid | 스크롤 밖 생성 시 보이도록 |

### 4.5 참조 디자인 비교

| 요소 | Windows Terminal | VS Code Sidebar | cmux | GhostWin 결정 |
|------|-----------------|-----------------|------|---------------|
| Active indicator | 하단 accent bar | 좌측 accent bar + bold | 좌측 accent + bold | **좌측 accent bar 3 DIP + SemiBold** |
| Hover | 배경 밝아짐 | 배경 밝아짐 | 배경 밝아짐 | **SubtleFill 배경** |
| Close button | hover 시만 | hover 시만 | 항상 표시 | **항상 표시** |
| Text overflow | Ellipsis | Ellipsis | Truncate | **CharacterEllipsis** |
| Scroll | 수평 overflow → 화살표 | 수직 ScrollViewer | 수직 스크롤 | **수직 ScrollViewer Auto** |

---

## 5. Key Data Structures

### 5.1 TabItemUIRefs (신규)

```cpp
/// UI element references for in-place update (no element replacement)
/// WinRT value-type 멤버만 → Rule of Zero 적용
struct TabItemUIRefs {
    controls::Grid root{nullptr};
    controls::Border accent_bar{nullptr};          // active indicator (좌측 3 DIP)
    controls::TextBlock title_block{nullptr};
    controls::TextBlock cwd_block{nullptr};        // nullable — cwd 없으면 null
    controls::StackPanel text_panel{nullptr};       // cwd 동적 추가용
};
```

### 5.2 삭제 대상

```cpp
// 전부 삭제
controls::ListView list_view_{nullptr};
IObservableVector<IInspectable> items_source_{nullptr};
bool updating_selection_ = false;
struct SelectionGuard { ... };
```

---

## 6. cpp.md Compliance

### 6.1 Public API (≤ 7)

7개 유지 — initialize, root, request_new_tab, request_close_active, update_dpi, toggle_visibility, is_visible. ListView/StackPanel 전환은 private만 영향.

### 6.2 Function ≤ 40 lines

| Function | 예상 줄 수 | 분리 전략 |
|----------|-----------|----------|
| setup_tabs_panel() | ~18 | 안전 |
| create_tab_item_ui() → TabItemUIRefs 반환 | ~30 | 초과 시 apply_active_visual 분리 |
| on_session_activated() | ~15 | 안전 (in-place O(2)) |
| attach_drag_handlers() | **~91 (기존 위반)** | lambda → named private method 추출 |
| apply_active_visual() | ~8 | 안전 |
| append_tab() / remove_tab_at() | ~8 each | 안전 |

**attach_drag_handlers 분리 전략**: 4개 lambda를 named private method로 추출.
- `on_drag_pressed(SessionId, PointerRoutedEventArgs)`
- `on_drag_moved(UIElement, PointerRoutedEventArgs)`
- `on_drag_released(UIElement, PointerRoutedEventArgs)`
- `on_drag_canceled(UIElement, PointerRoutedEventArgs)`

### 6.3 Parameters ≤ 3

`create_tab_item_ui` 시그니처 변경:
```cpp
// Before: winui::UIElement create_tab_item_ui(const TabItemData& data);
// After:  TabItemUIRefs create_tab_item_ui(const TabItemData& data);
// TabItemUIRefs.root가 Grid를 포함 → 별도 out param 불필요, 파라미터 1개 유지
```

### 6.4 Rule of Zero

- `TabItemUIRefs`: WinRT value-type 멤버만 → 컴파일러 생성 special members 사용
- `TabSidebar`: copy deleted 유지, move/dtor 컴파일러 생성
- SelectionGuard 제거 → 새 RAII 불필요 (DragState는 PointerCanceled fallback으로 보장)

### 6.5 constexpr (매직넘버 추출)

```cpp
static constexpr double kAccentBarWidth = 3.0;
static constexpr double kTabItemMinHeight = 40.0;
static constexpr double kCwdOpacity = 0.6;
static constexpr double kDragLiftOpacity = 0.85;
static constexpr double kDragLiftScale = 1.03;
```

---

## 7. Error Handling

| 시나리오 | 대응 |
|----------|------|
| WinRT 요소 생성 실패 | create_tab_item_ui try/catch → items_ rollback, LOG_E |
| items_/item_refs_ 비동기화 | debug assert 선행, release: rebuild_list fallback |
| Drag 중 session close | on_session_closed에서 `drag_ = {}` 강제 취소 후 remove_tab_at |
| CWD 도착 시 탭 미초기화 | find_index nullopt → early return (safe) |
| 제거 순서 실패 | Children.RemoveAt 먼저 (UI), 실패 시 데이터 무변경 |

---

## 8. Implementation Order

```
Step 1:  TabItemUIRefs 구조체 + item_refs_ + constexpr 상수 (헤더)
Step 2:  find_index / append_tab / remove_tab_at atomic 헬퍼
Step 3:  setup_tabs_panel() (ScrollViewer + StackPanel, ListView 대체)
Step 4:  create_tab_item_ui → TabItemUIRefs 반환 + 3-column Grid + accent bar + TextTrimming
Step 5:  apply_active_visual / apply_inactive_visual + hover/pressed 핸들러
Step 6:  on_session_created → append_tab + apply_active_visual + BringIntoView
Step 7:  on_session_closed → drag 취소 + remove_tab_at
Step 8:  on_session_activated → in-place O(2) visual 전환
Step 9:  on_title_changed/on_cwd_changed → in-place TextBlock.Text() + cwd lazy 생성
Step 10: rebuild_list → item_refs_.root 재활용 Children 재구성
Step 11: attach_drag_handlers → lambda를 named method로 분리 + tabs_panel_ 기반
Step 12: SelectionGuard/updating_selection_/list_view_/items_source_ 전부 삭제
Step 13: 빌드 + 테스트 (T1~T11)
```

---

## 9. Test Plan

### 수동 테스트 체크리스트

- [ ] T1: 앱 시작 → 첫 탭 active visual (accent bar + Background + SemiBold) 확인
- [ ] T2: "+" 클릭 → 새 탭 생성, active visual 이동, 이전 탭 inactive 전환 확인
- [ ] T3: 기존 탭 클릭 → active visual 전환, 터미널 전환 확인
- [ ] T4: `echo test` → title 변경 시 active visual 유지 확인
- [ ] T5: `cd /tmp` → cwd 표시 갱신, active visual 유지 확인
- [ ] T6: 탭 드래그 재정렬 → DPI 100%/125%/150% 각각 정상 확인
- [ ] T7: 탭 × 닫기 → 다음 탭 자동 활성, 목록 정합성 확인
- [ ] T8: 마지막 탭 닫기 → 앱 종료 확인
- [ ] T9: Ctrl+Tab / Ctrl+1~9 → 탭 전환 정상
- [ ] T10: Ctrl+Shift+B → sidebar toggle + titlebar 영역 재계산 확인
- [ ] T11: 탭 10개+ 생성 → ScrollViewer 스크롤 + 신규 탭 BringIntoView 확인
- [ ] T12: hover → 배경 변경 확인, 마우스 나가면 복원 확인
- [ ] T13: 긴 제목(50자+) → TextTrimming ellipsis 확인

---

## 10. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| 탭 많을 때 스크롤 | LOW | ScrollViewer Auto + BringIntoView |
| Accent bar 레이아웃 | LOW | 3-column Grid, Col 0 Auto width |
| 드래그 좌표 계산 | LOW | list_view_ → tabs_panel_ 1:1 치환 |
| Accessibility | LOW | Phase 6+ 필요 시 AutomationProperties 수동 설정 |
| Migration regression | MED | 단일 커밋 전환 + git tag 복구 지점 |
| Tab grouping (미래) | LOW | nested StackPanel 또는 TreeView로 대응 가능 |
| attach_drag_handlers 91줄 | MED | Step 11에서 4개 named method로 분리 |

---

## 11. Lifecycle & Destruction Order

```
winui_app.h 멤버 선언 순서 (GhostWinApp):
  m_session_mgr (1st) → 소멸: 2nd (마지막) → mgr_ 포인터가 TabSidebar보다 오래 생존
  m_tab_sidebar (2nd) → 소멸: 1st (먼저)  → WinUI3 element 먼저 정리 → 안전
```

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.0 | 2026-04-04 | 10-agent 100% 합의 기반 초안. ListView→StackPanel 전환 계획 | 노수장 |
| 1.1 | 2026-04-04 | 5-agent 리뷰 반영: Visual Design Spec, cpp.md Compliance, Passive View 3계층, Event Flow, Triple-sync Invariant, Error Handling, Test Plan, constexpr 추출 | 노수장 |
