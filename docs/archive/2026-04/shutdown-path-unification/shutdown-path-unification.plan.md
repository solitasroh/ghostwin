# shutdown-path-unification Planning Document

> **Summary**: OnClosing Task.Run + OnExit Environment.Exit 이중 종료 경로를 단일화하고, ConPty I/O 취소 가능성 확보
>
> **Project**: GhostWin Terminal
> **Author**: Claude + 노수장
> **Date**: 2026-04-10
> **Status**: Draft

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 앱 종료 시 MainWindow.OnClosing (Task.Run + Environment.Exit)과 App.OnExit (Environment.Exit)가 독립적으로 경합하며, ConPty I/O 스레드가 gw_engine_destroy에서 블로킹되어 정상 종료 불가 |
| **Solution** | 종료 경로를 OnClosing → (엔진 정리) → Application.Shutdown → OnExit 단일 체인으로 통합. ConPty I/O에 취소 메커니즘 또는 타임아웃 도입 |
| **Function/UX Effect** | 종료 버튼 클릭 또는 마지막 workspace 닫기 시 깔끔하게 프로세스 종료. 행(hang) 없음 |
| **Core Value** | 프로세스 안정성 — Environment.Exit 강제 종료 의존 제거로 리소스 누수/크래시 위험 해소 |

---

## 1. Overview

### 1.1 Purpose

현재 GhostWin의 종료 경로가 이중화되어 있어 race condition과 불완전한 리소스 정리가 발생할 수 있다. 이를 단일 경로로 통합하고, ConPty I/O 스레드의 블로킹 문제를 해결한다.

### 1.2 Background

Phase 5-E pane-split 구현 과정에서 여러 crash fix가 추가되면서 종료 경로가 점진적으로 복잡해졌다. 10-agent v0.5 평가 §4에서 P0-3으로 식별된 기술 부채.

### 1.3 Related Documents

- Pane-Split Design (v0.5): `docs/02-design/features/pane-split.design.md`
- v0.5 완성도 평가: `docs/03-analysis/pane-split-workspace-completeness-v0.5.md`

---

## 2. Current State Analysis

### 2.1 이중 종료 경로

```
경로 A: 윈도우 닫기 (X 버튼 / Alt+F4)
───────────────────────────────────────
OnClose() → Window.Close()
  → OnClosing()
    → SaveWindowBounds()
    → TsfBridge.Dispose()
    → RenderStop()
    → Task.Run {
        engine.Dispose()          ← gw_engine_destroy (블로킹 가능)
        Environment.Exit(0)       ← 강제 종료 ①
      }
  → (동시에) App.OnExit()
    → SettingsService.Dispose()
    → RenderStop() (중복 호출)
    → Environment.Exit(0)         ← 강제 종료 ②

경로 B: 마지막 workspace 닫기 (Ctrl+W)
───────────────────────────────────────
CloseWorkspace() → WorkspaceClosedMessage
  → MainWindowViewModel: Workspaces.Count == 0
    → Application.Current.Shutdown()
      → App.OnExit()
        → SettingsService.Dispose()
        → RenderStop()
        → Environment.Exit(0)     ← 강제 종료 ②
      (OnClosing은 호출되지 않을 수 있음 — Shutdown()이 Window.Close()를 트리거하지만
       타이밍에 따라 경합)
```

### 2.2 문제점

| # | 문제 | 심각도 | 영향 |
|:-:|-------|:------:|------|
| 1 | **Environment.Exit 이중 호출** — OnClosing의 Task.Run과 OnExit 모두 호출 | High | Race condition, 불완전한 정리 |
| 2 | **RenderStop 중복 호출** — OnClosing과 OnExit 양쪽에서 호출 | Low | 현재는 무해하지만 fragile |
| 3 | **engine.Dispose 미호출 경로** — 경로 B에서는 OnExit만 실행, engine Dispose 없음 | Medium | 네이티브 리소스 누수 |
| 4 | **ConPty I/O 블로킹** — gw_engine_destroy가 I/O 스레드 종료 대기 | High | 프로세스 행(hang), Environment.Exit 강제 종료 의존 |
| 5 | **Task.Run 비동기 경합** — 엔진 정리가 백그라운드에서 실행되는 동안 WPF가 OnExit 진행 | Medium | 메모리 접근 위반 가능 |

### 2.3 참조 구현 (Windows Terminal)

Windows Terminal은 종료 시:
1. `AppHost::Close()` → 모든 탭/pane 종료
2. ConPty handle 닫기 → 자식 프로세스에 CTRL_CLOSE_EVENT
3. 프로세스 종료 대기 (짧은 타임아웃)
4. 타임아웃 시 TerminateProcess

핵심: **타임아웃 기반 graceful shutdown + 강제 종료 fallback**.

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| FR-01 | 종료 경로를 단일 체인으로 통합 (Environment.Exit 호출 1회로 축소) | High | Pending |
| FR-02 | 경로 A (윈도우 닫기)와 경로 B (마지막 workspace) 모두 동일 shutdown 시퀀스 사용 | High | Pending |
| FR-03 | 엔진 Dispose가 모든 종료 경로에서 호출됨을 보장 | High | Pending |
| FR-04 | ConPty I/O 블로킹 시 타임아웃 후 프로세스 강제 종료 (fallback) | Medium | Pending |
| FR-05 | RenderStop 중복 호출 제거 | Low | Pending |

### 3.2 Non-Functional Requirements

| Category | Criteria | Measurement |
|----------|----------|-------------|
| 안정성 | 종료 시 크래시/행 없음 | 수동 smoke 10회 |
| 성능 | 종료까지 2초 이내 (타임아웃 포함) | 수동 측정 |
| 호환성 | 기존 동작 변경 없음 (사용자 관점) | E2E MQ-8 (window resize) + 수동 종료 테스트 |

---

## 4. Proposed Design (Draft)

### 4.1 단일 종료 시퀀스

```
[Any trigger]
  → ShutdownAsync()                    ← 새로운 단일 진입점
    → SaveWindowBounds()
    → TsfBridge.Dispose()
    → RenderStop()
    → engine.Dispose() (타임아웃 포함)  ← gw_engine_destroy + ConPty 정리
    → Application.Current.Shutdown()
    → (OnExit에서는 최소한의 정리만)
```

### 4.2 ConPty 타임아웃 전략

```csharp
// MainWindow.xaml.cs 또는 전용 ShutdownService
private async Task ShutdownAsync()
{
    SaveWindowBounds();
    _tsfBridge?.Dispose();
    _engine?.RenderStop();

    // 엔진 정리 (타임아웃 2초)
    var disposeTask = Task.Run(() => (_engine as IDisposable)?.Dispose());
    if (!disposeTask.Wait(TimeSpan.FromSeconds(2)))
    {
        // ConPty I/O 블로킹 — 강제 종료 fallback
        CrashLog.Write("shutdown timeout: engine dispose blocked >2s");
    }

    Application.Current.Shutdown();
}
```

### 4.3 App.OnExit 최소화

```csharp
protected override void OnExit(ExitEventArgs e)
{
    (Ioc.Default.GetService<ISettingsService>() as IDisposable)?.Dispose();
    base.OnExit(e);
    // Environment.Exit 제거 — 정상 종료 경로에서는 불필요
    // WPF Application.Shutdown()이 프로세스를 자연스럽게 종료
}
```

### 4.4 Environment.Exit 최후 수단

`Environment.Exit(0)`는 **ConPty 타임아웃 시에만** 호출. 정상 경로에서는 WPF의 자연스러운 프로세스 종료에 의존.

---

## 5. Scope

### 5.1 In Scope

- [x] FR-01: OnClosing + OnExit 이중화 해소
- [x] FR-02: 윈도우 닫기 / 마지막 workspace 닫기 통합
- [x] FR-03: engine.Dispose 보장
- [x] FR-04: ConPty 타임아웃 (2초)
- [x] FR-05: RenderStop 중복 제거

### 5.2 Out of Scope

- ConPty 네이티브 측 CancellationToken 구현 (C++ I/O 스레드 취소) — 별도 사이클
- SessionManager 리팩토링 (17 public → SRP) — 별도 기술 부채
- P0-4 PropertyChanged detach — 별도 사이클

---

## 6. Risks and Mitigation

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| 타임아웃 2초가 너무 짧아 정상 종료도 강제 종료됨 | Medium | Low | 실측 후 조정. WT도 유사 패턴 |
| OnClosing에서 async 호출 시 WPF가 Window를 먼저 닫음 | High | Medium | `e.Cancel = true` 로 닫기 지연, 정리 완료 후 수동 Close |
| engine.Dispose 후 WPF 컨트롤이 이미 해제된 네이티브 리소스 접근 | Medium | Low | Dispose 순서 엄격 관리 (TsfBridge → RenderStop → Engine) |

---

## 7. Success Criteria

### 7.1 Definition of Done

- [ ] Environment.Exit 호출이 타임아웃 fallback에서만 발생
- [ ] 윈도우 닫기(X/Alt+F4)와 마지막 workspace 닫기가 동일 shutdown 경로 사용
- [ ] RenderStop 1회만 호출
- [ ] engine.Dispose가 모든 종료 경로에서 호출됨
- [ ] 수동 smoke: 정상 종료 10회 행/크래시 없음

### 7.2 Quality Criteria

- [ ] ConPty I/O 블로킹 시 2초 내 프로세스 종료
- [ ] 기존 E2E 시나리오 regression 없음

---

## 8. Affected Files (Estimate)

| File | Change Type | Description |
|------|:-----------:|-------------|
| `src/GhostWin.App/MainWindow.xaml.cs` | Modify | OnClosing → ShutdownAsync 통합, Task.Run 제거 |
| `src/GhostWin.App/App.xaml.cs` | Modify | OnExit 최소화, Environment.Exit 제거 |
| `src/GhostWin.App/ViewModels/MainWindowViewModel.cs` | Modify | Workspaces==0 시 Application.Shutdown 대신 MainWindow.Close 호출 검토 |

---

## 9. Next Steps

1. [ ] Design 문서 작성 (`/pdca design shutdown-path-unification`)
2. [ ] 구현 + smoke 테스트
3. [ ] Gap analysis

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-10 | Initial draft | Claude + 노수장 |
