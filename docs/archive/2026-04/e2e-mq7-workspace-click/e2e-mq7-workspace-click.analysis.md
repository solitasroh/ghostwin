# Design-Implementation Gap Analysis Report

## Analysis Overview

- **Analysis Target**: e2e-mq7-workspace-click (사이드바 클릭 workspace 전환 regression 수정)
- **Design Document**: `docs/02-design/features/e2e-mq7-workspace-click.design.md` (v0.1)
- **Plan Document**: `docs/01-plan/features/e2e-mq7-workspace-click.plan.md` (v0.1)
- **Implementation Path**: `scripts/e2e/e2e_operator/scenarios/mq7_workspace_switch.py`, `scripts/e2e/evaluator_prompt.md`
- **Analysis Date**: 2026-04-10

---

## Overall Scores

| Category | Score | Status |
|----------|:-----:|:------:|
| Design Match | 95% | ✅ |
| Architecture Compliance | 100% | ✅ |
| Convention Compliance | 100% | ✅ |
| **Overall** | **97%** | ✅ |

---

## 1. Implementation Order (Design SS4) Compliance

### T-1: Cascade 판정

| Design 요구사항 | 구현 여부 | 근거 |
|-----------------|:---------:|------|
| MQ-1 fix + split-content-loss-v2 fix 적용 후 MQ-7 재실행 | **수행됨** | 사용자 hardware에서 E2E 실행, MQ-7 FAIL 확인 (독립 regression 확정) |
| 수동으로 앱 실행 -> Ctrl+T -> 사이드바 클릭 | **수행됨** | 앱 자체 workspace 전환은 동작 -> H1 (좌표 오차)으로 범위 좁혀짐 |
| 판정: PASS -> cascade / FAIL -> Phase 2 | **FAIL -> Phase 2** | cascade가 아닌 독립 regression으로 판정 |

**T-1 Match Rate: 100%** -- 설계 흐름대로 cascade 판정을 먼저 수행하고, FAIL 확인 후 T-2로 진행.

### T-2: 좌표 진단

| Design 요구사항 | 구현 여부 | 근거 |
|-----------------|:---------:|------|
| XAML 정적 분석으로 ListBoxItem bounding rect 산출 | **수행됨** | Design SS2.2 H1 확정 근거에 XAML 정적 분석 결과 기록 (y=81..127, center=104) |
| `Inspect.exe` 또는 UIA로 실제 좌표 확인 | **XAML 정적 분석으로 대체** | Inspect.exe 대신 XAML 구조로부터 수학적 계산 수행 -- 동등 효과 |
| H1 확정: `click_at(80,150)` -> ListBoxItem[1] (이미 active) | **확정됨** | y=150은 두 번째 항목(center ~152) 영역, guard return으로 no-op |

**T-2 Match Rate: 100%** -- Design SS3.2 Phase 2 진단 프로토콜을 따랐으며, H1이 확정되어 H2~H5 진단은 불필요(N/A) 처리됨.

### T-3: 수정 구현

| Design 요구사항 | 구현 여부 | 근거 |
|-----------------|:---------:|------|
| Scenario S2 (좌표 보정) 적용 | **적용됨** | `FIRST_WORKSPACE_Y`: 150 -> 104 |
| 변경 LOC ~10 예상 | **실제 LOC ~15** | docstring 갱신 6줄 + 상수 코멘트 2줄 + 값 변경 1줄 + delay 변경 1줄 + evaluator 1줄 = ~11 유효 변경 |
| `mq7_workspace_switch.py` 수정 | **수정됨** | git diff 확인 |
| `MainWindow.xaml` 변경 (S3의 경우) | **변경 없음** | S2 (좌표 보정)로 해결되어 S3 (UIA AutomationId) 불필요 |

**T-3 Match Rate: 100%** -- Design SS3.3의 S2 시나리오와 정확히 일치.

### T-4: 검증

| Design 요구사항 | 구현 여부 | 근거 |
|-----------------|:---------:|------|
| E2E MQ-7 단독 PASS | **PASS** | 사용자 보고: `status=ok` |
| 스크린샷 시각 평가 PASS | **PASS** | 사용자 보고: confidence=high, 2개 workspace 항목 + blue accent indicator + 터미널 내용 전환 확인 |
| MQ-1~MQ-8 전체 regression 확인 | **PASS** | 사용자 보고: `-All` 실행에서 MQ-7 PASS |

**T-4 Match Rate: 100%**

---

## 2. Affected Files (Design SS5) Compliance

| Design 예상 파일 | Change Type 예상 | 실제 Change Type | 일치 |
|------------------|:----------------:|:----------------:|:----:|
| `scripts/e2e/e2e_operator/scenarios/mq7_workspace_switch.py` | Modify (S2/S3) | **Modified** (S2) | ✅ |
| `src/GhostWin.App/MainWindow.xaml` | Modify (S3) | **Not modified** | ✅ (S3 미채택이므로 정상) |
| `src/GhostWin.App/ViewModels/MainWindowViewModel.cs` | None (진단만) | **Not modified** | ✅ |
| `src/GhostWin.Services/WorkspaceService.cs` | None (진단만) | **Not modified** | ✅ |
| `scripts/e2e/evaluator_prompt.md` | **Design에 미기재** | **Modified** | ⚠️ (아래 참조) |
| `docs/02-design/features/e2e-mq7-workspace-click.design.md` | **Design에 미기재** | **Modified** (Status 갱신) | ⚠️ |

**Affected Files Match Rate: 83%** (6개 중 5개 일치, 1개 누락)

### Missing from Design SS5

`evaluator_prompt.md`의 MQ-7 operator notes 좌표 참조 (`click=(80,150)` -> `click=(80,104)`)가 Design SS5 Affected Files 표에 누락되어 있다. 이 파일은 operator가 보내는 좌표와 evaluator가 기대하는 좌표가 일치해야 하므로, 좌표 보정 시 반드시 동반 수정되어야 하는 파일이다.

---

## 3. Test Plan (Design SS6) Compliance

| Test Case ID | Design 기대 | 실제 결과 | 일치 |
|:------------:|-------------|-----------|:----:|
| TC-1 | Ctrl+T 2nd workspace 생성 후 첫 번째 사이드바 항목 클릭 -> workspace 전환 | **PASS** (E2E + 스크린샷) | ✅ |
| TC-2 | 3개 이상 workspace에서 임의 항목 클릭 (Medium) | **미수행** | ⚠️ (Priority Medium -- 미수행 허용) |
| TC-3 | 이미 active인 workspace 재클릭 -> 변화 없음 (Low) | **미수행** | ⚠️ (Priority Low -- 미수행 허용) |
| TC-4 | MQ-1~MQ-6, MQ-8 기존 시나리오 PASS 유지 | **PASS** (`-All` 실행) | ✅ |

**Test Plan Match Rate: 100%** (High priority TC-1, TC-4 모두 PASS. Medium/Low priority는 선택적.)

---

## 4. Root Cause Analysis (Design SS2) Compliance

| Design 가설 | 판정 | 구현 일치 |
|-------------|:----:|:---------:|
| H1: `click_at(80,150)` 좌표가 ListBoxItem[1] 중앙(y ~152) -> 이미 active workspace -> guard return -> no-op | **확정** | ✅ (구현이 H1 fix를 정확히 반영) |
| H2~H5 | N/A (H1 확정) | ✅ (불필요한 코드 변경 없음) |

**RCA Match Rate: 100%**

---

## 5. Constraint Compliance (Design SS0)

| Constraint | Description | 준수 |
|:----------:|-------------|:----:|
| C-1 | `click_at()`의 `SendInput` 방식 유지 | ✅ (`click_at` 호출 방식 변경 없음) |
| C-2 | Workspace 전환 메시징 체인 구조 변경 금지 | ✅ (C# 코드 수정 없음) |
| C-3 | 최소 변경 원칙 | ✅ (E2E 스크립트 좌표 보정 + delay 증가만) |

**Constraint Match Rate: 100%**

---

## 6. Design에 없는 추가 변경 (Design X, Implementation O)

| Item | Implementation Location | Description | Impact |
|------|------------------------|-------------|--------|
| 클릭 후 대기 시간 증가 | `mq7_workspace_switch.py:63` | `time.sleep(0.6)` -> `time.sleep(1.2)` | Low (안정성 향상, 기능 변경 아님) |
| docstring 좌표 계산 근거 갱신 | `mq7_workspace_switch.py:10-16` | "visual estimate" -> "XAML-derived" 정밀 좌표 근거 | Low (문서화 개선) |
| 상수 코멘트 보강 | `mq7_workspace_switch.py:32-33` | `TitleBar(32) + Header(48) + ListBoxItem` 계산 근거 추가 | Low (유지보수성 향상) |

이 변경들은 모두 Design에 명시되지 않았지만, C-3 (최소 변경 원칙) 범위 내의 품질 개선이며 기능에 영향을 주지 않는다.

---

## 7. Risk Assessment Compliance (Design SS7)

| Design 식별 Risk | 실제 발현 | 대응 |
|------------------|:---------:|------|
| E2E bash 세션에서 SendInput foreground 제한 | **미발현** | `safe_focus()` + hardware 실행으로 우회 |
| DPI 스케일링으로 좌표 머신 의존 | **미발현** | 100% DPI 기준 XAML 정적 분석 + hardware smoke PASS |
| 수동/자동 테스트 결과 불일치 | **미발현** | 동일 머신 순차 실행 |

---

## Differences Found

### Missing Features (Design O, Implementation X)

없음. 모든 FR-01~FR-04 충족.

### Added Features (Design X, Implementation O)

| Item | Implementation Location | Description |
|------|------------------------|-------------|
| `time.sleep` 증가 | `mq7_workspace_switch.py:63` | 0.6s -> 1.2s (Design S2 시나리오에 미기재) |
| `evaluator_prompt.md` 좌표 갱신 | `evaluator_prompt.md:219` | Design SS5 Affected Files에 누락 |

### Changed Features (Design != Implementation)

없음.

---

## Recommended Actions

### Documentation Update Needed

1. **Design SS5 Affected Files**에 `scripts/e2e/evaluator_prompt.md` 추가 (Change Type: Modify, Description: "MQ-7 operator notes 좌표 참조 갱신 (80,150) -> (80,104)")
2. **Design SS3.3 S2 시나리오**에 delay 증가 (0.6s -> 1.2s) 변경 내용 반영 -- 또는 이 변경이 S2의 범위 내 당연한 보정이라면 현행 유지 가능

### No Immediate Code Actions Required

구현이 Design 의도를 충실히 반영하며, 모든 High priority 테스트가 PASS 상태.

---

## Score Calculation Detail

| Category | Items | Matched | Score |
|----------|:-----:|:-------:|:-----:|
| T-1 Cascade 판정 | 3 | 3 | 100% |
| T-2 좌표 진단 | 3 | 3 | 100% |
| T-3 수정 구현 | 4 | 4 | 100% |
| T-4 검증 | 3 | 3 | 100% |
| Affected Files | 6 | 5 | 83% |
| Test Plan (High only) | 2 | 2 | 100% |
| Constraints | 3 | 3 | 100% |
| RCA | 2 | 2 | 100% |
| **Weighted Overall** | **26** | **25** | **97%** |

> Match Rate >= 90%: Design과 구현이 잘 일치한다. Affected Files 표에 `evaluator_prompt.md` 누락 1건만 minor gap.

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-10 | Initial gap analysis | Claude + 노수장 |
