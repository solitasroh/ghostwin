# e2e-mq7-workspace-click Planning Document

> **Summary**: 사이드바 클릭 workspace 전환 regression 진단 및 수정
>
> **Project**: GhostWin Terminal
> **Author**: Claude + 노수장
> **Date**: 2026-04-10
> **Status**: Draft

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | E2E MQ-7 시나리오에서 사이드바 클릭으로 workspace 전환이 동작하지 않는 regression 발견 (silent — operator는 OK 보고, evaluator가 시각적으로 FAIL 판정) |
| **Solution** | MQ-1 fix (first-pane-render-failure) + split-content-loss-v2 fix 적용 후 cascade 여부 판정 → 독립 regression이면 근본 원인 진단 및 수정 |
| **Function/UX Effect** | 사이드바에서 workspace를 클릭하면 해당 workspace의 pane 레이아웃과 터미널 내용이 즉시 표시되어야 함 |
| **Core Value** | 다중 workspace 전환은 multi-session UI의 핵심 기능 — 동작하지 않으면 Phase 5-E workspace layer의 존재 의미 상실 |

---

## 1. Overview

### 1.1 Purpose

E2E 자동화 평가(evaluator-automation)에서 발견된 MQ-7 (Workspace Switch via Sidebar Click) silent regression을 해결한다. Operator는 성공으로 보고했으나, evaluator의 스크린샷 시각 분석에서 workspace 전환이 실제로 일어나지 않았음을 포착했다.

### 1.2 Background

- **발견 시점**: 2026-04-08, `e2e-evaluator-automation` PDCA 완료 시 `diag_all_h9_fix` run 첫 평가
- **판정**: FAIL — `failure_class="key-action-not-applied"` (사이드바 active highlight 미변경, pane 레이아웃 미전환)
- **분류**: MQ-1 (first-pane-render-failure)과의 cascade 가능성 있었으나, MQ-1 fix (Option B: HC-4 `Initialize()` 동기 구독) + split-content-loss-v2 fix (`sessionId != 0` 가드 제거)가 모두 완료된 현재, 재평가 필요
- **우선순위**: HIGH — CLAUDE.md Follow-up Cycles #1

### 1.3 Related Documents

- E2E Evaluator Automation Report: `docs/archive/2026-04/e2e-evaluator-automation/e2e-evaluator-automation.report.md`
- First-Pane-Render-Failure Report: `docs/archive/2026-04/first-pane-render-failure/first-pane-render-failure.report.md`
- Split-Content-Loss-v2 Report: `docs/archive/2026-04/split-content-loss-v2/split-content-loss-v2.report.md`
- Pane-Split Design (v0.5): `docs/02-design/features/pane-split.design.md`
- E2E Evaluator Prompt: `scripts/e2e/evaluator_prompt.md` (MQ-7 spec: §3.7)
- MQ-7 Operator: `scripts/e2e/e2e_operator/scenarios/mq7_workspace_switch.py`

---

## 2. Scope

### 2.1 In Scope

- [x] FR-01: MQ-1/split-content-loss-v2 fix 적용 후 MQ-7 cascade 여부 판정 (e2e 재실행)
- [ ] FR-02: 독립 regression인 경우 근본 원인 진단
- [ ] FR-03: 수정 구현 및 hardware smoke 검증
- [ ] FR-04: E2E MQ-7 시나리오 PASS 확인

### 2.2 Out of Scope

- MQ-7 operator 스크립트 좌표 보정 (클릭 좌표가 맞는지는 FR-01에서 확인)
- Workspace 전환 애니메이션 또는 전환 UX 개선
- 사이드바 active indicator 시각적 디자인 변경

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| FR-01 | E2E 재실행으로 MQ-7이 MQ-1/split-content fix의 cascade인지 판정 | High | Pending |
| FR-02 | 독립 regression인 경우 workspace 전환 체인 전체 진단 | High | Pending |
| FR-03 | 근본 원인 수정 (코드 변경) | High | Pending |
| FR-04 | Hardware smoke: 사이드바 클릭 → workspace 전환 동작 확인 | High | Pending |

### 3.2 Non-Functional Requirements

| Category | Criteria | Measurement Method |
|----------|----------|-------------------|
| Regression 방지 | 기존 MQ-1~MQ-6, MQ-8 시나리오 PASS 유지 | E2E evaluator 전체 실행 |

---

## 4. Investigation Strategy

### 4.1 Phase 1: Cascade 판정 (FR-01)

MQ-1 fix (`4492b5d`, HC-4 동기 구독) + split-content-loss-v2 fix (`8a5c77d`, sessionId 가드 제거)가 모두 적용된 현재 코드에서 E2E를 재실행한다.

```
판정 기준:
  MQ-7 PASS → cascade 확정 → 이 cycle 조기 종료 (report만 작성)
  MQ-7 FAIL → 독립 regression → Phase 2 진행
```

**실행 방법**: `scripts/test_e2e.ps1` (operator + evaluator)

### 4.2 Phase 2: 독립 regression 진단 (FR-02)

MQ-7이 여전히 FAIL이면 workspace 전환 체인을 추적한다.

```
사이드바 ListBox 클릭
  ↓ SelectedItem binding
MainWindowViewModel.OnSelectedWorkspaceChanged()        ← (1) 호출 확인
  ↓
WorkspaceService.ActivateWorkspace(workspaceId)          ← (2) 실행 확인
  ↓ IsActive 업데이트 + WorkspaceActivatedMessage 발행
PaneContainerControl.Receive(WorkspaceActivatedMessage)  ← (3) 수신 확인
  ↓
SwitchToWorkspace() → BuildGrid()                        ← (4) 시각적 전환
```

**진단 후보 (우선순위순)**:

| # | 가설 | 근거 | 확인 방법 |
|:-:|-------|------|-----------|
| H1 | E2E `click_at(80, 150)` 좌표가 실제 sidebar ListBox 항목에 안 맞음 | DPI 스케일링, 창 크기 변동, sidebar width 변경 | 수동 좌표 확인 또는 UIA 기반 클릭으로 전환 |
| H2 | `PostMessage WM_LBUTTONDOWN` 이 WPF ListBox에 도달하지 않음 | WPF의 HwndHost airspace 문제, 메시지 라우팅 | `click_at` 이 `SendMessage`/`PostMessage` 중 어떤 것을 쓰는지 확인 |
| H3 | ListBox `SelectedItem` binding이 workspace 전환 후 재진입 방지에 걸림 | `OnSelectedWorkspaceChanged` 의 guard (`ActiveWorkspaceId != value.WorkspaceId`) | 진단 로그 추가 |
| H4 | `WorkspaceActivatedMessage` 가 `SwitchToWorkspace`까지 도달하나 `BuildGrid`가 빈 tree를 받음 | paneLayout.Root가 null | 진단 로그 |
| H5 | HwndHost가 workspace 전환 시 올바르게 복원되지 않음 (`_hostsByWorkspace` 캐시 오류) | SwitchToWorkspace의 host save/restore 로직 | 진단 로그 |

### 4.3 Phase 3: 수정 및 검증 (FR-03, FR-04)

근본 원인 확정 후 최소 변경으로 수정. Hardware smoke 검증 필수.

---

## 5. Success Criteria

### 5.1 Definition of Done

- [ ] MQ-7 E2E 시나리오 PASS (evaluator verdict)
- [ ] MQ-1~MQ-6, MQ-8 기존 시나리오 regression 없음
- [ ] Hardware smoke: 수동으로 사이드바 클릭 workspace 전환 동작 확인
- [ ] 근본 원인 문서화 (cascade였으면 그 사실만 기록)

### 5.2 Quality Criteria

- [ ] 코드 변경 시 기존 단위 테스트 PASS 유지
- [ ] 변경 LOC 최소화 (진단 목적의 임시 로그는 제거)

---

## 6. Risks and Mitigation

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| E2E bash 세션에서 키보드/마우스 시나리오 제한 (H-RCA4) | Medium | Medium | Hardware 수동 검증 병행, UIA 기반 클릭 검토 |
| 좌표 기반 클릭이 DPI/해상도에 민감 | Medium | Medium | UIA AutomationId 기반 클릭으로 전환 검토 |
| Cascade로 이미 해결되었을 가능성 | Low (긍정적) | Medium | FR-01에서 조기 판정 → 불필요한 코드 변경 방지 |

---

## 7. Architecture Considerations

### 7.1 Project Level

| Level | Selected |
|-------|:--------:|
| **Enterprise** (WPF Clean Architecture, 4-project, DI, MVVM) | **O** |

### 7.2 관련 아키텍처

| Component | File | Role |
|-----------|------|------|
| MainWindowViewModel | `src/GhostWin.App/ViewModels/MainWindowViewModel.cs:97-102` | Sidebar 선택 → ActivateWorkspace 호출 |
| WorkspaceService | `src/GhostWin.Services/WorkspaceService.cs:128-143` | ActivateWorkspace 구현 + 메시지 발행 |
| PaneContainerControl | `src/GhostWin.App/Controls/PaneContainerControl.cs:71-123` | WorkspaceActivatedMessage 수신 → SwitchToWorkspace |
| MQ-7 Operator | `scripts/e2e/e2e_operator/scenarios/mq7_workspace_switch.py` | 사이드바 클릭 + 스크린샷 캡처 |
| Evaluator Spec | `scripts/e2e/evaluator_prompt.md` §3.7 | MQ-7 PASS/FAIL 판정 기준 |

### 7.3 메시징 체인

```
ListBox.SelectedItem (XAML binding)
  → OnSelectedWorkspaceChanged (ViewModel)
    → ActivateWorkspace (Service)
      → WorkspaceActivatedMessage (Messenger)
        → SwitchToWorkspace (PaneContainerControl)
          → BuildGrid → BuildElement → TerminalHostControl
```

---

## 8. Next Steps

1. [ ] **FR-01 실행**: E2E 전체 실행으로 MQ-7 cascade 여부 판정 (hardware 필요)
2. [ ] Cascade면 → 바로 report 작성으로 조기 종료
3. [ ] 독립이면 → Design 문서 작성 (`/pdca design e2e-mq7-workspace-click`)
4. [ ] 수정 구현 → Gap 분석 → Report

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-10 | Initial draft | Claude + 노수장 |
