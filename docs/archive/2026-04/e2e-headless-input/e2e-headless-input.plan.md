# E2E Headless Input — Planning Document

> **Summary (v0.2)**: Claude Code bash 세션에서 `scripts/test_e2e.ps1 -All` 호출 시 keyboard/mouse 의존 MQ 시나리오(MQ-2~MQ-7)가 **원인 미확정 상태**로 visual PASS에 도달하지 못한다. v0.1은 원인을 "Windows UIPI" 로 단정했으나 v0.2 에서 3건의 empirical 증거로 이 단정을 **반박**하고 Design phase 첫 Spike 를 **RCA (Root Cause Analysis)** 로 고정한다. RCA 우선순위: (1순위) **FlaUI**, (2순위) **Appium + WinAppDriver** (maintenance status 선 falsify), (3순위) 현행 `input.py` SendInput 구현 RCA.
>
> **Project**: GhostWin Terminal
> **Phase**: 5-E.5 부채 청산 follow-up (`pane-split`과 pdca-batch 병렬)
> **Author**: 노수장 (CTO Lead, standalone subagent)
> **Date**: 2026-04-09
> **Status**: **Draft v0.2** — Plan only, Design/Do 미착수, Design 첫 작업 RCA 로 고정
> **Previous**: `docs/archive/2026-04/e2e-evaluator-automation/` (D19/D20 closed loop) + `docs/archive/2026-04/e2e-ctrl-key-injection/` (R4 H9 fix 5/8 → 8/8 on interactive session) + `docs/archive/2026-04/first-pane-render-failure/` (Appendix A hotfix `4492b5d`)
> **Parallel**: `pane-split` (pdca-batch, max 3 concurrent — 본 cycle은 docs-only + `scripts/e2e/**`에 한정된 future 변경이라 파일/빌드 충돌 없음)
>
> **v0.2 변경사항 요약**: (1) UIPI 단일 원인론을 empirical 증거 3건으로 반박, (2) Executive Summary Problem/Solution 재작성, (3) §2.0 신설 — "UIPI 가설 반박 + 진짜 원인 가설 H-RCA1~H-RCA3", (4) §5 에 후보 G (FlaUI) / H (Appium+WinAppDriver) / I (현행 `input.py` SendInput RCA) 추가, (5) §6 에 Q10~Q13 추가, (6) §7 에 R-RCA 추가, (7) §10 Milestones 에 Design 첫 Spike = RCA gate 주입, (8) A~F 후보 우선순위를 "RCA 결과에 따라 확정" 으로 하향

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | **원인 미확정**. `feedback_e2e_bash_session_limits.md` 가 원인을 "Windows UIPI foreground 요구" 로 단정했으나, 2026-04-09 재조사에서 empirical 증거 3건이 이 단정을 **반박**한다: (a) `e2e-test-harness` attempt #1 pywinauto.uia + #2 pywinauto standalone + #3 ctypes SendInput batch 가 **Alt+V 는 성공, Ctrl+T 는 실패** — UIPI 라면 두 키 모두 차단돼야 함, (b) PrintWindow capturer 로 screenshot 은 **정상적으로 찍힘** — IL mismatch 라면 capture 도 차단됨, (c) PostMessage fallback (`645bcac`) 이 operator status=OK 반환 — window queue 는 도달함. 실제 원인은 RCA 로 확정 예정. 상세는 §2.0 empirical 증거 표. 결과적으로 MQ-2 Alt+V, MQ-3 Alt+H, MQ-4 mouse focus, MQ-5 Ctrl+Shift+W, MQ-6 Ctrl+T, MQ-7 sidebar click 6개 시나리오가 bash session 에서 visual verify 불가 상태이며, e2e-evaluator-automation 이 닫은 D19/D20 closed loop 가 keyboard/mouse 경로에서는 여전히 사용자 interactive session 에 의존. |
| **Solution** | **Plan 단계는 결정하지 않는다. Design phase 첫 Spike 를 RCA 로 고정한다.** v0.1 의 6개 후보(A~F) 는 유지하되 우선순위를 "RCA 결과에 따라 확정" 으로 하향. v0.2 에서 신규 후보 3개 추가: **G — FlaUI** (1순위 RCA 도구, `FlaUI.UIA3` .NET WPF UIA 전용, 재현 성공 시 원인이 우리 `input.py` 구현), **H — Appium + WinAppDriver** (2순위, Microsoft archive 여부 Design 첫 30분 falsify), **I — 현행 `scripts/e2e/e2e_operator/input.py` SendInput RCA** (scancode 매핑 · AttachThreadInput · KEYEVENTF_EXTENDEDKEY · Spy++ WM_KEYDOWN wParam/lParam 검증). RCA 확정 후에만 injection layer **근본 원인 해소** 방향을 결정. 각 후보의 **open question** 은 Design 단계로 escalate. |
| **Function/UX Effect** | 사용자 가시 기능 변경 0. 개발자/QA 관점: 본 cycle이 Do 단계까지 성공적으로 닫히면 follow-up cycle `e2e-mq7-workspace-click` (사이드바 regression)을 bash session에서 가장 먼저 visually reproduce + fix 가능해지고, Phase 5-F session-restore부터 모든 downstream feature의 Check phase가 사용자 hardware를 기다리지 않고 closed loop가 된다. 추측(estimation): 체감 Cycle time 단축 — **"잘 모르겠음"** (수치 근거 없음). |
| **Core Value** | `feedback_e2e_bash_session_limits.md`가 "작업 전략 1. Unit test로 우회"로 정의한 **work-around-first** 패러다임을 근본 원인 해소로 전환. `.claude/rules/behavior.md`의 "우회 금지 — 근거 기반 문제 해결"과 "확실한 근거 없이 코드 수정 금지" 원칙을 e2e injection layer 자체에 적용. 2026-04-09 first-pane hotfix verify가 unit test로 우회됐던 것이 구조적으로 해소되는지 여부는 Design의 PoC 결과에 달려 있음 — **현 시점에서 단정 불가**. |

---

## 1. Overview

### 1.1 Purpose

Claude Code bash session (non-interactive, foreground-less)에서 `scripts/test_e2e.ps1 -All`이 keyboard/mouse 의존 MQ-2~MQ-7을 **visual PASS**로 증명할 수 있도록 injection layer를 재설계할 수 있는 후보 전략을 모으고, 각 후보의 feasibility를 선 실증 없이 공정하게 평가하기 위한 Plan 문서.

### 1.2 Load-bearing **Observation** (인용, 원인 해석은 §1.5 에서 재평가)

본 cycle 의 **관찰 사실**은 아래 `feedback` 문서에서 출발한다. 단, v0.1 은 이 문서의 **원인 해석 ("UIPI 때문")** 까지 그대로 수용했으나, v0.2 에서 §1.5 empirical 반박으로 원인 해석 부분은 **무효화**된다. 아래 인용은 **observation layer 만** 유효.

**`feedback_e2e_bash_session_limits.md` (`C:\Users\Solit\.claude\projects\C--Users-Solit-Rootech-works-ghostwin\memory\feedback_e2e_bash_session_limits.md`)**:

- L13-L16 (관찰): `SendInput` 호출이 `WinError 0` 으로 0/N events injected 리턴 (특정 키 조합에서). **원인 해석 "UIPI foreground 획득 불가" 는 §1.5 에서 반박**
- L18-L22 (관찰): PostMessage fallback (`645bcac`) 사용 시 operator status=OK 리턴되나 PrintWindow screenshot 에 split 변화 없음 (MQ-3 Alt+H, 2026-04-09). **원인 해석 "HwndSource routing 안 됨" 은 가설이지 확정 아님** — 본 cycle RCA 대상
- L24-L27 (관찰): WGC capture 는 "window may not be visible" 에러, PrintWindow capturer 는 작동. **§1.5 evidence #2 에서 이 관찰이 오히려 UIPI 가설을 반박**
- L39-L42 (관찰): Claude bash 에서 MQ-2/3/4/5/6/7 이 visual verify 불가 — 본 cycle 이 해소하려는 증상 layer

### 1.3 Related Documents (읽은 순서대로)

| # | Document | 역할 |
|---|---|---|
| 1 | `feedback_e2e_bash_session_limits.md` | **Load-bearing 제약** (quote 대상) |
| 2 | `docs/archive/2026-04/e2e-ctrl-key-injection/e2e-ctrl-key-injection.report.md` | R4 H9 fix, `window.py` focus() Alt-tap 2-line 제거. 8/8 on **interactive** session |
| 3 | `docs/archive/2026-04/e2e-evaluator-automation/e2e-evaluator-automation.report.md` | D19/D20 Operator/Evaluator closed loop. `test_e2e.ps1 -Evaluate/-EvaluateOnly/-Apply` 3-mode wrapper. Retroactive run이 2 silent regression drop out |
| 4 | `docs/archive/2026-04/first-pane-render-failure/*` (Appendix A hotfix `4492b5d`) | split-content-loss hotfix가 e2e visual verify **불가능해서 unit test로 우회**된 최근 사례 |
| 5 | `scripts/e2e/e2e_operator/input.py` (L117-L147, L157-L225) | 현재 injection surface: ctypes `SendInput` batch → `_post_message_chord` fallback |
| 6 | `scripts/e2e/e2e_operator/capture/__init__.py` (L42-L46) | capture factory 순서: `wgc → printwindow → dxcam`. bash session은 `GHOSTWIN_E2E_CAPTURER=printwindow` 강제 |
| 7 | `scripts/test_e2e.ps1` (L67-L76) | `-Scenario/-All/-Bootstrap/-Evaluate/-EvaluateOnly/-Apply` 스위치 |
| 8 | `docs/01-plan/features/wpf-migration.plan.md` §Executive Summary | 본 Plan 포맷의 참조 템플릿 |
| 9 | `docs/01-plan/features/bisect-mode-termination.plan.md` | 소형 최근 Plan 예시 (tabular 스타일) |

### 1.4 Scope Alignment with CLAUDE.md Follow-up Table

CLAUDE.md "Follow-up Cycles (Next Up)" 표:

| 행 | Cycle | 본 cycle과의 관계 |
|---:|---|---|
| 1 | `e2e-mq7-workspace-click` (HIGH) | **다운스트림 소비자**. MQ-7 sidebar click regression을 visually reproduce하려면 본 cycle이 먼저 mouse injection의 bash-compatible 경로를 확보해야 함. **중복 아님** — 본 cycle은 injection-layer breakthrough, 행 1은 production regression 조사 |
| 2 | `first-pane-manual-verification` (MEDIUM) | **동일 제약의 또 다른 희생자**. 본 cycle 성공 시 MEDIUM → 자동화 가능 |
| 3 | `repro-script-fix` (MEDIUM) | AMSI window-capture 차단 우회는 **capture 경로 문제**로 본 cycle scope 외. 단 (D) test-hook 접근에서 diagnostics sidecar를 공유할 여지는 있음 |

### 1.5 **UIPI 단일 원인론 반박 + 진짜 원인 가설** (v0.2 신설)

> **이 섹션은 v0.2 의 핵심 reframe 이다.** v0.1 Plan 은 `feedback_e2e_bash_session_limits.md` 의 "UIPI 때문" 단정을 그대로 수용했다. 사용자가 2026-04-09 피드백에서 "기존 현행 자동화 시스템에서는 UIPI 가 무시되고 정상 제어 가능한데 왜 우리는 단정했나" 로 이 단정에 의문을 제기했고, 재조사 결과 아래 3건의 empirical 증거가 UIPI 단일 원인론과 **모순**됨을 확인했다 (paraphrased).

#### 1.5.1 UIPI 단일 원인론 반박 증거 3건

| # | 관찰된 사실 | 출처 | UIPI 단일 원인론의 모순 |
|:-:|---|---|---|
| **1** | `e2e-test-harness` attempt #1 (`pywinauto.Application(backend='uia')` + `window.type_keys`) + attempt #2 (`pywinauto.keyboard.send_keys` standalone) + attempt #3 (`ctypes SendInput` batch) 모두 **Alt+V ✅ / Ctrl+T ❌** 동일 패턴 | `docs/archive/2026-04/e2e-ctrl-key-injection/e2e-ctrl-key-injection.plan.md:51` attempts 테이블, `project_e2e_test_harness_do_phase.md` (MEMORY), `scripts/e2e/e2e_operator/input.py:92-99` attempt history 주석 | UIPI 가 원인이라면 **두 키 모두 차단돼야** 함. 일부 키 조합만 실패하는 패턴은 OS 보안 차단 (UIPI) 이 아니라 **키 조합 / modifier / scancode 처리 레벨** 의 문제를 시사. Alt+V 가 성공한다는 것 자체가 SendInput event 가 target window 까지 도달하고 있음을 증명 |
| **2** | 2026-04-09 empirical: `GHOSTWIN_E2E_CAPTURER=printwindow` 환경에서 **PrintWindow capturer 로 screenshot 이 정상적으로 찍힘** (MQ-1 초기 렌더 PASS) | `feedback_e2e_bash_session_limits.md:27,35,55`, `scripts/e2e/e2e_operator/capture/__init__.py:13-19` | UIPI (User Interface **Privilege** Isolation) 는 프로세스 **Integrity Level (IL) mismatch** 일 때 차단. IL 이 mismatch 라면 `PrintWindow` API 도 동일하게 차단돼야 함. **PrintWindow 가 작동한다는 것은 operator 와 GhostWin 프로세스의 IL 이 동일 (Medium-Medium) 하다는 empirical 증명** → UIPI 차단 가설은 성립 불가 |
| **3** | 2026-04-09 empirical: PostMessage fallback (`645bcac` commit) 이 operator status=OK 리턴 (PostMessageW `BOOL` non-zero). screenshot 에 split 변화만 없음 | `645bcac` commit (scripts/e2e/e2e_operator/input.py `_post_message_chord`), `feedback_e2e_bash_session_limits.md:20-22` | PostMessage 가 OK 를 리턴한다는 것은 **메시지가 target window 의 message queue 에 정상 posting** 됐다는 뜻. UIPI 가 차단하면 PostMessage 도 실패해야 함 (`SetLastError`). 차단이 아니라 **WPF `HwndSource` → `InputManager` routing 또는 `Keyboard.Modifiers` (=`GetKeyState`) populate 단계의 문제** 로 frame 이 이동 |

**결론**: UIPI 단일 원인론은 위 3건과 모순되므로 **기각**. 진짜 원인은 아래 H-RCA1~3 가설 중 하나 또는 조합일 가능성이 높으나, **현 Plan 시점에서는 확정 불가** — RCA 필수.

#### 1.5.2 진짜 원인 가설 (모두 **추측**, RCA 로 확정 필요)

| 가설 ID | 내용 | 신뢰도 | Falsify 방법 (Design 에서 수행) |
|:-:|---|:-:|---|
| **H-RCA1** | WPF `Keyboard.Modifiers` 는 `GetKeyState` 를 실시간 조회. `PostMessage` 는 OS 전역 키 상태를 업데이트하지 않음 → `KeyBinding Ctrl+T` 가 "Ctrl 눌림" 으로 인식 안 됨. `SendInput` 은 OS 키 상태를 업데이트하므로 이론상 작동해야 하는데 attempt #3 Ctrl+T 실패는 **별개 원인** (H-RCA2/3 중 하나일 가능성) | 추측 (high) | GhostWin Debug 빌드에 KeyDiag instrumentation 재가동 + PostMessage vs SendInput vs FlaUI 3경로 `Keyboard.Modifiers` snapshot 비교. 이미 `e2e-ctrl-key-injection` cycle 에서 KeyDiag 존재 — 재활용 |
| **H-RCA2** | attempt #3 `input.py` 는 `wScan = 0` 즉 **scancode 미설정**. 특정 키 조합 (Ctrl+letter 등) 은 virtual key code 만으로는 부족하고 `MapVirtualKey(VK_CONTROL, MAPVK_VK_TO_VSC)` 결과 scancode 0x1D (LCONTROL) + `KEYEVENTF_SCANCODE` flag 조합이 필요. attempt #4 에서 scancode 를 추가했을 때 Alt+V 까지 깨진 것은 **scancode mapping 자체가 잘못** 됐음을 시사 | 추측 (high) | `input.py:134-135` 에서 `wScan = 0` 확인. Microsoft docs `MapVirtualKey` 기반 최소 repro 로 VK_CONTROL → LCONTROL scancode 확인. Spy++ 로 WM_KEYDOWN `lParam` bits 16-23 (scancode) 검사 |
| **H-RCA3** | `AttachThreadInput` + `SetFocus` 타이밍 또는 순서 오류. `SendInput` 전후의 foreground queue attachment 상태에 따라 일부 키 조합만 routing 실패 가능 | 추측 (medium) | Spy++ + WinSpy++ 로 foreground thread queue 상태 캡처. attempt #4 의 "Alt+V 까지 깨짐" 회귀 재현 + `AttachThreadInput` 호출 순서를 변수로 분리 실험 |

> **참고**: H-RCA1/2/3 은 mutually exclusive 가 아니다. Ctrl+T 실패의 실제 원인은 **복합적** 일 수 있고, FlaUI (후보 G) 가 동일 시나리오를 성공시키면 원인이 우리 `input.py` 구현에 국한됨이 empirical 로 증명된다. FlaUI 가 실패하면 가설 공간이 WPF internals 또는 .NET 10 API 레벨로 확장. 이 실험 설계는 §5.2 G 항목과 §10 Milestones 에 명시.

### 1.6 사용자 피드백 paraphrased (2026-04-09)

> "UIPI 가 원인이라고 단정 짓지 마라. 기존 현행 자동화 시스템들 (FlaUI, WinAppDriver, pywinauto 등) 은 UIPI 를 무시하고 정상 제어하는 사례가 존재한다. 우리가 실패하는 이유는 UIPI 가 아니라 우리 구현 어딘가의 bug 일 가능성이 높다. Plan 에서 UIPI 를 확정 사실로 다루지 말고, Design phase 첫 작업을 RCA 로 고정해서 확정하라."

이 피드백은 `.claude/rules/behavior.md` 의 "우회 금지 — 근거 기반 문제 해결" 원칙 중 **"확실한 근거 없이 코드 수정 금지"** 조항과 **"휴리스틱/추측 기반 우회 절대 금지"** 조항의 직접 적용이다. v0.1 Plan 은 사실상 feedback 문서의 UIPI 해석을 검증 없이 수용한 **heuristic adoption** 이었음을 인정한다.

---

## 2. Scope

### 2.1 In Scope (Plan 문서가 산출할 것)

- [x] Load-bearing **observation** 인용 (§1.2)
- [x] **UIPI 단일 원인론 empirical 반박 + H-RCA1~3 진짜 원인 가설** (§1.5) — v0.2 신설
- [x] **사용자 피드백 paraphrased** (§1.6) — v0.2 신설
- [x] Candidate approaches **A~F (기존) + G/H/I (v0.2 신설, RCA 도구)** brainstorm 테이블 (§5)
- [x] 각 후보의 **open question** 목록 — Design이 답할 것 (§6)
- [x] Success Criteria의 **측정 가능한** 정의 (§4)
- [x] Non-goals 명시 (§2.2)
- [x] Risk matrix — **R-RCA 포함** (§7)
- [x] Parallel execution plan with `pane-split` (§9)
- [x] Milestone ordering — **Design 첫 Spike = RCA gate** (§10)

### 2.2 Out of Scope (Non-Goals) — 명시 금지 목록

- ❌ **Design/Do 진입** — 본 문서는 Plan only. 어떤 후보도 "채택"하지 않음. PoC 실증은 Design 단계 승인 후
- ❌ **`scripts/e2e/**` 코드 수정** — `input.py`, `window.py`, `capture/*.py`, `runner.py` 등 일절 손대지 않음
- ❌ **`src/GhostWin.App/**` production code 수정** — (D) test-hook 후보조차도 Plan 단계에서는 brainstorm만
- ❌ **빌드/테스트 실행** — `scripts/build_*.ps1`, `scripts/test_*.ps1` 호출 금지
- ❌ **Git commit** — Plan 문서 작성 자체도 commit은 user 승인 후
- ❌ **`e2e-mq7-workspace-click` regression 조사** (CLAUDE.md follow-up 행 1) — 본 cycle의 **소비자**이지 **scope가 아님**
- ❌ **신규 MQ 시나리오 추가** — MQ-1~MQ-8 registry는 불변
- ❌ **CI integration** — GitHub Actions/GitLab runner 등
- ❌ **`pane-split` plan/design 파일 수정** — pdca-batch parallel cycle의 자원 격리
- ❌ **Capture backend 재설계** — WGC/PrintWindow/dxcam 순서는 본 cycle의 관심사 아님. `GHOSTWIN_E2E_CAPTURER=printwindow`로 고정된 현 상태를 전제로 함
- ❌ **사용자 hardware 검증을 Plan 단계에서 가정** — 본 cycle의 정의 자체가 "hardware 없이" 돌파하는 것

### 2.3 Explicit Assumptions

| ID | Assumption | 검증 시점 |
|----|-----------|---------|
| A1 | PrintWindow capturer는 bash session에서 안정 동작 중 (2026-04-09 확인됨) | Plan — 확인됨 |
| A2 | MQ-1 (초기 렌더) + MQ-8 (window resize)는 이미 bash session에서 PASS | Plan — `feedback` L35 |
| A3 | WPF `HwndSource` WndProc이 `WM_*KEYDOWN`을 `InputManager`로 routing하는 조건은 **확실하지 않음** (empirical evidence는 "안 됨"이지만 root cause 미확정) | Design PoC (RCA) |
| A4 | ~~`SendInput`의 UIPI foreground 요구는 문서화된 Windows 보안 정책~~ — **v0.2 에서 철회**. §1.5 empirical 증거 3건으로 UIPI 단일 원인론은 기각. SendInput 특정 키 실패의 진짜 원인은 H-RCA1~3 가설 중 하나 또는 조합으로 **RCA 에서 확정 필요** | Design RCA |
| A5 | .NET 10 WPF `System.Windows.Automation.Peers.AutomationPeer` 경로가 `OnTerminalKeyDown` preview handler에 도달 가능한지 **잘 모르겠음** | Design PoC |
| A6 | `PrintWindow` 가 bash session 에서 작동한다는 것은 operator ↔ GhostWin 두 프로세스의 **Integrity Level 이 동일** 하다는 empirical 증명 — **확실** (§1.5 evidence #2) | Plan — 확인됨 |
| A7 | FlaUI `FlaUI.UIA3` 최신 버전이 .NET 10 + WPF + Windows 11 26200 에서 동작하는지 **잘 모르겠음** — Design 첫 Spike 에서 확인 | Design RCA |
| A8 | Microsoft WinAppDriver 의 2026-04 maintenance status **잘 모르겠음** — archive 소문 존재, Design 첫 30분 내 GitHub 공식 repo 에서 falsify | Design RCA |

---

## 3. Requirements

### 3.1 Functional Requirements (Plan 수준)

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| FR-01 | Plan 문서가 Candidate approaches 6개(A~F)에 대해 Feasibility/Effort/Risk/Bash-compat/Security/Production-surface 5축 비교표를 포함한다 | High | **본 문서 §5** |
| FR-02 | 각 후보에 대해 "Confidence" 컬럼을 두어 **확실 / 추측 / 잘 모르겠음**을 명시한다 (user 글로벌 rule §4 준수) | High | **본 문서 §5** |
| FR-03 | Success Criteria를 PrintWindow pixel diff 또는 동등한 **측정 가능한 신호**로 정의한다 | High | **본 문서 §4** |
| FR-04 | Open question 목록이 **Design이 답해야 할 것**으로 구조화된다 | High | **본 문서 §6** |
| FR-05 | Risk matrix가 각 후보별 실패 모드 + mitigation + trigger signal을 포함한다 | Medium | **본 문서 §7** |
| FR-06 | `pane-split`과의 병렬 실행 계획 (파일/빌드/리소스 격리) 이 명시된다 | Medium | **본 문서 §9** |
| FR-07 | CLAUDE.md Follow-up 표 행 1(`e2e-mq7-workspace-click`)과 본 cycle의 **중복 아님** 관계가 §1.4에 명시된다 | Medium | **본 문서 §1.4** |

### 3.2 Non-Functional Requirements (Plan이 요구할 것)

| Category | Criteria | Measurement Method |
|----------|----------|-------------------|
| Honesty | 추측/미확인은 "추측" 또는 "잘 모르겠음"으로 명시 | grep 본 문서 |
| Source citation | 외부 참조에는 MSDN 링크 또는 GitHub URL | §5 Candidate 표 각주 |
| Parallel safety | `pane-split` plan/design 파일 0개 touch | git diff (user 검증) |
| Production surface | Plan 단계에서 production code 참조 ≤ 문서 언급만 (수정 0) | `src/**` git diff empty |
| Build independence | 본 Plan 작성 중 `scripts/build_*.ps1` 또는 `scripts/test_*.ps1` 실행 0회 | shell history |

---

## 4. Success Criteria (Measurable)

Design/Do가 본 cycle을 **close**시키기 위해 필요한 조건. 본 Plan은 이 조건들을 **정의**만 한다.

### 4.1 Primary Criterion (P0 — 필수)

**SC-P0**: `scripts/test_e2e.ps1 -All` 을 Claude Code bash session에서 (사용자 interactive session 아님) 호출했을 때, **N ≥ 3회 연속** 아래 MQ 시나리오가 **Evaluator verdict PASS**를 받는다:

- MQ-2 Alt+V split — PrintWindow capture에서 **좌우 2개 pane**이 visually 식별 가능해야 함. Evaluator 기준: `scripts/e2e/evaluator_prompt.md`의 MQ-2 section (기존 Evaluator prompt가 정의하는 heuristic 그대로 사용 — Plan 단계에서 prompt 재작성 안 함)
- MQ-3 Alt+H split — 상하 2개 pane
- MQ-5 Ctrl+Shift+W close — split 직후 pane 수 감소
- MQ-6 Ctrl+T new workspace — 사이드바 entry count 증가
- MQ-4 mouse focus — active pane border/tint 변화 (heuristic 기준)
- MQ-7 sidebar workspace click — active workspace visual change

> **Note (확실하지 않음)**: 각 MQ의 Evaluator heuristic이 현재 true PASS를 낼 수 있을 만큼 robust한지는 `e2e-evaluator-automation` retroactive run에서 MQ-1/MQ-7 2건이 false positive/silent regression으로 drop out한 이력이 있음. Design 단계에서 heuristic 검증이 선행되어야 하나, **본 cycle의 responsibility는 아님** — Evaluator 개선은 별도 cycle (`e2e-evaluator-heuristics-v2` 가상).

### 4.2 Secondary Criteria (P1 — 강력 권장)

- **SC-P1-a**: 선택된 접근이 production code surface (`src/GhostWin.App/**`, `src/GhostWin.Services/**`) 에 추가하는 라인 수가 측정 가능해야 한다. 목표치는 Design에서 결정 — Plan은 "measurable" 만 요구.
- **SC-P1-b**: 선택된 접근이 `scripts/test_ghostwin.ps1` PaneNode 9/9 유지 (regression 0)
- **SC-P1-c**: 선택된 접근이 사용자 hardware 입력 (`feedback` L50-L58의 hardware smoke 5 chord) 에 0% regression

### 4.3 Tertiary Criteria (P2 — 있으면 좋음)

- **SC-P2-a**: 본 cycle 종료 후 bash session에서의 e2e 실행 시간 (`-All`) 이 사용자 interactive session 대비 ±30% 이내. **추측**: interactive session baseline ≈ 3~5분 (e2e-evaluator-automation Report에서 관측됨, 정확도 낮음)
- **SC-P2-b**: 선택된 접근이 추가 Windows 보안 prompt / UAC elevation / driver install 을 **처음 설정 시점에만** 요구 (매 테스트 실행마다 아님)

### 4.4 Explicit Anti-Criteria (실패로 간주되는 상태)

- **AC-1**: "PostMessage fallback이 status=ok를 리턴하지만 screenshot은 split 없음" — `feedback` L22 empirical이 재현되면 FAIL
- **AC-2**: MQ-2~MQ-7 중 1건이라도 **사용자 hardware에서는 PASS인데 bash session에서는 FAIL** 인 상태가 유지되면 FAIL
- **AC-3**: 접근이 injection layer 근본 원인을 해소했다고 claim 하지만 실측 증거 (log, screenshot hash, Evaluator verdict, RCA 문서 내 가설 falsify chain) 가 없으면 FAIL
- **AC-4** (v0.2 신설): Design phase 에서 **RCA 를 건너뛰고** 구현 후보를 선택하면 FAIL — RCA gate (§10 Milestone 2a) 는 우회 불가

---

## 5. Candidate Approaches (Brainstorm Only — 결정 Design 이월, RCA 선행 필수)

> **중요 (v0.2)**: 본 섹션은 **의사 결정이 아닌 옵션 열거**. v0.2 에서 후보 A~F 의 우선순위는 모두 **"RCA 결과에 따라 확정"** 으로 하향되었다. 신규 후보 G / H / I 는 **RCA 도구** 이며 구현 후보가 아니다. RCA 가 원인을 확정한 뒤에만 A~F 중 실제 구현 대상이 결정된다. 각 후보의 Confidence 컬럼은 user 글로벌 rule §4 ("확실하지 않으면 명시")를 따른다. 모든 "효과"는 Design PoC 전까지 **추측**으로 분류한다.

### 5.1 Candidate Summary Table

#### 5.1.1 기존 구현 후보 A~F (v0.1 유지, 우선순위는 RCA 결과에 따라 확정)

| ID | Approach | Feasibility | Effort (T-shirt) | Risk | Bash-compat? | Security / Signing | Production surface? | Confidence | v0.2 우선순위 |
|---:|---|:--:|:--:|:--:|:--:|---|:--:|:--:|:--:|
| **A** | UI Automation (재시도) — FlaUI / `UIAutomationCore` COM / `pywinauto.uia` raw. v0.2 에서 **후보 G (FlaUI) 가 구체화** 된 형태로 분리 — A 는 일반 UIA 범주 | 불확실 | M | High | **Unknown** | 없음 | No | 잘 모르겠음 | **RCA 결과에 따라 확정** |
| **B** | Kernel-level input driver — Interception (oblita) / 유사 signed driver | 가능 | L | Medium | **Yes (원리상)** | **Signed driver 필수**, UAC install | No | 추측 (원리 기반) | **RCA 결과에 따라 확정** |
| **C** | Low-level keyboard hook — `SetWindowsHookEx(WH_KEYBOARD_LL)` injection | 낮음 | S | High | **No (추측)** | Defender AV 휴리스틱 trigger 가능 | No | 추측 (불가) | **Drop 권고** |
| **D** | WPF in-process test-hook — `GhostWin.App`에 `ITestInputSink` + IPC (Named Pipe / localhost HTTP / AutomationPeer) | 가능 | M | Low | **Yes (구조적)** | 없음 | **Yes** — 신규 API | 추측 (원리 기반) | **RCA 결과에 따라 확정** |
| **E** | WinAppDriver / Appium — v0.2 에서 **후보 H** 로 구체화 | **잘 모르겠음** | M | High | **Unknown** | 없음 | No | 잘 모르겠음 | **RCA 결과에 따라 확정** |
| **F** | Hybrid — PrintWindow capture (현행) + (D) test-hook 또는 (B) driver | 가능 | M-L | Low-Medium | **Yes** | (D) 없음 / (B) 드라이버 | (D): Yes / (B): No | 추측 (원리 기반) | **RCA 결과에 따라 확정** |

> **UIPI 표현 주의 (v0.2)**: v0.1 의 "Bash-compat (확실)" / "bypass" 같은 표현 중 UIPI 를 원인으로 단정한 부분은 §1.5 empirical 반박으로 무효화됨. 위 표에서는 "원리상" 으로 약화 표기하고, 실제 bash-compat 여부는 RCA 결과에 따라 **재평가** 된다.

#### 5.1.2 **RCA 도구 후보 G/H/I** (v0.2 신설, 구현 후보 아님)

| ID | Approach | 목적 | Bash-compat? | Production surface | Effort | 우선순위 |
|---:|---|---|:--:|:--:|:--:|:--:|
| **G** | **FlaUI** (`FlaUI.UIA3` NuGet, .NET WPF UIA 전용 라이브러리) 로 GhostWin 직접 재현. Alt+V / Ctrl+T 동일 시퀀스를 `FlaUI.Core.Input.Keyboard.Type` / `Keyboard.Press` / `FlaUI.UIA3.Application` 경로로 호출. **재현 성공 시** 원인이 우리 `input.py` 구현에 국한됨이 empirical 로 증명 (H-RCA1/2/3 narrow). **재현 실패 시** 가설 공간이 WPF / .NET 10 / OS API 레벨로 확장 | **RCA 1순위 도구** | 미확정 (조사 대상) | 0 (외부 라이브러리) | **S** | **1순위 RCA** |
| **H** | **Appium + WinAppDriver**. 주의: Microsoft 가 WinAppDriver 를 2026 에 archive 했다는 소문 존재 — Design 첫 30분 내 [github.com/microsoft/WinAppDriver](https://github.com/microsoft/WinAppDriver) 공식 repo 에서 maintenance status 확정 필수. Archive 확정 시 H drop. 살아있으면 G 와 독립된 2 경로 empirical 로 원인 공간 축소 | **RCA 2순위 도구** (살아있을 시) | 미확정 | 0 | **M** | **2순위 RCA** (maintenance status 먼저 falsify) |
| **I** | **현재 `scripts/e2e/e2e_operator/input.py` SendInput 구현 RCA**. 최소 repro 로 Ctrl+T 실패 지점 특정. 검증 항목: (a) `MapVirtualKey(VK_CONTROL, MAPVK_VK_TO_VSC)` 로 scancode 0x1D 추출 후 `wScan` 세팅, (b) `KEYEVENTF_SCANCODE` flag 사용 여부, (c) `KEYEVENTF_EXTENDEDKEY` 필요성, (d) `AttachThreadInput` + `SetFocus` 순서 및 타이밍, (e) Spy++ / WinSpy++ 로 실제 `WM_KEYDOWN` `wParam`/`lParam` bits 검사 (특히 bits 16-23 scancode, bit 24 extended). **G/H 모두 fail 또는 G 재현 성공 시 narrow** 에 사용 | **RCA 3순위 도구** (G/H 결과에 따라) | N/A | 0 | **S** | **3순위 RCA** (G/H 결과 반영) |

### 5.2 Candidate Detail

#### A. UI Automation 재시도

- **배경**: `e2e-test-harness` Do phase **attempt 1** (`docs/archive/2026-04/e2e-ctrl-key-injection/e2e-ctrl-key-injection.plan.md` L51) 에서 `Application(backend='uia') + window.type_keys()` 이 **Alt+V/H는 OK, Ctrl+T/Ctrl+Shift+W는 FAIL** 패턴을 보였다. 당시 root cause는 H9 (focus() Alt-tap System Menu activation) 이었고 H9 fix 후 UIA 경로를 **재시도하지 않았다** (`e2e-ctrl-key-injection.design.md` L409 comment: "Still called focus() first → menu activated"). 
- **Open question**: H9 fix가 적용된 현재 UIA backend가 bash session에서 동작하는가? `FlaUI` (Microsoft 공식 계승자) 또는 `UIAutomationCore` COM 직접 호출이 `pywinauto.uia`와 동일한 한계를 가지는가? UIA의 `InvokePattern`이 WPF `KeyBinding`에 **key chord 형태가 아닌 command invocation**으로 도달할 수 있는가 (이 경우 OS level keyboard event 자체가 불필요).
- **Bash-compat 평가**: UIA 자체는 COM API이고 SendInput에 의존하지 않는다. 하지만 `pywinauto.type_keys()`가 내부적으로 SendInput을 쓰는지 여부는 pywinauto version-specific — **잘 모르겠음**. `FlaUI.Keyboard` (.NET) 는 SendInput wrapper — 같은 UIPI 제약. `AutomationPeer.InvokeProvider` 경로는 OS injection 우회 가능 — **확신 필요**.
- **Pros**: production code 수정 0, 기존 인프라 재사용.
- **Cons**: H9 fix 후 재검증 없음, Ctrl-key 실패 원인이 진짜로 H9였는지 vs UIA 자체 한계인지 분리 실험 미수행.
- **참조**: [FlaUI GitHub](https://github.com/FlaUI/FlaUI), [pywinauto UIA backend docs](https://pywinauto.readthedocs.io/en/latest/controls_overview.html#uia-controls).

#### B. Kernel-level Input Driver

- **배경**: Interception ([github.com/oblita/Interception](https://github.com/oblita/Interception)) 은 signed kernel driver가 PS/2·USB HID 이벤트를 가로채고 주입한다. UIPI는 user-mode 정책이므로 kernel 경로는 원천적으로 영향 밖.
- **Open question**: Interception driver가 .NET 10 / Windows 11 26200 에 호환되는가? 드라이버 서명이 현재 유효한가 (배포 시기 확인 필요)? CI / 사용자 로컬에 unattended install 가능한가? WPF `HwndSource`가 kernel-injected 이벤트를 SendInput과 동일 경로로 routing하는가? 
- **Bash-compat 평가**: Driver가 OS level에서 event를 주입하면 WPF는 user physical input과 구분 불가 — **확실** (원리 기반). 단 실측 없음.
- **Pros**: 모든 UIPI 제약 완전 우회, WPF 코드 수정 0.
- **Cons**: signed driver install 요구, Defender/AV 오탐 위험, 개발자 머신 친화도 낮음, CI 친화도 **잘 모르겠음**, 드라이버 life-cycle 관리 (uninstall on test failure 등).
- **참조**: [Interception](https://github.com/oblita/Interception), MSDN [WDM Input Stack](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/).

#### C. Low-level Keyboard Hook

- **배경**: `SetWindowsHookEx(WH_KEYBOARD_LL)` 로 설치된 hook callback에서 `CallNextHookEx` 를 생략하고 이벤트를 "주입"하려 시도하는 접근. 엄밀히는 hook은 **관찰용**이지 주입용이 아님.
- **Bash-compat 평가**: Hook은 `keybd_event` / `SendInput` 을 대체하지 않는다. 단순히 event queue를 가로챌 뿐. 주입이 필요하면 여전히 `SendInput` → UIPI 제약 그대로. **추측**: 불가능에 가까움.
- **Pros**: 없음 (관찰 용도로는 쓸 수 있으나 주입 목적에 부적합).
- **Cons**: Defender가 global keyboard hook을 suspicious behavior로 flagging하는 경우 존재. False positive risk.
- **권고**: Design 단계에서 **falsify 후 폐기** (자원 낭비 방지).

#### D. WPF In-Process Test-Hook

- **배경**: `GhostWin.App` 내부에 `ITestInputSink` 인터페이스를 정의하고, Debug/Development 빌드에서만 Named Pipe / localhost HTTP server 를 열어 외부에서 "Alt+V command invoke" 요청을 받아 **`InputManager.Current.ProcessInput`** 또는 **`RoutedCommand.Execute`** 를 직접 호출. OS level keyboard event를 완전히 우회.
- **Open question**: `InputManager.ProcessInput(new KeyEventArgs(...))` 경로가 `MainWindow.OnTerminalKeyDown`의 `PreviewKeyDown` chain에 동일하게 도달하는가? `Keyboard.Modifiers` 를 올바르게 populate할 수 있는가? Mouse click path (MQ-4/MQ-7)를 `Mouse.Synchronize` 또는 `MouseButtonEventArgs` 직접 raise로 대체 가능한가? Production binary 크기 영향 (`[Conditional("DEBUG")]` vs env-var gate)? Release 빌드에서의 attack surface?
- **Bash-compat 평가**: 본 접근의 핵심 — **OS level injection을 완전 우회**하므로 UIPI, foreground, Defender 전부 무관. Named Pipe는 Windows Integrity Level 내에서 동작하지만 동일 user session이면 OK — **확실**.
- **Pros**: 구조적으로 bash session 호환, production에서 CLI-driven 워크플로우 기반 확보 가능, `e2e-ctrl-key-injection`의 H9 같은 side-effect 원천 차단, Evaluator heuristic과 독립.
- **Cons**: Production code surface 추가 (ADR 후보), 테스트 전용 API의 보안 감사 필요, OS-level 사용자 입력과 **완전히 동등한가**는 실증 필요 (Keyboard.Modifiers, Focus scope, InputScope), WPF version upgrade 시 internal API 변경 risk.
- **선례**: VS Code의 `vscode-test` electron backend, Chromium의 `ChromeDriver` BiDi, Windows Terminal의 `testharness.exe` 접근이 유사 패턴. **확실하지 않음**: 각 선례가 정확히 어떤 layer에서 inject하는지 URL 검증 필요.
- **참조**: [WPF InputManager docs](https://learn.microsoft.com/en-us/dotnet/api/system.windows.input.inputmanager.processinput) — 이 API의 accessibility (internal vs public)는 Design에서 확인 필요 — **잘 모르겠음**.

#### E. WinAppDriver / Appium

- **배경**: Microsoft의 공식 UI Test Automation driver. Appium protocol 위에 WPF/UWP/Win32를 노출.
- **Status**: **확실하지 않음** — 2020~2023 사이 커밋 빈도가 급감했고 Microsoft가 archive 또는 maintenance mode로 전환했다는 커뮤니티 관측이 있음. Design 단계에서 [github.com/microsoft/WinAppDriver](https://github.com/microsoft/WinAppDriver) 의 **최신 커밋 날짜**, **.NET 10 호환성**, **Windows 11 26200 호환성**을 직접 확인해야 한다.
- **Bash-compat 평가**: WinAppDriver 내부 구현이 SendInput 기반이라면 UIPI 제약 동일. UIA 기반이라면 (A)와 동일. **잘 모르겠음**.
- **Open question**: 프로젝트가 아직 active한가? 아니라면 archival 가능.
- **권고**: Design 단계에서 30분 내 status 확인 → active 아니면 폐기, active면 (A)의 하위 변형으로 통합.

#### F. Hybrid: PrintWindow capture + (D) test-hook

- **배경**: `feedback_e2e_bash_session_limits.md` §"작업 전략 1"이 이미 권고한 "Unit test로 우회" 방향과 가장 정렬된 접근. Capture는 현행 PrintWindow (bash-compat 검증 완료) 유지, Input만 (D) test-hook 또는 (B) kernel driver로 교체.
- **Bash-compat 평가**: (D) 또는 (B) 중 어느 하위를 선택하든 bash-compat. Capture는 이미 검증됨.
- **Pros**: 가장 작은 변경량, 기존 `e2e_operator/capture/` factory 재사용, `test_e2e.ps1` 3-mode wrapper 재사용.
- **Cons**: (D) 하위 선택 시 production surface 추가, (B) 하위 선택 시 driver install 요구. 둘 다의 open question 상속.
- **v0.2 권고**: v0.1 의 "default 작업 가설 F(D)" 는 **철회**. RCA 결과에 따라 재평가. FlaUI 가 직접 주입 경로를 성공시키면 F 는 불필요할 수 있음.

#### G. **FlaUI** (v0.2 신설 — RCA 1순위 도구)

- **배경**: [FlaUI](https://github.com/FlaUI/FlaUI) 는 UI Automation v3 (`FlaUI.UIA3`) 과 v2 (`FlaUI.UIA2`) 를 .NET 에서 노출하는 WPF 친화적 라이브러리. pywinauto 의 .NET 계승자 격. `FlaUI.Core.Input.Keyboard`, `FlaUI.Core.Input.Mouse` 가 low-level SendInput 을 wrap.
- **RCA 목적**: v0.1 후보 A ("UIA 재시도") 를 **구체적 도구** 로 landing. FlaUI 를 별도 .NET console 앱 (tests/flaui_rca/ 같은 sandbox, **본 Plan scope 외 — Design 에서 만듦**) 에서 호출해서 GhostWin Alt+V / Ctrl+T 를 bash session 에서 재현 시도. 세 가지 결과 분기:
  1. **Alt+V ✅ Ctrl+T ✅ 동시 성공**: 원인이 우리 `input.py` 구현에 국한됨 empirical 확정 → H-RCA1/2/3 중 (H-RCA2 scancode) 가장 강력 → 후보 I 로 narrow + `input.py` 수정만으로 해결 가능
  2. **Alt+V ✅ Ctrl+T ❌ (동일 패턴 재현)**: 원인이 FlaUI 하위 SendInput 과 우리 구현 하위 SendInput 에 **공통** 으로 존재 → WPF / .NET / OS 레벨 버그 의심 → 후보 D 또는 B 로 확장
  3. **둘 다 ❌ (injection 자체 실패)**: §1.5 empirical 반박에도 불구하고 RCA 가 원인을 재확정해야 함 → 후보 H (WinAppDriver), 후보 C (LL Hook, 진단 용도만), 사용자 hardware 비교 필요
- **Bash-compat 평가**: **미확정** — FlaUI 자체의 SendInput 래핑이 bash session 에서 동작하는지가 본 RCA 의 1차 질문. FlaUI 가 동작 = UIPI 가설 **완전 기각** (§1.5 보강). FlaUI 가 동작 안 함 = 원인 공간 재평가.
- **Pros**: production code 수정 0, 기존 `scripts/e2e/**` 수정 0 (sandbox 에서 실험), 저비용 (S effort), 공식 .NET WPF 라이브러리라 .NET 10 호환성 조사 용이, open source 로 내부 구현 검증 가능.
- **Cons**: Python `e2e_operator` 와 언어 불일치 → Design 에서 sandbox 결과를 `input.py` 에 어떻게 반영할지 별도 논의 필요. FlaUI 의존성 크기 (~수 MB).
- **Plan 단계 open question**: Q10 (§6)
- **참조**: [FlaUI GitHub](https://github.com/FlaUI/FlaUI), [FlaUI Keyboard docs](https://github.com/FlaUI/FlaUI/wiki/Keyboard-and-Mouse), NuGet `FlaUI.UIA3`, `FlaUI.Core`

#### H. **Appium + WinAppDriver** (v0.2 신설 — RCA 2순위 도구, maintenance status 선 falsify)

- **배경**: Microsoft 공식 WinAppDriver 는 Appium protocol 위에 WPF/UWP/Win32 앱의 UI Automation 을 노출. 2018~2020 사이 활발히 개발됐으나 2021 이후 commit 빈도 급감, 2026-04 현재 **archive 여부에 대한 소문** 존재 (확정 아님).
- **RCA 목적**: 후보 G (FlaUI) 와 **독립된 경로** 로 injection 시도. FlaUI 와 다르게 out-of-process 로 동작하기 때문에 만약 FlaUI 가 in-process 제약 (e.g. thread affinity) 에 걸리는 경우 WinAppDriver 는 다른 실패/성공 패턴을 보일 수 있음.
- **선행 falsify (Design 첫 30분)**: [github.com/microsoft/WinAppDriver](https://github.com/microsoft/WinAppDriver) 에서 (a) 최근 commit 날짜, (b) Release 페이지 마지막 binary 날짜, (c) README / issue tracker 에서 "archive" / "deprecat" 키워드, (d) .NET 10 / Windows 11 26200 호환성 언급 확인. **Archive 확정 시 후보 H drop**, 살아있으면 후보 G 와 병렬 실험.
- **Bash-compat 평가**: **미확정**. WinAppDriver 내부 구현이 SendInput 기반이면 후보 G 와 동일 패턴, UIA 기반이면 후보 A 의 하위 변형. Design 에서 소스 일부 검토 필요.
- **Pros**: production code 수정 0, 외부 프로세스로 격리됨, 공식 Microsoft 도구라 문서/커뮤니티 자료 존재.
- **Cons**: archive 가능성, 설치/설정 overhead, 최신 Windows 호환성 불명.
- **Plan 단계 open question**: Q11 (§6)
- **참조**: [WinAppDriver GitHub](https://github.com/microsoft/WinAppDriver), [Appium Windows Driver](https://github.com/appium/appium-windows-driver) (별도 project, archive 시 fallback 후보)

#### I. **현행 `scripts/e2e/e2e_operator/input.py` SendInput RCA** (v0.2 신설 — RCA 3순위 도구)

- **배경**: 현행 `input.py:117-147` 의 `send_keys` 는 `ctypes SendInput` batch 를 사용하며 `wScan = 0` 즉 scancode 를 명시적으로 세팅하지 않는다 (`input.py:64-76` `_make_keybd_input`). `attempt #3` 이 Ctrl+T 에서 실패한 직접 원인 후보가 **H-RCA2 (scancode 누락)** 로 §1.5 에서 명시됨.
- **RCA 목적**: 후보 G 결과에 따라 narrow. G 가 Alt+V + Ctrl+T 모두 성공시키면 I 는 "FlaUI 의 어떤 차이가 결정적인가" 를 binary 로 좁히는 데 사용. G 가 실패하면 I 는 "현행 구현에 무엇이 빠졌는가" 를 독립적으로 탐색.
- **검증 체크리스트** (Design 에서 수행, Plan 에서는 **아이템만 나열**):
  1. `MapVirtualKey(VK_CONTROL, MAPVK_VK_TO_VSC)` → 0x1D (LCONTROL) / 0xE0 0x1D (RCONTROL extended) 확인
  2. `wScan` 을 `MapVirtualKey` 결과로 세팅 + `KEYEVENTF_SCANCODE` flag ON 시도
  3. `KEYEVENTF_EXTENDEDKEY` 의 필요성 (VK_CONTROL 기본은 LCONTROL 이지만 일부 키보드 레이아웃에서 차이)
  4. `AttachThreadInput(srcThread, dstThread, TRUE)` 호출 순서 — SendInput 전 vs 후 vs 미사용
  5. `SetForegroundWindow` / `SetFocus` 재시도 policy — 현재 `window.py` 가 H9 fix 이후 어떻게 처리하는지 재검토
  6. Spy++ / WinSpy++ / `KeyDiag` (e2e-ctrl-key-injection cycle 의 인프라) 로 실제 수신된 `WM_KEYDOWN` 의 `lParam` bits 16-23 (scancode), bit 24 (extended), bit 29 (context) 캡처 + hardware baseline 과 byte-for-byte 비교
- **Bash-compat 평가**: N/A — RCA 도구이지 injection 경로 자체가 아님.
- **Pros**: production code 수정 0, 기존 KeyDiag 인프라 재사용, 가장 저비용, 원인 확정에 직결.
- **Cons**: H-RCA1/2/3 가 조합형이면 단독으로는 원인 특정 불가 — G/H 와 상호보완 필수.
- **Plan 단계 open question**: Q12 (§6)
- **참조**: MSDN [MapVirtualKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-mapvirtualkeyw), [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput), [KEYBDINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput), `docs/archive/2026-04/e2e-ctrl-key-injection/` KeyDiag 인프라

### 5.3 Candidate Ranking (v0.2 — **RCA 선행 후 확정**)

**Phase 1 — RCA (Design 첫 Spike, §10 Milestone 2a gate)**:

| Rank | RCA Tool | 근거 | Confidence |
|:---:|---|---|:--:|
| **1** | **G — FlaUI** | .NET WPF UIA 전용 라이브러리로 가장 직접적. 결과에 따라 원인 공간 3분기 narrow (§5.2 G). 저비용 (S effort) | 추측 (실험 결과에 따라 확정) |
| **2** | **H — Appium + WinAppDriver** | FlaUI 와 독립된 out-of-process 경로. **단 maintenance status falsify 선행** | 잘 모르겠음 (archive 여부 불명) |
| **3** | **I — 현행 `input.py` SendInput RCA** | G/H 결과에 따라 narrow 목적. scancode / AttachThreadInput / Spy++ WM_KEYDOWN 검증 | 추측 (H-RCA2 신뢰도 high) |

**Phase 2 — Implementation (RCA 결과 확정 후)**:

RCA 결과에 따라 A~F 중 대상 선정. Plan 단계에서는 순위 미확정. 예시 시나리오:

| RCA 결과 | 선정될 가능성 높은 구현 후보 | 근거 |
|---|---|---|
| G 가 Alt+V + Ctrl+T 모두 성공 (원인 = `input.py` scancode 누락) | **I-기반 수정** (`input.py` 소규모 fix, production surface 0) 또는 **A (FlaUI)** 로 `input.py` 교체 | 가장 작은 변경량 |
| G 가 Alt+V 만 성공 (원인 = WPF / OS 레벨) | **D (test-hook)** 또는 **F(D)** | production surface 추가 |
| G/H 모두 실패 (원인 공간 확장) | **B (kernel driver)** 또는 재Plan | install friction 감수 |

> **Plan 결정 아님**: 위는 **시나리오 예시** 일 뿐. 어느 후보도 본 Plan 에서 "채택" 상태가 아니며, 실제 결정은 Design RCA 결과 기록 후에만 가능.

---

## 6. Open Questions (Design이 답해야 할 것)

| # | Question | 대상 후보 | 실험 방법 | Priority |
|---:|---|:--:|---|:--:|
| Q1 | `pywinauto.Application(backend='uia') + window.type_keys()` 가 H9 fix 적용 후 bash session에서 Alt+V 주입 + WPF `OnTerminalKeyDown` 도달하는가? | A | 최소 PoC: 기존 attempt #1 재실행, KeyDiag log 비교 | High |
| Q2 | `FlaUI.Keyboard.Press()` 또는 `UIAutomationCore.IUIAutomationElement::FindFirst + InvokePattern`이 SendInput을 우회하는 경로가 존재하는가? | A | FlaUI source 검토 + API 호출 trace | High |
| Q3 | `System.Windows.Input.InputManager.ProcessInput(new KeyEventArgs(...))` 이 **public accessibility**로 호출 가능한가? 아니라면 reflection 수준의 workaround가 존재하는가? | D | .NET 10 SDK 참조 문서 + WPF source (`wpf` GitHub repo) 확인 | Critical |
| Q4 | `InputManager.ProcessInput` 경로가 `PreviewKeyDown` handler에 도달할 때 `Keyboard.Modifiers` 상태가 SendInput 경로와 동일하게 populate되는가? | D | Minimal WPF 실험 앱 (별도 sandbox) 또는 GhostWin.App Debug 빌드에 1-shot 실험 추가 | Critical |
| Q5 | Mouse click 경로 (`MouseButtonEventArgs` + `Mouse.Synchronize`) 가 `MainWindow`의 hit-test 결과와 동일한가 (특히 MQ-4 pane focus, MQ-7 sidebar click) | D | Q4와 동일 방법, MQ-7 sidebar `ListViewItem` target | High |
| Q6 | Interception driver (oblita) 의 최신 signed release 가 Windows 11 26200에서 정상 install/uninstall 되는가? 제거 시 lingering device가 없는가? | B | 별도 VM에서 install → reboot → test → uninstall → registry/driver store diff | Medium |
| Q7 | WinAppDriver GitHub repo의 **last commit date** 와 Windows 11 26200 호환성 | E | GitHub 검사 (30분 이내) | Low |
| Q8 | Named Pipe IPC server 를 WPF `App.OnStartup` 에서 Debug 빌드 조건으로 띄울 때의 port collision / orphan pipe risk | D | Design 단계에서 pattern 설계 | Medium |
| Q9 | 본 cycle의 Evaluator heuristic (MQ-2~MQ-7) 은 현재 robust한가? `e2e-evaluator-automation` retroactive run의 MQ-1/MQ-7 silent regression에서 false positive 가능성이 남아 있는가? | ALL | Evaluator prompt §MQ-2~7 재검토, 과거 summary.json sampling | Medium |
| **Q10** (v0.2) | **FlaUI `FlaUI.Core.Input.Keyboard.Type` / `Keyboard.Press` 가 bash session 에서 GhostWin WPF `KeyBinding Ctrl+T` 를 trigger 하는가?** 또한 `FlaUI.UIA3.Application.Attach(pid)` 경로가 `OnTerminalKeyDown` 에 도달하는가? | **G (1순위 RCA)** | Design 첫 Spike — 별도 sandbox .NET console 앱 (tests/flaui_rca/, Design 에서 생성) 에서 FlaUI 호출 + KeyDiag log 대조 | **Critical** |
| **Q11** (v0.2) | **Microsoft WinAppDriver 의 2026-04 maintenance status** — GitHub 공식 repo (github.com/microsoft/WinAppDriver) 기준 archive 여부, 마지막 release binary 날짜, .NET 10 + Windows 11 26200 호환성 | **H (2순위 RCA)** | Design 첫 30분 — WebFetch 또는 gh CLI 로 repo metadata 확인, README / issue tracker 의 "archive" / "deprecat" 키워드 스캔 | **Critical** (drop gate) |
| **Q12** (v0.2) | **`scripts/e2e/e2e_operator/input.py` SendInput INPUT 구조체 byte layout 검증** — (a) `wScan = 0` 인 현행 구현이 Microsoft docs 의 best practice 와 일치하는가, (b) `MapVirtualKey` 기반 scancode + `KEYEVENTF_SCANCODE` 조합이 Ctrl+T 에 필요한가, (c) Spy++ 로 실제 수신된 WM_KEYDOWN `lParam` bits 16-23 (scancode) / bit 24 (extended) 가 hardware baseline 과 일치하는가 | **I (3순위 RCA)** | KeyDiag 재가동 + MapVirtualKey 호출 + Spy++ capture + hardware baseline 과 side-by-side 비교. `e2e-ctrl-key-injection` 인프라 재사용 | **High** |
| **Q13** (v0.2) | **WPF `Keyboard.Modifiers` 가 `PostMessage` 로는 업데이트되지 않는 것이 maintained behavior 인가** (v0.1 `feedback` 의 해석이 Microsoft 공식 소스에 근거하는가)? `GetKeyState` / `GetAsyncKeyState` / `InputManager` 세 경로 중 `Keyboard.Modifiers` 는 실제로 어떤 API 를 조회하는가? | H-RCA1 | Microsoft Reference Source (github.com/dotnet/wpf) `KeyboardDevice` / `ModifierKeysConverter` 검색. PostMessage vs SendInput 하위 OS 키 상태 업데이트 비교 | High |
| Q14 | `[Conditional("DEBUG")]` vs env-var gate 로 test-hook 활성화할 때, Release 빌드에서의 binary size impact 및 dead code elimination이 실제로 동작하는가? **v0.1 Q10 → v0.2 Q14 로 번호 이동** | D | ILSpy / dotnet-reduce 로 Release artifact 검사 | Low |

---

## 7. Risks and Mitigation

| Risk | Impact | Likelihood | Mitigation | Trigger Signal |
|------|--------|------------|------------|----------------|
| **R-RCA** (v0.2 신설) — Design phase 의 RCA (Q10/Q11/Q12) 가 원인을 확정하지 못하고 "여전히 잘 모르겠음" 으로 끝남. G (FlaUI) / H (WinAppDriver) / I (현행 SendInput) 세 경로 모두 결정적 결과를 주지 못하는 경우 | **High** | Medium (추측) | (a) 그 경우에도 후보 A~F + G/H/I 의 **black-box empirical 결과** 로 우선순위 재조정 (예: G 가 부분 성공이면 그 부분 패턴만이라도 기록해서 구현 후보 D 의 scope 축소에 활용). (b) 사용자에게 **재Plan** 옵션 제시 — RCA 가 가설 공간을 확장시킨 경우 Plan v0.3 으로 reframe. (c) 최후의 수단으로 사용자 hardware 1회 검증 후 그 결과를 기준점으로 RCA 재설계 | Design RCA 3경로 모두 inconclusive |
| **R1** — Q3/Q4 (InputManager API accessibility) 가 negative → 후보 D 의 구조적 기반 약화 → 구현 후보 narrowing 어려움 (v0.2: "유일한 구조적 후보 손실" 표현 완화 — RCA 가 선행되므로 F(D) 가 default 가 아님) | High | Medium (추측) | B/F(B) kernel driver 를 Design 단계 대체 후보로 유지. Q3/Q4 는 **RCA 이후 구현 phase 초반** 에 수행 — Plan 단계 우선순위 아님 | InputManager API 탐색 결과 |
| **R2** — Interception driver 최신 signed release 부재 → 후보 B 도 불가 | High | Low | 직접 signed driver 빌드? **out of scope** — 그 시점에 본 cycle 재평가 + user에게 report | github release 없음 또는 SignTool error |
| **R3** — G/A/FlaUI 가 bash session에서 동작하지만 일부 MQ만 — 현재 PostMessage fallback처럼 status=ok 지만 visual FAIL | Medium | Medium | Evaluator verdict를 **단일 Source of Truth**로 사용 (operator status 무시). RCA 결과 분기 (§5.2 G 의 "Alt+V ✅ Ctrl+T ❌ 재현" 케이스) 로 직접 연결 | PrintWindow capture가 expected diff 안 보임 |
| **R4** — Plan 단계에서 후보 D 가 "production surface 추가"로 분류돼 사용자 reject — v0.2 에서는 RCA 선행으로 D 가 **불필요해질 가능성** 도 있음 (G 가 전부 성공 시) | Medium | Unknown | 본 Plan 문서에서 decision 금지, RCA 결과 확정 후 Design 문서에서 user 에게 선택지 surface | 사용자 feedback |
| **R5** — `pane-split` 병렬 cycle과 `scripts/e2e/` 파일 충돌 (양쪽 모두 Do 단계 진입 시) | Low | Very Low | 본 cycle은 Plan 단계에서 파일 수정 0 — Do 진입 시점에 재평가 | git status 확인 |
| **R6** — Evaluator heuristic false positive로 Q9에서 본 cycle이 PASS로 착각됨 | Medium | Medium | SC-P0의 N ≥ 3회 연속 + cross-validation with operator_notes (D13 5-layer safeguard 재사용) | Evaluator verdict 불일치 |
| **R7** — .NET 10 `InputManager` 관련 internal API가 .NET version upgrade 시 깨짐 (후보 D 선택 시) | Low | Low | ADR 후보로 기록 + integration test | .NET version bump |
| **R8** — 본 cycle이 Plan 단계에서 scope creep (Evaluator 개선, Capture 재설계까지 편입) | Medium | Medium | §2.2 Non-Goals 명시, user가 scope 확장 요청 시 별도 cycle 권고 | 본 문서 §2.2 |
| **R9** — **확실하지 않음**: Windows 26200 에서 Interception 등 오래된 driver가 HVCI/Memory Integrity 와 충돌 가능 | High | Unknown | Q6 PoC에서 확인. HVCI 활성화 상태로 테스트 | Install 실패 |
| **R10** (v0.2 신설) — FlaUI sandbox 실험이 "우리 `input.py` 와 다른 경로" 인 것이 밝혀져 RCA narrowing 에 실패 (예: FlaUI 가 UIA `InvokePattern` 을 쓰고 KeyBoard.Type 을 우회) | Medium | Medium | Design 첫 Spike 에서 FlaUI 내부 소스 (open source) 확인 — `FlaUI.Core.Input.Keyboard` 가 정말 `SendInput` 을 호출하는지 코드 레벨로 검증 후 실험 설계 | FlaUI 실험이 "성공" 인데 원인은 설명 못 함 |

---

## 8. Dependencies

### 8.1 Upstream (본 cycle이 의존하는 것)

- **E1** `scripts/test_e2e.ps1` 3-mode wrapper (`-Operate/-Evaluate/-EvaluateOnly/-Apply`) — e2e-evaluator-automation에서 완료됨. 본 cycle이 이 wrapper를 수정 없이 재사용 전제.
- **E2** `scripts/e2e/evaluator_prompt.md` 500+ line prompt (e2e-evaluator-automation Do Step 11) — Evaluator heuristic 현상 유지 전제. Q9에서 robustness 점검만.
- **E3** PrintWindow capturer (`scripts/e2e/e2e_operator/capture/printwindow.py`) — bash session 에서 동작 검증됨.
- **E4** `GHOSTWIN_E2E_CAPTURER=printwindow` 환경 변수 경로 (e2e-evaluator-automation 이후).
- **E5** `feedback_e2e_bash_session_limits.md` 제약 문서.
- **E6** (후보 D 선택 시) `GhostWin.App` DI 컨테이너 (WPF M-1 완료) + `MainWindow.OnTerminalKeyDown` (WPF M-3 완료).

### 8.2 Downstream (본 cycle을 기다리는 것)

- **D1** `e2e-mq7-workspace-click` (CLAUDE.md follow-up 행 1) — HIGH priority. 본 cycle의 직접 소비자.
- **D2** `first-pane-manual-verification` (CLAUDE.md 행 2) — MEDIUM priority. MQ-2/3 visual verify가 본 cycle에 의존.
- **D3** Phase 5-F session-restore — 전체 cycle의 Check phase가 본 cycle 이후 closed loop 활용 기대.

### 8.3 No-Overlap with `pane-split`

- `pane-split` cycle은 `src/engine-api/ghostwin_engine.cpp`, `src/GhostWin.Services/PaneLayoutService.cs` 중심.
- 본 cycle은 `scripts/e2e/**`, (후보 D 선택 시) `src/GhostWin.App/**` 로컬.
- **파일 교집합 0** — 양 cycle이 독립적으로 Do 진입 가능. 단 양쪽 모두 `MainWindow` 수정 시 merge 조율 필요 (Design 단계에서 재확인).

---

## 9. Parallel Execution Plan with `pane-split`

pdca-batch max 3 concurrent 제약 하에서 본 cycle + `pane-split` + (optional) 3rd cycle 운영.

| 자원 | 본 cycle 점유 | `pane-split` 점유 | 충돌 여부 |
|---|---|---|:--:|
| `docs/01-plan/features/e2e-headless-input.plan.md` | **Write** | 무관 | ❌ |
| `docs/02-design/features/e2e-headless-input.design.md` (future) | Write (Design 단계) | 무관 | ❌ |
| `docs/01-plan/features/pane-split.*` | **Read-only** (reference) | Write | ❌ (read-only 준수) |
| `scripts/e2e/**` | Design/Do 단계 write 예정 | 무관 | ❌ |
| `src/engine-api/**` | 무관 | Write | ❌ |
| `src/GhostWin.App/**` | **가능 (후보 D 선택 시)** | 가능 (pane-split 종속) | ⚠️ 조율 필요 — Design 단계에서 branch 전략 재평가 |
| `scripts/build_*.ps1` | 실행 금지 (Plan) | 가능 | ❌ |
| `scripts/test_*.ps1` | 실행 금지 (Plan) | 가능 | ❌ |

> **Plan 단계 결론**: 충돌 0. Design 진입 시점에 `src/GhostWin.App/**` 교집합만 재확인.

---

## 10. Milestones (Ordering Only — No Date Estimates)

> CLAUDE.md `.claude/rules/*` 에 따라 날짜 추정 금지. 순서만 명시.

1. **Plan** (현재) — 본 문서 draft → user review → approve
2. **Design** — council 구성:
   - `dotnet-expert`: Q3/Q4 (InputManager API) + Q5 (Mouse.Synchronize) 실험 주도
   - `code-analyzer`: 현행 `scripts/e2e/**` injection surface 전수 분석 + production code touch point 식별
   - `qa-strategist`: Q9 (Evaluator robustness) + SC-P0 측정 기준 확립
   - `wpf-architect` (optional): 후보 D 선택 시 `InputManager.ProcessInput` 경로 WPF 아키텍처 정합성 검증
2a. **Design phase 첫 Spike — RCA gate (v0.2 필수, 우회 불가)**. Design 문서 §1 또는 §2 에 "원인 확정" 섹션 을 만들고 아래 실험을 **순서대로** 수행 + 결과를 evidence log 로 기록:
   - **Step RCA-1 (≤ 30분)**: 후보 H (WinAppDriver) maintenance status falsify. github.com/microsoft/WinAppDriver 의 최근 commit 날짜 / Release / archive 키워드 확인. **Archive 확정 시 H drop**, 살아있으면 RCA-3 에서 G 와 병렬 실험
   - **Step RCA-2 (≤ 1시간)**: 후보 G (FlaUI) 최소 PoC. `tests/flaui_rca/` sandbox .NET console 앱 신설 (Design 이 승인한 경로). FlaUI `Keyboard.Type("%v")` / `Keyboard.Type("^t")` 호출 후 GhostWin `OnTerminalKeyDown` KeyDiag log 수집. 세 가지 결과 분기를 §5.2 G 참조
   - **Step RCA-3 (≤ 1시간)**: G 결과에 따라 분기. (a) G 가 전부 성공하면 후보 I (input.py scancode 검증) 로 narrow + H 는 confirmation 용도만. (b) G 가 부분 성공 / 실패하면 후보 H (archive 아닐 시) + I 를 병렬 실행. (c) 모두 실패하면 사용자 hardware 비교 baseline 수집
   - **Step RCA-4**: RCA 결과를 Design 문서 §"원인 확정" 에 기록 — H-RCA1~3 가설 중 어느 것이 confirm / falsify 됐는지 empirical 로 명시. 이 섹션이 비어 있으면 구현 phase 진입 금지 (**AC-4 gate**)
3. **Do — 구현 후보 선정**: RCA 결과 확정 후 A~F 중 대상 선정. 예시 (§5.3 Phase 2 참조): G 가 Ctrl+T 성공 → 후보 I 기반 `input.py` scancode fix / G 가 Ctrl+T 실패 → 후보 D (test-hook) 또는 F(D) / 둘 다 실패 → 후보 B (kernel driver) 또는 재Plan
4. **Do — PoC 후 full implementation**: 선정된 후보로 SC-P0 (N ≥ 3회 연속) + SC-P1/P2 측정 후 production-ready 수준 코드화
5. **Check**: `qa-strategist` + `gap-detector` Match Rate ≥ 90% + Evaluator verdict PASS + 사용자 hardware smoke 회귀 0
6. **Report**: Archive to `docs/archive/2026-04/e2e-headless-input/` + CLAUDE.md 갱신 + Follow-up 행 1 (`e2e-mq7-workspace-click`) unblocked 표시

> **§10 v0.2 핵심 변경**: Milestone 2a (RCA gate) 가 신설되어 Design phase 의 첫 작업이 강제된다. v0.1 은 "Do — PoC 후보 순위 F(D) → F(B) → ..." 로 바로 구현에 들어갔으나 v0.2 는 RCA 가 원인을 확정한 뒤에만 A~F 선정이 가능하다. §4.4 AC-4 가 이 gate 의 실패 조건이다.

---

## 11. Convention Prerequisites

### 11.1 Existing Conventions (준수 대상)

- [x] `.claude/rules/behavior.md` — **"우회 금지 — 근거 기반 문제 해결"**. 본 cycle이 특히 엄격 준수. 현 PostMessage fallback이 "부분적으로 동작"하는 상태에 안주하지 말 것.
- [x] `.claude/rules/commit.md` — 영문 커밋, AI 언급 없음, feature branch
- [x] `.claude/rules/build-environment.md` — `scripts/build_ghostwin.ps1 -Config Release` 사용 (Design/Do 단계에서만)
- [x] User 글로벌 rule — 한글 답변, 추측/불확실 명시, 출처 링크
- [x] v1.6.0 PDCA mandate — Executive Summary 4-perspective 테이블 (§Executive Summary)

### 11.2 Conventions to Define/Verify (Design 단계)

- [ ] Test-hook IPC 프로토콜 스펙 (후보 D 선택 시) — Named Pipe 이름, JSON schema, authentication
- [ ] Driver install lifecycle policy (후보 B 선택 시)
- [ ] Evaluator verdict cross-validation rule (Q9 연계)

### 11.3 Environment Variables

- 기존 `GHOSTWIN_E2E_CAPTURER` 재사용 (capture 경로)
- 신규 (후보 D 선택 시) 예: `GHOSTWIN_TEST_HOOK_PIPE`, `GHOSTWIN_TEST_HOOK_ENABLED` — Plan 단계에서는 이름만 명시, 값/semantics는 Design.

---

## 12. Team Assignments (Dynamic Level — Leader Pattern)

| Teammate | 역할 | Plan 단계 | Design 단계 | Note |
|---|---|:--:|:--:|---|
| **cto-lead** (본 agent) | Plan 문서 작성 주도, 후보 Research 직접 수행 | ✅ 완료 | 주도 | CC v2.1.69+ standalone subagent 제약으로 nested Task spawn 불가 → Leader가 research 를 직접 수행 |
| **developer teammate** | (계획) Candidate A/B 외부 라이브러리 source link + 최신 release 상태 조사 | **생략** (CC 제약) | Do 단계 PoC 주도 | Design 단계에서 full-delegation 가능 |
| **qa teammate** | (계획) SC-P0 PrintWindow pixel diff 기준 정의 | **생략** (CC 제약) | Q9 Evaluator robustness 점검 주도 | 본 Plan §4 + §6 Q9 에 qa 관점 이미 반영 |
| **frontend teammate** | N/A (UI 작업 없음) | skip | skip | — |

> **CC v2.1.69+ 아키텍처 주의**: 본 agent 가 standalone subagent 로 호출되었기 때문에 Task() 로 다른 teammate 를 spawn 할 수 없음. Leader 패턴에서 research 를 직접 수행하는 것은 본 제약에 대한 정상 fallback. Design 단계에서 `/pdca team design e2e-headless-input` 로 full team 운영 가능.

---

## 13. Next Steps (User Action Required)

1. [ ] User review of this Plan **v0.2** → approve / reject / request amendments
2. [ ] If approved: `/pdca team design e2e-headless-input` (Expanded Slim 4-agent council: dotnet-expert / code-analyzer / qa-strategist / wpf-architect)
3. [ ] **Design doc 첫 섹션으로 "원인 확정 (RCA)" 작성** — Milestone 2a (§10) 의 RCA-1~4 순서 준수 + H-RCA1/2/3 가설 falsification evidence 기록 (AC-4 gate)
4. [ ] Design doc 에서 Q1~Q9, **Q10~Q13 (RCA)**, Q14 답변 확정 + A~F 중 구현 후보 선정 + production surface 합의
5. [ ] Do PoC → Full implementation → Check → Report

---

## 14. Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-09 | Initial draft. Candidate approaches 6개 brainstorm, Q1~Q10 open question 목록, SC-P0/P1/P2 정의, Non-Goals §2.2 명시. CLAUDE.md follow-up 행 1 과의 중복 아님 관계 §1.4. `pane-split` 병렬 resource 격리 §9. CC v2.1.69+ standalone subagent 제약 §12 fallback note. | 노수장 (CTO Lead) |
| **0.2** | 2026-04-09 | **UIPI 단일 원인론 반박 + RCA-first reframe**. (1) 상단 Summary v0.1 → v0.2 + 변경이력. (2) Executive Summary Problem 칸 재작성 — empirical 증거 3건 inline. Solution 칸 "UIPI 우회" → "근본 원인 해소" 일반화. (3) §1.2 load-bearing 인용을 "observation only" 로 약화. (4) **§1.5 신설** — UIPI 가설 empirical 반박 표 3건 + 진짜 원인 가설 H-RCA1/2/3 (추측 명기). (5) **§1.6 신설** — 사용자 피드백 paraphrased. (6) §2.3 A4 (UIPI 가정) **철회** + A6/A7/A8 신설 (IL 동일, FlaUI 호환성 불명, WinAppDriver 상태 불명). (7) §4.4 AC-3 일반화 + **AC-4 신설** (RCA gate 우회 금지). (8) **§5.1.2 신설** — 후보 G (FlaUI) / H (Appium+WinAppDriver) / I (현행 input.py SendInput RCA). §5.1.1 A~F 우선순위 "RCA 결과에 따라 확정" 으로 하향. (9) §5.2 에 G/H/I 상세 항목 추가. (10) §5.3 ranking 을 2-phase (RCA → Implementation) 재구성. (11) **§6 Q10~Q13 신설** (FlaUI / WinAppDriver / input.py scancode / Keyboard.Modifiers PostMessage 공식 확인), 기존 Q10 → Q14 번호 이동. (12) **§7 R-RCA 신설** + R10 (FlaUI narrowing 실패), R1/R4 "F(D) 유일한 구조적 후보" 표현 완화. (13) **§10 Milestone 2a RCA gate 신설** — RCA-1 WinAppDriver falsify ≤30분, RCA-2 FlaUI PoC ≤1시간, RCA-3 분기, RCA-4 Design 문서 §"원인 확정" 기록. (14) §13 Next Steps Q10 → Q14 참조 수정 + RCA gate 강조. 기존 §2.2 Non-Goals, §5.1.1 A~F 6 후보 비교표, §9 병렬 계획, §12 Team 모두 **유지**. | 노수장 (CTO Lead) |
