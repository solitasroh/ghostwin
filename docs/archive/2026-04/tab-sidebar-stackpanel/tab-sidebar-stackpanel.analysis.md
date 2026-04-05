# Tab Sidebar StackPanel Gap Analysis Report

> **분석 대상**: tab-sidebar-stackpanel (ListView -> StackPanel 전환)
> **디자인 문서**: `docs/02-design/features/tab-sidebar-stackpanel.design.md` (v1.0)
> **계획 문서**: `docs/01-plan/features/tab-sidebar-stackpanel.plan.md` (v1.1)
> **구현 경로**: `src/ui/tab_sidebar.h`, `src/ui/tab_sidebar.cpp`
> **분석 일자**: 2026-04-05
> **최신 커밋**: `a3e1846 feat: replace ListView with StackPanel, upgrade WinAppSDK 1.8`

---

## Overall Scores

| Category | Score | Status |
|----------|:-----:|:------:|
| Design Match (구조/기능) | 93% | ⚠️ |
| Architecture Compliance (Passive View, SSOT, Triple-sync) | 98% | ✅ |
| Convention Compliance (cpp.md, constexpr, 줄 수) | 88% | ⚠️ |
| Visual Spec Compliance (ThemeResource, 색상, 상태) | 78% | ⚠️ |
| **Overall** | **91%** | **⚠️** |

---

## 1. 삭제 목표 검증 (Plan 2.1)

| 삭제 항목 | 상태 | 근거 |
|-----------|:----:|------|
| `ListView list_view_` | ✅ | tab_sidebar.h에 없음 |
| `IObservableVector items_source_` | ✅ | tab_sidebar.h에 없음 |
| `SelectionGuard` 구조체 | ✅ | tab_sidebar.h에 없음 |
| `updating_selection_` 플래그 | ✅ | tab_sidebar.h에 없음 |
| `SelectionChanged` 핸들러 | ✅ | tab_sidebar.cpp에 없음 |
| `SelectedIndex` 수동 관리 (6곳) | ✅ | 모든 on_* 핸들러에서 제거 확인 |
| `setup_listview()` | ✅ | `setup_tabs_panel()`으로 대체 |
| `update_item` 템플릿 | ✅ | in-place 직접 갱신으로 대체 |

**삭제 목표: 8/8 완료 (100%)**

---

## 2. 추가 목표 검증 (Plan 2.2)

| 추가 항목 | 상태 | 근거 |
|-----------|:----:|------|
| `StackPanel tabs_panel_` | ✅ | tab_sidebar.h:83 |
| `ScrollViewer scroll_viewer_` | ✅ | tab_sidebar.h:82 |
| `TabItemUIRefs` 구조체 | ✅ | tab_sidebar.h:34-40, 5개 필드 모두 일치 |
| `item_refs_` 벡터 | ✅ | tab_sidebar.h:79 |
| 자체 active visual (accent bar + Background + SemiBold) | ⚠️ | 구현됨, 단 ThemeResource 미사용 (아래 3번 참조) |
| Hover visual | ⚠️ | 구현됨, 단 하드코딩 색상 (아래 3번 참조) |
| Pressed visual (`SubtleFillColorTertiaryBrush`) | ❌ | **미구현** — PointerPressed에서 배경색 변경 없음 |
| In-place TextBlock 갱신 | ✅ | on_title_changed/on_cwd_changed에서 직접 .Text() 설정 |
| TextTrimming(CharacterEllipsis) | ✅ | create_text_panel()에서 title_block, cwd_block 모두 적용 |
| Triple-sync atomic 헬퍼 | ✅ | append_tab, remove_tab_at 구현 + assert |
| `find_index(SessionId)` | ✅ | std::ranges::find_if, optional<size_t> 반환 |
| `apply_active_visual` / `apply_inactive_visual` | ✅ | 구현 완료 |
| Custom pointer drag (tabs_panel_ 기반) | ✅ | attach_drag_handlers가 tabs_panel_ 좌표 사용 |
| ScrollViewer.BringIntoView() | ✅ | on_session_created에서 `StartBringIntoView()` 호출 |

**추가 목표: 12/14 완료 (86%)**

---

## 3. Visual Spec 검증 (Plan 4.2 / Design 2.4)

### 3.1 상태별 Visual 비교

| 상태 | Design | Implementation | 일치 |
|------|--------|----------------|:----:|
| **Active Background** | `SubtleFillColorSecondaryBrush` (ThemeResource) | `ColorHelper::FromArgb(0x1A, 0xFF, 0xFF, 0xFF)` (하드코딩) | ❌ |
| **Active Accent Bar** | `SystemAccentColor` (ThemeResource) | `Colors::CornflowerBlue()` (하드코딩) | ❌ |
| **Active FontWeight** | SemiBold | SemiBold | ✅ |
| **Active Accent Bar Visible** | Visible | Visible | ✅ |
| **Inactive Background** | Transparent (nullptr) | `Colors::Transparent()` (SolidColorBrush) | ⚠️ |
| **Inactive FontWeight** | Normal | Normal | ✅ |
| **Inactive Accent Bar** | Collapsed | Collapsed | ✅ |
| **Hover Background** | `SubtleFillColorSecondaryBrush` | `FromArgb(0x0F, 0xFF, 0xFF, 0xFF)` (하드코딩) | ❌ |
| **Pressed Background** | `SubtleFillColorTertiaryBrush` | **미구현** | ❌ |
| **Dragging Opacity** | 0.85 | 0.85 (`kDragLiftOpacity`) | ✅ |
| **Dragging Scale** | 1.03 | 1.03 (`kDragLiftScale`) | ✅ |

### 3.2 Theme Support 비교

| 요소 | Design | Implementation | 영향 |
|------|--------|----------------|------|
| 모든 색상 WinUI3 ThemeResource 사용 | ThemeResource 필수, 하드코딩 RGB 금지 | 4곳 하드코딩 (accent, active bg, hover bg, inactive bg) | **High** — Dark/Light 테마 전환 시 색상 고정 |

**Visual Spec: 7/11 일치 (64%)**

> Note: Inactive Background를 `nullptr` 대신 `Transparent()` SolidColorBrush로 설정한 것은 hit-test 유지를 위한 의도적 변경으로 판단됨 (코드 주석에 명시). 이는 합리적 기술 결정이나 Design 문서에는 미반영.

---

## 4. Architecture Compliance 검증

### 4.1 Passive View 3계층 분리

| 계층 | 역할 | 상태 |
|------|------|:----:|
| SessionManager (Domain) | 세션 생명주기 | ✅ |
| GhostWinApp (Mediator) | 이벤트 라우팅 | ✅ — friend on_* 호출만 사용 |
| TabSidebar (Passive View) | UI 렌더링 | ✅ — binding 없음, 명시적 setter |

### 4.2 Single Source of Truth

| 항목 | 상태 |
|------|:----:|
| `items_` = data truth | ✅ |
| `item_refs_` = view cache, 1:1 | ✅ |
| `tabs_panel_.Children()` = rendered UI, 1:1 | ✅ |

### 4.3 Triple-sync Invariant

| 헬퍼 | 구현 | assert |
|------|:----:|:------:|
| `append_tab` | ✅ | ✅ `assert(items_.size() == item_refs_.size())` |
| `remove_tab_at` | ✅ | ✅ |
| `rebuild_list` | ✅ | ⚠️ assert 없음 (Design에서도 명시하지 않음) |

제거 순서: UI -> refs -> data (Design 일치)

### 4.4 이벤트 흐름

| 이벤트 | Design | Implementation | 일치 |
|--------|--------|----------------|:----:|
| Session Created | append_tab + apply_active + BringIntoView | 동일 | ✅ |
| Title Changed | find_index + in-place Text() | 동일 | ✅ |
| Tab Click | PointerReleased (not drag) -> activate | 동일 | ✅ |
| Drag Reorder | items_ + item_refs_ 동시 이동 + rebuild_list | 동일 | ✅ |
| Session Closed | drag 취소 + remove_tab_at | 동일 | ✅ |
| Session Activated | O(n) scan, O(2) visual change | 동일 | ✅ |
| CWD Changed | find_index + lazy cwd_block 생성 | 동일 | ✅ |

**Architecture Compliance: 98%**

---

## 5. Header Definition 검증 (Design 1.2 / 1.3)

### 5.1 구조체/멤버

| 항목 | Design | Implementation | 일치 |
|------|--------|----------------|:----:|
| TabItemUIRefs 5개 필드 | root, accent_bar, title_block, cwd_block, text_panel | 동일 | ✅ |
| items_ | `vector<TabItemData>` | 동일 | ✅ |
| item_refs_ | `vector<TabItemUIRefs>` | 동일 | ✅ |
| scroll_viewer_ | `ScrollViewer` | 동일 | ✅ |
| tabs_panel_ | `StackPanel` | 동일 | ✅ |
| root_panel_ 타입 | Design: `StackPanel` (Plan 3.2) | **Implementation: `Grid`** | ⚠️ |

### 5.2 root_panel_ 타입 차이

Design/Plan 문서에서 `root_panel_`은 `StackPanel`으로 기술되어 있으나, 구현에서는 `controls::Grid`로 변경됨. 이유: StackPanel은 무한 높이를 제공하여 ScrollViewer가 스크롤할 수 없는 문제가 있음 (코드 주석: "StackPanel gives infinite height -> ScrollViewer can't scroll"). Grid의 Star row를 사용해야 ScrollViewer가 제한된 높이 내에서 동작함.

**판정**: 합리적 기술 결정이나 Design 문서 업데이트 필요.

### 5.3 Public API (7개)

| API | Design | Implementation | 일치 |
|-----|--------|----------------|:----:|
| initialize | ✅ | ✅ | ✅ |
| root | ✅ | ✅ | ✅ |
| request_new_tab | ✅ | ✅ | ✅ |
| request_close_active | ✅ | ✅ | ✅ |
| update_dpi | ✅ | ✅ | ✅ |
| toggle_visibility | ✅ | ✅ | ✅ |
| is_visible | ✅ | ✅ | ✅ |

### 5.4 Private Methods

| Method | Design | Implementation | 일치 |
|--------|--------|----------------|:----:|
| setup_tabs_panel | ✅ | ✅ | ✅ |
| setup_add_button | ✅ | ✅ | ✅ |
| create_tab_item_ui -> TabItemUIRefs | ✅ | ✅ | ✅ |
| create_text_panel | 암시적 (Design 2.3) | ✅ 명시적 선언 | ✅ |
| create_close_button | 암시적 (Design 2.2) | ✅ 명시적 선언 | ✅ |
| apply_active_visual | ✅ | ✅ | ✅ |
| apply_inactive_visual | ✅ | ✅ | ✅ |
| append_tab | ✅ | ✅ | ✅ |
| remove_tab_at | ✅ | ✅ | ✅ |
| find_index | ✅ | ✅ | ✅ |
| attach_drag_handlers | ✅ | ✅ | ✅ |
| attach_hover_handlers | Design: `(Grid, size_t idx)` | Implementation: `(Grid)` | ⚠️ |
| apply_drag_reorder | ✅ | ✅ | ✅ |
| reset_drag_visuals | ✅ | ✅ | ✅ |
| calc_insert_index | ✅ | ✅ | ✅ |
| rebuild_list | ✅ | ✅ | ✅ |
| sidebar_width | ✅ | ✅ | ✅ |

`attach_hover_handlers` 시그니처 차이: Design은 `size_t idx` 파라미터를 포함하지만 구현에서는 Tag 기반 SessionId 조회로 변경하여 idx 파라미터가 불필요해짐. 이는 개선 사항.

### 5.5 constexpr 상수

| 상수 | Design | Implementation | 일치 |
|------|--------|----------------|:----:|
| kBaseWidth (220.0) | ✅ | ✅ | ✅ |
| kDragThreshold (5.0f) | ✅ | ✅ | ✅ |
| kAccentBarWidth (3.0) | ✅ | ✅ | ✅ |
| kTabItemMinHeight (40.0) | ✅ | ✅ | ✅ |
| kCwdOpacity (0.6) | ✅ | ✅ | ✅ |
| kDragLiftOpacity (0.85) | ✅ | ✅ | ✅ |
| kDragLiftScale (1.03) | ✅ | ✅ | ✅ |

**7/7 constexpr 완전 일치**

---

## 6. Convention Compliance (cpp.md)

### 6.1 Function 줄 수 (<=40 lines)

| Function | 줄 수 | 상태 |
|----------|------:|:----:|
| setup_tabs_panel | 16 | ✅ |
| setup_add_button | 7 | ✅ |
| initialize | 30 | ✅ |
| create_tab_item_ui | 39 | ✅ |
| create_text_panel | 21 | ✅ |
| create_close_button | 10 | ✅ |
| apply_active_visual | 8 | ✅ |
| apply_inactive_visual | 7 | ✅ |
| append_tab | 7 | ✅ |
| remove_tab_at | 7 | ✅ |
| find_index | 6 | ✅ |
| attach_hover_handlers | 20 | ✅ |
| **attach_drag_handlers** | **82** | **❌** |
| reset_drag_visuals | 6 | ✅ |
| calc_insert_index | 8 | ✅ |
| apply_drag_reorder | 18 | ✅ |
| rebuild_list | 6 | ✅ |
| on_session_created | 18 | ✅ |
| on_session_closed | 10 | ✅ |
| on_session_activated | 12 | ✅ |
| on_title_changed | 8 | ✅ |
| on_cwd_changed | 15 | ✅ |

Plan 6.2에서 attach_drag_handlers를 4개 named private method로 분리하겠다고 명시했으나 미수행.

### 6.2 Include 잔여

| Include | Design 지시 | Implementation | 상태 |
|---------|------------|----------------|:----:|
| `Windows.Foundation.Collections.h` | 삭제 | **잔존** (tab_sidebar.h:18) | ❌ |
| `Windows.UI.Xaml.Interop.h` | 삭제 | 삭제됨 | ✅ |
| `<optional>` | 유지/추가 | ✅ | ✅ |

---

## 7. 파일 변경 범위 검증 (Design 3.2)

| 파일 | Design: 변경 없음 | 실제 | 일치 |
|------|:--:|-------|:----:|
| src/app/winui_app.h | ✅ | 확인 불가 (미검사) | - |
| src/app/winui_app.cpp | ✅ | list_view_ 직접 참조 0건, friend on_* 호출만 | ✅ |
| CMakeLists.txt | ✅ | 파일 추가/삭제 없음 | ✅ |
| src/ui/titlebar_manager.* | ✅ | sidebar_width_fn 경로 불변 | ✅ |

---

## Differences Found

### Missing Features (Design O, Implementation X)

| # | Item | Design Location | Description | Impact |
|---|------|-----------------|-------------|--------|
| 1 | Pressed visual | Design 2.4 / Plan 4.2 | `SubtleFillColorTertiaryBrush` 배경색이 PointerPressed 시점에 적용되지 않음 | Low |
| 2 | attach_drag_handlers 분리 | Plan 6.2 | 4개 named private method 추출 미수행 (82줄 인라인 lambda 유지) | Medium |
| 3 | Include 정리 | Design 4 | `Windows.Foundation.Collections.h` 불필요 include 잔존 | Low |

### Changed Features (Design != Implementation)

| # | Item | Design | Implementation | Impact | 판정 |
|---|------|--------|----------------|--------|------|
| 1 | Active Background | `SubtleFillColorSecondaryBrush` (ThemeResource) | `FromArgb(0x1A, 0xFF, 0xFF, 0xFF)` | **High** | Dark theme에서 흰색 반투명이 의도와 다를 수 있음 |
| 2 | Accent Bar Color | `SystemAccentColor` (ThemeResource) | `Colors::CornflowerBlue()` 고정 | **Medium** | 사용자 Windows 테마 색상 미반영 |
| 3 | Hover Background | `SubtleFillColorSecondaryBrush` (ThemeResource) | `FromArgb(0x0F, 0xFF, 0xFF, 0xFF)` | **Medium** | Light theme에서 거의 안 보일 수 있음 |
| 4 | Inactive Background | `nullptr` (transparent) | `SolidColorBrush(Transparent)` | **None** | 의도적 변경 (hit-test 유지, 코드 주석 명시) |
| 5 | root_panel_ 타입 | `StackPanel` | `Grid` (2-row) | **None** | 의도적 변경 (ScrollViewer 높이 제한 필요, 코드 주석 명시) |
| 6 | attach_hover_handlers 시그니처 | `(Grid, size_t idx)` | `(Grid)` | **None** | 개선 — Tag 기반 조회로 idx 파라미터 불필요 |

### Added Features (Design X, Implementation O)

| # | Item | Location | Description |
|---|------|----------|-------------|
| 1 | tabs_panel_ Transparent Background | tab_sidebar.cpp:30-31 | hit-test 활성화를 위한 투명 배경 (Design 미기술) |
| 2 | ScrollMode 명시 설정 | tab_sidebar.cpp:37-38 | VerticalScrollMode::Enabled, HorizontalScrollMode::Disabled |
| 3 | LOG_I/LOG_W 호출 | 복수 위치 | 런타임 디버깅용 로그 (Design 미기술, 합리적 추가) |

---

## Score Calculation

### Design Match (93%)

- 삭제 목표: 8/8 = 100%
- 추가 목표: 12/14 = 86% (Pressed visual 미구현, ThemeResource 미사용)
- 이벤트 흐름: 7/7 = 100%
- 구조체/멤버: 16/17 = 94% (root_panel_ 타입 변경은 합리적)
- 가중 평균: **93%**

### Architecture Compliance (98%)

- Passive View 3계층: 100%
- Single Source of Truth: 100%
- Triple-sync: 100% (rebuild_list assert 미포함은 Design과 동일)
- 이벤트 흐름: 100%
- 감점: root_panel_ 타입 변경을 Design 미반영 (-2%)

### Convention Compliance (88%)

- constexpr: 7/7 = 100%
- Public API 7개 유지: 100%
- 함수 줄 수: 21/22 = 95% (attach_drag_handlers 82줄 위반)
- Include 정리: 1/2 = 50%
- 가중 평균: **88%**

### Visual Spec Compliance (78%)

- 상태별 Visual: 7/11 = 64%
- ThemeResource 사용: 0/4 = 0% (4곳 모두 하드코딩)
- Dragging visual: 2/2 = 100%
- TextTrimming: 100%
- Tab Item Layout (3-column Grid): 100%
- 가중 평균: **78%**

---

## Recommended Actions

### Immediate Actions (Match Rate 향상)

1. **ThemeResource 전환 (High)**: `apply_active_visual`, `attach_hover_handlers`에서 하드코딩 색상을 WinUI3 ThemeResource로 교체. Dark/Light 테마 자동 전환 보장.
   - `FromArgb(0x1A, 0xFF, 0xFF, 0xFF)` -> `SubtleFillColorSecondaryBrush`
   - `Colors::CornflowerBlue()` -> `SystemAccentColor`
   - `FromArgb(0x0F, 0xFF, 0xFF, 0xFF)` -> `SubtleFillColorSecondaryBrush`

2. **Pressed visual 추가 (Low)**: PointerPressed 시점에 `SubtleFillColorTertiaryBrush` 배경 적용 (drag threshold 미달 시).

3. **불필요 include 제거 (Low)**: `Windows.Foundation.Collections.h` 삭제 — IObservableVector 미사용.

### Deferred Actions (다음 이터레이션)

4. **attach_drag_handlers 분리 (Medium)**: Plan 6.2에 명시된 4개 named private method 추출. 현재 82줄 -> 목표 4x20줄.

### Documentation Update Needed

5. **root_panel_ 타입 변경 반영**: Plan 3.2와 Design 1.3의 `StackPanel` -> `Grid` 변경 사유 문서화.
6. **Inactive Background 의도적 변경 기록**: `nullptr` -> `Transparent()` SolidColorBrush 변경 사유 (hit-test 유지).
7. **attach_hover_handlers 시그니처 변경 반영**: `(Grid, size_t idx)` -> `(Grid)` 변경 반영.

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.0 | 2026-04-05 | Plan v1.1 + Design v1.0 기반 초기 Gap Analysis | 노수장 |
