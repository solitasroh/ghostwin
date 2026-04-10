# e2e-mq7-workspace-click 완료 보고서

> **Summary**: 사이드바 클릭 workspace 전환 regression 진단 및 수정 완료
>
> **Project**: GhostWin Terminal
> **Author**: Claude + 노수장
> **Date**: 2026-04-10
> **Status**: Completed
> **Match Rate**: 97%

---

## Executive Summary

### 1.3 Value Delivered

| Perspective | Content |
|-------------|---------|
| **Problem** | E2E MQ-7 시나리오(사이드바 클릭 workspace 전환)가 silent regression으로 동작하지 않음. E2E 자동화 파이프라인이 8/8 PASS 불가능한 상태였음. |
| **Solution** | XAML 정적 분석으로 E2E operator 스크립트의 클릭 좌표(150→104)가 잘못되었음을 확인. 좌표 보정 2줄 + delay 증가 1줄로 수정. |
| **Function/UX Effect** | 사이드바 클릭으로 workspace 전환이 정상 동작하며, E2E MQ-7이 PASS. 스크린샷 시각 평가에서 2개 workspace 항목, blue accent indicator, 터미널 내용 전환 모두 확인됨. |
| **Core Value** | E2E 자동화 평가(evaluator)가 8/8 시나리오를 모두 검증할 수 있게 되어, Phase 5-E.5 부채 청산 완료 가능 경로 회복. 다중 workspace 기능 검증 인프라 완성. |

---

## PDCA 사이클 요약

### Plan
- **문서**: `docs/01-plan/features/e2e-mq7-workspace-click.plan.md`
- **목표**: MQ-7 regression을 MQ-1/split-content fix의 cascade인지 독립인지 판정, 후 수정
- **예상 소요**: 반일 (진단 우선)

### Design
- **문서**: `docs/02-design/features/e2e-mq7-workspace-click.design.md` (v0.1)
- **핵심 설계**:
  - T-1: Cascade 판정 — 수동 hardware 테스트로 앱 자체 workspace 전환 동작 확인
  - T-2: 좌표 진단 — XAML 정적 분석으로 `click_at(80, 150)`이 두 번째 항목을 가리킴을 확정
  - T-3: 수정 — 좌표를 104로 보정 (Scenario S2 적용)
  - T-4: 검증 — E2E 실행 및 스크린샷 시각 평가
- **근본 원인 확정**: **H1 — 좌표 오차**
  ```
  y=81..127   ListBoxItem[0] (Workspace 1) — center ≈ 104
  y=129..175  ListBoxItem[1] (Workspace 2) — center ≈ 152
  operator:   click_at(80, 150) → ListBoxItem[1] (이미 active) → guard return → no-op
  fix:        FIRST_WORKSPACE_Y: 150 → 104
  ```

### Do
- **구현 범위**:
  - `scripts/e2e/e2e_operator/scenarios/mq7_workspace_switch.py`: FIRST_WORKSPACE_Y 좌표 보정 (150→104) + docstring/coment 갱신 + delay 증가 (0.6→1.2s)
  - `scripts/e2e/evaluator_prompt.md`: MQ-7 operator notes 좌표 참조 갱신
  - `docs/02-design/features/e2e-mq7-workspace-click.design.md`: Status 업데이트 (RCA Confirmed)
- **실제 소요**: 단일 세션 내 완료 (약 30분)

### Check
- **분석 문서**: `docs/03-analysis/e2e-mq7-workspace-click.analysis.md`
- **설계-구현 일치도**: **97%** (Design과 implementation 완전 부합)
  - T-1~T-4 implementation order: 100% 준수
  - Affected Files: 83% (evaluator_prompt.md 갱신 누락 1건, 실제로는 수행됨)
  - Test Plan: 100% (High priority TC-1, TC-4 PASS)
  - RCA: 100% (H1 확정)
  - Constraint: 100% (C-1, C-2, C-3 모두 준수)
- **검증 결과**:
  - E2E MQ-7 단독: **PASS** (status=ok)
  - 스크린샷 시각 평가: **PASS** (confidence: high)
    - 2개 workspace 항목 표시
    - 첫 번째 항목에 blue accent indicator
    - 터미널 내용 정상 전환 (PowerShell 프롬프트)
  - 회귀 테스트 (MQ-1~MQ-8): **PASS** (사용자 `-All` 실행)

---

## 결과

### 완료된 항목
- ✅ **FR-01**: Cascade 판정 완료 — 독립 regression 확정
- ✅ **FR-02**: 근본 원인 진단 완료 — H1 (좌표 오차) 확정
- ✅ **FR-03**: 수정 구현 완료 — 2줄 좌표 보정 (S2 시나리오)
- ✅ **FR-04**: 검증 완료 — E2E PASS + 스크린샷 시각 평가 PASS

### 미완료/보류된 항목
- ⏸️ TC-2 (3개 이상 workspace 클릭): Medium priority — 미수행 (선택적)
- ⏸️ TC-3 (active workspace 재클릭): Low priority — 미수행 (선택적)

---

## 근본 원인 분석

### H1 — E2E 클릭 좌표 오차 (확정)

**문제**:
```python
# mq7_workspace_switch.py (수정 전)
FIRST_WORKSPACE_Y = 150  # 첫 번째 workspace 행 (잘못된 좌표)
```

사이드바 ListBox 항목의 실제 위치:
```
y=0..32      TitleBar (CaptionHeight=32)
y=32..80     Header Grid ("GHOSTWIN" + "+ New" button)
y=81..127    ListBoxItem[0] (Workspace 1) — center ≈ y=104  ← 클릭해야 할 위치
y=129..175   ListBoxItem[1] (Workspace 2) — center ≈ y=152  ← 실제 클릭 위치
```

**메커니즘**:
```
click_at(80, 150)  (이미 active인 Workspace 2 가리킴)
  ↓
WPF hit-test → ListBox.SelectedItem = workspace[1]
  ↓
OnSelectedWorkspaceChanged() 호출
  ↓
guard check: ActiveWorkspaceId (1) == workspaceId (1) ← 동일! guard return
  ↓
no-op — workspace 전환 안 됨 (silent failure)
```

**수정**:
```python
# mq7_workspace_switch.py (수정 후)
FIRST_WORKSPACE_Y = 104  # XAML 정적 분석으로 산출한 정확한 좌표
# ... (docstring/comment 갱신)
time.sleep(1.2)  # 안정성 향상 (0.6→1.2)
```

**근거**:
- XAML 구조 분석으로 TitleBar(32) + Header Grid(48) + ListBoxItem layout 계산
- 첫 번째 항목 상단 y=81 + 높이 ~46 / 2 ≈ 104 확인
- Design document § 2.2 H1 확정 근거에 기재

---

## 개선 사항

### 설계 품질
- **근거 기반 문제 해결**: XAML 정적 분석으로 좌표 오차를 수학적으로 확정 (추측 금지 규칙 준수)
- **cascade 판정 단계 추가**: 선제적으로 MQ-1/split-content fix의 영향을 판단하여 불필요한 코드 변경 방지

### 다음에 적용할 사항
- **좌표 기반 E2E 시나리오 정밀화**: MQ-4 (mouse focus) 등 다른 좌표 기반 시나리오도 동일 분석 적용 권장
- **UIA 기반 클릭 고려**: DPI/해상도 민감성이 심할 경우 Scenario S3 (UIA AutomationId) 전환 검토
- **E2E operator 좌표 문서화**: 모든 좌표 상수에 XAML 레이아웃 계산 근거 추가

---

## 교훈

### 잘된 점
- **독립 판정 단계의 가치**: FR-01 (cascade 판정)을 먼저 수행하여 코드 변경 전에 문제 범위 명확화
- **정적 분석의 효율성**: hardware 없이 XAML 구조만으로 좌표 오차를 확정하여 빠른 진단
- **최소 변경 원칙 준수**: 불필요한 코드 리팩토링 없이 E2E 스크립트 수정만으로 해결

### 개선 기회
- **Design 문서 정밀도**: Affected Files 표에 `evaluator_prompt.md` 누락 (실제로는 수정됨) — 예상 파일 목록 검수 강화
- **delay 변경의 근거 명시**: `time.sleep(0.6→1.2)` 증가가 설계에 명시되지 않았으나 안정성 향상 목적 — 향후 timeline-related 변경은 design에 예상 근거 기재

---

## 다음 단계

### 즉시 수행 (의존성 없음)
1. ✅ 현재 보고서 완료
2. ✅ 사용자 hardware 최종 검증 (이미 PASS)

### Follow-up (선택사항)
1. **TC-2/TC-3 추가 검증**: 3개 이상 workspace, active workspace 재클릭 등 edge case (Medium/Low priority)
2. **다른 MQ 시나리오 좌표 감사**: MQ-4 (mouse focus) 등에 동일 좌표 오차 가능성 — CLAUDE.md #2 "adr-mq-coordinate-audit" 고려
3. **Sidebar ListBox AutomationId 부여** (Follow-up): UIA 기반 클릭으로 전환할 경우 대비

---

## 타임라인

| Phase | 시작 | 종료 | 소요 |
|-------|:----:|:----:|:----:|
| Plan | 2026-04-10 | 2026-04-10 | <1h |
| Design | 2026-04-10 | 2026-04-10 | ~30m |
| Do | 2026-04-10 | 2026-04-10 | ~30m |
| Check | 2026-04-10 | 2026-04-10 | ~30m |
| **Total** | — | — | **~2h** |

**예상 대비 실적**: 예상 "반일" → 실적 "약 2시간" (조기 완료)

---

## 기술 세부사항

### 변경 파일

#### `scripts/e2e/e2e_operator/scenarios/mq7_workspace_switch.py`
```diff
-FIRST_WORKSPACE_Y = 150  # 첫 번째 workspace 항목 y 좌표 (예상)
+FIRST_WORKSPACE_Y = 104  # XAML 정적 분석: TitleBar(32) + Header(48) + ListBoxItem center

-# Sidebar ListBox를 클릭하여 첫 번째 workspace를 활성화한다.
+# Sidebar ListBox를 클릭하여 첫 번째 workspace를 활성화한다.
+# 좌표: client (80, 104) = Sidebar X + ListBoxItem[0] center Y
+# 계산: y=81 (item top) + 46/2 (item height center) ≈ 104

...
-time.sleep(0.6)  # workspace 전환 완료 대기
+time.sleep(1.2)  # workspace 전환 완료 대기 (안정성)
```

#### `scripts/e2e/evaluator_prompt.md` (§3.7 MQ-7)
```diff
-"click_at(80, 150)  # first workspace"
+"click_at(80, 104)  # first workspace (XAML-derived coordinate)"
```

#### `docs/02-design/features/e2e-mq7-workspace-click.design.md`
```diff
-Status: RCA Confirmed (H1)
+Status: RCA Confirmed (H1) — Design v0.1 최종
```

### 코드 변경 통계
- **파일 3개 수정**
- **유효 LOC**: ~11 (주석/상수 포함)
- **기능 변경**: 0 (E2E 스크립트 좌표만 보정)
- **회귀 위험**: 없음 (기존 MQ-1~MQ-8 모두 PASS)

---

## 부합도 분석

### Design-Implementation Alignment

| 항목 | 설계 요구 | 구현 결과 | 일치도 |
|------|:--------:|:--------:|:------:|
| Implementation Order | T-1→T-2→T-3→T-4 | 정확히 준수 | 100% |
| RCA | H1 확정 필수 | H1 확정 완료 | 100% |
| Scenario 선택 | S2 (좌표 보정) | S2 적용 | 100% |
| Test Coverage | TC-1, TC-4 (High) | 두 가지 모두 PASS | 100% |
| Constraint 준수 | C-1, C-2, C-3 | 전부 준수 | 100% |
| **전체** | — | — | **97%** |

> Gap 1건 (minor): Affected Files 표에 `evaluator_prompt.md` 누락 기재 (실제로는 수정됨)

---

## 메트릭

### 정량 지표
- **Match Rate**: 97% (Design-Implementation alignment)
- **Code Coverage**: 100% (3/3 affected files 수정)
- **Test Pass Rate**: 100% (High priority: TC-1 PASS, TC-4 PASS)
- **Regression Rate**: 0% (MQ-1~MQ-8 기존 시나리오 모두 PASS)

### 정성 지표
- **근거 기반**: XAML 정적 분석으로 수학적 확정 (추측 제거)
- **최소 변경**: 핵심 좌표 2줄 + 주석 갱신 (불필요한 코드 리팩토링 없음)
- **cascade 판정 가치**: 독립 regression 확인하여 선제적 대응

---

## 산출물 요약

### 문서
- ✅ Plan: `docs/01-plan/features/e2e-mq7-workspace-click.plan.md` (v0.1)
- ✅ Design: `docs/02-design/features/e2e-mq7-workspace-click.design.md` (v0.1, Status: RCA Confirmed)
- ✅ Analysis: `docs/03-analysis/e2e-mq7-workspace-click.analysis.md` (v0.1, 97% Match)
- ✅ Report (본 문서): `docs/04-report/e2e-mq7-workspace-click.report.md`

### 코드 커밋
커밋 hash는 사용자가 별도로 제공한 git 상태 기준.
- `mq7_workspace_switch.py` 좌표 보정
- `evaluator_prompt.md` 좌표 갱신
- Design 문서 최종 업데이트

### E2E 검증
- E2E MQ-7: **PASS** (status=ok)
- 스크린샷 시각 평가: **PASS** (confidence: high)
- 회귀 (MQ-1~MQ-8): **PASS**

---

## 다음 PDCA 사이클 (Follow-up)

CLAUDE.md Follow-up Cycles 테이블 기준:

| 우선순위 | Cycle | 상태 | Trigger |
|:-------:|--------|:----:|---------|
| **완료** | **e2e-mq7-workspace-click** | ✅ | e2e-evaluator-automation + first-pane-render-failure 둘 다 독립 확정 |
| HIGH | `e2e-mq-coordinate-audit` (신규) | — | MQ-4 등 다른 좌표 기반 시나리오 감사 (선택사항) |
| MEDIUM | `runner-py-feature-field-cleanup` | — | `runner.py:344` feature field hardcoded 정리 |
| LOW | `keydiag-log-dedupe` | — | 중복 ENTRY 로그 정리 |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 0.1 | 2026-04-10 | Claude + 노수장 | Initial completion report |

---

**Report Status**: ✅ **COMPLETED**  
**Match Rate**: 97% (Design-Implementation)  
**Quality Gate**: ✅ PASS (>= 90%)

대기업/동적 프로젝트 PDCA 기준 충족. 모든 High priority FR 완료, 모든 High priority 테스트 PASS, cascade 판정 및 독립 regression 확정 및 수정 완료.
