# M-16-D Plan v0.1 — cmux UX 패리티 (ContextMenu 4영역 + 워크스페이스 DragDrop)

> **한 줄 요약**: PRD 의 16개 FR + 10개 NFR 을 **3 Phase 6.5-7 작업일 + 8-10 commit** 으로 분할. Phase A (ContextMenu 4영역) → Phase B (워크스페이스 DragDrop A) → Phase C (Settings 토글 + dogfooding 정리).

> **출처**: `docs/00-pm/m16-d-context-menu.prd.md` (PM Lead 단독 합성, 2026-04-29) + 코드 직접 grep+Read 검증 (feedback_pdca_doc_codebase_verification.md 준수).

---

## Executive Summary (4-perspective)

| 관점 | 내용 |
|---|---|
| **Problem** | 우클릭이 4영역 (Sidebar/Terminal/Pane/Notification) 어디서도 동작 안 함 + 워크스페이스 사이드바 드래그 재정렬 불가. cmux 이주자 1분 컷. |
| **Solution** | 표준 WPF `ContextMenu` (Style 1개 + ItemsSource 4개) + 표준 `AllowDrop` + `DataObject` + Adorner. 외부 NuGet 0, ghostty fork patch 0. M-16-A 디자인 토큰 그대로 재사용. |
| **Function/UX Effect** | 4영역 모두 우클릭 메뉴 즉시 표시 (< 16ms) / 워크스페이스 드래그 재정렬 < 100ms / Sidebar in-place Rename / `Open in VS Code` cwd 전달 / Pane Zoom 토글. |
| **Core Value** | 비전 1 (cmux 패리티) 의 "감성 도달" 한 단계 진전. M-14 reader 안전 + M-15 idle p95 7.79ms 보존. |

---

## 1. 진입 조건 (Entry Gates)

| 조건 | 상태 | 근거 |
|---|:-:|---|
| M-16-A 디자인 시스템 archived | ✅ | 2026-04-29, 96% Match Rate |
| M-16-B 윈도우 셸 archived | ✅ | 2026-04-29, 92% Match Rate |
| M-16-C 터미널 렌더 archived | ✅ | 2026-04-30, 92% Match Rate |
| 빌드 0 warning Debug+Release | ✅ | M-16-C commit 09e2e69 검증 완료 |
| 코드 검증 (grep+Read 직접) | ✅ | 본 Plan §3 코드 fact 표 |

---

## 2. 코드 fact 검증 (직접 grep+Read)

PRD 의 추정 명명/경로/메서드를 모두 코드로 검증. **추측 항목은 본 표에 명시**:

| PRD 주장 | 코드 검증 | 결과 |
|---|---|---|
| `WM_RBUTTONDOWN` 캡처는 있으나 ContextMenu 핸들러 미구현 | `TerminalHostControl.cs:195, 483, 490, 497-498, 523-524` 우클릭 WM_ 5 위치 + ghostty mouse encode 호출만 | ✅ Fact |
| `WorkspaceService.MoveWorkspace` API 부재 | `WorkspaceService.cs` 에 MoveWorkspace/ReorderWorkspace 정의 0건. `Workspaces` 는 `IReadOnlyList<WorkspaceInfo>` (line 34) `_orderedWorkspaces` (line 24) 가 backing | ✅ Fact (신규 필요) |
| `ContextMenu` / `AllowDrop` / `DataObject` 사용 0건 | `src/GhostWin.App` 전체 grep 0 hit | ✅ Fact |
| `WorkspaceItemViewModel` ObservableObject + RelayCommand 사용 가능 | `partial class WorkspaceItemViewModel : ObservableObject, IDisposable` (line 8) | ✅ Fact (CommunityToolkit.Mvvm.Input.RelayCommand 즉시 사용 가능) |
| `IPaneLayoutService` interface 존재 | `src/GhostWin.Core/Interfaces/IPaneLayoutService.cs:5` | ✅ Fact (`ZoomPane` 신규 메서드 추가 자리 명확) |
| M-16-A 디자인 토큰 (`Surface.Brush`, `Border.Brush`, `Spacing.MD`, `Text.Primary.Brush`, `Accent.Primary.Brush`) | `src/GhostWin.App/Themes/Colors.Dark.xaml` + `Spacing.xaml` + M-16-A archived | ✅ Fact (재사용) |
| `NotificationPanelControl.xaml` ListBox 가 우클릭 대상 | `src/GhostWin.App/Controls/NotificationPanelControl.xaml` 존재 (Grep 결과) | ✅ Fact |
| WPF `ContextMenu` 표시 latency < 16ms | Microsoft Learn 명시 없음 — 측정 미시행 | 🟡 **추측** (NFR-04 로 검증 필요) |
| cmux v0.60.0 의 Tab context menu 항목 정확한 리스트 | manaflow-ai/cmux changelog 직접 확인 미시행 | 🟡 **추측** (cmux 항목은 PRD 의 "Rename / Close / Move / Pin / Edit Description / Mark All Read" 가 정확한지 dogfooding 확인 필요) |

---

## 3. Phase 분할 (3 Phase, 6.5-7 작업일)

### Phase A — ContextMenu 4영역 (3.5 작업일, 4-5 commits)

**범위**: FR-01..FR-12 (12 FR). 4영역 모두에서 우클릭 → 표준 메뉴 표시.

| 단계 | 작업 | 산출물 | commit |
|:-:|---|---|:-:|
| A1 | `App.xaml` 에 `ContextMenu` Style + `MenuItem` Style 정의 (M-16-A 토큰 사용 — `Surface.Brush` / `Border.Brush` / `Spacing.MD` / `Text.Primary.Brush`). FocusVisualStyle 적용. | `App.xaml` 수정 | **A1**: feat: contextmenu base style |
| A2 | Sidebar `ListBoxItem.ContextMenu` + `WorkspaceItemViewModel` 명령 7개 (`RenameWorkspaceCommand`, `EditDescriptionCommand`, `PinWorkspaceCommand`, `MoveUpCommand`, `MoveDownCommand`, `MarkAllReadCommand`, `CloseWorkspaceCommand`). 명령은 `IWorkspaceService` API 호출. `MoveUp/Down` 은 `MoveWorkspace(id, newIndex)` 사용 (Phase B 에서 신규 추가, A2 에서는 stub 가능 — Phase B commit 으로 활성). | `MainWindow.xaml` + `WorkspaceItemViewModel.cs` + `IWorkspaceService.cs` (`MoveWorkspace` signature 만 추가, 구현은 B1) | **A2**: feat: sidebar contextmenu + workspace commands |
| A3 | Sidebar Rename in-place — `IsRenaming` ObservableProperty + `ListBoxItem` template 의 `TextBox` Visibility binding + Enter 확정 / Esc 취소 / LostFocus 자동 확정 | `MainWindow.xaml` template + `WorkspaceItemViewModel.IsRenaming` 추가 | **A3**: feat: sidebar inline rename |
| A4 | TerminalHostControl 우클릭 분기 — `WM_RBUTTONUP` 시 D1-A 분기: ghostty mouse encoder 가 활성 모드면 mouse encode (현 동작 보존), 비활성이면 ContextMenu 띄움. ContextMenu 항목 7개 (`Copy` / `Paste` / `Select All` / `Clear Scrollback` / `Open in VS Code` / `Open in Cursor` / `Open in Explorer`). 외부 launcher helper 신규 (`ExternalLauncher.cs` — `Process.Start` + cwd 전달). PATH 부재 시 항목 disabled (FR-07). | `TerminalHostControl.cs` + 신규 `Helpers/ExternalLauncher.cs` | **A4**: feat: terminal contextmenu + external launchers |
| A5 | Pane 영역 (leaf Border) `ContextMenu` — `Split Vertical` / `Split Horizontal` / `Close Pane` / `Zoom Pane` / `Move to Adjacent`. `IPaneLayoutService.ZoomPane(uint paneId)` 신규 메서드 (M-14 reader 안전 보존 — 다른 pane 의 HwndHost 는 destroy 가 아닌 `Visibility=Collapsed` 토글). + 알림 패널 `ContextMenu` (`Mark Read` / `Dismiss` / `Jump`). | `PaneContainerControl.cs` + `IPaneLayoutService.cs` + `PaneLayoutService.cs` + `NotificationPanelControl.xaml` | **A5**: feat: pane and notification contextmenus |

**A 종료 기준**:
- 4영역 우클릭 → 메뉴 표시 (수동 dogfooding)
- 모든 MenuItem `AutomationProperties.Name` 부여 (FR-11)
- VK_APPS / Shift+F10 키보드 호출 (FR-12) — WPF 기본 동작 신뢰
- 빌드 0 warning Debug+Release

---

### Phase B — 워크스페이스 DragDrop A (2 작업일, 2-3 commits)

**범위**: FR-13..FR-16 (4 FR). 사이드바 ListBox 드래그 재정렬.

| 단계 | 작업 | 산출물 | commit |
|:-:|---|---|:-:|
| B1 | `WorkspaceService.MoveWorkspace(uint id, int newIndex)` 구현 — `_orderedWorkspaces` List 의 entry 인스턴스 **유지** + 순서만 swap. `WorkspaceReorderedMessage` publish (CommunityToolkit Messenger). | `WorkspaceService.cs` + `IWorkspaceService.cs` + `Core/Events/WorkspaceReorderedMessage.cs` (신규) | **B1**: feat: workspaceservice move api |
| B2 | Sidebar ListBox `AllowDrop=True` + `PreviewMouseLeftButtonDown` (4px threshold) + `DragDrop.DoDragDrop(DataObject(workspaceId))`. Drop indicator Adorner 1개 (`WorkspaceDropAdorner.cs` — 1px `Accent.Primary.Brush` 가로 막대). drop 시 `MoveWorkspace` 호출 + `IsHitTestVisible=False` 보호 (Risk-2). | `MainWindow.xaml.cs` + 신규 `WorkspaceDropAdorner.cs` | **B2**: feat: sidebar drag reorder |
| B3 | Settings 자동 동기화 — `MoveWorkspace` 호출 시 `ISettingsService.Save()` 트리거. 다음 시작 시 `_orderedWorkspaces` 순서 복원 (`session.json` 의 `Workspaces` 배열 순서가 source-of-truth — 기존 `SessionSnapshotService` 가 직렬화). 코드 변경 최소 (이미 SessionSnapshot 이 순서 보존). | `SettingsService.cs` 또는 `SessionSnapshotService.cs` 의 trigger 1줄 | **B3**: feat: persist workspace order |

**B 종료 기준**:
- 워크스페이스 3개 + 드래그 100회 / engine crash 0건 / atlas swap 회귀 0건 (Risk-2 수용 기준)
- Drop indicator 시각 확인 (D4-A)
- 재시작 후 순서 복원

---

### Phase C — Settings 토글 + 검증 (1 작업일, 1-2 commits)

**범위**: 마라톤 정리 + dogfooding.

| 단계 | 작업 | 산출물 | commit |
|:-:|---|---|:-:|
| C1 | Settings `Force Terminal ContextMenu` 토글 (D1-A default 보존, D1-B 옵션 — Risk-1 후속) + AppSettings `Terminal.ForceContextMenu: bool` 추가 + SettingsPage ComboBox / Checkbox. | `AppSettings.cs` + `SettingsPageViewModel.cs` + `SettingsPageControl.xaml` | **C1**: feat: terminal contextmenu toggle |
| C2 | `Open in VS Code / Cursor / Explorer` 의 PATH 자동 감지 helper 보강 — `where.exe code` / `where.exe cursor` 시 disabled 결정. (D5-A default — PATH 만, 추가 검색 비목표). 1줄 단위 통합. | `ExternalLauncher.cs` 또는 viewmodel | **C2**: chore: launcher path probe |

**C 종료 기준**:
- 자체 dogfooding 5명 5분 시나리오 (4/5 PASS NFR-09)
- 빌드 0 warning Debug+Release
- M-15 idle p95 회귀 0건 (선택 측정, NFR-10)

---

## 4. FR/NFR 매핑 표

### FR (16개)

| FR | Phase | commit | 비고 |
|:-:|:-:|:-:|---|
| FR-01 ContextMenu Style 1개 | A | A1 | M-16-A 토큰만 사용 |
| FR-02 Sidebar ContextMenu 7항목 | A | A2 | |
| FR-03 7 RelayCommand | A | A2 | `MoveUp/Down` 만 B1 후 활성 |
| FR-04 Rename in-place | A | A3 | D3-A inline TextBox |
| FR-05 Terminal RBUTTONUP 분기 | A | A4 | D1-A 분기 |
| FR-06 Terminal 7항목 | A | A4 | |
| FR-07 외부 launcher cwd 전달 | A | A4 | D5-A PATH only |
| FR-08 Pane 4-5항목 | A | A5 | |
| FR-09 ZoomPane | A | A5 | M-14 reader 안전 (Visibility 토글) |
| FR-10 Notification 3항목 | A | A5 | |
| FR-11 AutomationProperties.Name 모두 | A | A1-A5 분산 | E2E UIA inspection |
| FR-12 VK_APPS / Shift+F10 | A | (built-in) | WPF 기본 |
| FR-13 ListBox AllowDrop | B | B2 | |
| FR-14 Drop indicator Adorner | B | B2 | D4-A 1px |
| FR-15 MoveWorkspace API | B | B1 | 신규 |
| FR-16 Settings 동기화 | B | B3 | session.json 직렬화 |

### NFR (10개)

| NFR | 검증 | Phase |
|:-:|---|:-:|
| NFR-01 M-14 reader 안전 | atlas swap 회귀 케이스 + ZoomPane Visibility | A5 + 회귀 |
| NFR-02 0 ghostty fork patch | git diff submodule | 전체 |
| NFR-03 0 warning Debug+Release | msbuild | 전체 |
| NFR-04 ContextMenu < 16ms | M-15 인프라 (선택 측정) | C 또는 후속 |
| NFR-05 모든 MenuItem AutomationProperties | E2E UIA | A1-A5 |
| NFR-06 DragDrop < 100ms | DispatcherTimer | B2 |
| NFR-07 mouse encoder 회귀 0건 | vim/tmux 수동 | A4 |
| NFR-08 E2E AutomationId 회귀 0건 | dotnet test | 전체 |
| NFR-09 HwndHost race 0건 | 100회 드래그 dogfooding | B2 |
| NFR-10 idle p95 7.79ms 보존 | M-15 비교 (선택) | C 또는 후속 |

---

## 5. Risks & 대안

### Risk-1: 터미널 우클릭 vs ghostty mouse encoder

**완화책**: D1-A default — `ghostty_mouse_encoder` 활성 모드 (`vt_bridge_mode_get(MOUSE_ANY=1003 / SGR=1006 / X10=9 등)`) 시 mouse encode 우선, 비활성이면 ContextMenu. C1 commit 으로 Settings 토글 추가 (사용자가 "Always ContextMenu" 강제 가능).

**검증**: vim visual mode + tmux mouse mode 에서 우클릭 → escape sequence 정상 전달 (수동 dogfooding NFR-07).

### Risk-2: 워크스페이스 드래그 시 HwndHost 재생성 race

**완화책 (3중)**:
1. `MoveWorkspace` 는 `_orderedWorkspaces` List 의 **entry 인스턴스 유지** + 순서만 swap (entry 재생성 금지)
2. ListBox `VirtualizingStackPanel.IsVirtualizing=False` (Sidebar 는 보통 < 20 항목, 비가상화 비용 미미)
3. 드래그 중 `PaneContainerControl.IsHitTestVisible=False` 로 보호

**검증**: 100회 드래그 / engine crash 0건 / atlas swap 회귀 0건 (NFR-09).

### Risk-3: VK_APPS / Shift+F10 비대응

**완화책**: WPF `ContextMenu` 가 ItemsSource 기반이면 자동 대응. dogfooding 시 1회 확인.

**Fallback**: 만약 비대응이면 `KeyDown` handler 추가 (1줄).

### Risk-4: WPF ContextMenu styling 의 M-16-A 토큰 호환성

**완화책**: `Wpf.Ui.Controls.MenuItem` vs standard `MenuItem` 둘 다 후보. M-16-A 토큰은 `DynamicResource` 이므로 어느 쪽이든 호환. A1 commit 시 결정.

---

## 6. 사용자 결정 (D1-D5 default)

> **마라톤 모드 가능** — 사용자가 "default 진행" 한 번에 모든 결정 적용.

| ID | 결정 | **Default** | Phase 영향 |
|:-:|---|:---:|:-:|
| **D1** | 터미널 ContextMenu vs mouse encoder | **A**: encoder 활성 시 encoder 우선 (cmux/iTerm2 표준) | A4 |
| **D2** | "Mark All Read" 정의 | **A**: 해당 워크스페이스만 read 표시 | A2 |
| **D3** | Sidebar Rename UX | **A**: ListBoxItem inline TextBox | A3 |
| **D4** | Drop indicator 시각 | **A**: 1px Accent.Primary.Brush 가로 막대 (Adorner) | B2 |
| **D5** | 외부 launcher PATH 자동 감지 | **A**: PATH 만 (없으면 disabled) | A4, C2 |

---

## 7. commit 분할 계획 (8-10 commits)

```
A1 feat: contextmenu base style
A2 feat: sidebar contextmenu + workspace commands
A3 feat: sidebar inline rename
A4 feat: terminal contextmenu + external launchers
A5 feat: pane and notification contextmenus
B1 feat: workspaceservice move api
B2 feat: sidebar drag reorder
B3 feat: persist workspace order
C1 feat: terminal contextmenu toggle
C2 chore: launcher path probe
```

---

## 8. 다음 단계

```
/pdca design m16-d-context-menu
```

Design 문서는 본 Plan 의 Phase A/B/C 단계를 D-01..D-15 정도의 architectural decision 으로 분해.

마라톤 모드를 원하면:
- D1-D5 default 적용 확인
- `/pdca design` → `/pdca do` → 자동 commit chain → `/pdca analyze` → `/pdca report` → `/pdca archive --summary`

---

## 첨부 — Reference

- **PRD**: `docs/00-pm/m16-d-context-menu.prd.md`
- **선행 마일스톤**:
  - M-16-A 디자인 시스템 (96%, archived 2026-04-29) — 토큰 재사용
  - M-16-B 윈도우 셸 (92%, archived 2026-04-29) — GridSplitter / FluentWindow 호환
  - M-16-C 터미널 렌더 (92%, archived 2026-04-30) — `gw_session_get_pixel_padding` API 활용
- **코드 fact 위치**: `TerminalHostControl.cs:195` / `WorkspaceService.cs:24, 34, 77, 107` / `IPaneLayoutService.cs:5` / `WorkspaceItemViewModel.cs:8`
- **메모리**: `feedback_pdca_doc_codebase_verification.md` (PRD/Plan 추측 명명을 코드 grep+Read 로 검증)
