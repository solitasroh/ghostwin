# shutdown-path-unification Design Document

> **Summary**: 이중 종료 경로를 단일 시퀀스로 통합 + ConPty 타임아웃 도입
>
> **Project**: GhostWin Terminal
> **Author**: Claude + 노수장
> **Date**: 2026-04-10
> **Status**: Draft
> **Planning Doc**: [shutdown-path-unification.plan.md](../../01-plan/features/shutdown-path-unification.plan.md)

---

## 0. Constraints & Locks

| ID | Constraint | Rationale |
|----|-----------|-----------|
| C-1 | `gw_engine_destroy` 네이티브 시그니처 변경 금지 | C++ 엔진 코드는 이 사이클 범위 밖 |
| C-2 | WPF Application lifecycle 순서 존중 | `Closing → Closed → OnExit` 표준 흐름 |
| C-3 | `EngineService.Shutdown()` 내부 순서 유지 | `RenderStop → gw_engine_destroy → Cleanup` 검증 완료 |

---

## 1. Overview

### 1.1 Design Goals

1. `Environment.Exit(0)` 호출을 **타임아웃 fallback 1회로** 축소
2. 윈도우 닫기(X/Alt+F4)와 마지막 workspace 닫기가 **동일한 shutdown 시퀀스** 사용
3. `RenderStop` 중복 호출 제거
4. `engine.Dispose` 가 모든 종료 경로에서 호출됨을 보장

### 1.2 Design Principles

- 참조 구현 (Windows Terminal): 타임아웃 기반 graceful → 강제 종료 fallback
- 최소 변경: 3파일, 기존 구조 재활용
- 행동 규칙 §우회 금지: `Environment.Exit` 는 정당한 fallback이지 우회가 아님

---

## 2. Architecture

### 2.1 Before (현재)

```
경로 A: X 버튼/Alt+F4
  MainWindow.OnClosing()
    ├─ SaveWindowBounds + TsfBridge.Dispose + RenderStop     [UI thread]
    └─ Task.Run { engine.Dispose(); Environment.Exit(0); }   [ThreadPool]
  App.OnExit()                                                [UI thread, 경합]
    ├─ SettingsService.Dispose + RenderStop (중복)
    └─ Environment.Exit(0)                                    [이중 호출]

경로 B: 마지막 workspace 닫기
  MainWindowViewModel → Application.Current.Shutdown()
  App.OnExit()
    ├─ SettingsService.Dispose + RenderStop
    └─ Environment.Exit(0)                                    [engine.Dispose 누락]
```

### 2.2 After (목표)

```
경로 A: X 버튼/Alt+F4
  MainWindow.OnClosing()
    ├─ e.Cancel = true                                        [Window 닫기 지연]
    ├─ if (_shuttingDown) return                               [재진입 방지]
    └─ PerformShutdownAsync()                                  [단일 진입점]
         ├─ SaveWindowBounds()
         ├─ TsfBridge.Dispose()
         ├─ EngineService.Dispose() via Task.Run + 2s timeout
         ├─ SettingsService.Dispose()
         └─ Application.Current.Shutdown()
              → App.OnExit()
                   └─ base.OnExit(e)                           [최소한만]

경로 B: 마지막 workspace 닫기
  MainWindowViewModel → Application.Current.Shutdown()
  → MainWindow.OnClosing() → PerformShutdownAsync()           [동일 경로]
```

### 2.3 핵심 변경: `PerformShutdownAsync()`

```csharp
// MainWindow.xaml.cs
private bool _shuttingDown;

private async void OnClosing(object? sender, CancelEventArgs e)
{
    if (_shuttingDown) return;  // 재진입 방지
    e.Cancel = true;            // Window 닫기 지연
    _shuttingDown = true;

    // 1. UI 리소스 정리
    SaveWindowBounds();
    _tsfBridge?.Dispose();

    // 2. 엔진 정리 (타임아웃 2초)
    var engineRef = _engine;
    var disposeTask = Task.Run(() => (engineRef as IDisposable)?.Dispose());
    if (!disposeTask.Wait(TimeSpan.FromSeconds(2)))
    {
        // ConPty I/O 블로킹 — CrashLog 기록 후 강제 종료
        App.WriteCrashLog("shutdown", new TimeoutException(
            "engine.Dispose blocked >2s (ConPty I/O hang)"));
        Environment.Exit(0);
        return;
    }

    // 3. 설정 저장
    var settings = Ioc.Default.GetService<ISettingsService>();
    (settings as IDisposable)?.Dispose();

    // 4. 정상 종료
    Application.Current.Shutdown();
}
```

### 2.4 `App.OnExit` 최소화

```csharp
// App.xaml.cs
protected override void OnExit(ExitEventArgs e)
{
    base.OnExit(e);
    // Environment.Exit 제거 — PerformShutdownAsync가 모든 정리를 완료함.
    // 타임아웃 시에만 Environment.Exit가 호출됨 (OnClosing 내부).
}
```

### 2.5 `MainWindowViewModel` 변경 불필요

```csharp
// 현재 코드 (변경 없음)
if (Workspaces.Count == 0)
    Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
```

`Application.Current.Shutdown()` → WPF가 `MainWindow.Closing` 이벤트 발생 → `OnClosing` → `PerformShutdownAsync()`. 경로 B가 자연스럽게 경로 A와 합류.

---

## 3. Detailed Changes

### 3.1 `MainWindow.xaml.cs`

| 영역 | Before | After |
|------|--------|-------|
| Field | — | `private bool _shuttingDown;` 추가 |
| `OnClosing` | `Task.Run { Dispose; Exit; }` 직접 실행 | `e.Cancel = true` + `PerformShutdownAsync` |
| RenderStop | `_engine.RenderStop()` 직접 호출 | 제거 — `EngineService.Shutdown()` 내부에서 호출 |

### 3.2 `App.xaml.cs`

| 영역 | Before | After |
|------|--------|-------|
| `OnExit` | SettingsService.Dispose + RenderStop + `Environment.Exit(0)` | `base.OnExit(e)` 만 |

### 3.3 `EngineService.cs` (변경 없음)

`Shutdown()` 메서드가 이미 `RenderStop → gw_engine_destroy → Cleanup` 순서를 수행. `Dispose()` → `Shutdown()` 호출 체인 유지.

---

## 4. Edge Cases

### 4.1 Application.Shutdown() 후 OnClosing 재호출

WPF `Application.Shutdown()`은 열린 Window에 `Close()`를 호출 → `Closing` 이벤트 재발생.
`_shuttingDown` 플래그가 재진입을 차단.

### 4.2 EngineService.Dispose 이중 호출

`EngineService.Shutdown()`은 `_engine == IntPtr.Zero` guard가 있어 안전.

### 4.3 ConPty 2초 타임아웃 판단 근거

- Windows Terminal: 프로세스 종료 대기 타임아웃 유사 패턴
- GhostWin 현재 경험: `gw_engine_destroy`가 정상 시 <500ms, 블로킹 시 무한
- 2초: 정상 종료 여유 + 사용자 체감 허용 범위

### 4.4 경로 B에서 engine 이미 Dispose 된 경우

마지막 workspace 닫기 → `PaneLayoutService.CloseFocused` → SurfaceDestroy + CloseSession → 이 시점에서 engine은 아직 살아있음 (surface만 파괴). `OnClosing`에서 engine.Dispose가 최종 정리.

---

## 5. Implementation Order

```
T-1: MainWindow.xaml.cs — OnClosing 리팩토링
     - _shuttingDown 필드 추가
     - e.Cancel + PerformShutdownAsync 패턴
     - Task.Run + 2초 타임아웃
     - 기존 RenderStop 직접 호출 제거

T-2: App.xaml.cs — OnExit 최소화
     - SettingsService.Dispose 제거 (T-1로 이동)
     - RenderStop 제거 (engine.Dispose 내부)
     - Environment.Exit 제거

T-3: Smoke Test
     - X 버튼 종료 5회
     - Ctrl+W 마지막 workspace 종료 5회
     - Alt+F4 종료 3회
     - 행/크래시 없음 확인
```

---

## 6. Affected Files

| File | Lines | Change |
|------|:-----:|--------|
| `src/GhostWin.App/MainWindow.xaml.cs` | :38, :201-219 | OnClosing → async shutdown + timeout |
| `src/GhostWin.App/App.xaml.cs` | :89-107 | OnExit 최소화 (body ~3줄로 축소) |

**변경 없음**:
- `MainWindowViewModel.cs` — `Application.Shutdown()` 호출 유지
- `EngineService.cs` — `Shutdown()/Dispose()` 내부 변경 없음
- 네이티브 코드 — 변경 없음

---

## 7. Test Plan

| ID | Case | Method | Expected |
|----|------|--------|----------|
| TC-1 | X 버튼 클릭 종료 | 수동 | 프로세스 2초 내 종료, 행 없음 |
| TC-2 | Alt+F4 종료 | 수동 | TC-1과 동일 |
| TC-3 | Ctrl+W 마지막 workspace 종료 | 수동 | 동일 shutdown 경로 사용, 프로세스 종료 |
| TC-4 | 2개 workspace → 1개 닫기 → 나머지 1개 닫기 | 수동 | 첫 닫기 시 workspace만 제거, 두 번째에 프로세스 종료 |
| TC-5 | Split pane 상태에서 X 버튼 | 수동 | 모든 pane/session 정리 후 종료 |
| TC-6 | E2E MQ-8 (window resize) | E2E | 기존 PASS 유지 (종료 경로와 무관하지만 regression 확인) |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-10 | Initial draft | Claude + 노수장 |
