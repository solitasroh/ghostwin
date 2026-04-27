# split-content-loss-v2 완료 보고서

## 개요

**기능**: Alt+V/Alt+H split 후 첫 session의 cell buffer 손실 regression 해결
**기간**: 2026-04-09 ~ 2026-04-10
**담당자**: 노수장
**상태**: 완료 (Match Rate 100%)

## Executive Summary

### 1.3 Value Delivered

| 관점 | 내용 |
|------|------|
| **문제** | Alt+V split 직후 좌측(기존) pane의 PowerShell 프롬프트와 출력이 화면에서 사라지는 현상. |
| **확정된 원인** | `PaneContainerControl.cs:201`의 `sessionId != 0` 가드가 첫 세션(SessionId=0)의 host 재사용을 차단. Split 시 원래 host가 폐기되고 새 HWND로 재생성되지만, Surface 1의 swapchain은 파괴된 옛 HWND에 바인딩 유지 → 화면 빈칸. 진단 로그(`GHOSTWIN_RESIZE_DIAG` + `GHOSTWIN_RENDERDIAG`)로 empirical 확정. |
| **해결책** | `&& sessionId != 0` 가드 제거 (1줄). `is { }` 패턴 매치가 null을 이미 걸러주므로 불필요한 가드였음. 부수적으로 `RenderFrame` capacity-backed 재설계도 적용하여 shrink-then-grow sequence에서도 cell data 보존하는 방어적 인프라 확보. |
| **기능/UX 효과** | Alt+V split 후 좌측 pane에 pre-split shell content 유지 (100% visual recovery). RenderDiag 로그: fix 전 BuildWindowCore 3회(원본 폐기) → fix 후 2회(원본 재사용). Hardware smoke 검증 완료. |
| **핵심 가치** | 3라운드 40명+ 에이전트 독립 분석에서 5~6개 가설로 분열(합의 실패)했으나, **진단 로그 empirical 관측**으로 투표율 15%(최소)였던 가설이 실제 원인임을 확정. 추측 없는 근거 기반 수정. |

---

## PDCA 사이클 요약

### Plan
- 문서: `docs/01-plan/features/split-content-loss-v2.plan.md`
- 목표: 3라운드 40명+ 에이전트 분석 후 5~6개 가설 중 `sessionId != 0` 가드가 원인임을 hardware diagnostic으로 empirical 확정. 진단 로그 기반 RCA 완료
- 예상 소요: 2일

### Design
- 문서: `docs/02-design/features/split-content-loss-v2.design.md`
- 주요 설계 결정:
  - D1: `RenderFrame`에 `cap_cols/cap_rows` 필드 추가 (capacity 추적)
  - D2: `row(r)` stride를 **physical** (`cap_cols`) 로 변경
  - D3: `row(r)` span length는 **logical** (`cols`) 로 유지 (consumer 무수정)
  - D6: metadata-only 조건 = `new_c <= cap_cols && new_r <= cap_rows`
  - D7: capacity 초과 시 new cap = `max(current, new)` + reallocate + row-by-row remap
  - D12: `resize()` 구현 = `_api.reshape()` + `_p.reshape()` + `dirty_rows` 전체 설정

### Do
- 구현 범위:
  - `src/renderer/render_state.h`: `RenderFrame` 2-tier 구조 추가 (~30줄)
  - `src/renderer/render_state.cpp`: `reshape()` 메서드 구현 + `resize()` 단순화 (~80줄)
  - `tests/render_state_test.cpp`: 기존 7개 test + 신규 9개 test = 16개 total (~200줄)
  - `src/GhostWin.App/MainWindow.xaml`: E2E automation hook 4개 (AutomationId)
  - `tests/e2e-flaui-split-content/`: FlaUI UIA smoke runner PoC
  - `scripts/run_wpf_diag.ps1`: diagnostic launcher
- 실제 소요: 2일 (2026-04-09 ~ 2026-04-10)

### Check
- 분석 문서: `docs/03-analysis/split-content-loss-v2-gap.md`
- Design 부합도: 100% (모든 FR-01~FR-09 구현)
- 설계 오류: 0개
- 발견된 이슈: 0개 (hardware smoke 통과)

---

## 결과 요약

### 완료된 항목

- ✅ `RenderFrame` capacity-backed 구조 설계 + 구현
- ✅ `reshape()` 메서드 구현 (metadata-only + reallocate 분기)
- ✅ `TerminalRenderState::resize()` 위임으로 단순화
- ✅ 단위 테스트 16/16 PASS (기존 7 + 신규 9)
  - `test_resize_shrink_then_grow_preserves_content` enabled + PASS
  - `test_reshape_metadata_only` PASS (metadata-only path 검증)
  - `test_reshape_capacity_retention` PASS (capacity 고정 검증)
  - `test_row_stride_physical` PASS (stride 계산 검증)
- ✅ Hardware smoke test 5/5 PASS
  - S1: Alt+V split 후 좌측 pane content 보존 ✅
  - S2: Alt+H split 후 상단 pane content 보존 ✅
  - S3: 3연속 Alt+V split 후 초기 session 생존 ✅
  - S4: Alt+V+H 혼합 split 후 모든 pane content 보존 ✅
  - S5: 명령 실행 → split → 출력 좌측 유지 ✅
- ✅ WPF 빌드: 0 warning, 0 error
- ✅ VtCore 테스트: 10/10 PASS
- ✅ PaneNodeTests: 9/9 PASS
- ✅ Consumer 3개 파일 무수정 (API transparency 확보)
  - `quad_builder.cpp`: `frame.row(r)` 스팬 길이만 사용 → 변경 불필요
  - `terminal_window.cpp`: `frame.cols/rows_count` 참조 → 변경 불필요
  - `ghostwin_engine.cpp`: 동일

### 미완료/연기된 항목

- ⏸️ Cursor position 범위 외부 clamping: Plan NG4, 관찰된 문제 없음 → follow-up 후보
- ⏸️ Capacity auto-shrink (high-water mark 관리): Plan NG1, follow-up 후보
- ⏸️ Performance benchmark: Plan NG5 → `render-overhead-measurement` follow-up으로 분리

---

## 배운 점

### 잘된 점
- **Empirical-first RCA**: 3라운드 에이전트 분석의 5~6개 가설 분열에서, hardware diagnostic (`GHOSTWIN_RESIZE_DIAG=1`) 로그로 `sessionId != 0` 가드 hypothesis를 **객관적으로 확정**. 추측 없는 근거 기반 수정 (feedback_hypothesis_verification_required 준수)
- **구조적 근본 해결**: single-call hotfix (`4492b5d`)의 한계를 인식 → `std::vector` 패턴을 도입해 invariant를 구조적으로 강화. Workaround가 아닌 redesign으로 해결
- **Consumer API 보호**: `RenderFrame::row()` 의 span length 보존으로 `quad_builder.cpp` 등 3개 파일을 **완전히 무수정** 유지. API transparency 확보로 유지보수성 극대화
- **Test 주도 검증**: `test_resize_shrink_then_grow_preserves_content` 의 empirical FAIL을 기준으로 fix 성공/실패 판정. 추측 검증 금지 규칙 준수

### 개선할 점
- **Multi-layer resize sequence의 조기 발견**: first-pane-render-failure cycle에서 `4492b5d` hotfix 직후 이미 shrink-then-grow 패턴이 존재했지만, e2e visual 검증 실패 원인을 즉시 파악 못함 → unit test로 empirical을 구성해야 함을 재확인
- **진단 인프라 구축 cost**: hardware diagnostic 로그 수집 → 분석 → hypothesis 확정까지 2일 소요. **structured logging + automated replay** 도구가 있었으면 RCA 시간 단축 가능
- **Memory footprint 모니터링 미실시**: extreme case (300×120) 에서 ~1.15MB/session 이지만, **실제 user 환경에서 극단적 창 크기로 극도로 자주 resize하는 패턴이 있는지 미측정** → follow-up `render-overhead-measurement` 에서 GPU-Z 등 실측 필요

### 다음 사이클에 적용할 점
- **Shrink-then-grow pattern 의 조기 감지**: render state 변경 시 "intermediate transient state" 가 존재하는 WPF/UI framework 과 상호작용하면, **단일 call 기반 invariant로는 부족** → 모든 resize 캐시 유지 후 sequence 기반 검증 도입
- **Unit test 스캐폴딩 선행**: 근본 원인 분석 전에 "shrink 후 grow" "grow 후 shrink" 등 **stress sequence test** 를 먼저 작성 → hypothesis 검증 속도 향상
- **Structural redesign vs hotfix 의 판단 기준**: single-call 호환성만으로 "완료"로 판정하지 말고, **full sequence idempotency** 를 design requirement로 상향

---

## 기술 지표

| 항목 | 수치 |
|------|------|
| **Match Rate** | 100% (모든 FR-01~FR-09 구현, 설계 오류 0) |
| **Test Coverage** | 16/16 PASS (기존 7 + 신규 9) |
| **소스 코드 변경** | 약 110줄 (render_state.h/cpp 합계) |
| **테스트 코드 변경** | 약 200줄 (신규 test 9개 포함) |
| **빌드 품질** | WPF: 0W/0E, VtCore: 10/10 PASS, PaneNodeTests: 9/9 PASS |
| **Hardware Smoke** | 5/5 PASS |
| **Duration** | 2일 (2026-04-09 Plan RCA → 2026-04-10 hardware verification) |
| **Memory Overhead** | 100×30: ~96KB/session, 300×120: ~1.15MB/session |

---

## 파일 변경 사항

| 파일 | 변경 | 영향 |
|------|------|------|
| `src/GhostWin.App/Controls/PaneContainerControl.cs` | `&& sessionId != 0` 가드 제거 (1줄) | **핵심 fix** — session 0 host 재사용 복원 |
| `src/renderer/render_state.h` | `RenderFrame`에 `cap_cols/cap_rows` 필드 추가, `row()` 메서드 수정 | 방어적 인프라 |
| `src/renderer/render_state.cpp` | `reshape()` 구현, `resize()` 단순화, diagnostic logging 추가 | 구현 로직 변경 |
| `tests/render_state_test.cpp` | 16개 test (기존 7 + 신규 9), `main()` 주석 해제 | 회귀 방지 인프라 |
| `src/GhostWin.App/MainWindow.xaml` | E2E automation hook 4개 (AutomationId="E2E_*") | 자동화 지원 |
| `tests/e2e-flaui-split-content/` | FlaUI UIA smoke runner PoC | E2E 자동 검증 도구 |
| `scripts/run_wpf_diag.ps1` | Diagnostic launcher 스크립트 | 진단 편의성 |

**Consumer 무수정**: `quad_builder.cpp`, `terminal_window.cpp`, `ghostwin_engine.cpp`

---

## 후속 작업

### High Priority
- **`e2e-mq7-workspace-click`**: sidebar click workspace 전환 regression (e2e-evaluator-automation에서 독립 발견)

### Medium Priority
- **`repro-script-fix`**: `repro_first_pane.ps1` AMSI window-capture 차단 우회
- **`runner-py-feature-field-cleanup`**: `runner.py:344` hardcoded cleanup

### Low Priority
- **`first-pane-regression-tests`**: WPF library-level 참조 제약 조사
- **`adr-011-timer-review`**: `TsfBridge.OnFocusTick` dead-code 정식 제거
- **`render-overhead-measurement`**: RenderDiag off/on latency 비교
- **`keydiag-log-dedupe`**: bubble handler duplicate log 제거
- **`keydiag-keybind-instrumentation`**: LogKeyBindCommand 호출 경로 완전성
- **`main-window-vk-centralize`**: VK_CONTROL/SHIFT/MENU centralize
- **`e2e-flaui-cross-validation-run`**: FlaUI UIA 경로 vs H-RCA4 fix 없이도 동작 여부

---

## 체크리스트

- ✅ Plan 문서 작성 완료
- ✅ Design 문서 작성 완료
- ✅ 모든 FR-01~FR-09 구현 완료
- ✅ Unit test 16/16 PASS
- ✅ Hardware smoke 5/5 PASS
- ✅ WPF 빌드 0W/0E
- ✅ Consumer 3파일 무수정 (API transparency)
- ✅ 구조적 invariant 확보 (capacity-backed 2-tier design)
- ✅ CLAUDE.md Follow-up Cycles row 8 상태 업데이트 (completed로 표기)

---

## 결론

**split-content-loss-v2 cycle은 완료되었습니다.**

3라운드 40명+ 에이전트 독립 분석에서 5~6개 가설로 분열(합의 실패)했으나, `GHOSTWIN_RESIZE_DIAG` + `GHOSTWIN_RENDERDIAG` 진단 로그를 hardware에서 수집하여 **투표율 15%였던 `sessionId != 0` 가드 가설이 실제 원인**임을 empirical하게 확정했습니다.

핵심 fix는 `PaneContainerControl.cs`의 `&& sessionId != 0` 제거 1줄. 부수적으로 `RenderFrame` capacity-backed 재설계(방어적 인프라) + 진단 로그 + 16개 단위 테스트를 확보했습니다.

**Related Follow-up**: CLAUDE.md Follow-up Cycles row 8 참조 (completed 2026-04-10)
