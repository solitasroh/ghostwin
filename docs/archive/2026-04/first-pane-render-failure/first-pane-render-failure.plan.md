# first-pane-render-failure — Planning Document

> **Summary**: 앱 콜드 스타트 시 첫 pane 이 빈 화면으로 남는 production bug. e2e-evaluator-automation MQ-1 으로 capture 되었고, 사용자 hardware 검증으로 "capture timing 이 아니라 실제 render failure" 확정됨. bisect-mode-termination v0.1 §5 R2 ("초기 pane HostReady 레이스, High impact / Low~Medium likelihood") 의 최초 reproduction. CLAUDE.md TODO `_initialHost 흐름을 폐기하고 PaneContainer 가 host 라이프사이클 단일 owner 가 되도록` 의 merge target.
>
> **Project**: GhostWin Terminal
> **Version**: WPF Migration M-9 + Phase 5-E.5 P0 follow-up
> **Phase**: Phase 5-E.5 부채 청산 — Production Bug fix #1
> **Author**: 노수장
> **Date**: 2026-04-08
> **Status**: Draft
> **Previous (analysis)**: `docs/archive/2026-04/e2e-evaluator-automation/e2e-evaluator-automation.report.md` §4.6 + §8.5 (MQ-1 production bug, follow-up cycle 1)
> **Previous (latent diagnosis)**: `docs/02-design/features/bisect-mode-termination.design.md` v0.1 §5 R2 (HostReady race, High×Low~Medium → 본 cycle 에서 High×High 로 reclassify)
> **Related sibling (active)**: `docs/02-design/features/bisect-mode-termination.design.md` (P0-2 종료, v0.3 §10.2 evaluator judgment 가 본 bug 의 cross-cycle 발견 경로)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 앱 콜드 스타트 시 첫 pane 이 비어있는 채로 표시되는 경우가 있음. 사용자가 수개월간 "앱이 느리게 시작한다" 로 체감해 온 현상이 e2e-evaluator-automation MQ-1 capture 와 사용자 hardware verification 으로 **실제 first-pane render failure** 임이 확정됨. bisect-mode-termination Plan v0.1 §5 R2 가 "잠재적 위험, 수동 QA 20회 재시작 시도" mitigation 으로 closeout 했던 위험이 Evaluator 의 첫 run 에서 drop out — bisect R2 (HostReady race, wpf-architect C4) 의 최초 reproduction. **확정**: 빈도가 High×Medium~High (사용자 체감 + 자동화 첫 run capture). |
| **Solution** | 두 단계 접근. Phase 1 (필수): 진단 — KeyDiag 패턴을 따른 `RenderDiag` 또는 기존 `Trace.TraceError` 확장으로 4가지 H 가설 (H1 Dispatcher priority race, H2 SurfaceCreate==0 silent failure, H3 Border re-parent BuildWindowCore 재호출, H4 MainWindow.OnLoaded 타이밍) 을 5-pass evidence-first falsification 으로 1개로 축소. Phase 2 (구조 fix): CLAUDE.md `_initialHost` TODO 와 merge — `PaneContainer` 가 첫 pane host 의 라이프사이클 단일 owner 가 되도록 재구성. RenderInit 의 hwnd 의존성 (현재 bootstrap swapchain 후 즉시 release) 도 본 cycle 에서 함께 정리 가능 여부 평가. **e2e-ctrl-key-injection 의 falsification 방법론을 본 bug 에 그대로 적용** — 추측 금지, 로그 evidence 없이 코드 수정 금지. |
| **Function/UX Effect** | 콜드 스타트 시 첫 pane 이 항상 즉시 렌더링됨 (현재 간헐적 blank). 사용자 체감 "느린 시작" 현상 제거. Pane split 경로는 변경 없음 (이미 정상). 개발자 관점: bisect R2 가 잠재적 → 종료, `_initialHost` ghost 참조 패턴 제거 → MainWindow.xaml.cs 단순화. |
| **Core Value** | **Latent risk → confirmed bug → structural fix** 의 closed loop. e2e-evaluator-automation 의 D19/D20 분리 원칙이 잠재 R2 를 reproduction 으로 격상시켰고, 본 cycle 이 reproduction 을 root cause 로 격상시킨다. Evidence-first falsification (e2e-ctrl-key-injection H1~H9) 의 방법론적 재사용 — production bug 진단의 표준 패턴 확립. P0-2 BISECT termination → P0-* e2e harness/evaluator → P0-bug 본 cycle 의 부채 청산 chain 의 마지막 piece. |

---

## 1. Overview

### 1.1 Purpose

**문제 정의 (사용자 관찰 + 자동화 capture)**:

> 앱을 콜드 스타트 (process kill 후 재시작) 했을 때, 첫 번째 pane (workspace 의 root pane) 이 검은 화면 / 빈 화면 / partial 화면으로 남는 경우가 있다. Alt+V 또는 Alt+H 로 split 하면 새로 생긴 pane 은 정상 렌더링되지만, **첫 pane 만** 비어있다.

**확정 근거**:
1. **e2e-evaluator-automation MQ-1** (`docs/archive/2026-04/e2e-evaluator-automation/e2e-evaluator-automation.report.md` §4.6) — Evaluator 가 `diag_all_h9_fix` run 의 첫 screenshot 에서 first-pane render 실패를 capture
2. **사용자 hardware verification** — "capture timing artifact 가 아니라 실제 render failure 다" (CTO Lead correction)
3. **bisect-mode-termination Plan v0.1 §5 R2** — wpf-architect council C4 가 사전 식별한 race 의 정확한 reproduction (High×Low~Medium 으로 mitigation 했던 잠재 위험이 실제 발현)
4. **CLAUDE.md `_initialHost` TODO** — 본 cycle 의 `merge target` 으로 명시됨 (TODO 가 작성된 시점부터 알려진 구조적 약점)

**본 cycle 의 목표**: 추측 없이 evidence-based diagnosis 후 root cause 1개 confirm → structural fix 적용 → 회귀 0 보장.

### 1.2 Background

**역사적 맥락**:

| 시점 | Event | 본 bug 와의 관계 |
|---|---|---|
| 2026-03 (Phase 4-A) | WPF migration M-3 — `MainWindow.InitializeRenderer` 가 `_initialHost` 패턴 도입. RenderInit 이 HWND 를 요구하므로 host 를 미리 만들고, 나중에 PaneContainer 에 transfer | 본 race 의 origin |
| 2026-04-07 (P0-2 Plan v0.1) | bisect-mode-termination wpf-architect council 이 §5 R2 에 "초기 pane HostReady 레이스, **High impact / Low~Medium likelihood**, mitigation = 수동 QA 20회 재시작 시도" 로 식별. **재현 안 됨** | 잠재적 위험으로 closeout |
| 2026-04-07 (P0-2 종료) | bisect-mode-termination v0.1 design + Operator 8/8 retroactive QA 로 closeout. R2 는 "수동 QA 에서 재현 안 되었으므로 여전히 잠재적" 상태 유지 | False negative 의 첫 사례 |
| 2026-04-08 (e2e-evaluator-automation Step 6 retroactive) | Evaluator 의 first run (`diag_all_h9_fix`) 에서 MQ-1 partial-render capture. 초기 가정: WGC capture timing artifact | Latent → ambiguous |
| 2026-04-08 (사용자 hardware correction) | 사용자가 "실제 first-pane render failure" 로 정정 → bisect R2 의 **최초 reproduction** 확정. e2e-evaluator-automation report §4.6 narrative 격상 | **Latent → Confirmed** |
| 2026-04-08 (본 cycle) | `first-pane-render-failure` plan 시작 | Confirmed → Root cause → Fix |

**왜 수동 QA 에서 재현 안 되었는가**:

1. **Dispatcher priority race 는 hardware 의 단일 frame timing 에 민감**. 사람이 앱을 재시작하는 속도 ~1-2초 vs Evaluator 가 동기화된 fast restart 에서 race window 가 미세하게 다름
2. **기존 수동 QA 가 visual regression 을 "ConPty 출력 늦은 것" 과 구분하지 못함**. PowerShell 프롬프트가 늦게 나오는 정도의 issue 로 false-negative
3. **D19/D20 분리 원칙의 부재** — Operator 가 evidence 를 모으고 Evaluator 가 시각 판단을 한다는 분리가 없을 때 인간의 quick visual scan 은 partial render 를 "정상" 으로 흡수 (e2e-evaluator-automation report §7 의 D19/D20 closed loop 화 narrative)

**bisect R2 의 R 에서 R 로 (Risk → Reproduction)**:

bisect-mode-termination v0.3 §10.2 (e2e-evaluator-automation 이 작성한 retroactive note) 가 본 cycle 의 진입점. R2 가 "Low~Medium likelihood" 로 분류된 이유는 council 시점에서 reproduction evidence 가 없었기 때문 (정직한 분류). 본 cycle 은 **그 분류를 근본적으로 수정** — High×Medium~High.

### 1.3 Related Documents

**Active 의존성**:
- `docs/01-plan/features/bisect-mode-termination.plan.md` (P0-2, archived 후)
- `docs/02-design/features/bisect-mode-termination.design.md` v0.1 §5 R2 + v0.3 §10.2 (latent diagnosis 의 origin)
- `CLAUDE.md` "TODO Phase 5-E 잔여 품질 항목" 의 `_initialHost` 항목 (merge target)

**Archived references**:
- `docs/archive/2026-04/e2e-evaluator-automation/e2e-evaluator-automation.report.md` §4.6 + §8.3 + §8.5 (production bug 발견 narrative + bisect R2 cross-cycle update)
- `docs/archive/2026-04/e2e-evaluator-automation/e2e-evaluator-automation.design.md` v0.2 (Step 6 findings)
- `docs/archive/2026-04/e2e-ctrl-key-injection/e2e-ctrl-key-injection.design.md` v0.2 §11.7 + §11.6 (5-pass evidence-first falsification 방법론 — 본 cycle 의 진단 단계가 그대로 재사용)

**Source files (확정)**:
- `src/GhostWin.App/MainWindow.xaml.cs:22` (`_initialHost` 필드)
- `src/GhostWin.App/MainWindow.xaml.cs:111-173` (`InitializeRenderer` 메서드 — race 의 발원지)
- `src/GhostWin.App/Controls/PaneContainerControl.cs:48-77` (`AdoptInitialHost` — race 의 후반부, HostReady 구독 시점)
- `src/GhostWin.App/Controls/TerminalHostControl.cs:37-75` (`BuildWindowCore` — `Dispatcher.BeginInvoke` 가 HostReady 를 enqueue)
- `src/GhostWin.Services/PaneLayoutService.cs:178-199` (`OnHostReady` — SurfaceCreate 호출, 0 silent failure 경로 포함)
- `src/engine-api/ghostwin_engine.cpp:247-307` (`gw_render_init` — bootstrap swapchain 생성 후 즉시 release)

---

## 2. Scope

### 2.1 In Scope — Phase 1: Diagnosis (필수)

**진단 instrumentation 추가** (`src/GhostWin.App/Diagnostics/RenderDiag.cs` 신설 또는 기존 KeyDiag 확장)
- [ ] 첫 pane 라이프사이클의 모든 시점에 timestamp + thread + dispatcher priority + state snapshot 로깅
  - `MainWindow.InitializeRenderer` enter
  - `new TerminalHostControl()` 직후 (PaneId=0 상태)
  - `PaneContainer.Content = new Border` 직전/직후
  - `BuildWindowCore` enter / `CreateWindowEx` 후 / `Dispatcher.BeginInvoke(HostReady)` enqueue
  - HostReady 콜백 enter (subscriber count 포함)
  - `Dispatcher.BeginInvoke(Loaded)` 콜백 enter
  - `RenderInit` 호출 전후 + return code
  - `CreateWorkspace` enter/exit
  - `AdoptInitialHost` enter — 시점에 HostReady 가 이미 fire 되었는지 plain bool 기록
  - `OnHostReady` (PaneLayoutService) enter / `SurfaceCreate` return value
  - `RenderStart` 호출
- [ ] runtime gate: `GHOSTWIN_RENDERDIAG=1` 환경 변수 (KeyDiag 패턴 그대로). Release build 에서도 동작
- [ ] log target: 기존 `Trace.TraceError` 또는 `Diagnostics/CrashLog` 와 동일한 sink. 단일 파일에 append, 콜드 스타트 30 회 분 데이터 수집 가능 수준

**가설 falsification — 5-pass evidence-first protocol** (e2e-ctrl-key-injection v0.2 §11.6 패턴)
- [ ] **H1 (primary)**: Dispatcher priority race — `BuildWindowCore` 의 `Dispatcher.BeginInvoke` (default `Normal`=9) 가 `InitializeRenderer` 의 `Dispatcher.BeginInvoke(Loaded`=6) 보다 **먼저** drain → HostReady fire 시점에 subscriber 0 → 이벤트 lost → SurfaceCreate 미호출 → blank
  - Evidence required: HostReady 콜백의 `subscriber_count == 0` 이 빈 화면 case 에서 1 회 이상
- [ ] **H2**: `SurfaceCreate` 가 0 반환 (silent failure) — DX11 state race 또는 RenderInit 직후 호출 시 SurfaceManager 가 아직 ready 가 아님
  - Evidence required: `OnHostReady` 진입 + `SurfaceCreate` return == 0 + `Trace.TraceError` 출력
- [ ] **H3**: Border re-parent triggers `DestroyWindowCore` + 두 번째 `BuildWindowCore` — 첫 BuildWindowCore 의 child HWND 가 destroy 됨, RenderInit 이 binding 한 hwnd 가 stale, 두 번째 HostReady 는 다른 hwnd 로 fire
  - Evidence required: `BuildWindowCore` 가 동일 host 에 2회 호출, hwnd 값 변경
- [ ] **H4**: `MainWindow.OnLoaded` → `Engine.Initialize` 의 비동기 콜백 race — `IsInitialized` true 직후 `InitializeRenderer` 가 dispatch 되지만 native engine 의 일부 thread 가 아직 ready 가 아님
  - Evidence required: RenderInit return code != 0 또는 SurfaceCreate 직전 native error
- [ ] **H5 (catch-all)**: 위 4건이 모두 falsified 되면 새 가설 도출 후 5-pass 재실행

**진단 reproduction**
- [ ] 콜드 스타트 30회 자동화 — `scripts/repro_first_pane.ps1` 신설. 매 회차마다 process kill → start → 1초 대기 → screenshot → kill. KeyDiag 패턴
- [ ] 결과 집계: blank 발생 비율 + 각 회차의 RenderDiag 로그 저장
- [ ] 가설 1개로 축소 후 design phase 진입

### 2.2 In Scope — Phase 2: Structural Fix (Phase 1 결과 의존)

**Option A — Minimal fix** (race 만 fix, `_initialHost` 패턴 유지)
- [ ] HostReady 구독을 visual tree attach 보다 **전에** 이동 — `MainWindow.InitializeRenderer` 가 `PaneContainer.PrepareInitialHost(host)` 를 호출 (subscribe early), 이후 workspace/pane id 확정 후 `PaneContainer.CommitInitialHost(workspaceId, paneId, sessionId)` 로 finalize
- [ ] AdoptInitialHost 의 두 단계 분리. 첫 단계에서 HostReady += OnHostReady 만 수행

**Option B — Structural fix** (CLAUDE.md TODO 와 merge — **권장**)
- [ ] `_initialHost` 필드 삭제. `MainWindow.xaml.cs` 의 `InitializeRenderer` 가 host 를 직접 만들지 않음
- [ ] `PaneContainer` 가 `Initialize(IWorkspaceService, IEngineService, ...)` 시점에 첫 host 를 본인 visual tree 에 attach 하기 전에 HostReady 구독을 끝내도록 재구성
- [ ] `RenderInit` 의 hwnd 의존성 검토 — bootstrap swapchain 이 release 즉시 폐기되므로, hwnd=null 또는 dummy 로 호출 가능한지 평가. 가능하면 `RenderInit(null, ...)` 으로 변경하여 host 의존성 완전 분리
- [ ] 첫 pane 과 split pane 이 **동일한 코드 경로** 를 따르도록 통일. 특수 케이스 0개

**선택 기준** (Design phase 에서 확정):
- Phase 1 진단이 H1 만 confirm: Option A 충분
- Phase 1 진단이 H1 + H3 또는 H4 confirm: Option B 필요
- Phase 1 진단이 H2 만 confirm: Option A 도 Option B 도 부족 — DX11 state machine 직접 fix

**검증 (양 옵션 공통)**
- [ ] 콜드 스타트 30회 자동화 재실행 — blank 발생 0회 (사전 fail-fast 기준)
- [ ] PaneNode 9/9 PASS (회귀 0)
- [ ] e2e harness MQ-1 (single pane render) PASS
- [ ] e2e harness MQ-2~MQ-8 PASS (split/focus/close 회귀 0)
- [ ] e2e Evaluator full run — MQ-1 visual verdict PASS

**문서 갱신**
- [ ] `bisect-mode-termination.design.md` v0.4 §10.2 → §10.3 — R2 reclassification (Low~Medium → High×High → Closed) 및 본 cycle reference
- [ ] `CLAUDE.md` "TODO Phase 5-E 잔여 품질 항목" 의 `_initialHost` 항목 종료 표시
- [ ] `CLAUDE.md` "프로젝트 진행 상태" 표에 본 cycle 추가

### 2.3 Out of Scope

- **MQ-7 sidebar workspace click regression** — `e2e-mq7-workspace-click` follow-up cycle. 본 cycle (MQ-1 fix) 후 재평가하여 cascade 였는지 독립 regression 인지 판정
- **`runner.py:344` `feature` field hardcoded cleanup** — `runner-py-feature-field-cleanup` micro-cycle
- **DX11 SurfaceManager refactoring** — H2 가 confirm 되지 않는 한 DX11 state machine 직접 수정 금지
- **WPF HwndHost 전체 재설계** — 본 race 만 fix, HwndHost 패턴 자체는 유지
- **Pane split race** — split pane 은 PaneContainerControl.BuildElement 가 host 생성과 동시에 HostReady 를 구독하므로 본 race 와 무관
- **bisect-mode-termination Plan v0.1 §5 R3** (`SurfaceCreate==0` silent failure) 의 일반화된 에러 핸들링 — 본 cycle 은 R3 가 H2 로 confirm 될 경우에만 fix, 그 외는 향후 cycle
- **Window.OnLoaded → Engine.Initialize 비동기 패턴 전환** — H4 confirm 시에만, 그 외 유지

### 2.4 Cross-cycle 의존성

| Cycle | 관계 | 본 cycle 의 영향 |
|---|---|---|
| `bisect-mode-termination` (P0-2, Active sibling) | 본 cycle 이 v0.4 entry 작성 — R2 reclassification + 본 cycle 의 root cause 결과 추가 | bisect cycle 의 archive 시점은 본 cycle 의 fix 검증 후 |
| `e2e-evaluator-automation` (P0-*, Archived) | 본 cycle 의 origin. report §4.6 narrative + §8.5 follow-up #1 | 변경 없음 (archived) |
| `e2e-ctrl-key-injection` (P0-*, Archived) | 5-pass falsification 방법론을 본 cycle 이 그대로 재사용 | 변경 없음 (archived) |
| `e2e-mq7-workspace-click` (Deferred) | 본 cycle fix 후 evaluator 재실행하여 MQ-7 이 cascade 였는지 판정 | 본 cycle 종료 후 trigger |
| `runner-py-feature-field-cleanup` (Micro) | 무관, 별도 처리 | 무관 |
| `Phase 5-F session-restore` (Downstream) | session-restore plan 진입의 마지막 blocker | 본 cycle closeout 후 진입 가능 |

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| FR-01 | 콜드 스타트 시 첫 pane 이 사용자 가시 ConPty 프롬프트와 함께 즉시 (≤ 200ms) 렌더링 | **High** | Pending |
| FR-02 | 진단 instrumentation 이 환경 변수 gate (`GHOSTWIN_RENDERDIAG=1`) 로 켜지며, 꺼진 상태에서 production overhead 0 | High | Pending |
| FR-03 | 30회 콜드 스타트 자동화 스크립트가 blank 발생 0/30 을 reproducible 하게 검증 | High | Pending |
| FR-04 | Pane split 경로 (Alt+V/H) 가 회귀 없이 동작 (PaneNode 9/9 + e2e MQ-2~MQ-8 PASS) | High | Pending |
| FR-05 | bisect-mode-termination v0.1 §5 R2 가 본 cycle 의 root cause 와 fix 로 closed 표시 | Medium | Pending |
| FR-06 | (Option B 채택 시) `_initialHost` 필드 + `AdoptInitialHost` 특수 경로 제거, 첫 pane 과 split pane 이 동일 코드 경로 | Medium | Pending |

### 3.2 Non-Functional Requirements

| Category | Criteria | Measurement Method |
|----------|----------|-------------------|
| **Reliability** | 30회 콜드 스타트에서 blank 0회 | `scripts/repro_first_pane.ps1` 자동화 + 결과 JSON |
| **Diagnostic Cost** | RenderDiag overhead (꺼진 상태) — gating cost ≤ 1 cache line lookup | KeyDiag와 동일 패턴 (`LEVEL_OFF` early-return) |
| **Diagnostic Cost (켜진 상태)** | 콜드 스타트 latency 증가 ≤ 50ms (사람이 체감 못 함) | 30회 평균 latency 측정 |
| **No Regression** | PaneNode 9/9 PASS, e2e harness 8/8 PASS, ConPty I/O 정상 | `scripts/test_ghostwin.ps1` + `scripts/test_e2e.ps1 -All -Evaluate` |
| **Documentation Honesty** | 진단 단계의 모든 가설 falsification 결과 (PASS/FAIL + evidence) 문서화 | Design phase v0.x 가 H1~H4 진단 결과 표 |

---

## 4. Success Criteria

### 4.1 Definition of Done

- [ ] **Phase 1 Diagnosis**: 4개 가설 H1~H4 중 1개 (또는 1개 이상) 가 evidence-based 로 confirm, 나머지는 evidence-based 로 falsified. 5-pass protocol log 남김
- [ ] **Phase 2 Structural Fix**: Confirmed root cause 에 대응하는 fix 적용 (Option A 또는 B). 빌드 성공 + 진단 reproduction 0/30 blank
- [ ] **Verification Suite**: PaneNode 9/9 PASS + e2e harness `-All -Evaluate` 8/8 PASS (MQ-1 포함) + Evaluator full run JSON 첨부
- [ ] **Cross-cycle update**: bisect-mode-termination design v0.4 §10.3 entry 작성, R2 closed 표시
- [ ] **CLAUDE.md update**: `_initialHost` TODO 항목 종료 (Option B 채택 시) + 본 cycle 의 archive entry 추가
- [ ] **Report**: `docs/04-report/first-pane-render-failure.report.md` 작성 + Match Rate 계산

### 4.2 Quality Criteria

- [ ] **Falsification rigor**: 단 하나의 가설도 추측만으로 confirm/falsify 되지 않음. 모든 결정에 RenderDiag log evidence 첨부
- [ ] **Single change discipline**: Phase 2 fix 는 1개 commit 으로 분리 가능한 단위. 부수 refactoring 금지 (CLAUDE.md `feedback_no_compromise_quality.md` + behavior.md "우회 금지")
- [ ] **No workaround**: 만약 정석 fix 가 어려워도 임의 휴리스틱 (e.g., `Thread.Sleep(100)` 후 재시도, double-init) 도입 절대 금지. 정석이 실패하면 사용자에게 보고 후 재진단
- [ ] **No regression**: 모든 pane 관련 회귀 테스트 PASS
- [ ] **Reproducibility**: 진단 단계에서 사용한 30회 cold start 스크립트가 3rd party 가 재실행 가능

### 4.3 Acceptance Gates (Design phase 에서 확정될 수치)

| Gate | 기준 | 측정 |
|---|---|---|
| **G1 Diagnosis** | H1~H4 중 ≥ 1 confirmed + 나머지 falsified, evidence 첨부 | RenderDiag log + 진단 표 |
| **G2 Reproduction baseline** | Fix 적용 전 30회 cold start 에서 blank 발생 ≥ 1회 (확정 reproduction) | `repro_first_pane.ps1` 결과 |
| **G3 Fix verification** | Fix 적용 후 30회 cold start 에서 blank 0회 | `repro_first_pane.ps1` 결과 |
| **G4 Regression unit** | PaneNode 9/9 PASS | `scripts/test_ghostwin.ps1` |
| **G5 Regression e2e** | e2e harness `-All` 8/8 + Evaluator MQ-1 visual PASS | `scripts/test_e2e.ps1 -All -Evaluate` |
| **G6 Cross-cycle update** | bisect-mode-termination design v0.4 entry + CLAUDE.md update | git diff |
| **G7 No new TODO** | Fix 가 새로운 `// TODO` 또는 `// FIXME` 를 도입하지 않음 | `grep` |

---

## 5. Risks and Mitigation

| # | Risk | Impact | Likelihood | Mitigation |
|---|---|---|---|---|
| R1 | Phase 1 진단이 4개 가설 모두 falsify (H5 catch-all 까지) | **High** | Low | 5-pass protocol 의 catch-all 단계에서 새 가설 도출. 사용자에게 진행 상황 보고 후 재진단. e2e-ctrl-key-injection 의 H1~H8 falsification → H9 도출 패턴이 선례 (8 가설 falsify 후 9번째 가 정답) |
| R2 | Phase 1 진단 reproduction 자동화 (30회 cold start) 가 blank 를 재현 못 함 | **High** | Medium | 이 경우 evidence 부족 → fix 진행 금지. 사용자 hardware 에서 직접 reproduction (사용자 협조 필요). KeyDiag 의 `flush_pending` 패턴으로 buffered log 손실 방지 |
| R3 | Option B (structural fix) 가 split pane 경로에 회귀를 도입 | **High** | Medium | PaneNode 9/9 + e2e harness 8/8 회귀 게이트. Design phase 에서 BuildElement vs PrepareInitialHost 의 코드 경로 통일 검증. Code review (council) 필수 |
| R4 | RenderInit 의 hwnd 의존성 제거가 DX11 init 에 영향 | Medium | Medium | hwnd 분리는 Option B 의 sub-task. Phase 1 진단 후 별도 spike 로 가능성 평가. 위험 시 hwnd 의존성 유지 (Option A 채택) |
| R5 | RenderDiag instrumentation 이 race window 자체를 변경 (Heisenbug) | Medium | Low | 모든 로그가 `Dispatcher.BeginInvoke` 가 아닌 동기 `Trace.WriteLine` 또는 lock-free queue 사용. KeyDiag 패턴 — 이미 e2e-ctrl-key-injection 에서 검증된 non-perturbing instrumentation |
| R6 | bisect-mode-termination v0.1 §5 R2 의 mitigation ("수동 QA 20회") 자체가 실패 함을 본 cycle 이 입증하지만, 그 사실이 rkit 평가 보고서의 "정직한 불확실성" 원칙과 모순되지 않음을 명확히 해야 함 | Low | High | 본 cycle plan §1.2 "R2 의 R 에서 R 로" narrative 가 이미 처리. 추가 처리 불필요 |
| R7 | H2 (`SurfaceCreate==0`) 가 confirm 되면 Option A/B 모두 부적합, DX11 직접 fix 필요 | Medium | Low~Medium | 본 cycle scope 를 SurfaceCreate fix 로 pivot. Plan revision 필요. 사용자 보고 후 결정 |
| R8 | "콜드 스타트" 정의 모호 — 사용자 사례와 자동화 사례 가 다른 race window 를 trigger | Medium | Medium | Reproduction script 가 process kill → 1초 wait → start 의 명시적 timing 사용. Design phase 에서 timing variation 표준화 |

---

## 6. Architecture Considerations

### 6.1 Project Level Selection

| Level | Characteristics | Recommended For | Selected |
|-------|-----------------|-----------------|:--------:|
| Starter | — | — | ☐ |
| Dynamic | — | — | ☐ |
| **Enterprise** | Strict layer separation, council-based design, evidence-first diagnosis | WPF Native production bug (race condition) | ☑ |

본 cycle 은 **L2_Standard, Enterprise level** 적용. 5-pass falsification + council review (Design phase) 필수.

### 6.2 Key Architectural Decisions (Plan-level — Design 에서 finalize)

| Decision | Options | Tentative | Rationale |
|----------|---------|-----------|-----------|
| Diagnosis strategy | Random debugging / 5-pass falsification / Trace logging only | **5-pass falsification + RenderDiag** | e2e-ctrl-key-injection 에서 검증된 패턴, 추측 금지 원칙과 일치 |
| Diagnosis instrumentation pattern | new RenderDiag.cs / extend KeyDiag.cs / inline Trace | **new RenderDiag.cs (KeyDiag 동일 패턴)** | 책임 분리, KeyDiag 보존, runtime gate 동일성 |
| Reproduction harness | Manual / PowerShell loop / e2e harness extension | **PowerShell loop (`scripts/repro_first_pane.ps1`)** | 빠른 iteration, e2e harness 와 별도 (e2e harness 는 fix 검증 단계) |
| Fix scope | Option A (minimal race fix) / Option B (structural `_initialHost` 폐기) / Hybrid | **Option B 권장, Phase 1 진단 결과 의존** | CLAUDE.md TODO merge target, 첫 pane = split pane 경로 통일 |
| RenderInit hwnd 의존성 | 유지 / null 허용 / dummy hwnd | **Phase 1 진단 후 결정** | bootstrap swapchain 이 즉시 release 되므로 분리 가능성 있음 |
| 가설 검증 순서 | H1 → H2 → H3 → H4 / 모두 동시 / 우선순위별 | **H1 우선 (council 사전 식별), H2~H4 병렬** | wpf-architect council 의 사전 분석 신뢰 + 시간 효율 |
| Logging sink | Trace / file / EventTracing | **Trace + file rotation** | 기존 패턴 (CrashLog) 과 일관 |
| Test data retention | RenderDiag log 영구 / 임시 | **artifacts/repro_first_pane/{timestamp}/** | e2e harness 의 artifacts 패턴 따름 |

### 6.3 Code Path Diagram (현재 — Phase 1 진단 대상)

```
┌─────────────────────────────────────────────────────────────────────┐
│ MainWindow.OnLoaded                                                 │
│   _engine.Initialize(callbackContext)                               │
│   if (!_engine.IsInitialized) return                                │
│   Dispatcher.BeginInvoke(Loaded, InitializeRenderer)                │
└─────────────────┬───────────────────────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────────────┐
│ MainWindow.InitializeRenderer                                       │
│   PaneContainer.Initialize(_workspaceService)                       │
│   var initialHost = new TerminalHostControl()    ◄── PaneId=0       │
│   _initialHost = initialHost                                        │
│   PaneContainer.Content = new Border { Child = initialHost }        │
│       │                                                             │
│       └─► WPF visual tree attach → eventually calls...              │
│           ┌────────────────────────────────────────────────────┐    │
│           │ TerminalHostControl.BuildWindowCore                │    │
│           │   CreateWindowEx → _childHwnd                      │    │
│           │   Dispatcher.BeginInvoke(NORMAL=9, () =>           │    │
│           │     HostReady?.Invoke(this, ...))   ◄── ENQUEUED   │    │
│           └────────────────────────────────────────────────────┘    │
│   Dispatcher.BeginInvoke(Loaded=6, () =>                            │
│     RenderInit(hwnd, ...)                                           │
│     CreateWorkspace                                                 │
│     PaneContainer.AdoptInitialHost(...)  ◄── HostReady += subscribe │
│     RenderStart                                                     │
│     _initialHost = null                                             │
│   )                                                                 │
└─────────────────────────────────────────────────────────────────────┘

Race window:
  Normal (9) > Loaded (6) → BuildWindowCore 의 BeginInvoke 가 먼저 drain
  → HostReady fires
  → subscribers == 0 (AdoptInitialHost 가 아직 안 됨)
  → 이벤트 lost
  → SurfaceCreate 미호출
  → 첫 pane blank
```

**진단 핵심**: 위 race 가 **항상** 발생하면 모든 콜드 스타트가 blank 여야 한다. 그렇지 않다는 것은 BuildWindowCore 의 enqueue 가 어떤 조건에서 Loaded BeginInvoke 보다 늦게 drain 될 수 있다는 것 — 그 조건이 무엇인지가 본 진단의 미스터리.

---

## 7. Convention Prerequisites

### 7.1 Existing Project Conventions

- [x] `CLAUDE.md` 가 행동 규칙 + commit 규칙 + ADR + Phase 진행 상태 보유
- [x] `.claude/rules/behavior.md` "우회 금지 — 근거 기반 문제 해결" 최우선 규칙 (본 cycle 의 5-pass falsification 의 근거)
- [x] `.claude/rules/commit.md` (English only, no AI attribution, develop branch flow)
- [x] `.claude/rules/build-environment.md` (`scripts/build_ghostwin.ps1` 필수)
- [x] PowerShell 스크립트 우선 (bat/cmd 금지)
- [x] 회귀 게이트 — `scripts/test_ghostwin.ps1` (PaneNode 9/9) + `scripts/test_e2e.ps1` (8 MQ scenarios)
- [x] Diagnostics 패턴 — `src/GhostWin.App/Diagnostics/KeyDiag.cs` (runtime env-var gate, Release safe)
- [x] e2e-ctrl-key-injection v0.2 §11.6 의 5-pass evidence-first falsification protocol

### 7.2 Conventions to Define/Verify

| Category | Current State | To Define | Priority |
|----------|---------------|-----------|:--------:|
| **RenderDiag pattern** | KeyDiag exists | Mirror KeyDiag — `GHOSTWIN_RENDERDIAG=1` env-var, lock-free queue, file sink, Release safe | **High** |
| **Reproduction script** | None | `scripts/repro_first_pane.ps1` — 30 iterations, screenshot, JSON summary | **High** |
| **Hypothesis log format** | None | Markdown table in design doc — H#, statement, evidence_required, evidence_found, verdict | **High** |
| **Cross-cycle update format** | bisect-mode-termination v0.3 §10.2 가 선례 | 동일 — design v0.4 에 §10.3 entry 추가 | Medium |
| **Falsification protocol** | e2e-ctrl-key-injection §11.6 가 선례 | 동일 — Plan/Design 에 5-pass step 명시 | Medium |

### 7.3 Environment Variables Needed

| Variable | Purpose | Scope | To Be Created |
|----------|---------|-------|:-------------:|
| `GHOSTWIN_RENDERDIAG` | RenderDiag instrumentation gate (0/1) | Process | ☑ |
| `GHOSTWIN_REPRO_ITERATIONS` | Reproduction script iteration count (default 30) | Script | ☑ |
| `GHOSTWIN_REPRO_DELAY_MS` | Reproduction script process kill → restart delay (default 1000) | Script | ☑ |

### 7.4 Pipeline Integration

GhostWin 은 9-phase Pipeline 적용 외 프로젝트 — Phase 5-E.5 부채 청산 cycle 의 일부로 진행. PDCA 단독 cycle.

---

## 8. Plan-level Implementation Order

> **Note**: Detailed step-by-step is Design phase 의 책임. 본 절은 Plan-level high-level 순서.

| # | Step | Owner | Phase | Gate |
|:-:|---|---|---|---|
| 1 | Plan v0.1 작성 (본 문서) + 사용자 검토 | CTO Lead | Plan | 사용자 승인 |
| 2 | Design phase — wpf-architect + dotnet-expert + code-analyzer 3-agent council. Phase 1 instrumentation + reproduction harness 설계, H1~H4 진단 protocol 확정 | Council | Design | Design v0.1 작성 |
| 3 | Do Phase 1 — RenderDiag.cs 작성 + repro_first_pane.ps1 작성 + 30회 baseline reproduction (G2: blank ≥ 1회 발생 확인) | Single agent | Do | G2 PASS |
| 4 | Do Phase 1 — H1~H4 falsification 5-pass protocol 실행, 가설 1개로 축소 (G1) | Council | Do | G1 PASS |
| 5 | Design v0.2 — Phase 1 진단 결과 반영, Phase 2 fix 옵션 (A/B) 확정, council 동의 | Council | Design (re-entry) | Design v0.2 |
| 6 | Do Phase 2 — Confirmed fix 적용 (Option A 또는 B). 단일 commit 단위로 분리 | Single agent | Do | 빌드 성공 |
| 7 | Check — `scripts/repro_first_pane.ps1` 30회 재실행 (G3) + PaneNode 9/9 (G4) + e2e harness `-All -Evaluate` (G5) | code-analyzer + qa | Check | G3+G4+G5 PASS |
| 8 | Cross-cycle update — bisect-mode-termination design v0.4 §10.3 + CLAUDE.md `_initialHost` TODO closeout (Option B 채택 시) (G6) | CTO Lead | Check | G6 PASS |
| 9 | Report — `docs/04-report/first-pane-render-failure.report.md` 작성 + Executive Summary inline output | report-generator | Report | Match Rate ≥ 90% |
| 10 | Archive — `docs/archive/2026-04/first-pane-render-failure/` 이동 | CTO Lead | Archive | — |
| 11 | Trigger follow-up — `e2e-mq7-workspace-click` cycle plan (MQ-7 cascade vs 독립 판정) | CTO Lead | — | — |

---

## 9. Next Steps

1. [ ] 사용자 검토 — Plan v0.1 의 scope (Phase 1 + Phase 2) 와 Option A/B 권장 합의
2. [ ] Design phase — `/pdca design first-pane-render-failure` 호출, 3-agent council slim 또는 full Enterprise team 선택
3. [ ] Council 부분 충돌 시 CTO Lead 가 hybrid resolution
4. [ ] Plan v0.1 → Design v0.1 → Do (Phase 1 진단) → Design v0.2 (진단 반영) → Do (Phase 2 fix) → Check → Report → Archive

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-08 | Initial draft. e2e-evaluator-automation report §4.6/§8.5 follow-up #1 (HIGH) 으로 trigger. bisect-mode-termination v0.1 §5 R2 latent diagnosis 의 reproduction → root cause cycle. CLAUDE.md `_initialHost` TODO merge target. 4 hypothesis (H1 priority race, H2 SurfaceCreate==0, H3 BuildWindowCore 재호출, H4 Engine.Initialize race) + 5-pass evidence-first falsification (e2e-ctrl-key-injection §11.6 패턴 재사용). Option A (minimal) vs Option B (structural, 권장) 선택을 Phase 1 진단 결과에 의존. 8 risks (R1~R8). 11-step plan-level implementation order. | 노수장 (CTO Lead) |
