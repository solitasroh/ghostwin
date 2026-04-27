# e2e-mq7-workspace-click Design Document

> **Summary**: 사이드바 클릭 workspace 전환 regression 진단 및 수정 설계
>
> **Project**: GhostWin Terminal
> **Author**: Claude + 노수장
> **Date**: 2026-04-10
> **Status**: RCA Confirmed (H1)
> **Planning Doc**: [e2e-mq7-workspace-click.plan.md](../../01-plan/features/e2e-mq7-workspace-click.plan.md)

---

## 0. Constraints & Locks

| ID | Constraint | Rationale |
|----|-----------|-----------|
| C-1 | `click_at()`의 `SendInput` 방식 유지 | MQ-4 mouse focus에서 검증 완료된 방식 — PostMessage 는 H-RCA4 문제 있음 |
| C-2 | Workspace 전환 메시징 체인 구조 변경 금지 | `OnSelectedWorkspaceChanged → ActivateWorkspace → WorkspaceActivatedMessage → SwitchToWorkspace` 체인은 Phase 5-E에서 arch-lock |
| C-3 | 최소 변경 원칙 | 진단 우선, 코드 수정은 근본 원인 확정 후에만 |

---

## 1. Overview

### 1.1 Design Goals

1. **Cascade 여부 판정**: MQ-1 fix (HC-4) + split-content-loss-v2 fix (sessionId 가드 제거) 적용 후 MQ-7이 자동 해소되었는지 확인
2. **독립 regression 수정**: cascade가 아닌 경우 근본 원인을 진단하고 최소 변경으로 수정
3. **E2E 회귀 방지**: MQ-1~MQ-8 전체 시나리오 PASS 유지

### 1.2 Design Principles

- 근거 기반 문제 해결 (behavior.md §우회 금지)
- 참조 구현 패턴 준수 (WPF ListBox 표준 바인딩)
- 진단 → 확정 → 수정 순서 엄격 준수

---

## 2. Root Cause Analysis

### 2.1 현재 workspace 전환 체인

```
┌─────────────────────────────────────────────────────────┐
│ E2E Operator: SendInput mouse click at (80, 150)        │
│   ↓ Win32 input injection (physical mouse move)         │
│ WPF hit-test → ListBox.SelectedItem binding 갱신        │
│   ↓                                                     │
│ MainWindowViewModel.OnSelectedWorkspaceChanged()  :97   │
│   ↓ Guard: ActiveWorkspaceId != value.WorkspaceId       │
│ WorkspaceService.ActivateWorkspace(workspaceId)   :128  │
│   ↓ IsActive 업데이트 + ActivateSession                 │
│ WorkspaceActivatedMessage 발행                    :142  │
│   ↓ WeakReferenceMessenger                              │
│ PaneContainerControl.Receive()                    :71   │
│   ↓                                                     │
│ SwitchToWorkspace() → BuildGrid()                 :91   │
└─────────────────────────────────────────────────────────┘
```

### 2.2 가설 분석

| # | 가설 | 가능성 | 진단 방법 | 판정 |
|:-:|-------|:------:|-----------|:----:|
| **H1** | `click_at(80, 150)` 좌표가 첫 번째 workspace ListBoxItem에 안 맞음 | **High** | XAML 정적 분석으로 좌표 계산 | **확정** |
| **H2** | `SendInput` 클릭이 WPF ListBox에 도달하지만 `SelectedItem` 이 갱신 안 됨 | Medium | 진단 로그 (OnSelectedWorkspaceChanged 진입 여부) | N/A (H1 확정) |
| **H3** | Cascade — MQ-1 render blank 상태에서 사이드바도 비정상이었음 | Medium | MQ-1/split fix 적용 후 MQ-7 재실행으로 확인 | N/A (H1 확정) |
| **H4** | `SwitchToWorkspace` guard (`_activeWorkspaceId == workspaceId`) 재진입 차단 | Low | 진단 로그 | N/A (H1 확정) |
| **H5** | `BuildGrid` 가 빈 tree로 `Content = null` 설정 | Low | paneLayout.Root 값 진단 로그 | N/A (H1 확정) |

**H1 확정 근거 (XAML 정적 분석)**:

```
y=0..32   TitleBar (CaptionHeight=32)
y=32..80  Header Grid (Margin 12+8, Button H=28)
y=81..127 ListBoxItem[0] (Workspace 1) — center ≈ y=104
y=129..175 ListBoxItem[1] (Workspace 2) — center ≈ y=152
```

`click_at(80, 150)` → ListBoxItem[1] (Workspace 2, 이미 active) → `ActivateWorkspace` guard return → **no-op**.
수정: `FIRST_WORKSPACE_Y = 150 → 104`

### 2.3 H1 상세 분석 (최유력)

MQ-7 operator 시나리오의 클릭 좌표:

```python
SIDEBAR_X = 80        # 사이드바 중앙 (sidebar width=200, 패딩 포함)
FIRST_WORKSPACE_Y = 150  # 첫 번째 workspace 행 예상 위치
```

이 좌표는 **client 좌표** (윈도우 client area 기준)이며 `ClientToScreen` 변환 후 `SendInput`에 전달된다.

**잠재적 문제점**:

1. **TitleBar 높이 변동**: WPF custom TitleBar (`WindowChrome.CaptionHeight`) + "GHOSTWIN" 헤더 + "+ New" 버튼 영역의 높이가 예상과 다를 수 있음
2. **DPI 스케일링**: `click_at()`은 물리 좌표 → SendInput 절대좌표 변환을 수행하지만, WPF 는 논리 좌표(96 DPI 기준)를 사용. DPI 150%에서 client 좌표 계산이 어긋날 수 있음
3. **ListBox 항목 위치**: `SidebarItemStyle` 의 `Margin="4,1"` + `Padding="8,6"` + 행 높이 합산이 y=150에 실제로 첫 번째 항목이 오는지

**XAML 구조 분석** (`MainWindow.xaml`):

```
Border (Sidebar, Width=200)
  └─ DockPanel
      ├─ Grid (Header: "GHOSTWIN" + "+ New" button)  ← 높이 ~40-50px
      ├─ ListBox (Workspaces)                         ← y 시작점
      │   └─ ListBoxItem[0] (Workspace 1)            ← 첫 번째 항목
      │   └─ ListBoxItem[1] (Workspace 2)            ← 두 번째 항목
      └─ (bottom area)
```

Custom TitleBar의 `CaptionHeight`가 client area에 포함되므로, y=150이 실제로 ListBox 내부에 위치하는지가 핵심이다.

---

## 3. Investigation Protocol

### 3.1 Phase 1: Cascade 판정 (T-1)

| Step | Action | Expected | Actual |
|:----:|--------|----------|--------|
| 1 | Hardware에서 GhostWin 실행 | 앱 정상 기동 | — |
| 2 | Ctrl+T 로 두 번째 workspace 생성 | 사이드바에 2개 항목 표시 | — |
| 3 | 첫 번째 workspace를 사이드바에서 클릭 | Pane 레이아웃 전환, active indicator 이동 | — |
| 4 | 결과 기록 | PASS → cascade 확정, FAIL → Phase 2 | — |

수동 확인으로 앱 자체의 workspace 전환이 동작하는지 먼저 판정한다. 앱 자체가 동작하면 E2E operator 좌표 문제(H1)로 범위를 좁힌다.

### 3.2 Phase 2: E2E 좌표 진단 (T-2)

**앱 workspace 전환이 수동으로 동작하는 경우** (H1 확인):

| Step | Action | Tool |
|:----:|--------|------|
| 1 | GhostWin 실행 후 `Inspect.exe` (Windows SDK) 로 ListBox 항목의 실제 bounding rect 확인 | Inspect.exe / UIA |
| 2 | 첫 번째 ListBoxItem의 client 좌표 범위가 `(80, 150)` 을 포함하는지 확인 | 수동 계산 |
| 3 | 불일치 시 올바른 좌표 산출 | — |

**앱 workspace 전환이 수동으로도 동작하지 않는 경우** (H2~H5 탐색):

| Step | Action | Tool |
|:----:|--------|------|
| 1 | `OnSelectedWorkspaceChanged` 에 `System.Diagnostics.Debug.WriteLine` 추가 | 코드 수정 |
| 2 | `ActivateWorkspace` 진입/퇴장 로그 추가 | 코드 수정 |
| 3 | `SwitchToWorkspace` 의 guard, root null 여부 로그 추가 | 코드 수정 |
| 4 | 재빌드 후 수동 테스트, Output 창에서 로그 확인 | VS Output |

### 3.3 Phase 3: 수정 구현 (T-3)

근본 원인 확정 후 구현. 예상 수정 시나리오별:

| Scenario | Fix | 변경 파일 | LOC 예상 |
|----------|-----|-----------|:--------:|
| **S1: Cascade** | 코드 변경 없음 | — | 0 |
| **S2: 좌표 오차 (H1)** | `mq7_workspace_switch.py` 좌표 보정 또는 UIA 기반 클릭 | `scenarios/mq7_workspace_switch.py` | ~10 |
| **S3: SelectedItem 미갱신 (H2)** | ListBox에 AutomationId 추가 + UIA 기반 선택 | `MainWindow.xaml` + `mq7_workspace_switch.py` | ~15 |
| **S4: 기타 WPF 문제 (H4/H5)** | 근본 원인에 따라 결정 | TBD | TBD |

---

## 4. Implementation Order

```
T-1: Cascade 판정 (수동 hardware 테스트)
 ↓
 ├─ PASS → T-4 (E2E 전체 재실행 확인 → report)
 │
 └─ FAIL → T-2: 좌표 진단
              ↓
              T-3: 수정 구현
              ↓
              T-4: E2E 전체 재실행 검증
```

### T-1: Cascade 판정

- **전제조건**: 현재 코드 (MQ-1 fix + split-content fix 적용 완료)
- **방법**: 수동으로 앱 실행 → Ctrl+T → 사이드바 첫 번째 항목 클릭
- **판정**: workspace 전환 동작 여부

### T-2: 좌표 진단 (T-1 FAIL 시)

- 앱 자체 동작 → H1 (좌표 오차) → `Inspect.exe`로 실제 bounding rect 확인
- 앱 자체 미동작 → H2~H5 → 진단 로그 추가 후 추적

### T-3: 수정 구현 (T-2 결과 기반)

- S2: 좌표 보정 (가장 가능성 높음)
- S3: UIA 기반 클릭 전환 (좌표가 DPI에 너무 민감한 경우)

### T-4: 검증

- E2E 전체 실행 (MQ-1~MQ-8) — hardware 필요
- 또는 수동 smoke 테스트

---

## 5. Affected Files

| File | Change Type | Description |
|------|:-----------:|-------------|
| `scripts/e2e/e2e_operator/scenarios/mq7_workspace_switch.py` | Modify (S2/S3) | 좌표 보정 또는 UIA 클릭 전환 |
| `src/GhostWin.App/MainWindow.xaml` | Modify (S3) | ListBox에 AutomationId 추가 (선택적) |
| `src/GhostWin.App/ViewModels/MainWindowViewModel.cs` | None | 진단 로그만 (임시, 제거) |
| `src/GhostWin.Services/WorkspaceService.cs` | None | 진단 로그만 (임시, 제거) |

---

## 6. Test Plan

### 6.1 Test Scope

| Type | Target | Tool |
|------|--------|------|
| E2E | MQ-7 workspace switch | `scripts/test_e2e.ps1` |
| E2E | MQ-1~MQ-8 전체 | `scripts/test_e2e.ps1` (regression) |
| Manual | 사이드바 클릭 전환 | Hardware smoke |

### 6.2 Test Cases

| ID | Case | Expected | Priority |
|----|------|----------|:--------:|
| TC-1 | Ctrl+T로 2nd workspace 생성 후, 첫 번째 사이드바 항목 클릭 | Active workspace 전환, pane 레이아웃 변경 | High |
| TC-2 | 3개 이상 workspace에서 임의 항목 클릭 | 해당 workspace 활성화 | Medium |
| TC-3 | 이미 active인 workspace 재클릭 | 변화 없음 (guard 정상 작동) | Low |
| TC-4 | MQ-1~MQ-6, MQ-8 기존 시나리오 | 기존 PASS 유지 | High |

---

## 7. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| E2E bash 세션에서 SendInput이 foreground 윈도우에만 동작 | Medium | `safe_focus(hwnd)` 가 이미 `SetForegroundWindow` 호출 — 확인 필요 |
| DPI 스케일링으로 좌표가 머신마다 다름 | Medium | UIA 기반 클릭으로 전환하면 좌표 독립적 |
| 수동 테스트 결과와 자동 테스트 결과 불일치 | Low | 동일 머신에서 순차 실행 |

---

## 8. Decision Record

### D-1: Cascade 판정을 Design 앞에 배치

Plan에서는 FR-01 (cascade 판정)을 수행 전에 Design을 작성하지 않을 계획이었으나, 사용자가 Design 진행을 요청. 모든 시나리오(S1~S4)를 커버하는 조건부 설계로 작성.

### D-2: UIA 기반 클릭 vs 좌표 보정

좌표 보정(S2)이 최소 변경이지만, DPI/해상도 민감성이 있다. 만약 H1이 확정되면:
- **1차**: 정확한 좌표로 보정 (간단)
- **2차**: 반복 실패 시 UIA `AutomationId` 기반 클릭으로 전환 (견고)

---

## 9. Follow-up

| # | Item | Trigger |
|:-:|------|---------|
| 1 | Sidebar ListBox에 AutomationId 일괄 부여 | S3 채택 시 |
| 2 | 다른 좌표 기반 MQ 시나리오(MQ-4 mouse focus) 좌표 검증 | H1 확정 시 동일 문제 가능 |
| 3 | E2E FlaUI cross-validation (`tests/e2e-flaui-cross-validation/`) | UIA 경로 전환 시 참고 |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-10 | Initial draft | Claude + 노수장 |
