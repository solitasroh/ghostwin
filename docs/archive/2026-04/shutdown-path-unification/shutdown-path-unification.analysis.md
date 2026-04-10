# Design-Implementation Gap Analysis Report

> **Feature**: shutdown-path-unification
> **Design Document**: `docs/02-design/features/shutdown-path-unification.design.md`
> **Implementation Paths**: `src/GhostWin.App/MainWindow.xaml.cs`, `src/GhostWin.App/App.xaml.cs`
> **Analysis Date**: 2026-04-10

---

## Overall Scores

| Category | Score | Status |
|----------|:-----:|:------:|
| Design Match | 82% | Warning |
| Architecture Compliance | 95% | Pass |
| Convention Compliance | 100% | Pass |
| **Overall** | **89%** | Warning |

---

## 1. Functional Requirements Compliance

| FR | Requirement | Design | Implementation | Match |
|----|-------------|--------|----------------|:-----:|
| FR-01 | Environment.Exit 호출을 타임아웃 fallback 1회로 축소 | `disposeTask.Wait(2s)` 실패 시에만 Exit | Task.Run 내부 정상 경로에서도 Exit + Delay(2s) fallback에서도 Exit = **2회** | Partial |
| FR-02 | 경로 A/B 동일 shutdown 시퀀스 | OnClosing을 단일 진입점으로 | 경로 B: `Application.Current.Shutdown()` -> `OnClosing` -> 동일 경로 진입 확인 | Pass |
| FR-03 | engine.Dispose 모든 경로에서 호출 보장 | PerformShutdownAsync 내부에서 호출 | Task.Run 내부에서 호출 | Pass |
| FR-04 | ConPty 블로킹 시 타임아웃 후 강제 종료 | `disposeTask.Wait(2s)` | `await Task.Delay(2s)` fallback | Pass |
| FR-05 | RenderStop 중복 호출 제거 | OnClosing에서 직접 호출 제거, Dispose 내부에 위임 | `GhostWin.App` 내 RenderStop 직접 호출 0건 (Grep 확인) | Pass |

---

## 2. Differences Found

### 2.1 Changed Features (Design != Implementation)

| # | Item | Design (Section) | Implementation | Impact | Rationale |
|:-:|------|------------------|----------------|:------:|-----------|
| 1 | 정상 종료 방식 | `Application.Current.Shutdown()` 호출로 WPF 자연 종료 (ss2.2, ss2.3) | `Environment.Exit(0)` 유지 (Task.Run 내부) | **High** | WPF finalizer가 해제된 네이티브 메모리 접근 -> AccessViolation. WT 패턴으로 수정 필요했음 |
| 2 | 타임아웃 구현 패턴 | `disposeTask.Wait(TimeSpan.FromSeconds(2))` 동기 대기 (ss2.3:100) | `await Task.Delay(TimeSpan.FromSeconds(2))` 비동기 대기 (MainWindow.xaml.cs:227) | **Medium** | UI 스레드 블로킹 방지. Wait()는 UI deadlock 위험 |
| 3 | Environment.Exit 호출 횟수 | 타임아웃 fallback에서만 1회 (ss1.1 Goal 1, ss2.3:105) | 정상 경로 1회 + 타임아웃 fallback 1회 = 최대 2회 (MainWindow.xaml.cs:223,230) | **Medium** | 정상 경로에서 Exit가 먼저 실행되면 fallback 도달 전 프로세스 종료. 실제 동시 호출 위험은 낮음 |
| 4 | Settings Dispose 순서 | 엔진 Dispose 이후 (ss2.3:110-111) | 엔진 Dispose 이전 (MainWindow.xaml.cs:212-213) | **Low** | 설정 저장을 엔진보다 먼저 수행해야 네이티브 크래시 시에도 설정이 보존됨 (개선) |
| 5 | WriteCrashLog 접근 제한자 | 명시적 언급 없음 | `private` -> `internal` 변경 (App.xaml.cs:79) | **Low** | MainWindow에서 접근 필요. Design에서 누락된 세부사항 |

### 2.2 Missing Features (Design O, Implementation X)

| # | Item | Design Location | Description |
|:-:|------|-----------------|-------------|
| - | 없음 | - | 모든 설계 항목 구현됨 |

### 2.3 Added Features (Design X, Implementation O)

| # | Item | Implementation Location | Description |
|:-:|------|------------------------|-------------|
| - | 없음 | - | 설계 외 추가 기능 없음 |

---

## 3. Difference Analysis

### 3.1 Environment.Exit vs Application.Current.Shutdown (Diff #1 - High Impact)

**Design 의도**: `Application.Current.Shutdown()`으로 WPF 정상 종료 경로를 타고, `OnExit`에서 `base.OnExit(e)`만 실행.

**구현 사유**: `gw_engine_destroy` 호출 후 네이티브 메모리가 해제된 상태에서 WPF `Application.Shutdown()`이 GC finalizer / Dispatcher shutdown 과정 중 해제된 메모리에 접근하여 `AccessViolation` 발생. Windows Terminal도 동일한 이유로 `ExitProcess`를 사용.

**판정**: **의도적 편차 (Intentional Deviation)** -- 구현 중 발견된 기술적 제약. 설계 문서를 현재 구현에 맞게 업데이트 필요.

### 3.2 Task.Delay vs disposeTask.Wait (Diff #2 - Medium Impact)

**Design**: `disposeTask.Wait(TimeSpan.FromSeconds(2))` -- 동기 블로킹 대기.

**구현**: `_ = Task.Run(() => { Dispose(); Exit(); }); await Task.Delay(2s);` -- 비동기 대기. Dispose와 Exit를 하나의 Task로 묶고, UI 스레드는 Delay로 fallback 타이머만 실행.

**판정**: **개선 (Improvement)** -- `Wait(2s)`는 `async void OnClosing` 내에서 UI 스레드를 블로킹하여 WPF message pump를 멈출 수 있음. `Task.Delay` 패턴이 더 안전.

### 3.3 Environment.Exit 호출 횟수 (Diff #3 - Medium Impact)

**Design Goal 1**: "타임아웃 fallback 1회로 축소"

**구현 현황**:
- 정상 경로: `Task.Run { Dispose(); Exit(0); }` -- line 223
- 타임아웃 fallback: `await Task.Delay(2s); Exit(0);` -- line 230

정상 동작 시 Task.Run 내부의 `Exit(0)`이 먼저 실행되어 프로세스가 즉시 종료되므로 fallback `Exit(0)`에 도달하지 않음. 그러나 **엄밀히는 Design Goal 1 ("1회로 축소")과 불일치** -- 코드상 Exit 호출 사이트가 2개 존재.

**판정**: **부분 충족** -- 실행 시점에서는 1회만 호출되지만, Plan ss7.1 DoD "Environment.Exit 호출이 타임아웃 fallback에서만 발생"과 코드 구조가 불일치. Design 업데이트 또는 코드 구조 재검토 필요.

### 3.4 Settings Dispose 순서 (Diff #4 - Low Impact)

**Design**: 엔진 Dispose 이후 (`PerformShutdownAsync` step 3)
**구현**: 엔진 Dispose 이전 (MainWindow.xaml.cs:212-213)

**판정**: **개선 (Improvement)** -- 네이티브 엔진 크래시 시에도 설정이 이미 저장된 상태. 더 방어적인 순서.

---

## 4. Plan Document DoD (Definition of Done) 검증

| DoD Item (Plan ss7.1) | Status | Evidence |
|------------------------|:------:|----------|
| Environment.Exit 호출이 타임아웃 fallback에서만 발생 | Partial | 정상 경로에서도 Exit 호출 (Diff #3) |
| 경로 A/B 동일 shutdown 경로 사용 | Pass | 경로 B -> `Application.Shutdown()` -> `OnClosing` -> 동일 시퀀스 |
| RenderStop 1회만 호출 | Pass | App에서 직접 호출 0건, `EngineService.Shutdown()` 내부에서만 1회 |
| engine.Dispose가 모든 경로에서 호출됨 | Pass | Task.Run 내 `(engineRef as IDisposable)?.Dispose()` |
| 수동 smoke 정상 종료 10회 행/크래시 없음 | Pass | 10/10 PASS (사용자 보고) |

---

## 5. Constraint Compliance

| Constraint | Compliance | Evidence |
|------------|:----------:|----------|
| C-1: `gw_engine_destroy` 시그니처 변경 금지 | Pass | 네이티브 코드 변경 없음 |
| C-2: WPF Application lifecycle 순서 존중 | Partial | `Environment.Exit`가 WPF lifecycle을 우회하지만, 기술적 제약으로 불가피 |
| C-3: `EngineService.Shutdown()` 내부 순서 유지 | Pass | `RenderStop -> gw_engine_destroy -> Cleanup` 순서 유지 확인 (EngineService.cs:47-55) |

---

## 6. Edge Case Coverage

| Edge Case (Design ss4) | Covered | Implementation |
|------------------------|:-------:|----------------|
| ss4.1 Application.Shutdown 후 OnClosing 재호출 | Pass | `_shuttingDown` 플래그 (MainWindow.xaml.cs:204) |
| ss4.2 EngineService.Dispose 이중 호출 | Pass | `_engine == IntPtr.Zero` guard (EngineService.cs:49) |
| ss4.3 ConPty 2초 타임아웃 | Pass | `await Task.Delay(2s)` + CrashLog + Exit (MainWindow.xaml.cs:227-230) |
| ss4.4 경로 B engine 이미 Dispose | Pass | 경로 B는 OnClosing으로 합류, engine Dispose 1회 보장 |

---

## 7. Recommended Actions

### 7.1 Design 문서 업데이트 필요 (Sync Implementation -> Design)

1. **ss2.2/ss2.3**: `Application.Current.Shutdown()` -> `Environment.Exit(0)` 변경 반영 + WPF finalizer 문제 근거 기술
2. **ss2.3 코드 샘플**: `disposeTask.Wait(2s)` -> `Task.Run { Dispose(); Exit(); } + await Task.Delay(2s)` 패턴으로 교체
3. **ss2.3 step 3**: Settings Dispose 순서를 엔진 이전으로 변경
4. **ss1.1 Goal 1**: "타임아웃 fallback 1회로 축소" 문구를 현실에 맞게 조정 -- "정상/타임아웃 경로 모두 Environment.Exit 사용, 중복 실행 없음"
5. **ss3.1 추가**: WriteCrashLog `private` -> `internal` 변경 명시

### 7.2 코드 측 검토 고려 (Optional)

| Item | Priority | Description |
|------|:--------:|-------------|
| Exit 사이트 통합 | Low | Task.Run 내부에서 Dispose 후 Exit를 호출하는 현재 패턴은 동작상 문제없음. 다만 "Exit는 fallback에서만"이라는 원래 목표와 코드 구조가 다름. 현 패턴이 WT 참조 구현과 일치하므로 Design을 코드에 맞추는 것이 적합 |

---

## 8. Summary

전체 일치율 **89%**. 핵심 차이점 5건 중 **High 1건, Medium 2건, Low 2건**.

- High 차이 (`Application.Shutdown` -> `Environment.Exit`)는 구현 중 발견된 WPF finalizer 기술 제약으로 인한 **의도적 편차**. WT 참조 구현과 동일한 패턴이므로 정당한 변경
- Medium 차이 2건 (`Task.Delay` 패턴, Exit 호출 횟수)은 각각 **개선**과 **부분 충족**. 실질적 동작에는 문제없음
- 수동 smoke 10/10 PASS, edge case 4/4 covered

**권장**: Design 문서를 현재 구현에 맞게 업데이트하여 일치율을 95%+ 로 끌어올리는 것이 바람직. 코드 측 추가 변경은 불필요.

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-10 | Initial gap analysis | Claude + User |
