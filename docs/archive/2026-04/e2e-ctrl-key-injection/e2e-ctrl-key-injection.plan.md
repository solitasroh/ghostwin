# E2E Ctrl-Key Injection — Planning Document

> **Summary**: e2e-test-harness Do phase에서 confirmed된 R4 (Ctrl+키 미전달)의 root cause를 empirically 진단하고 fix해서 e2e harness `--all` Match Rate 5/8 → 8/8로 끌어올린다. 4가지 input layer 시도가 모두 실패한 이상, 다음 단계는 **GhostWin source 측 진단 logging + KeyBinding 인벤토리 + hardware vs SendInput side-by-side trace**다. 추측 기반 5번째 input 시도가 아니라 evidence 기반 root cause 확정이 본 cycle의 본질.
>
> **Project**: GhostWin Terminal
> **Phase**: 5-E.5 부채 청산 (e2e-test-harness Do의 잔여 R4 follow-up)
> **Author**: 노수장
> **Date**: 2026-04-08
> **Status**: Draft
> **Previous**:
> - `docs/01-plan/features/e2e-test-harness.plan.md` — R4 원본 risk
> - `docs/02-design/features/e2e-test-harness.design.md` §1.2 C8, §10 R4, §12 v0.1.2 — 4 attempts empirical confirmation
> - `scripts/e2e/e2e_operator/input.py` — 현재 채택된 attempt 3 (ctypes SendInput batch)
> - `src/GhostWin.App/MainWindow.xaml.cs:212-329` — 의심 ground zero `OnTerminalKeyDown(PreviewKeyDown)`
> - `src/GhostWin.App/MainWindow.xaml:148-159` — 동일 단축키의 `Window.InputBindings` 정의

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | e2e-test-harness Do phase 에서 R4 (window resize 검증)는 PASS했지만, 그 과정에서 **Ctrl+키 주입 자체가 GhostWin에 전달되지 않는** 더 깊은 비대칭이 confirmed됨. 4가지 시도 (pywinauto UIA type_keys, pywinauto.keyboard.send_keys, ctypes SendInput batch, +AttachThreadInput +scancode +cross-thread SetFocus) 모두 동일 패턴: **Alt+V/H, mouse click, window resize는 OK, Ctrl+T / Ctrl+W / Ctrl+Shift+W만 OnTerminalKeyDown에 도달 안 함**. 사용자가 손가락으로 직접 키보드를 누르면 정상 동작 (Phase 5-E 검증 완료, CLAUDE.md). 결과적으로 e2e harness는 MQ-1/2/3/4/8 (5개)만 실행 가능하고 MQ-5/6/7 (3개)는 prerequisite 미충족으로 skip → **Match Rate 5/8 = 62.5% cap**. bisect-mode-termination MQ retroactive QA도 같은 cap에 묶여 8건 중 3건이 미검증 상태. e2e harness가 자체 발견한 한계인데 framework scope 외였기에 별도 PDCA cycle로 분리. |
| **Solution** | **Hypothesis-driven empirical diagnosis** 5단계: (1) `OnTerminalKeyDown`에 진입 시점 logging 추가 — `Keyboard.Modifiers`, `e.Key`, `e.SystemKey`, `e.OriginalSource`, `e.RoutedEvent.Name`을 file/Debug에 기록. (2) `Window.InputBindings` (XAML `MainWindow.xaml:148-159`)와 PreviewKeyDown 직접 dispatch (`OnTerminalKeyDown:246-285`) 의 **이중 dispatch 인벤토리** — 둘이 동일 단축키를 중복 등록하고 있음. (3) hardware key 1회 + SendInput 1회 side-by-side로 logging 결과 비교, 어느 경로가 어느 시점에 끊기는지 확정. (4) H1~H5 가설을 evidence로 좁혀서 **root cause 1개**로 수렴 (확정 안 되면 사용자 보고). (5) Minimal fix 구현 + e2e harness 재실행으로 8/8 PASS 회귀 검증. **5번째 input layer 시도는 H1~H5가 모두 falsified된 후에만** (behavior.md "정석 방법 실패 시 근본 원인 분석" 준수). |
| **Function/UX Effect** | 사용자 가시 변경 0 — 키 매핑 자체는 그대로. 개발자/QA 관점: (a) e2e harness `scripts/test_e2e.ps1 -All` → Operator 8/8 OK + Evaluator 8/8 PASS 도달, (b) bisect-mode-termination retroactive QA 5/8 → 8/8 closeout, (c) 향후 Phase 5-F session-restore + P0-3 종료 경로 + P0-4 PropertyChanged detach 등 모든 후속 feature가 신뢰 가능한 e2e harness 위에서 검증, (d) Ctrl 단축키 구현이 evidence 기반으로 검증돼 향후 회귀 방지. |
| **Core Value** | e2e harness가 **자체 발견한 한계**를 root cause로 닫는 closed-loop. behavior.md 우회 금지 원칙 — "정석 방법 실패 시 근본 원인 분석" — 의 실천 사례. 이전 4 attempts는 input layer만 바꾸는 휴리스틱 시도였고, 본 cycle은 그 휴리스틱을 멈추고 **WPF 분기에서 evidence 수집**으로 전환. 결과적으로 (R4 fix) + (e2e trustworthy) + (debugging methodology document)의 3중 ROI. |

---

## 1. Overview

### 1.1 Purpose

e2e-test-harness Do phase의 잔여 risk R4 (Ctrl+키 주입 미전달)를 empirical diagnosis로 root cause를 확정하고, GhostWin source 또는 e2e input layer 중 결정된 위치에 minimal fix를 적용해서 e2e harness `--all` Match Rate를 100%로 끌어올린다.

### 1.2 Why a Separate Feature

본 cycle을 e2e-test-harness 안으로 흡수하지 않는 이유:

- e2e-test-harness scope는 **harness framework 자체** — capture/operator/evaluator/orchestrator 4 layer 구축이 본질이었고, 이미 archive 가능한 deliverable 완성 상태 (commit `35f7d24`)
- R4 root cause는 **GhostWin source 영역** (`MainWindow.xaml.cs` + `MainWindow.xaml`)일 가능성이 가장 큼 — harness scope 밖
- Diagnosis에는 **production source code 진단 logging 추가**가 필요해 빌드 + manual smoke 검증 cycle 동반 (harness Do와 책임 경계 흐려짐)
- PDCA 관점에서 별도 cycle로 추적해야 ROI/회귀 측정이 가능

### 1.3 Empirical Facts (재조사 금지)

design v0.1.2 §10 R4 + §12 entry에서 confirmed된 사실:

| Attempt | Implementation | Alt+V/H | Mouse | Resize | Ctrl+T | Ctrl+Shift+W |
|:---:|---|:---:|:---:|:---:|:---:|:---:|
| 1 | pywinauto Application(uia) + window.type_keys | OK | OK | OK | FAIL | FAIL |
| 2 | pywinauto.keyboard.send_keys (standalone) | OK | OK | OK | FAIL | FAIL |
| **3** (final, committed) | **ctypes SendInput batch (atomic)** | **OK** | **OK** | **OK** | **FAIL** | **FAIL** |
| 4 | + AttachThreadInput + scancode + cross-thread SetFocus | **broke Alt+V** | OK | OK | FAIL | FAIL |

핵심 비대칭:

1. **Alt+키, mouse SendInput, window resize → 전부 통과** (4번 attempt 제외)
2. **Ctrl+키만 OnTerminalKeyDown에 도달 안 함** — Operator는 SendInput 성공 (sent == requested), GhostWin은 무반응
3. **사용자 손가락으로 직접 누르면 OK** (Phase 5-E pane-split 검증 완료, CLAUDE.md memory)
4. **Attempt 4의 cross-thread SetFocus가 Alt+V도 break** → focus가 잘못된 control로 옮겨졌음을 시사 (revert됨)

### 1.4 Related Source

| 파일 | 라인 | 역할 |
|---|---|---|
| `src/GhostWin.App/MainWindow.xaml` | 148-159 | `Window.InputBindings` — 6개 KeyBinding (Ctrl+T, Ctrl+W, Ctrl+Tab, Ctrl+Shift+W, Alt+V, Alt+H) |
| `src/GhostWin.App/MainWindow.xaml.cs` | 166-167 | `PreviewKeyDown += OnTerminalKeyDown;` — InitializeRenderer 안에서 등록 (RenderInit 성공 후) |
| `src/GhostWin.App/MainWindow.xaml.cs` | 212-329 | `OnTerminalKeyDown` PreviewKeyDown 핸들러 — Alt+arrow, Ctrl+T/W/Tab, Ctrl+Shift+W, Alt+V/H 직접 dispatch |
| `src/GhostWin.App/MainWindow.xaml.cs` | 239-245 | 코멘트 — phase 5e crash fix #12, "WM_KEYDOWN consumed by HwndHost child WndProc → DefWindowProc before WPF InputBinding" |
| `scripts/e2e/e2e_operator/input.py` | 79-142 | `send_keys()` ctypes SendInput batch (attempt 3) |

---

## 2. Scope

### 2.1 In Scope

**진단 도구 (Diagnosis tooling)**

- [ ] `OnTerminalKeyDown` 진입부에 진단 logging — `Keyboard.Modifiers`, `e.Key`, `e.SystemKey`, `e.OriginalSource?.GetType().Name`, `e.RoutedEvent.Name`, `Keyboard.FocusedElement?.GetType().Name`, timestamp
- [ ] `OnTerminalKeyDown` 분기별 traversal logging — switch case 진입, `e.Handled` 변화, early return 위치
- [ ] `Window.InputBindings` 인벤토리 — XAML KeyBinding과 cs PreviewKeyDown 직접 dispatch 의 **중복/충돌 매트릭스** 산출
- [ ] (선택) `KeyDown` 일반 핸들러도 추가해서 PreviewKeyDown vs KeyDown 도달 여부 비교
- [ ] (선택) `MainWindow.HandlesScroll = true` 같은 XAML 속성 또는 InputManager hook으로 raw key event를 capture
- [ ] Hardware vs SendInput side-by-side trace 결과를 Design 단계 evidence로 사용

**Hypothesis verification**

- [ ] H1~H5 (§4) 각각의 evidence 수집 — 어느 가설이 traversal log와 일치하는지 결정
- [ ] Root cause 1개로 수렴 — 확정 안 되면 사용자에게 보고 (behavior.md 우회 금지)

**Fix implementation**

- [ ] Root cause 위치에 minimal fix — diagnosis 결과에 따라 결정 (GhostWin source OR e2e input.py OR 둘 다)
- [ ] 진단 logging은 production build에서 비활성 (DEBUG conditional 또는 logger level)

**Regression verification**

- [ ] e2e harness `scripts/test_e2e.ps1 -All` → Operator 8/8 OK
- [ ] Evaluator 수동 호출 → 8/8 PASS, Match Rate 100%
- [ ] bisect-mode-termination retroactive QA 8/8 PASS로 update
- [ ] Hardware 입력 manual smoke (Ctrl+T/W/Shift+W/Alt+V/H 각 1회) — 회귀 0 확인

### 2.2 Out of Scope

| 항목 | YAGNI 근거 |
|---|---|
| 새 e2e 시나리오 추가 (MQ-9 이상) | 본 cycle은 기존 8개 시나리오를 trustworthy로 만드는 것이 본질. 추가 시나리오는 별도 feature |
| GhostWin 키 매핑 자체 변경 (KeyMap settings) | 사용자 가시 동작은 그대로. 본 cycle은 dispatch 경로 fix만 |
| WPF/.NET version upgrade | 추측 기반 우회. evidence 없이 framework 교체는 우회 금지 |
| 새 input library 도입 (AutoHotkey, SendKeys 등) | attempt 5 후보지만 H1~H5가 모두 falsified된 후에만 고려. 의존성 추가는 마지막 수단 |
| `_initialHost` 라이프사이클 리팩토링 | 별도 부채 항목 (Phase 5-E TODO에 이미 등재) |
| 종료 경로 단일화 (P0-3) | 별도 PDCA cycle |

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | 검증 방법 |
|---|---|---|
| **FR-01** | `OnTerminalKeyDown` 진입부에 진단 logging 추가 — `Keyboard.Modifiers`, `e.Key`, `e.SystemKey`, `e.OriginalSource?.GetType().Name`, `e.RoutedEvent.Name`, `Keyboard.FocusedElement?.GetType().Name`, timestamp 모두 single line으로 출력 | hardware Ctrl+T 1회 → log 1줄 출력 확인 |
| **FR-02** | `MainWindow.xaml` `Window.InputBindings`와 `MainWindow.xaml.cs:OnTerminalKeyDown` switch 분기의 단축키 인벤토리 표 작성 — 각 단축키별로 (XAML 정의 여부, cs 직접 dispatch 여부, e.Handled 처리 여부) | Design 문서에 표 첨부 |
| **FR-03** | Hardware key event ETW 또는 Spy++ equivalent로 raw input baseline 수집 — `WM_KEYDOWN` / `WM_SYSKEYDOWN` 메시지가 GhostWin top-level HWND에 도달하는지 직접 확인. ETW Microsoft-Windows-Win32k provider 또는 PowerShell `Get-WinEvent` 활용 (확실하지 않음 — Design에서 도구 확정) | hardware Ctrl+T 1회 → ETW에 WM_KEYDOWN entry 1건 확인 |
| **FR-04** | Side-by-side comparison: 동일 GhostWin 인스턴스에 (a) hardware Ctrl+T 1회, (b) e2e SendInput Ctrl+T 1회 → FR-01 logging + FR-03 ETW 결과를 모두 record. 차이점을 Design 문서에 evidence로 인용 | 두 trial 모두 logging file이 존재 + diff 명시 |
| **FR-05** | H1~H5 가설별 evidence 매핑 — 어느 가설이 traversal log와 일치하는지 1개로 좁힘. 확정 불가 시 사용자에게 "이 부분은 확실하지 않음" 명시 후 의사결정 요청 | Design 문서 §Hypothesis Verification 섹션 |
| **FR-06** | Root cause에 해당하는 위치 (GhostWin source / e2e input.py / 둘 다)에 minimal fix 구현. fix는 평균 50 LOC 이하 권장 (확실하지 않음 — root cause에 따라 가변) | Code review + diff 크기 확인 |
| **FR-07** | Regression test: `scripts/test_e2e.ps1 -All` 실행 → Operator 8/8 OK + Evaluator 수동 호출 → 8/8 PASS. Run id를 Design 문서 v0.x entry에 기록 | `summary.json` 첨부 |

### 3.2 Non-Functional Requirements

| ID | Requirement | 검증 방법 |
|---|---|---|
| **NFR-01** | 진단 logging은 production build에 leak되지 않음 — `#if DEBUG` conditional 또는 `Microsoft.Extensions.Logging` LogLevel.Trace로 빌드 후 미출력 검증 | Release build → log file 미생성 확인 |
| **NFR-02** | Fix가 정상 사용자 경험에 0 영향 — hardware Ctrl+T/W/Shift+W/Alt+V/H 각 1회 manual smoke 회귀 0 | Smoke test report |
| **NFR-03** | 진단 logging이 hot path 성능에 영향 없음 — `Keyboard.FocusedElement?.GetType().Name` 등 reflection 호출은 logging 활성 시에만 lazy evaluation | 활성 시 60fps 유지 (확실하지 않음 — 정량 측정 어려움) |

---

## 4. Hypotheses (Diagnosis 우선순위)

> **모두 "확실하지 않음" 표시**. 본 절은 추측이며 §5 Diagnosis Plan으로 evidence를 수집한 후에만 확정.

### H1 — Modifier State Race (가장 의심)

**Claim**: WPF `Keyboard.Modifiers`가 SendInput으로 들어온 keyboard event 시점에 `ModifierKeys.Control` flag를 미반영. `OnTerminalKeyDown:246` 의 `if (Keyboard.Modifiers == ModifierKeys.Control)` 조건이 false 반환 → switch에 도달 못 함.

**Evidence to collect**:
- FR-01 logging에서 SendInput trial 시 `Keyboard.Modifiers` 값 출력
- Hardware trial과 비교 → 동일 timestamp의 modifier 값 차이 확인
- WPF KeyboardDevice는 `WM_KEYDOWN`을 받으면 modifier state를 update하는데, SendInput으로 atomic batch로 들어오면 update timing이 다를 가능성 (확실하지 않음)

**Falsified if**: SendInput trial logging에 `Keyboard.Modifiers == Control`이 정상 출력되는데 switch에 도달 안 하는 경우

### H2 — HwndHost Child Focus가 Ctrl 키 흡수

**Claim**: `TerminalHostControl` (HwndHost) native child window가 keyboard focus를 점유하고, `WM_KEYDOWN(VK_CONTROL)` + `WM_KEYDOWN(VK_T)`를 ghostty engine WndProc로 dispatch → PowerShell stdin으로 전달 (Ctrl+T는 PowerShell에서 의미 없는 키이므로 시각적 효과 없음). PreviewKeyDown은 fire되지 않음. Alt+키는 system menu accelerator라 OS-level에서 부모 HwndSource로 dispatch되므로 우회.

**Evidence to collect**:
- FR-01 logging이 SendInput Ctrl+T trial에서 **호출 자체가 안 됨** 확인
- `Keyboard.FocusedElement?.GetType().Name` 값이 `HwndHost` 또는 `TerminalHostControl` 이면 강한 단서
- ghostty engine 측 ConPty stdin trace에 `\x14` (Ctrl+T) 또는 `\x14` byte 도착 여부 확인 (engine logging 추가 필요할 수 있음)
- `MainWindow.xaml.cs:239-245` 코멘트가 이미 동일 가설을 기록하고 있음 — phase 5e fix #12에서 PreviewKeyDown 직접 dispatch로 해결했었음

**Falsified if**: FR-01 logging이 SendInput Ctrl+T trial에서 정상 출력되는 경우

### H3 — KeyBinding vs PreviewKeyDown 이중 dispatch 충돌

**Claim**: `MainWindow.xaml:148-159` `Window.InputBindings`가 동일 단축키 (Ctrl+T, Ctrl+W, Ctrl+Tab, Ctrl+Shift+W, Alt+V, Alt+H)를 KeyBinding으로 정의하고 있고, `MainWindow.xaml.cs:166-167`에서 PreviewKeyDown 직접 dispatch도 등록. WPF input pipeline에서 PreviewKeyDown이 먼저 fire되지만, SendInput 경로에서는 InputBinding이 modifier 평가를 먼저 시도해서 fail하고 그게 PreviewKeyDown으로 이어지는 과정에서 swallow.

**Evidence to collect**:
- FR-02 인벤토리 표
- KeyBinding 6개를 임시로 모두 제거 → SendInput Ctrl+T 재시도 → 동작 변화 관찰 (binary search)
- 또는 PreviewKeyDown 등록을 임시로 InitializeComponent 직후로 이동 → 등록 시점 race 가설 검증

**Falsified if**: KeyBinding을 모두 제거해도 SendInput Ctrl+T가 여전히 fail

### H4 — Phase 5-E.5 P0-2 BISECT termination 회귀

**Claim**: 직전 commit `35f7d24` 또는 그 이전 P0-2 (`bisect-mode-termination`) 변경이 InitializeRenderer/PreviewKeyDown 등록 순서 또는 OnHostReady 흐름을 inadvertently 변경해서, SendInput 진입 시점에 PreviewKeyDown 핸들러가 아직 등록 안 됐을 가능성. Hardware trial은 사용자가 손으로 누를 때까지 시간이 지나므로 등록 완료 후, SendInput trial은 launcher 직후 즉시 시작이라 race가 노출.

**Evidence to collect**:
- `git log --oneline -- src/GhostWin.App/MainWindow.xaml.cs src/GhostWin.App/MainWindow.xaml` 으로 최근 변경 이력
- e2e harness Operator의 readiness wait sequence 확인 (`Tier B WGC frame mean luma > 0.05`만 기다리고 PreviewKeyDown 등록 완료는 안 기다림) — design D16
- Operator에서 fixed 5초 wait 후 SendInput Ctrl+T → 동작 여부 비교

**Falsified if**: 등록 완료를 기다린 후에도 SendInput Ctrl+T가 여전히 fail

### H5 — UIPI / Integrity Level mismatch

**Claim**: GhostWin이 elevated process (관리자 권한)로 실행되고, e2e Operator가 medium integrity로 실행되면 UIPI가 SendInput을 차단. Alt+키는 system-wide accelerator라 통과, Ctrl+키는 차단.

**Evidence to collect**:
- `Get-Process GhostWin.App | Select-Object Name, Path` + `whoami /groups` → integrity level 비교
- Operator 프로세스 integrity 확인
- 양쪽 동일 integrity로 강제 후 재시도

**Falsified if**: 양쪽이 동일 medium integrity인데도 SendInput Ctrl+T가 fail. (사용자 손가락은 OS kernel-mode이라 항상 통과)

### Hypothesis Decision Matrix

| Hypothesis | 사전 확률 (확실하지 않음) | Falsification 비용 |
|---|:---:|---|
| H1 Modifier state race | 30% | 낮음 (logging만으로 결정) |
| H2 HwndHost Ctrl 흡수 | 35% | 중간 (engine logging 필요할 수 있음) |
| H3 이중 dispatch 충돌 | 20% | 중간 (KeyBinding 임시 제거 + 빌드) |
| H4 P0-2 회귀 | 10% | 낮음 (git log + readiness wait 추가) |
| H5 UIPI mismatch | 5% | 낮음 (integrity 확인) |

> H1과 H2가 합쳐서 65% — Diagnosis Plan은 H1/H2를 1순위로 검증.

---

## 5. Diagnosis Plan

본 cycle의 핵심. **추측 기반 fix 금지** — 각 step의 evidence를 record한 후에만 다음 step.

| Step | 작업 | 산출 | 소요 (확실하지 않음) |
|:---:|---|---|:---:|
| 1 | FR-01 진단 logging 추가 + Release Quick build | 빌드 OK | 30분 |
| 2 | Hardware Ctrl+T 1회 → log 1줄 record. Hardware Alt+V 1회 → log 1줄 record | log file 2줄 baseline | 5분 |
| 3 | e2e harness Operator 단독으로 Ctrl+T 1회 + Alt+V 1회 SendInput → log 변화 관찰 | log file 추가 record (혹은 미출력) | 10분 |
| 4 | FR-02 인벤토리 — `MainWindow.xaml` KeyBinding과 cs 분기 매트릭스 작성 | 표 1개 | 20분 |
| 5 | Step 3 결과로 H1~H5 falsification 1차 진행 | Hypothesis status 갱신 | 30분 |
| 6 | 1순위 가설에 대한 추가 evidence 수집 (FR-03 ETW 또는 binary search 또는 engine logging) | Evidence file | 1-3시간 |
| 7 | Root cause 1개로 수렴. 확정 안 되면 **사용자에게 보고 후 결정 요청** (우회 금지) | Design 문서 §Hypothesis Verification | — |
| 8 | Minimal fix 구현 + 단위 회귀 (PaneNode 9/9) | Code diff | 1-2시간 |
| 9 | e2e harness `--all` 재실행 → Operator 8/8 + Evaluator 8/8 PASS 확인 | summary.json | 30분 |
| 10 | Hardware manual smoke (5 단축키) — 회귀 0 | smoke report | 10분 |

> **중요**: Step 7에서 root cause가 확정 안 되면 **임의로 fix 시도 금지**. 사용자에게 evidence 전체를 보고하고 의사결정 요청. behavior.md 우회 금지 + 추측 금지 원칙.

---

## 6. Risks

| ID | Risk | 영향 | Likelihood (확실하지 않음) | Mitigation |
|---|---|---|:---:|---|
| **R-A** | Root cause가 WPF framework 자체 bug라면 fix 불가능 → workaround 필요 | 본 cycle 미완료 | Low | 사용자에게 보고 후 workaround (예: KeyBinding 제거 후 PreviewKeyDown만 유지) 결정 |
| **R-B** | 진단 logging이 production build에 leak | 성능/디스크 | Low | NFR-01 — `#if DEBUG` 또는 LogLevel.Trace 강제 |
| **R-C** | Fix가 다른 키 조합 (Alt+arrow, Ctrl+C 텍스트 입력) 회귀 | UX 회귀 | Med | NFR-02 manual smoke + 단위 테스트 + e2e 회귀 |
| **R-D** | 사용자 손가락 vs SendInput 차이가 OS-level (UIPI / kernel-mode injection)에서만 통제 가능 → fix 위치가 우리 control 밖 | 본 cycle 미완료 | Low (H5만 해당) | H5를 빠르게 falsify해서 OS-level 의심 제거. UIPI 확정 시 → 별도 elevated launcher로 우회 |
| **R-E** | Diagnosis logging 추가 자체가 race condition을 mask (Heisenbug) | 가설 검증 무효 | Low | logging을 file flush 비동기 + buffer로 실행, 별도 thread 사용 안 함 |
| **R-F** | H1~H5 모두 falsified — H6 (미지의 가설) 등장 | 본 cycle 장기화 | Low | 사용자 보고 후 evidence 공유, council 호출 (`wpf-architect` + `dotnet-expert`) |

---

## 7. Dependencies

| 항목 | 상태 | 비고 |
|---|:---:|---|
| e2e-test-harness Do phase 완료 | Done (`35f7d24`) | Operator + Evaluator 인프라 사용 가능 |
| GhostWin source build 환경 | Ready | `scripts/build_ghostwin.ps1 -Config Release` |
| e2e venv + WGC capture 동작 | Ready | Step 2 PoC PASS (`mean luma 30.47`, `1697x1121`) |
| PaneNode 단위 테스트 9/9 | Ready (`scripts/test_ghostwin.ps1`) | 회귀 detection baseline |
| Council 후보 (필요 시) | `wpf-architect`, `dotnet-expert`, `code-analyzer` | Step 7에서 root cause 미확정 시 호출 |

---

## 8. Acceptance Criteria

본 cycle이 Done으로 close되려면 모두 PASS:

1. **FR-01~FR-07 100% 충족** — 진단 logging, 인벤토리, ETW baseline, side-by-side comparison, hypothesis verification, fix, regression test
2. **NFR-01~NFR-03 100% 충족** — production build leak 0, UX 회귀 0, hot path 영향 0
3. **e2e harness `scripts/test_e2e.ps1 -All`**:
   - Operator 8/8 OK (sent == requested for all SendInput calls)
   - Evaluator 수동 호출 → 8/8 PASS
   - **Match Rate 100%** (5/8 → 8/8)
4. **bisect-mode-termination retroactive QA** 8건 모두 PASS로 update — `bisect-mode-termination.report.md` (또는 design v0.5.2 entry)에 기록
5. **Hardware manual smoke** 5개 단축키 (Ctrl+T, Ctrl+W, Ctrl+Shift+W, Alt+V, Alt+H) 각 1회 → 회귀 0
6. **PaneNode 단위 테스트 9/9** 회귀 0
7. **Design 문서 (`docs/02-design/features/e2e-ctrl-key-injection.design.md`)** 작성 — Diagnosis Plan 실행 결과 + Hypothesis Verification + Root Cause + Fix Rationale 포함
8. **Plan/Design/Report/Archive PDCA cycle** 정상 closeout

---

## 9. Out of Plan (사용자 의사결정 필요 시)

다음 항목은 본 cycle 진행 중 evidence가 추가될 경우 사용자에게 보고 후 의사결정 요청:

- Root cause가 framework bug이거나 OS-level인 경우 (R-A, R-D)
- H1~H5 모두 falsified, H6 등장 (R-F)
- Fix 비용이 50 LOC를 초과해 minimal scope 벗어나는 경우
- KeyBinding 6개 전체 제거가 필요할 경우 (XAML 디자이너가 기대하는 패턴 변경)
- e2e harness Operator 측 readiness wait 변경이 필요해 다른 시나리오 회귀 위험이 생기는 경우
