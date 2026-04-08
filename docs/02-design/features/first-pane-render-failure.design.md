# first-pane-render-failure Design Document

> **Summary**: 콜드 스타트 시 첫 pane 이 빈 화면으로 남는 production bug 의 root cause = WPF Dispatcher priority race (`Render(7) > Loaded(6)` 에 의해 BuildWindowCore 의 `Dispatcher.BeginInvoke(Normal=9, HostReady fire)` 가 InitializeRenderer 의 inner `Dispatcher.BeginInvoke(Loaded=6, AdoptInitialHost)` 보다 **먼저** drain → HostReady fire 시점 subscriber=0 → 이벤트 lost → SurfaceCreate 미호출 → blank). Phase 1 진단은 RenderDiag instrumentation + 30회 cold-start reproduction harness + H1~H4 4 가설의 evidence-first falsification 으로 root cause 를 confirm. Phase 2 fix 는 **Option B 단독 채택** (council 만장일치 — Option A 의 priority alignment 만으로는 race 가 fundamentally 안 닫힘) — `_initialHost` 폐기 + PaneContainer 가 첫 pane host 의 single owner + RenderInit hwnd 의존성 제거. **4 동반 변경 lock-in**: HC-1 (DXGI cast log), HC-4 (RegisterAll timing), Q-A4 (gw_render_init hwnd-less), Q-D3 (TsfBridge parent → main window HWND). 7 hidden complexity 발굴 (HC-1~HC-7).
>
> **Project**: GhostWin Terminal
> **Version**: WPF Migration M-9 + Phase 5-E.5 P0 follow-up
> **Author**: 노수장 (CTO Lead)
> **Date**: 2026-04-08
> **Status**: Draft v0.1
> **Planning Doc**: [first-pane-render-failure.plan.md](../../01-plan/features/first-pane-render-failure.plan.md)
> **Council**: rkit:wpf-architect (slim) + rkit:dotnet-expert (slim) + rkit:code-analyzer (slim) + CTO Lead 통합

---

## 0. Council Synthesis Notes (CTO Lead)

본 design 은 3 council member 의 advisories 를 통합한 결과다. 합의 사항과 의견 차이를 explicit 하게 기록하여 향후 cycle 의 reference 로 남긴다.

### 0.1 Council 만장일치 합의

| # | 항목 | 합의 내용 | 근거 (advisory 출처) |
|---|---|---|---|
| C-1 | H1 mechanism = Dispatcher priority race | `Render(7) > Loaded(6)` 가 `Normal(9) HostReady fire` 의 우선 drain 을 야기 | wpf-architect §A1.3, dotnet-expert §D2 H1, code-analyzer §C2 Q-A7 |
| C-2 | H1 의 actual failure mode = subscriber_count==0 (이벤트 lost) | PaneId=0 silent drop (Mode B) 은 발생 안 함 — fire 시 subscriber 가 없어 콜백까지 도달 안 함 | code-analyzer §C2 Q-D1 (decisive evidence) |
| C-3 | H2 (`SurfaceCreate==0`) 의 root-cause 가능성 < 30% | 본 race 의 mechanism 과 직접 관계 없음. 단 native cast log (HC-1) 로 R3 가시화는 가치 있음 | code-analyzer §C4 |
| C-4 | H3 (Border re-parent → BuildWindowCore 재호출) 의 primary mechanism 은 unlikely | 같은 HwndSource 안에서 logical re-parent 는 child HWND 보존. variant (DPI snapshot race) 는 plausible | wpf-architect §A2.4 |
| C-5 | code-analyzer 의 7 hidden complexity (HC-1~HC-7) 모두 인정 | bisect-mode-termination §1.3 발굴 패턴의 재현 — design 시점 lock-in 가치 입증 | code-analyzer §C3, CTO Lead validation |
| C-6 | Option A 는 race 를 fundamentally fix 못 함 | priority alignment 만으로는 enqueue order 가 layout pass timing 에 의존 → race 잔존 가능. Subscribe 를 host 생성과 atomic 하게 묶어야 race-free | wpf-architect §A5.2, code-analyzer §C9, dotnet-expert §D5 (조건부 동의) |
| C-7 | Option B 단독 채택 | Phase 1 진단 결과와 무관. CLAUDE.md `_initialHost` TODO merge target | 만장일치 |
| C-8 | Option B 의 4 동반 변경 lock-in 필수 | HC-1, HC-4, Q-A4, Q-D3 | code-analyzer §C9, wpf-architect §A5.1, dotnet-expert §D5 |
| C-9 | 진단 instrumentation 은 KeyDiag pattern mirror | env-var gate, lock-free, sync I/O, Heisenbug 회피 | dotnet-expert §D1, wpf-architect §A7 |
| C-10 | 30회 cold-start 자동화 + dark-ratio threshold + 선택적 evaluator | manual evaluator 30회 호출은 cost 과다 | dotnet-expert §D3 |
| C-11 | 5-pass falsification 동시 측정 (parallel evidence collection) | 순차 fix-test 금지 — 한 번의 30회 run 으로 4 가설 evidence 동시 수집 | dotnet-expert §D4 |

### 0.2 Council 부분 충돌 (CTO Lead resolution)

| # | 충돌 | wpf-architect | dotnet-expert | code-analyzer | **CTO Lead resolution** |
|---|---|---|---|---|---|
| Δ-1 | Option A vs B 선택 기준 | Phase 1 결과 의존 (조건부 동의) | H1 only → Option A, H1+H3 → Option B | Phase 1 결과와 무관, Option B 강한 권장 | **code-analyzer 채택**. HC-3 (priority alignment 부족) 분석이 evidence-decisive. wpf-architect §A5.2 의 "구조적 단순성 > 최소 변경" 도 같은 방향. dotnet-expert 의 H1-only Option A 권장은 HC-3 evidence 인지 전 작성 → revisit 후 같은 결론으로 수렴 |
| Δ-2 | RenderInit hwnd-less 구현 방식 | `create_for_composition` 또는 `create_headless` 신설 | `RenderBindSwapChain` 분리 (Option B-2) | 현재 `gw_render_init` + 즉시 `release_swapchain` 패턴이 작동 중 → hwnd NULL check 만 완화 | **code-analyzer 채택**. 가장 minimal change. ABI 변경 최소. `create_headless` 신설은 future cycle 의 cleanup |
| Δ-3 | TsfBridge parent hwnd 변경 | 본 cycle 직접 분석 없음 | (대부분 인지 안 됨) | Q-D3 → main window HWND 사용. ADR-011 회귀 검증 필수 | **code-analyzer 채택**. G5b acceptance gate 신설 |
| Δ-4 | RenderDiag log sink rotation | 단일 파일 (KeyDiag 패턴) | 일자별 rotation (`render_{yyyyMMdd}.log`) | (언급 없음) | **dotnet-expert 채택**. 30회 reproduction 분량 + multi-day session 대응 |
| Δ-5 | Phase 1 vs Phase 2 분리 | (언급 없음) | 동시 측정 우선 | 동시 측정 우선 | **dotnet-expert + code-analyzer 합의** — 사용자 선택 (Design 통합) 와 일치 |

### 0.3 확실하지 않음 (Phase 1 진단으로 검증)

1. **WPF LayoutManager.InvalidateMeasure 의 정확한 priority** — high confidence inference 로 `Render(7)` 추정. `Loaded(6)` 보다 큰 priority 라는 사실만 확증되면 race chain 결론은 동일.
2. **HwndHost re-parenting 이 BuildWindowCore 재호출 trigger 여부** — wpf-architect §A2.4 가 unlikely 로 분류. Phase 1 RenderDiag 가 BuildWindowCore call count 를 직접 측정.
3. **`IDXGISwapChain1 → IDXGISwapChain2` cast 의 환경별 fail 빈도** — HC-1 의 LOG_E 추가 후 native log 로 검증.
4. **30회 cold-start baseline 의 blank reproduction 빈도** — Phase 1 G2 gate. baseline 이 0/30 이면 reproduction 강화 필요 (delay 단축, hardware 변경).

---

## 1. Overview

### 1.1 Design Goals

1. **Race confirmation**: Plan §6.3 의 H1 (Dispatcher priority race) 가설을 5-pass evidence-first falsification 으로 confirm 또는 falsify. 추측 절대 금지, 모든 결정에 RenderDiag log evidence 첨부
2. **Race elimination**: 첫 pane 의 host lifecycle 을 PaneContainer 가 단일 owner 로 흡수하여 첫 pane = split pane 의 동일 코드 경로. race 가 존재할 수 없는 구조
3. **Cross-cycle closure**: bisect-mode-termination v0.1 §5 R2 (HostReady race, Low~Medium → 본 cycle 에서 High×High → Closed) + R3 (`SurfaceCreate==0` silent failure) 의 native logging 확장 (HC-1)
4. **CLAUDE.md TODO merge**: `_initialHost 흐름을 폐기하고 PaneContainer 가 host 라이프사이클 단일 owner` 항목 본 cycle 에서 closed
5. **Hidden complexity lock-in**: code-analyzer 의 7 HC 발굴을 design 시점에 명시 — 후속 silent failure 0건 보장
6. **Methodology re-use**: e2e-ctrl-key-injection v0.2 §11.6 의 5-pass falsification 패턴을 production bug 진단의 표준 패턴으로 확립

### 1.2 Design Principles

- **Evidence > Speculation** (`.claude/rules/behavior.md` "근거 기반 문제 해결"): RenderDiag 가 capture 한 log 없이는 어떤 가설도 confirm/falsify 안 함
- **No Workaround**: `Thread.Sleep`, retry loop, double-init, `[Conditional("DEBUG")]` 절대 금지 (dotnet-expert §D6)
- **Heisenbug Avoidance**: instrumentation 이 race timing 을 변경하지 않도록 `Dispatcher.BeginInvoke` 사용 0, sync `Trace.WriteLine` + `File.AppendAllText` 만 (wpf-architect §A7)
- **Atomic Subscribe**: HostReady 구독과 host 생성은 같은 sync execution path 안에서 분리 불가능하게 묶임 (split pane 의 BuildElement 패턴 = race-free reference)
- **Single Code Path**: 첫 pane 과 split pane 의 코드 경로 통일 (Option B 의 핵심)
- **Falsification Rigor**: 단 하나의 가설도 추측만으로 confirm/falsify 안 함. H5 catch-all 까지 enforce
- **Cross-cycle Honesty**: bisect-mode-termination v0.1 의 "R2 Low~Medium" 분류는 false negative 였음을 정직하게 reclassification — "정직한 불확실성" 원칙과 일관

---

## 2. Architecture

### 2.1 Race Mechanism — Confirmed Diagram (council 통합)

```
┌─────────────────────────────────────────────────────────────────────────┐
│ Phase 0: MainWindow.OnLoaded (Loaded=6 priority)                        │
│   _engine.Initialize(callbackContext)         (sync native call)        │
│   _engine.IsInitialized → true                                          │
│   Dispatcher.BeginInvoke(Loaded=6, InitializeRenderer)  ← enqueue A     │
│                                                          [Q: A(L=6)]    │
└─────────────────┬───────────────────────────────────────────────────────┘
                  │ A drain (Loaded=6)
                  ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ Phase 1: A executes — InitializeRenderer (currently buggy)              │
│   PaneContainer.Initialize(_workspaceService)  (sync, store ref)        │
│   var initialHost = new TerminalHostControl()  (PaneId=0, no HWND)      │
│   _initialHost = initialHost                                            │
│   PaneContainer.Content = new Border { Child = initialHost }            │
│       │                                                                 │
│       └─► WPF LayoutManager.InvalidateMeasure                           │
│           → enqueue L(Render=7, layout pass)   ← enqueue L              │
│                                                          [Q: L(R=7)]    │
│   Dispatcher.BeginInvoke(Loaded=6, () => { RenderInit; CreateWorkspace; │
│                                            AdoptInitialHost; ... })     │
│                                                ← enqueue B              │
│                                                       [Q: L(R=7),B(L=6)]│
│   A returns. Dispatcher picks next highest priority.                    │
└─────────────────┬───────────────────────────────────────────────────────┘
                  │ L drain (Render=7) — HIGHER than B(Loaded=6)
                  ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ Phase 2: L executes — WPF layout pass                                   │
│   HwndHost.UpdateWindowSettings → BuildWindowCore                       │
│       │                                                                 │
│       └─► TerminalHostControl.BuildWindowCore                           │
│           CreateWindowEx → _childHwnd (first valid HWND)                │
│           _hostsByHwnd[_childHwnd] = this                               │
│           Dispatcher.BeginInvoke(() => HostReady?.Invoke(...))          │
│             ← Priority OMITTED → defaults to Normal=9                   │
│                                                ← enqueue C              │
│                                            [Q: C(N=9), B(L=6)]          │
│   L returns                                                             │
└─────────────────┬───────────────────────────────────────────────────────┘
                  │ C drain (Normal=9) — HIGHER than B(Loaded=6)
                  ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ Phase 3: C executes — HostReady fire 🚨 RACE TRIGGER                   │
│   HostReady?.Invoke(this, new(PaneId=0, _childHwnd, pw, ph))           │
│       │                                                                 │
│       └─► HostReady event 의 invocation list:                          │
│             ★ PaneContainerControl.OnHostReady 는 AdoptInitialHost      │
│               에서만 += 되는데, AdoptInitialHost 는 아직 안 호출됨      │
│             → invocation list = empty                                   │
│             → HostReady?.Invoke = no-op                                 │
│   ★ EVENT LOST. callback 자체가 호출되지 않으므로                       │
│     PaneLayoutService.OnHostReady 도 호출 안 됨                        │
│     → SurfaceCreate 0번 호출                                           │
│     → _leaves[paneId].SurfaceId 영원히 0                               │
│   C returns                                                             │
└─────────────────┬───────────────────────────────────────────────────────┘
                  │ B drain (Loaded=6) — TOO LATE
                  ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ Phase 4: B executes — InitializeRenderer inner callback                 │
│   var hwnd = initialHost.ChildHwnd  (valid, from Phase 2)               │
│   _engine.RenderInit(hwnd, ...) → bootstrap swapchain → release         │
│   _tsfBridge.Initialize(hwnd, ...)                                      │
│   _engine.RenderStart()                                                 │
│   _workspaceService.CreateWorkspace()  → workspace/session created      │
│   PaneContainer.AdoptInitialHost(initialHost, wsId, paneId, sessionId) │
│       │                                                                 │
│       └─► host.HostReady += OnHostReady   ← TOO LATE                   │
│           host.PaneResizeRequested += OnPaneResized                     │
│           host.PaneId = paneId           ← TOO LATE                    │
│           Content = new Border { Child = host, Tag = paneId }           │
│   _initialHost = null                                                   │
│ ★ HostReady 는 이미 lost. 사용자가 보는 화면: blank                     │
│   (active_surfaces 가 비어 있어 render_loop 가 첫 pane 을 그리지 않음)  │
└─────────────────────────────────────────────────────────────────────────┘
```

**핵심 통찰** (council 합의):

1. **두 priority 순서 역전이 존재**: `Render(7) > Loaded(6)` (layout pass) + `Normal(9) > Loaded(6)` (BuildWindowCore HostReady fire). Plan §6.3 가 단순화한 "Normal(9) > Loaded(6)" 비교는 한 단계 부족 — 실제 chain 은 Render(7) → Normal(9) → Loaded(6) drain order.
2. **Race 가 항상 발현되지는 않는 이유**: Layout invalidation 이 dispatcher frame 경계에 걸쳐 있으면 (B(Loaded) 가 먼저 drain 되어 AdoptInitialHost 가 완료된 뒤 L(Render) 이 drain 되는 시나리오) 정상 경로. 이는 timing 에 의존 — 사용자가 수개월간 "느리다" 로 느낀 hitRate 의 정체.
3. **Mode A 만 발생, Mode B 없음**: PaneId=0 으로 OnHostReady 까지 도달하는 경로는 없음 — fire 시점에 subscriber 가 0 이면 callback 자체가 호출 안 되므로 PaneLayoutService 까지 가지도 못함 (code-analyzer §C2 Q-D1).

### 2.2 Component Diagram — Current vs Option B Target

**Current (buggy)**:
```
┌──────────────┐  creates    ┌────────────────────┐
│  MainWindow  ├────────────▶│ TerminalHostControl│ ◄── _initialHost
│              │             │  (initialHost)     │     ghost ref
│              │  references │                    │
│              │◄────────────┤                    │
└──────┬───────┘             └────────┬───────────┘
       │ Content =                    │
       │ Border{Child=initialHost}    │ HostReady (lost)
       ▼                              ▼
┌──────────────────┐ AdoptInitial ┌──────────────────┐
│ PaneContainer    │◄─────────────┤ (subscribe TOO   │
│ Control          │   Host       │  LATE — race)    │
└──────────────────┘              └──────────────────┘
       │
       ▼
┌──────────────────┐
│ PaneLayoutService│ — OnHostReady NEVER called for first pane
│ (no SurfaceCreate)│
└──────────────────┘
```

**Option B Target (race-free)**:
```
┌──────────────┐                ┌──────────────────┐
│  MainWindow  │ Initialize     │ PaneContainer    │
│              ├───────────────▶│ Control          │ ◄─ Owns ALL hosts
│              │ (engine, ws)   │                  │     (first + split)
│              │                │ - Initialize()   │
│              │ CreateWorkspace│   subscribes to  │
│              ├───────────────▶│   Messenger HERE │
│              │                │   (HC-4 fix)     │
│              │ RenderInit     │                  │
│              │ (hwnd-less,    │                  │
│              │  HC: Q-A4)     │                  │
└──────┬───────┘                └────────┬─────────┘
       │ TsfBridge attach(             │ Receive
       │ MainWindowHwnd)               │ WorkspaceActivatedMessage
       │ (HC: Q-D3)                    ▼
       │                       ┌──────────────────┐
       │                       │ SwitchToWorkspace│
       │                       │  → BuildGrid     │
       │                       │  → BuildElement  │
       │                       │      ┌─ new TerminalHostControl()
       │                       │      ├─ host.HostReady += OnHostReady ◄─ ATOMIC
       │                       │      └─ Border attach
       │                       └────────┬─────────┘
       │                                │ HostReady (subscriber=1, race-free)
       │                                ▼
       │                       ┌──────────────────┐
       │                       │ PaneLayoutService│ — SurfaceCreate called
       │                       │  OnHostReady     │     SurfaceId set
       │                       └──────────────────┘
       │                                │
       │                                ▼
       │                       Render loop sees pane in active_surfaces
       │                                │
       ▼                                ▼
┌──────────────────────────────────────────┐
│ ghostwin_engine (DX11 native)            │
│ gw_render_init(NULL hwnd, ...) ← HC: Q-A4│
│   bootstrap swapchain + release          │
│ gw_surface_create(hwnd, ...) ← per-pane  │
│ gw_render_start                          │
└──────────────────────────────────────────┘
```

### 2.3 Data Flow — Phase 1 Diagnosis vs Phase 2 Fix

```
Phase 1 (Diagnosis):
  cold-start ─▶ MainWindow.OnLoaded ─▶ InitializeRenderer ─▶ ⟨RACE WINDOW⟩
                                                              │
                                                              ├─ RenderDiag captures
                                                              │   13 timestamps + state
                                                              │   per dotnet-expert §D2
                                                              │   schema
                                                              ▼
                                            scripts/repro_first_pane.ps1
                                              30 iterations × screenshot + log copy
                                                              │
                                                              ▼
                                                  summary.json + per-iteration logs
                                                              │
                                                              ▼
                                            5-pass evidence analysis (D4)
                                                              │
                                                              ▼
                                                  H1 confirmed (or H5 catch-all)
                                                              │
                                                              ▼
Phase 2 (Fix Option B):
  Source changes (4 files): MainWindow.xaml.cs, PaneContainerControl.cs,
                             ghostwin_engine.cpp, IEngineService/EngineService.cs
  Companion changes (HC-1, HC-2, HC-4, Q-A4, Q-D3):
                             surface_manager.cpp, PaneLayoutService.cs, TsfBridge.cs
                                                              │
                                                              ▼
                                            scripts/repro_first_pane.ps1 (verify)
                                                              │
                                                              ▼
                                                  blank 0/30 (G3 gate)
                                                              │
                                                              ▼
                                            PaneNode 9/9 + e2e 8/8 + manual G5b/c/d
                                                              │
                                                              ▼
                                                  bisect v0.4 §10.3 cross-cycle update
                                                  CLAUDE.md TODO closeout
                                                              │
                                                              ▼
                                                  Report + Archive
```

---

## 3. Phase 1 — Diagnosis Components

### 3.1 RenderDiag Instrumentation (`src/GhostWin.App/Diagnostics/RenderDiag.cs`)

**Source**: dotnet-expert §D1, wpf-architect §A7.3 (13 진입점), code-analyzer §C2 evidence schema

#### 3.1.1 Class Skeleton

```csharp
// src/GhostWin.App/Diagnostics/RenderDiag.cs
//
// First-pane render lifecycle diagnostic logger.
// Mirrors KeyDiag (src/GhostWin.App/Diagnostics/KeyDiag.cs) pattern:
//   - env-var runtime gate (no allocation when off)
//   - sync Trace.WriteLine + File.AppendAllText (no Dispatcher)
//   - lock-free Interlocked sequence + static lock for IO
//   - fail-silent (Debug.WriteLine fallback on exception)
//
// HEISENBUG AVOIDANCE (CRITICAL):
//   - NEVER use Dispatcher.BeginInvoke / Dispatcher.InvokeAsync inside RenderDiag
//   - NEVER use async/await (SynchronizationContext post 가능)
//   - NEVER use Task.Run (ThreadPool 이동)
//   - NEVER use ConfigureAwait(false)
//   - Stopwatch.GetTimestamp() (QPC) 만 사용 — DateTime.UtcNow 는 wall-clock jitter
//
// Runtime gate: GHOSTWIN_RENDERDIAG env var
//   unset / "0" → LEVEL_OFF (early-return, zero allocation)
//   "1"        → LEVEL_LIFECYCLE (BuildWindowCore, HostReady, OnHostReady, SurfaceCreate)
//   "2"        → LEVEL_TIMING (+ enqueue/dequeue ticks)
//   "3"        → LEVEL_STATE (+ subscriber_count, hwnd transitions)
//
// Output: %LocalAppData%\GhostWin\diagnostics\render_{yyyyMMdd}.log
//   - Append mode, line-per-event, ISO8601 ticks + Stopwatch ns
//   - Daily rotation (KeyDiag 는 단일 파일이지만 본 cycle 의 30회 reproduction 은 일자별 분리)
//   - Thread-safe via static lock_
//
internal static class RenderDiag
{
    public const int LEVEL_OFF       = 0;
    public const int LEVEL_LIFECYCLE = 1;
    public const int LEVEL_TIMING    = 2;
    public const int LEVEL_STATE     = 3;

    private static readonly object _lock = new();
    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private static int _seq;
    private static int _level = -1;
    private static string? _logPath;

    /// <summary>
    /// Primary logging API. Caller must specify minimum level required to emit.
    /// First instruction is GetLevel() check — zero allocation if disabled.
    /// </summary>
    public static void LogEvent(
        int requiredLevel,
        string eventName,
        params (string key, object? value)[] fields)
    {
        if (GetLevel() < requiredLevel) return;
        try
        {
            int seq = Interlocked.Increment(ref _seq);
            long elapsedTicks = _stopwatch.ElapsedTicks;
            long elapsedNs = elapsedTicks * 1_000_000_000L / Stopwatch.Frequency;
            int tid = Environment.CurrentManagedThreadId;
            bool onUi = Application.Current?.Dispatcher.CheckAccess() ?? false;

            var sb = new StringBuilder(256);
            sb.Append('[').Append(DateTime.UtcNow.ToString("O")).Append('|');
            sb.Append("ns=").Append(elapsedNs).Append('|');
            sb.Append('#').Append(seq.ToString("D5")).Append('|');
            sb.Append("tid=").Append(tid).Append('|');
            sb.Append("ui=").Append(onUi ? '1' : '0').Append('|');
            sb.Append("evt=").Append(eventName);
            foreach (var (k, v) in fields)
                sb.Append(' ').Append(k).Append('=').Append(v?.ToString() ?? "null");
            sb.Append(']');

            WriteLine(sb.ToString());
        }
        catch
        {
            // Fail-silent — diagnostics MUST NOT throw into production
        }
    }

    /// <summary>
    /// Scope-based timing helper. Caller pairs MarkEnter/MarkExit.
    /// Returns Stopwatch tick — caller must capture as local var.
    /// Uses QPC, no Dispatcher interaction.
    /// </summary>
    public static long MarkEnter(string scopeName)
    {
        if (GetLevel() < LEVEL_TIMING) return 0;
        long t = _stopwatch.ElapsedTicks;
        LogEvent(LEVEL_TIMING, scopeName + ":enter", ("tick", t));
        return t;
    }

    public static void MarkExit(string scopeName, long enterTick)
    {
        if (GetLevel() < LEVEL_TIMING) return;
        long exitTick = _stopwatch.ElapsedTicks;
        long elapsedNs = (exitTick - enterTick) * 1_000_000_000L / Stopwatch.Frequency;
        LogEvent(LEVEL_TIMING, scopeName + ":exit",
            ("tick", exitTick), ("elapsed_ns", elapsedNs));
    }

    private static int GetLevel()
    {
        if (_level >= 0) return _level;
        // Lazy init — only first call hits env var
        try
        {
            var v = Environment.GetEnvironmentVariable("GHOSTWIN_RENDERDIAG");
            _level = int.TryParse(v, out var n) && n >= 0 && n <= 3 ? n : LEVEL_OFF;
        }
        catch { _level = LEVEL_OFF; }
        return _level;
    }

    private static void WriteLine(string line)
    {
        Debug.WriteLine(line);  // sync, OutputDebugString — μs
        try
        {
            lock (_lock)
            {
                _logPath ??= ResolveLogPath();
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch { /* swallow */ }
    }

    private static string ResolveLogPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostWin", "diagnostics");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"render_{DateTime.UtcNow:yyyyMMdd}.log");
    }
}
```

#### 3.1.2 13 Log Entry Points (wpf-architect §A7.3 + dotnet-expert §D2)

| # | Phase | Location | Required level | Fields |
|---|---|---|---|---|
| 1 | `irenderer-enter` | `MainWindow.InitializeRenderer:111` | LIFECYCLE | `dispatcher_thread=onUi` |
| 2 | `init-host-new` | `MainWindow.InitializeRenderer:120` | LIFECYCLE | `paneId=0`, `instance_hash=N` |
| 3 | `init-host-attach` | `MainWindow.InitializeRenderer:126` | TIMING | `actual_w=N`, `actual_h=N` |
| 4 | `buildwindow-enter` | `TerminalHostControl.BuildWindowCore:38` | LIFECYCLE | `parent=hwnd` |
| 5 | `buildwindow-created` | `TerminalHostControl.BuildWindowCore:64` | LIFECYCLE | `child_hwnd=hwnd`, `w=N`, `h=N` |
| 6 | `hostready-enqueue` | `TerminalHostControl.BuildWindowCore:66` | TIMING | `priority=Normal` |
| 7 | `hostready-fire` | `TerminalHostControl.BuildWindowCore:71` (lambda body) | STATE | `subscriber_count=N`, `pane_id=N`, `child_hwnd=hwnd` |
| 8 | `loaded-enter` | `MainWindow.InitializeRenderer:128` (lambda body) | LIFECYCLE | — |
| 9 | `renderinit-call` | `MainWindow.InitializeRenderer:137` (before) | LIFECYCLE | `hwnd=hwnd`, `w=N`, `h=N` |
| 10 | `renderinit-return` | `MainWindow.InitializeRenderer:137` (after) | LIFECYCLE | `rc=N` |
| 11 | `adopt-call` | `MainWindow.InitializeRenderer:162` | LIFECYCLE | `wsId=N`, `paneId=N`, `sessionId=N` |
| 12 | `onhostready-enter` | `PaneLayoutService.OnHostReady:178` | LIFECYCLE | `paneId=N`, `hwnd=hwnd`, `w=N`, `h=N` |
| 13 | `surfacecreate-return` | `PaneLayoutService.OnHostReady:186` | LIFECYCLE | `surfaceId=N` |

**`subscriber_count` 측정 방법**:
```csharp
// In TerminalHostControl.BuildWindowCore lambda body, before HostReady?.Invoke:
var handler = HostReady;  // local copy, atomic snapshot
int subscriberCount = handler?.GetInvocationList().Length ?? 0;
RenderDiag.LogEvent(RenderDiag.LEVEL_STATE, "hostready-fire",
    ("subscriber_count", subscriberCount),
    ("pane_id", PaneId),
    ("child_hwnd", _childHwnd));
handler?.Invoke(this, new(PaneId, _childHwnd, pw, ph));
```

**`subscriber_count == 0` 가 H1 confirmation evidence** (code-analyzer §C2 Q-D1).

#### 3.1.3 추가 instrumentation (HC 동반)

**HC-1 — surface_manager.cpp:33-34 cast log** (native side):
```cpp
// surface_manager.cpp:33 (BEFORE)
hr = sc1.As(&surf->swapchain);
if (FAILED(hr)) return false;

// surface_manager.cpp:33 (AFTER, HC-1 fix)
hr = sc1.As(&surf->swapchain);
if (FAILED(hr)) {
    LOG_E("IDXGISwapChain1->IDXGISwapChain2 cast failed: 0x%08lX", hr);
    return false;
}
```

**HC-2 — PaneLayoutService.OnHostReady silent return logging** (D11 패턴 확장):
```csharp
// PaneLayoutService.cs:178 (AFTER)
public void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx)
{
    if (!_leaves.TryGetValue(paneId, out var state))
    {
        Trace.TraceError(
            $"[PaneLayoutService] OnHostReady drop: paneId={paneId} not in _leaves " +
            $"(count={_leaves.Count}). Race condition or stale event?");
        return;
    }
    if (state.SurfaceId != 0) return; // Already created — silent OK

    var leaf = FindLeaf(paneId);
    if (leaf?.SessionId == null)
    {
        Trace.TraceError(
            $"[PaneLayoutService] OnHostReady drop: leaf {paneId} has no SessionId. " +
            $"Tree corruption?");
        return;
    }

    var surfaceId = _engine.SurfaceCreate(hwnd, leaf.SessionId.Value, widthPx, heightPx);
    if (surfaceId == 0)
    {
        // Existing D11 log preserved
        Trace.TraceError(
            $"[PaneLayoutService] SurfaceCreate failed for pane {paneId} " +
            $"(session {leaf.SessionId.Value}, {widthPx}x{heightPx}). Pane will be blank.");
        return;
    }
    _leaves[paneId] = state with { SurfaceId = surfaceId };
}
```

### 3.2 Reproduction Harness (`scripts/repro_first_pane.ps1`)

**Source**: dotnet-expert §D3

#### 3.2.1 Function Signatures

```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
    Cold-start reproduction harness for first-pane-render-failure.
    Automates N iterations of: kill → start → wait → screenshot → kill.

.PARAMETER Iterations         (default 30, env GHOSTWIN_REPRO_ITERATIONS)
.PARAMETER DelayMs            (default 2000, env GHOSTWIN_REPRO_DELAY_MS)
.PARAMETER ExePath            (default auto-detect from src/GhostWin.App/bin/Release/...)
.PARAMETER DarkRatioThreshold (default 0.85, env GHOSTWIN_REPRO_THRESHOLD)
.PARAMETER EvaluatePartial    (switch — invoke e2e Evaluator only on "partial" verdicts)
.PARAMETER RenderDiagLevel    (default 3 = STATE)
#>

function Start-ReproRun { ... }                # orchestrator
function Invoke-SingleIteration { ... }        # 1 cycle
function Stop-GhostWinProcess { ... }          # kill
function Get-Screenshot { ... }                # WinAPI BitBlt to PNG
function Get-Verdict { ... }                   # dark-ratio threshold
function Copy-RenderDiagLog { ... }            # render_*.log → iteration dir
function Write-SummaryJson { ... }             # final aggregation
```

#### 3.2.2 summary.json Schema

```json
{
  "run_id": "20260408_143022",
  "exe_path": "C:\\...\\GhostWin.App.exe",
  "iterations": 30,
  "delay_ms": 2000,
  "dark_ratio_threshold": 0.85,
  "render_diag_level": 3,
  "started_at": "2026-04-08T14:30:22.000Z",
  "completed_at": "2026-04-08T14:31:52.000Z",
  "blank_count": 4,
  "partial_count": 2,
  "ok_count": 24,
  "results": [
    {
      "iteration": 1,
      "verdict": "blank",
      "dark_ratio": 0.92,
      "screenshot_path": "001/screenshot.png",
      "render_diag_log_path": "001/render_diag.log",
      "elapsed_ms": 2143,
      "process_pid": 12345
    }
  ]
}
```

#### 3.2.3 Verdict 알고리즘

```powershell
function Get-Verdict {
    param([string]$ScreenshotPath, [float]$Threshold)
    Add-Type -AssemblyName System.Drawing
    $bmp = [System.Drawing.Image]::FromFile($ScreenshotPath)
    try {
        $darkPx = 0
        $totalPx = $bmp.Width * $bmp.Height
        for ($y = 0; $y -lt $bmp.Height; $y += 4) {  # sample every 4th row for speed
            for ($x = 0; $x -lt $bmp.Width; $x += 4) {
                $px = $bmp.GetPixel($x, $y)
                if (($px.R + $px.G + $px.B) -lt 90) { $darkPx++ }  # near-black
            }
        }
        $sampledTotal = ($bmp.Width / 4) * ($bmp.Height / 4)
        $ratio = $darkPx / $sampledTotal
        if ($ratio -gt $Threshold) { return @{verdict='blank'; ratio=$ratio} }
        if ($ratio -gt 0.3) { return @{verdict='partial'; ratio=$ratio} }
        return @{verdict='ok'; ratio=$ratio}
    } finally {
        $bmp.Dispose()
    }
}
```

**Threshold 근거**: GhostWin clear color `0x1E1E2E` (R=30, G=30, B=46). R+G+B = 106 > 90 → 정상 dark BG 도 dark count 에 포함. 하지만 정상 렌더링은 흰 텍스트 + cursor + 프롬프트 등 밝은 픽셀 다수 → ratio < 0.3. blank 는 100% clear color → ratio ≈ 1.0. 0.85 threshold 는 conservative.

#### 3.2.4 Evaluator 통합 (선택적)

`-EvaluatePartial` 스위치 활성 시 "partial" verdict 가 1건 이상이면:
1. 해당 iteration 들의 screenshot + metadata 를 e2e harness `artifacts/repro_first_pane/{run_id}/` 의 sibling 으로 packaging
2. `scripts/test_e2e.ps1 -EvaluateOnly -RunId repro_first_pane_{run_id}` 호출
3. Evaluator subagent 가 시각 verdict 를 evaluator_summary.json 으로 작성

**비용**: 30회 자동 + 1회 evaluator (partial 있을 때만) = 본 cycle Plan §3.2 의 "Diagnostic Cost" NFR 충족.

### 3.3 5-Pass Falsification Protocol

**Source**: dotnet-expert §D4

#### 3.3.1 Protocol Steps

| Pass | Goal | Command | PASS criteria | FAIL action |
|---|---|---|---|---|
| **Pass 1** | Baseline reproduction | `repro_first_pane.ps1 -Iterations 30 -RenderDiagLevel 0` | `blank_count >= 1` (G2 gate) | `-DelayMs 1000` 단축 후 재시도. 여전히 0 이면 사용자 hardware 로 escalate |
| **Pass 2** | Evidence collection | `repro_first_pane.ps1 -Iterations 30 -RenderDiagLevel 3` | blank case 의 RenderDiag log 에 H1~H4 evidence field 모두 존재 | RenderDiag 의 Dispatcher 사용 여부 즉시 재검토 (Heisenbug check) |
| **Pass 3** | H1 analysis | log grep `evt=hostready-fire` → `subscriber_count` field | blank case 에서 `subscriber_count == 0` 관찰됨 → **H1 Confirmed**. 모든 blank case 에서 `subscriber_count >= 1` → **H1 Falsified** | 판단 보류 시 Pass 2 로 복귀 (더 많은 iterations) |
| **Pass 4** | H2/H3/H4 parallel analysis | log grep | H2: `evt=surfacecreate-return surfaceId=0` 관찰 → Confirmed. 모두 nonzero → Falsified.<br>H3: `evt=buildwindow-enter` count per host instance > 1 → Confirmed.<br>H4: `evt=renderinit-return rc!=0` 관찰 → Confirmed. | 같음 |
| **Pass 5** | Root cause decision | 종합 분석 | ≥1 confirmed → Phase 2 fix 진입 (Option B 채택). All falsified → **H5 catch-all** | H5 발화 시 사용자에게 evidence 전체 보고. 추측 fix 절대 금지. e2e-ctrl-key-injection H1~H8→H9 선례 |

#### 3.3.2 H5 Catch-All Exploration Order

H1~H4 모두 falsified 시 새 가설 도출 순서:
1. **H5a**: `gw_surface_create` native return path — surfaceId 가 0이 아닌데도 화면이 blank 인 case → DX11 swapchain present 경로 문제 (C++ 영역, council 보고 필요)
2. **H5b**: `RenderStart` 가 실제로 호출되었는지 (`MainWindow.cs:145` 의 lambda body 가 실행되지 않는 reason)
3. **H5c**: WPF `CompositionTarget.Rendering` 과 HWND present 경로의 z-order 충돌
4. **H5d**: 사용자 hardware specific (GPU driver, DPI scaling)

각 H5 가설이 confirm 되면 Plan revision 후 사용자 동의 받고 진행.

---

## 4. Phase 2 — Fix Components (Option B 단독)

### 4.1 Source Changes Overview

| # | File | Change | LOC delta | Justification |
|:-:|---|---|:-:|---|
| F1 | `src/GhostWin.App/MainWindow.xaml.cs` | `_initialHost` 필드 + `InitializeRenderer` 의 host 생성 + `AdoptInitialHost` 호출 모두 제거 | -50 | Option B core |
| F2 | `src/GhostWin.App/Controls/PaneContainerControl.cs` | `AdoptInitialHost` 메서드 제거. `Initialize(IWorkspaceService)` 안에서 `WeakReferenceMessenger.Default.RegisterAll(this)` 직접 호출 (HC-4 fix) | -35 / +10 | Option B + HC-4 |
| F3 | `src/engine-api/ghostwin_engine.cpp` | `gw_render_init` 의 hwnd NULL check 완화 (또는 dummy hwnd 허용). bootstrap swapchain 생성 시 NULL hwnd 면 skip. release_swapchain 호출은 그대로 | +15 / -5 | Q-A4 |
| F4 | `src/GhostWin.Core/Interfaces/IEngineService.cs` | `RenderInit` 시그니처는 변경 없음 (여전히 hwnd 인자, 단 IntPtr.Zero 허용 명시 XML doc) | +5 (doc only) | Q-A4, ABI 보존 |
| F5 | `src/GhostWin.Interop/EngineService.cs` | `RenderInit` 호출 전후 RenderDiag 진입점 #9, #10 (Phase 1 instrumentation) | +5 | Phase 1 only |
| F6 | `src/GhostWin.Interop/TsfBridge.cs` | parent hwnd 가 main window HWND 든 pane child HWND 든 동작하도록 idempotent (이미 가능 — change 없음 가능성) | 0~+5 | Q-D3 verification |
| F7 | `src/GhostWin.App/Controls/TerminalHostControl.cs` | `BuildWindowCore` 의 RenderDiag 진입점 #4, #5, #6, #7 추가. Production behavior 변화 없음 | +15 | Phase 1 only |
| F8 | `src/GhostWin.App/Diagnostics/RenderDiag.cs` | 신규 파일 (§3.1.1) | +120 | Phase 1 only |
| F9 | `src/engine-api/surface_manager.cpp` | line 33-34 cast fail 시 LOG_E 추가 (HC-1) | +3 | HC-1 |
| F10 | `src/GhostWin.Services/PaneLayoutService.cs` | `OnHostReady` 의 두 silent return 에 Trace.TraceError 추가 (HC-2). RenderDiag 진입점 #12, #13 추가 | +15 | HC-2 + Phase 1 |
| F11 | `scripts/repro_first_pane.ps1` | 신규 파일 (§3.2) | +180 | Phase 1 only |

**Total estimate**: source ~250 LOC delta (+ ~300 LOC for Phase 1 instrumentation that survives as production-safe diagnostics).

### 4.2 MainWindow.xaml.cs — `_initialHost` 폐기 (Option B 핵심)

**Before** (`MainWindow.xaml.cs:111-173`):
```csharp
private void InitializeRenderer()
{
    PaneContainer.Initialize(_workspaceService);

    var initialHost = new Controls.TerminalHostControl();
    _initialHost = initialHost;

    PaneContainer.Content = new Border { Child = initialHost };

    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
    {
        var hwnd = initialHost.ChildHwnd;
        if (hwnd == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(initialHost);
        var w = (uint)Math.Max(1, initialHost.ActualWidth * dpi.DpiScaleX);
        var h = (uint)Math.Max(1, initialHost.ActualHeight * dpi.DpiScaleY);

        if (_engine.RenderInit(hwnd, w, h, 14.0f, "Cascadia Mono") != 0) return;
        _engine.RenderSetClearColor(0x1E1E2E);

        _tsfBridge = new TsfBridge();
        if (_engine is EngineService es)
            _tsfBridge.Initialize(hwnd, es.Handle);
        _engine.TsfAttach(_tsfBridge.Hwnd);

        _engine.RenderStart();

        var workspaceId = _workspaceService.CreateWorkspace();
        if (_sessionManager.ActiveSessionId is not { } activeId) return;
        var initialPaneLayout = _workspaceService.GetPaneLayout(workspaceId);
        if (initialPaneLayout?.FocusedPaneId is not { } initialPaneId) return;

        _engine.TsfFocus(activeId);

        if (initialHost.Parent is Border tempBorder)
            tempBorder.Child = null;

        PaneContainer.AdoptInitialHost(initialHost, workspaceId, initialPaneId, activeId);

        PreviewKeyDown += OnTerminalKeyDown;
        PreviewTextInput += OnTerminalTextInput;

        _initialHost = null;
    });
}
```

**After** (Option B):
```csharp
private void InitializeRenderer()
{
    RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "irenderer-enter");

    // PaneContainer.Initialize 안에서 WeakReferenceMessenger.RegisterAll 을
    // 동기 호출 (HC-4). 이 시점부터 PaneContainer 는 WorkspaceActivatedMessage 등을 받을 수 있음.
    PaneContainer.Initialize(_workspaceService);

    // RenderInit 은 hwnd-less 모드 (IntPtr.Zero). gw_render_init 이 dummy size 로
    // bootstrap swapchain 을 생성/release 하는 기존 패턴 (bisect-mode-termination v0.5.1 §1.4)
    // 을 그대로 따르되, hwnd 인자만 NULL. 100x100 dummy size 는 atlas 가 즉시 재계산
    // (ghostwin_engine.cpp:310-313) 하므로 무관.
    RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "renderinit-call",
        ("hwnd", IntPtr.Zero), ("w", 100), ("h", 100));
    int rc = _engine.RenderInit(IntPtr.Zero, 100, 100, 14.0f, "Cascadia Mono");
    RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "renderinit-return", ("rc", rc));
    if (rc != 0) return;

    _engine.RenderSetClearColor(0x1E1E2E);

    // TSF bridge: main window HWND 를 parent 로 사용 (Q-D3). ADR-011 의 hidden HWND
    // 패턴은 parent 의 정체와 무관하게 동작.
    var mainWindowHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
    _tsfBridge = new TsfBridge();
    if (_engine is EngineService es)
        _tsfBridge.Initialize(mainWindowHwnd, es.Handle);
    _engine.TsfAttach(_tsfBridge.Hwnd);

    _engine.RenderStart();

    // Workspace 생성. WorkspaceService 가 sync 로 WorkspaceCreatedMessage +
    // WorkspaceActivatedMessage 발행. PaneContainer 는 (HC-4 fix 덕분에)
    // 이미 RegisterAll 완료 상태이므로 WorkspaceActivatedMessage 를 받아
    // SwitchToWorkspace → BuildGrid → BuildElement → new TerminalHostControl()
    // + host.HostReady += OnHostReady (atomic) → Border attach.
    var workspaceId = _workspaceService.CreateWorkspace();
    if (_sessionManager.ActiveSessionId is { } activeId)
        _engine.TsfFocus(activeId);

    PreviewKeyDown += OnTerminalKeyDown;
    PreviewTextInput += OnTerminalTextInput;
}
```

**Removed**:
- `private Controls.TerminalHostControl? _initialHost;` 필드 (line 22)
- `_initialHost = initialHost;` (line 121)
- `PaneContainer.Content = new Border { Child = initialHost };` (line 126) — 이제 PaneContainer 가 자기 Content 를 BuildElement 로 채움
- `Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { ... });` outer wrap (line 128) — 모든 작업이 sync InitializeRenderer 안에서 진행
- `if (initialHost.Parent is Border tempBorder) tempBorder.Child = null;` (line 158-159)
- `PaneContainer.AdoptInitialHost(...)` 호출 (line 162)
- `_initialHost = null;` (line 171)

### 4.3 PaneContainerControl.cs — Initialize 안에서 RegisterAll (HC-4 fix)

**Before** (`PaneContainerControl.cs:32-42`):
```csharp
public PaneContainerControl()
{
    Loaded += (_, _) => WeakReferenceMessenger.Default.RegisterAll(this);
    Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
}

public void Initialize(IWorkspaceService workspaces)
{
    _workspaces = workspaces;
}
```

**After**:
```csharp
public PaneContainerControl()
{
    // Loaded 이벤트 의존 제거. RegisterAll 은 Initialize 안에서 sync 호출.
    // Unloaded 시 UnregisterAll 만 유지 (자원 정리).
    Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
}

public void Initialize(IWorkspaceService workspaces)
{
    _workspaces = workspaces;
    // HC-4 fix: 메시지 구독을 Loaded 이벤트가 아닌 Initialize 시점에 sync 수행.
    // 이전에는 Loaded 가 InitializeRenderer 의 CreateWorkspace 호출보다 늦게 fire
    // 되면 WorkspaceActivatedMessage 가 lost 되는 race 가 잠재적으로 존재했음.
    // first-pane-render-failure cycle Design v0.1 §0.1 C-5 / HC-4.
    WeakReferenceMessenger.Default.RegisterAll(this);
}
```

**Removed**: `AdoptInitialHost(...)` 메서드 전체 (lines 48-77).

### 4.4 ghostwin_engine.cpp — gw_render_init hwnd-less (Q-A4)

**Before** (`ghostwin_engine.cpp:247-318`, 핵심 부분):
```cpp
GWAPI int gw_render_init(GwEngine engine, HWND hwnd,
                          uint32_t width_px, uint32_t height_px,
                          float font_size_pt, const wchar_t* font_family) {
    // ...
    RendererConfig config;
    config.hwnd = hwnd;
    config.cols = width_px / 8;
    config.rows = height_px / 16;
    // ...
    eng->renderer = DX11Renderer::create(config, &err);  // create() rejects null hwnd
    // ...
    eng->renderer->release_swapchain();  // line 307
    // ...
}
```

**After** (HC-1 + Q-A4):
```cpp
GWAPI int gw_render_init(GwEngine engine, HWND hwnd,
                          uint32_t width_px, uint32_t height_px,
                          float font_size_pt, const wchar_t* font_family) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
        // ... (existing init)

        RendererConfig config;
        config.hwnd = hwnd;  // may be NULL — handled below
        config.cols = (width_px > 0 ? width_px : 100) / 8;
        config.rows = (height_px > 0 ? height_px : 100) / 16;
        config.allow_null_hwnd = true;  // ★ NEW: explicit allow
        // ...

        // create() 가 hwnd==NULL 시 swapchain 생성 skip 하고 device + pipeline 만 만듦
        eng->renderer = DX11Renderer::create(config, &err);
        if (!eng->renderer) {
            LOG_E("DX11Renderer::create failed: %s", err.c_str());
            return GW_ERR_INTERNAL;
        }

        // bootstrap swapchain 이 NULL hwnd 로 인해 생성되지 않았으므로
        // release_swapchain 도 conditional. SurfaceManager 의 per-pane swapchain
        // 이 정석 경로 (Q-A3 confirmed).
        eng->renderer->release_swapchain();  // idempotent — null swapchain 도 안전하게 처리

        // 기존 staging buffer 초기화 (line 309-318) 그대로
        // ...
        return GW_OK;
    GW_CATCH
}
```

**`dx11_renderer.cpp:611-625` `DX11Renderer::create` 변경**:
```cpp
// dx11_renderer.cpp:611 (BEFORE)
std::unique_ptr<DX11Renderer> DX11Renderer::create(const RendererConfig& config, std::string* err) {
    if (!config.hwnd) {
        if (err) *err = "RendererConfig.hwnd is null";
        return nullptr;
    }
    // ...
}

// dx11_renderer.cpp:611 (AFTER, Q-A4 fix)
std::unique_ptr<DX11Renderer> DX11Renderer::create(const RendererConfig& config, std::string* err) {
    if (!config.hwnd && !config.allow_null_hwnd) {
        if (err) *err = "RendererConfig.hwnd is null and allow_null_hwnd is false";
        return nullptr;
    }
    auto r = std::unique_ptr<DX11Renderer>(new DX11Renderer());
    if (!r->impl_->create_device(err)) return nullptr;
    if (!r->impl_->create_pipeline(err)) return nullptr;
    if (config.hwnd) {
        if (!r->impl_->create_swapchain(config.hwnd, config.cols, config.rows, err))
            return nullptr;
    }
    // ... atlas, pipeline 등 hwnd-independent
    return r;
}
```

**`release_swapchain` idempotency** (`dx11_renderer.cpp` 의 release_swapchain 메서드):
```cpp
void DX11Renderer::Impl::release_swapchain() {
    if (!swapchain) return;  // NEW: idempotent for null
    rtv.Reset();
    swapchain.Reset();
}
```

**RendererConfig struct** 에 `bool allow_null_hwnd = false;` 필드 추가 (header 파일).

### 4.5 surface_manager.cpp — HC-1 cast log

이미 §3.1.3 에 명시. line 33-34 `IDXGISwapChain1 → IDXGISwapChain2` cast 의 silent failure 에 LOG_E 추가.

### 4.6 PaneLayoutService.cs — HC-2 silent return logging

이미 §3.1.3 에 명시. `OnHostReady` 의 두 silent return 에 Trace.TraceError 추가.

### 4.7 TsfBridge.cs — parent hwnd verification (Q-D3)

`MainWindow.InitializeRenderer` 가 main window HWND 를 parent 로 전달하도록 변경했으므로 TsfBridge 자체는 코드 변경 불필요. 단 ADR-011 의 IME 동작 검증을 manual QA acceptance gate 에 추가 (G5b).

만약 TsfBridge 가 parent hwnd 의 specific behavior 에 의존했다면 (e.g., child of pane HWND vs child of main window HWND), 본 cycle Phase 1 진단 단계에서 검증.

---

## 5. Verification Plan

### 5.1 Acceptance Gates (Plan §4.3 + council 추가)

| Gate | 기준 | 측정 | Source |
|---|---|---|---|
| **G1 Diagnosis** | H1~H4 ≥1 confirmed + 나머지 falsified, RenderDiag log evidence 첨부 | log grep + design v0.2 진단 표 | Plan §4.3 |
| **G2 Reproduction baseline** | Fix 적용 전 30회 cold start 에서 blank ≥ 1회 | `repro_first_pane.ps1 -RenderDiagLevel 0` | Plan §4.3 |
| **G3 Fix verification** | Fix 적용 후 30회 cold start 에서 blank 0회 | `repro_first_pane.ps1 -RenderDiagLevel 0` | Plan §4.3 |
| **G4 Regression unit** | PaneNode 9/9 PASS | `scripts/test_ghostwin.ps1 -Configuration Release` | Plan §4.3 |
| **G5 Regression e2e** | e2e harness `-All -Evaluate` 8/8 (MQ-1 visual PASS 포함) | `scripts/test_e2e.ps1 -All -Evaluate` | Plan §4.3 |
| **G5b Manual IME smoke** ★ NEW | 한국어 입력 → Alt+H/V split → 입력 회귀 0. ADR-011 + ADR-012 | manual scenario | code-analyzer §C7 |
| **G5c MicaBackdrop visual smoke** ★ NEW | 창 색상, mica 효과, focus indicator 정상 | manual visual | code-analyzer §C7 |
| **G5d Workspace cold-start sequence** ★ NEW | 콜드 스타트 → Ctrl+T → 두 번째 workspace → Ctrl+W → 첫 종료. 두 번째 workspace 의 첫 pane 정상 render | manual + repro_first_pane | code-analyzer §C7 |
| **G6 Cross-cycle update** | bisect-mode-termination design v0.4 §10.3 entry 작성 + CLAUDE.md `_initialHost` TODO closeout | git diff | Plan §4.3 |
| **G7 No new TODO** | Fix 가 새로운 `// TODO`/`// FIXME` 도입 0 | `grep` | Plan §4.3 |
| **G8 RenderDiag overhead off** ★ NEW | `GHOSTWIN_RENDERDIAG=0` (default) 에서 cold-start latency 증가 0 (KeyDiag 동등) | repro_first_pane 의 elapsed_ms 평균 비교 | NFR-Diagnostic Cost off |
| **G9 RenderDiag overhead on** ★ NEW | `GHOSTWIN_RENDERDIAG=3` 에서 cold-start latency 증가 ≤ 50ms | repro_first_pane 평균 비교 | NFR-Diagnostic Cost on |
| **G10 HC-1 native log** ★ NEW | `surface_manager.cpp` cast log 가 fail 시 trigger 됨 (실제 fail evidence 가 없으면 build 만 verify) | log inspection or build verify | code-analyzer §C9 |

### 5.2 Test Scenarios

**Phase 1 Diagnosis** (4 sequential days 분량):

| MQ | Scenario | Expected verdict |
|:-:|---|---|
| D-1 | Pass 1 baseline (RenderDiag off) — cold start 30 회 | blank_count ≥ 1 |
| D-2 | Pass 2 evidence collection (RenderDiag=3) — cold start 30 회 | blank_count ≥ 1 + log 완전성 |
| D-3 | log analysis (Pass 3-5) — H1 confirm 또는 H5 catch-all | H1 confirmed |
| D-4 | manual hardware reproduction (사용자) — 100 회 cold start | hitRate 측정 (참고 데이터) |

**Phase 2 Verification** (Option B fix 후):

| MQ | Scenario | Expected verdict |
|:-:|---|---|
| V-1 | Pass 1 fix verification — cold start 30 회 (RenderDiag off) | **blank 0/30** ★ G3 |
| V-2 | PaneNode 9/9 unit | 9/9 PASS ★ G4 |
| V-3 | e2e harness `-All -Evaluate` | 8/8 PASS, MQ-1 visual PASS ★ G5 |
| V-4 | Manual IME — 한국어 입력 + Alt+H + 입력 + Alt+V + 입력 | 회귀 0 ★ G5b |
| V-5 | Manual MicaBackdrop visual — 창 색상, mica, focus 인디케이터 | 회귀 0 ★ G5c |
| V-6 | Workspace cold-start sequence — 콜드 + Ctrl+T + Ctrl+W | 모든 단계 정상 ★ G5d |
| V-7 | RenderDiag overhead measurement | off=baseline, on ≤ +50ms ★ G8/G9 |
| V-8 | HC-1 native log build verify | 빌드 성공 ★ G10 |

### 5.3 Regression Risk Matrix (council §C7 통합)

| 영역 | Option B Risk | Likelihood | Mitigation Gate |
|---|---|:-:|---|
| Pane split (Alt+V/H) | BuildElement 첫 호출이 새 entry point | Low | G4 + G5 + G3 |
| Workspace switch | SwitchToWorkspace 첫 호출 = 실제 첫 workspace 진입 | Medium | G5 + G5d |
| Host disposal | OnClosing 영향 없음 (직교) | Very Low | G5 |
| **TSF/IME** | **TsfBridge parent → main window HWND** | **Medium** | **G5b 필수** |
| RenderInit hwnd-less | gw_render_init 의 NULL hwnd 경로 (NEW) | Medium | G3 + G10 |
| HC-4 (RegisterAll timing) | Loaded → Initialize 이동 | Low | G5 + G5d |
| **Mica backdrop** | TsfBridge parent 변경 의 visual side effect | **Low~Medium** | **G5c** |
| HC-1 silent failure (DXGI cast) | 환경 의존 (가상 머신, 일부 GPU driver) | Very Low | G10 (build verify) |
| `_initialHost` ghost reference 제거 | 본 cycle scope core | Low | G3 + G5 |

---

## 6. Implementation Order — Detailed Step-by-Step

Plan §8 의 11-step 을 council 통합으로 detail 화. Phase 1 + Phase 2 가 통합된 단일 흐름.

| # | Step | Owner | Phase | Deliverable | Gate |
|:-:|---|---|---|---|---|
| 1 | Plan v0.1 작성 (완료) | CTO Lead | Plan | `first-pane-render-failure.plan.md` | 사용자 승인 ✅ |
| 2 | Design v0.1 작성 (본 문서) — 3-agent slim council 통합 | CTO Lead | Design | `first-pane-render-failure.design.md` | 사용자 검토 |
| 3 | RenderDiag.cs 작성 (§3.1.1 + §3.1.2 의 13 진입점) — instrumentation only, production behavior 영향 0 | dotnet-expert agent | Do (P1) | `src/GhostWin.App/Diagnostics/RenderDiag.cs` + 진입점 hook | build success |
| 4 | repro_first_pane.ps1 작성 (§3.2) — PowerShell harness | dotnet-expert agent | Do (P1) | `scripts/repro_first_pane.ps1` | dry-run success |
| 5 | Phase 1 Pass 1 baseline 실행 — RenderDiag off, 30 iterations | single agent | Do (P1) | `summary.json` | **G2 PASS** |
| 6 | Phase 1 Pass 2 evidence collection — RenderDiag=3, 30 iterations | single agent | Do (P1) | render_diag.log × 30 | log 완전성 |
| 7 | Phase 1 Pass 3-5 analysis — H1~H4 falsification | code-analyzer agent | Do (P1) | 진단 표 + Design v0.2 §3.3 update | **G1 PASS** (H1 confirmed expected) |
| 8 | Phase 2 Option B implementation — F1~F11 (§4.1 의 11 file changes) | dotnet-expert + cpp-aware agent | Do (P2) | source diffs | build success |
| 9 | HC-1 + HC-2 + HC-4 + Q-A4 + Q-D3 동반 변경 검증 — 각각 별 commit unit | dotnet-expert agent | Do (P2) | 5 separate commits | build success |
| 10 | Phase 2 V-1 fix verification — repro_first_pane 30 iterations | qa-aware agent | Check | summary.json | **G3 PASS (blank 0/30)** |
| 11 | V-2 PaneNode 9/9 | qa-aware agent | Check | test_ghostwin output | **G4 PASS** |
| 12 | V-3 e2e harness `-All -Evaluate` | qa-aware agent | Check | evaluator_summary.json | **G5 PASS** |
| 13 | V-4 manual IME smoke (한국어 입력 + split) | 사용자 + agent | Check | manual log | **G5b PASS** |
| 14 | V-5 manual MicaBackdrop visual smoke | 사용자 | Check | manual log | **G5c PASS** |
| 15 | V-6 manual workspace cold-start sequence | 사용자 + agent | Check | manual log | **G5d PASS** |
| 16 | V-7 RenderDiag overhead measurement (off vs on) | qa-aware agent | Check | summary.json delta | **G8 + G9 PASS** |
| 17 | V-8 HC-1 native build verify | qa-aware agent | Check | build log | **G10 PASS** |
| 18 | bisect-mode-termination design v0.4 §10.3 entry 작성 (CTO Lead synthesis of wpf-architect §A8.3 + code-analyzer §C8 보강) | CTO Lead | Check | bisect design v0.4 | **G6 PASS** |
| 19 | CLAUDE.md update — `_initialHost` TODO 종료 + 본 cycle entry 추가 | CTO Lead | Check | CLAUDE.md diff | G6 |
| 20 | Gap analysis — `/pdca analyze first-pane-render-failure` | gap-detector agent | Check | `docs/03-analysis/first-pane-render-failure.analysis.md` | Match Rate ≥ 90% |
| 21 | Report 작성 — `/pdca report first-pane-render-failure` | report-generator agent | Report | `docs/04-report/first-pane-render-failure.report.md` + Executive Summary inline | — |
| 22 | Commit split (5-7 commits) — Phase 1 / Option B core / HC-1 / HC-2 / HC-4 / Q-A4 / Q-D3 + report | CTO Lead | Report | git history | clean |
| 23 | Archive — `docs/archive/2026-04/first-pane-render-failure/` 이동 | CTO Lead | Archive | archive folder | — |
| 24 | Trigger follow-up — `e2e-mq7-workspace-click` cycle plan | CTO Lead | — | new plan | — |

### 6.1 Critical Gates (Step-level)

- **Step 5 (G2)**: Baseline reproduction 실패 시 stop → 사용자 hardware 로 escalate. Phase 1 진단 자체 불가
- **Step 7 (G1)**: H5 catch-all 발화 시 stop → 사용자 보고. 새 가설 도출 후 design revision
- **Step 10 (G3)**: Fix verification 실패 시 stop → 진단으로 복귀. 절대 추측 fix 금지
- **Step 13-15 (G5b/c/d)**: 사용자 협조 필수. 사용자 부재 시 stop

### 6.2 Commit Split (CTO Lead 권장)

```
feat(diag): add RenderDiag instrumentation for first-pane race
feat(repro): add repro_first_pane.ps1 cold-start harness
feat(layout): close first-pane HostReady race via PaneContainer ownership
fix(engine): allow null hwnd in gw_render_init for hwnd-less mode
fix(diag): log DXGI swap chain cast failures in surface_manager
fix(layout): add silent-return diagnostics to PaneLayoutService.OnHostReady
fix(messenger): subscribe in PaneContainer.Initialize, not Loaded event
docs(report): first-pane-render-failure cycle completion + cross-cycle bisect v0.4
```

---

## 7. Risks (Plan §5 + council 추가)

| # | Risk | Impact | Likelihood | Mitigation |
|:-:|---|:-:|:-:|---|
| R1 (Plan) | Phase 1 진단이 4 가설 모두 falsify | High | Low | 5-pass H5 catch-all (§3.3.2) |
| R2 (Plan, updated U-6) | 30회 reproduction 자동화가 blank 재현 못 함 | High | **Medium (elevated, constrained user QA)** | **4-attempt fallback chain** (§9.2): attempt 1 default → attempt 2 delay 단축 → attempt 3 iteration 100회 → attempt 4 사용자 hardware 1세트. attempt 4 실패 시 stop + Plan revision |
| R3 (Plan) | Option B 가 split pane 회귀 도입 | High | Medium | G4 + G5 + G5d |
| R4 (Plan) | RenderInit hwnd 의존성 제거가 DX11 init 영향 | Medium | Medium | G3 + G10 + Q-A2 evidence (create_device hwnd 의존성 0) |
| R5 (Plan) | RenderDiag instrumentation 이 Heisenbug | Medium | Low | §3.1.1 의 Dispatcher.BeginInvoke 절대 금지 + lock-free design |
| R6 (Plan) | bisect R2 의 mitigation 실패 narrative 가 정직성 원칙과 모순 | Low | High | Plan §1.2 + Design §0.1 narrative 처리 완료 |
| R7 (Plan) | H2 confirm 시 DX11 직접 fix 필요 | Medium | Low~Medium | scope pivot, 사용자 보고. HC-1 가 evidence 강화 |
| R8 (Plan) | 콜드 스타트 정의 모호 | Medium | Medium | repro_first_pane 의 명시적 timing |
| **R9 (Council)** | **HC-4 (RegisterAll timing) fix 없으면 Option B 의 새 race** | **High** | **Medium** | **Step 8 의 F2 commit 에 HC-4 동시 적용 lock-in** |
| **R10 (Council)** | **TsfBridge parent hwnd 변경이 ADR-011 회귀** | **Medium** | **Low~Medium** | **G5b 필수** |
| **R11 (Council)** | **`IDXGISwapChain1→2` cast fail 환경 (HC-1 발현)** | **Low~Medium** | **Very Low** | **HC-1 LOG_E 추가만으로 가시화 충분, fix 는 future cycle** |
| **R12 (Council)** | **Mica backdrop visual side effect (TsfBridge parent 변경 부수)** | **Low** | **Low~Medium** | **G5c 필수** |
| **R13 (Council)** | **WPF LayoutManager.InvalidateMeasure priority 가 inference 와 다름** | **Medium** | **Low** | Phase 1 RenderDiag 가 priority 직접 측정 (`Dispatcher.CurrentPriority` 또는 wrapper 패턴), inference 검증 |
| **R14 (Council)** | **HwndHost re-parenting BuildWindowCore 재호출 inference (H3 variant)** | **Low** | **Low** | Phase 1 RenderDiag 의 BuildWindowCore call count 측정. Option B 채택 시 trivially 회피 (re-parent 자체가 사라짐) |

---

## 8. Cross-cycle Impact

### 8.1 bisect-mode-termination v0.4 §10.3 entry (CTO Lead synthesis)

**wpf-architect §A8.3 draft + code-analyzer §C8 보강 통합**:

```markdown
### 10.3 Cross-cycle Retroactive Update — R2 Reclassification + R3 Logging Extension (2026-04-??)

`first-pane-render-failure` cycle (`docs/archive/2026-04/first-pane-render-failure/`)
이 본 design v0.1 §8 R2 (초기 pane HostReady 레이스) 의 **최초 reproduction +
root cause confirmation + structural fix** 를 수행했다. 동시에 §8 R3 (`SurfaceCreate
== 0` silent failure) 의 진단 가시화를 native side 까지 확장 (HC-1: surface_manager.cpp
의 `IDXGISwapChain1 → IDXGISwapChain2` cast 의 LOG_E 추가).

**R2 reclassification — Mechanism (confirmed)**:

WPF Dispatcher priority race. 정확한 chain (council 합의):
1. `MainWindow.InitializeRenderer` 가 outer `Dispatcher.BeginInvoke(Loaded=6, ...)`
   안에서 `PaneContainer.Content = new Border { Child = initialHost }` 로 visual
   tree attach 를 trigger.
2. WPF LayoutManager 가 `InvalidateMeasure → Dispatcher.BeginInvoke(Render=7, ...)`
   를 enqueue (high confidence inference, Phase 1 RenderDiag verified).
3. InitializeRenderer 가 inner `Dispatcher.BeginInvoke(Loaded=6, AdoptInitialHost)`
   를 enqueue.
4. Dispatcher drain order: `Render(7) → Normal(9) → Loaded(6)`. layout pass 가
   `BuildWindowCore` 를 호출하고, `BuildWindowCore` 의 `Dispatcher.BeginInvoke()`
   (priority 미지정 = `Normal=9`) 가 HostReady fire 를 enqueue.
5. `Normal=9` HostReady fire 가 inner `Loaded=6` AdoptInitialHost 보다 **먼저**
   drain. 이 시점에 `HostReady` 의 invocation list 는 empty (AdoptInitialHost 가
   `host.HostReady += OnHostReady` 를 아직 호출하지 않았으므로).
6. **이벤트 lost**. PaneContainerControl.OnHostReady 호출 안 됨 → PaneLayoutService.
   OnHostReady 호출 안 됨 → SurfaceCreate 호출 안 됨 → `_leaves[paneId].SurfaceId == 0`
   → `active_surfaces` 가 비어 있어 render_loop 가 첫 pane 을 그리지 않음.
7. 사용자가 보는 화면: blank.

**R2 reclassification — Severity**:

- **Impact**: High (변경 없음)
- **Likelihood**: **Low~Medium → High×Medium~High** (수개월 사용자 체감 + e2e Evaluator
  첫 run capture). v0.1 의 "Low~Medium" 분류는 정직한 불확실성의 표현이었으며 오류
  아님. wpf-architect council C4 시점의 `DispatcherPriority.Normal=9` 의 FIFO 가정 (실은
  같은 priority 내 FIFO + 다른 priority 사이는 preemption) 이 likelihood 의 과소평가
  로 이어졌다 — 본 cycle 의 wpf-architect advisory §A1.1 가 정정.
- **Status**: **CLOSED**.

**R2 reclassification — Mitigation 교체**:

- v0.1: "수동 QA 에서 재현 시도 (앱 재시작 20회 반복)" — false negative 였음
- v0.4: **Option B structural fix** — `_initialHost` 폐기 (CLAUDE.md TODO merge),
  `MainWindow` 가 host lifecycle 을 더 이상 소유하지 않음, `PaneContainer` 가 첫
  pane host 의 single owner, 첫 pane = split pane 의 동일 코드 경로 (`BuildElement`
  의 `host.HostReady += OnHostReady` atomic subscribe 패턴). Race 가 **존재할 수 없는
  구조** 로 전환.

**False negative 의 4 가지 narrative** (wpf-architect §A8.2 + code-analyzer
§C2 통합):

1. **Primary**: 수동 QA 의 검증 절차가 "첫 화면이 완전히 blank 인지" 와 "ConPty 출력이
   늦게 나타나는지" 를 구분 못 함. 사용자 quick visual scan 이 "느린 정상" 으로
   blank 를 흡수.
2. **D19/D20 분리 부재**: Operator 와 Evaluator 가 같은 인간일 때 "앱이 켜졌다"
   판정이 "첫 pane 이 렌더되었다" 를 자동 흡수. e2e-evaluator-automation cycle 의
   D19/D20 closed loop 화가 이 흡수를 차단.
3. **Heisenbug**: 수동 재시작 (~1-2 초) 과 자동 재시작 (~수백 ms) 의 timing 차.
   warm queue 상태에서 OnLoaded 가 fire 되면 race window 가 닫힘 (확실하지 않음).
4. **HitRate**: 수개월간 수 백 회 콜드 스타트에서 사용자가 "느리다" 로 체감해 온 사례가
   실은 blank 였을 가능성. 수동 20회 가 운이 좋아 (또는 나빠) 모두 정상 시나리오에
   떨어진 통계적 outlier.

**R3 status update**:

D11 (Trace.TraceError silent failure 가시화) 만으로는 부족 — first-pane-render-failure
cycle 이 HC-1 (`surface_manager.cpp:33-34` 의 `IDXGISwapChain1 → IDXGISwapChain2`
cast 의 LOG_E 추가) 로 native 측 진단 가시화를 한 단계 확장. R3 자체는 본 cycle
에서 reproduction 안 됨 (H1 만 confirmed, H2 falsified) → 상태 = "**still latent,
native logging improved**". 향후 사용자 hardware 에서 R3 가 발현되면 LOG_E evidence
로 native HRESULT 를 즉시 식별 가능.

**Hidden complexity attribution (methodology validation)**:

bisect-mode-termination v0.1 §1.3 의 5 hidden complexity 발굴 패턴이 first-pane-
render-failure cycle 에서 **7 hidden complexity** (HC-1~HC-7) 발굴로 재현됨. 같은
council reviewer pattern (`code-analyzer` + `wpf-architect` + `dotnet-expert`)
하에서 design 시점 hidden complexity 발굴의 정량적 효과가 입증된다. 두 cycle 의
combined 12 HC 발견은 모두 design 시점에 lock-in 되어 Do phase 의 silent regression
0 건 보장.

**D7 (RenderResize ABI 보존) verification**:

bisect v0.1 D7 (`gw_render_resize` no-op deprecate, ABI 호환) 결정이 first-pane-
render-failure cycle 의 Option B (`gw_render_init` hwnd-less, Q-A4) 와 충돌 안 함.
두 변경은 직교 — D7 은 resize API 의 caller-side 호환, Q-A4 는 init API 의
NULL hwnd 허용. Q-A4 는 `RendererConfig.allow_null_hwnd` flag 추가로 backward-
compatible.
```

### 8.2 CLAUDE.md update

**TODO `_initialHost` 항목 종료**:

```diff
 ### TODO — Phase 5-E 잔여 품질 항목

 - [ ] Workspace title/cwd가 active pane의 session을 따라가도록 mirror 확장
 - [ ] MoveFocus DFS → spatial navigation (실제 좌표 기반)
-- [ ] `_initialHost` 흐름을 폐기하고 PaneContainer가 host 라이프사이클 단일 owner가 되도록 — **`first-pane-render-failure` cycle 의 merge target** (bisect R2 실제 reproduction 이 e2e-evaluator-automation 에서 drop out, 2026-04-08)
+- [x] `_initialHost` 흐름을 폐기하고 PaneContainer가 host 라이프사이클 단일 owner — `first-pane-render-failure` cycle 에서 closed (2026-04-??). bisect R2 reproduction confirmed + Option B structural fix
 - [ ] `Pane` 내 multi-surface tab (cmux Surface layer) — Phase 5-G 후보
```

**프로젝트 진행 상태 표 entry 추가**:

```diff
 - [x] **P0-* e2e-evaluator-automation** — ...
+- [x] **P0-* first-pane-render-failure** — bisect-mode-termination v0.1 §5 R2 (HostReady race, Low~Medium → High×High → CLOSED) 의 root cause confirmation + Option B structural fix. `_initialHost` 폐기 + PaneContainer single owner + RenderInit hwnd-less + 4 동반 변경 (HC-1 DXGI cast log, HC-4 RegisterAll timing, Q-A4 gw_render_init hwnd-less, Q-D3 TsfBridge main window HWND parent). Phase 1 진단 = RenderDiag instrumentation + 30회 cold-start reproduction harness + 5-pass evidence-first falsification (e2e-ctrl-key-injection §11.6 패턴 재사용). 7 hidden complexity 발굴 (HC-1~HC-7). Match Rate ?%, ? commit closeout. docs/archive/2026-04/first-pane-render-failure/
 - [ ] **P0-3 종료 경로 단일화** — ...
```

### 8.3 e2e-mq7-workspace-click follow-up

본 cycle 종료 후 trigger. MQ-1 fix 가 적용되면 e2e Evaluator 를 재실행하여 MQ-7
(sidebar workspace click regression) 이 cascade 였는지 (MQ-1 fix 와 동시에 closed)
또는 독립 regression 인지 (별도 cycle 필요) 판정.

### 8.4 Phase 5-F session-restore

본 cycle closeout 후 진입 가능. session-restore plan 이 e2e Evaluator 를 default
verification 으로 사용 → 본 cycle 의 first-pane fix + RenderDiag 인프라 활용.

---

## 9. Resolved Decisions (사용자 승인 2026-04-08)

Plan v0.1 + Design v0.1 3-agent slim council 통합 후 사용자 dialog 로 7 항목 lock-in 완료. 본 section 은 Do phase 진입 전의 final authoritative resolution.

### 9.1 Resolution Table

| # | Question | User decision | Impact |
|:-:|---|---|---|
| **U-1** | Fix scope: Option A vs Option B | **Option B 단독 채택** (council 권장 수락) | §4 Implementation 확정. `_initialHost` 폐기, PaneContainer single owner, 4 동반 변경 (HC-1, HC-4, Q-A4, Q-D3) |
| **U-2** | HC-4 fix 형태: Initialize 직접 vs Loaded + await | **(a) Initialize 안에서 RegisterAll 직접 호출** (council 권장 수락) | §4.3 PaneContainerControl.cs fix 확정 |
| **U-3** | HC-1 (DXGI cast LOG_E) 본 cycle scope 포함 | **포함** (council 권장 수락) | §4.5 + §3.1.3 HC-1 fix 확정. bisect R3 logging 확장 closeout |
| **U-4** | G5b manual QA 범위 | **G5b 만 — 삽입 smoke** (한국어 입력 + Alt+V/H split + 입력) | §5.2 V-4 scenario 확정. ADR-012 CJK fallback 는 out of scope |
| **U-5** | bisect v0.4 §10.3 entry 작성 시점 | **한 번에 (Check phase Step 18)** | §6 Implementation Order Step 18 확정. Evidence-complete 상태에서 단일 entry |
| **U-6** | 30회 reproduction 실패 시 사용자 hardware 협조 | **제한적 협조 — 30회 1 세트만 가능** | **R2 mitigation update 필요** (§9.2 참조) |
| **U-7** | HC-2/3/5/6/7 본 cycle 반영 우선순위 | **HC-2 동반 (D11 패턴 확장), HC-3/5/6/7 design 기록만** (council 권장 수락) | §4.6 HC-2 fix 확정. HC-3/5/6/7 는 design §3 에 evidence 로만 남음 |

### 9.2 U-6 Resolution — R2 Mitigation 강화

사용자 hardware 협조가 **제한적 (30회 1 세트)** 이라는 constraint 에 따라, Phase 1 의 G2 gate (baseline reproduction) 실패 시 fallback plan 을 강화:

**강화된 G2 fallback chain**:

1. **Attempt 1** — 개발자 hardware 에서 `repro_first_pane.ps1 -Iterations 30 -DelayMs 2000` (default) → blank ≥ 1 required
2. **Attempt 2** — 실패 시 개발자 hardware 에서 delay 단축 `-DelayMs 500` 재시도 (race window 압축)
3. **Attempt 3** — 여전히 실패 시 **iterations 확장 `-Iterations 100`** (hitRate 가 1% 수준일 가능성)
4. **Attempt 4** — 여전히 실패 시 **사용자 hardware 에서 30회 1 세트 (사용자 협조 범위)**. 이것이 최후 attempt
5. **Attempt 5 이상 없음** — attempt 4 실패 시 Phase 1 진단 불가 → 사용자에게 evidence 부족 보고 → Plan revision 논의 (H1 확증 없이 Option B 진행 금지)

**Attempt 4 의 운영 절차**:
- 사용자에게 `scripts/repro_first_pane.ps1 -Iterations 30 -RenderDiagLevel 3` 실행 요청
- Artifacts (`summary.json` + `render_diag.log × 30`) 를 repo 에 commit 하여 개발자 분석 enable
- 사용자 시간 < 10분 (30회 × 2 sec delay = ~60 sec 자동화 + log 수집)

**본 constraint 가 Design 에 주는 변화**:
- Risk R2 (Plan §5) 의 likelihood 를 "Medium" 에서 "Medium (elevated due to constrained user QA)" 로 reframe
- §3.3.1 Pass 1 의 FAIL action 에 "Attempt 4 으로 진행" 명시 추가
- Step 5 (G2) 의 critical gate status 유지 — attempt 1-4 모두 실패 시에만 stop

### 9.3 확정 Scope Summary

**Source changes (Phase 2 Option B)**:
1. `src/GhostWin.App/MainWindow.xaml.cs` — `_initialHost` 필드 + `InitializeRenderer` 의 host 생성 + `AdoptInitialHost` 호출 + outer Dispatcher.BeginInvoke(Loaded) wrap 모두 제거
2. `src/GhostWin.App/Controls/PaneContainerControl.cs` — `AdoptInitialHost` 메서드 제거 + `Initialize` 안에서 `RegisterAll` 직접 호출 (HC-4 U-2)
3. `src/engine-api/ghostwin_engine.cpp` — `gw_render_init` 의 `RendererConfig.allow_null_hwnd=true` flag 지원 (Q-A4)
4. `src/renderer/dx11_renderer.cpp` — `create` 의 null hwnd 분기 처리 (swapchain skip)
5. `src/engine-api/surface_manager.cpp` — line 33-34 IDXGISwapChain cast LOG_E (HC-1 U-3)
6. `src/GhostWin.Services/PaneLayoutService.cs` — `OnHostReady` 두 silent return Trace.TraceError (HC-2 U-7)

**Phase 1 instrumentation (production-safe)**:
7. `src/GhostWin.App/Diagnostics/RenderDiag.cs` — 신규 (KeyDiag mirror)
8. `src/GhostWin.App/MainWindow.xaml.cs` — RenderDiag 진입점 #1, #2, #3, #8, #9, #10, #11
9. `src/GhostWin.App/Controls/TerminalHostControl.cs` — 진입점 #4, #5, #6, #7
10. `src/GhostWin.Services/PaneLayoutService.cs` — 진입점 #12, #13
11. `scripts/repro_first_pane.ps1` — 신규 (30회 cold-start harness)

**HC-3/5/6/7 처리**: Design §3 에 evidence 로만 기록. 본 cycle 소스 변경 없음. Option B 가 자연 cover:
- HC-3 (BuildWindowCore BeginInvoke priority 미지정) — 첫 pane 이 `BuildElement` 경로로 가면 무관
- HC-5 (SwitchToWorkspace 첫 workspace 진입) — 검증된 정상 경로
- HC-6 (`_childHwnd` race) — negative finding (race 없음)
- HC-7 (Border re-parent) — `_initialHost` 폐기로 re-parent 자체가 사라짐

---

## 10. Version History

| Version | Date | Changes | Author |
|:-:|---|---|---|
| 0.1 | 2026-04-08 | Initial design — slim 3-agent council (rkit:wpf-architect + rkit:dotnet-expert + rkit:code-analyzer) + CTO Lead synthesis. **Council 만장일치 11 합의 + 5 부분 충돌 resolution**. H1 mechanism = `Render(7) → Normal(9) → Loaded(6)` priority chain (wpf-architect §A1.3 정확한 race diagram). H1 actual mode = subscriber_count==0 (code-analyzer §C2 Q-D1 — Mode A only, Mode B 발생 안 함). 7 hidden complexity 발굴 (HC-1~HC-7). **Option B 단독 채택** (Phase 1 진단 결과와 무관, council 만장일치 — Option A 의 priority alignment 만으로는 race fundamentally fix 안 됨, HC-3 evidence). 4 동반 변경 lock-in: HC-1 (DXGI cast log), HC-4 (RegisterAll timing in Initialize), Q-A4 (gw_render_init hwnd-less via `allow_null_hwnd` flag), Q-D3 (TsfBridge parent → main window HWND). RenderDiag instrumentation spec (KeyDiag mirror, 13 entry points). repro_first_pane.ps1 spec (PowerShell, dark-ratio threshold + selective Evaluator). 5-pass falsification protocol with H5 catch-all. 24-step implementation order (Phase 1 진단 + Phase 2 fix 통합). 14 risks (Plan 8 + Council 6). bisect-mode-termination v0.4 §10.3 entry draft (R2 reclassification + R3 logging extension + methodology validation). CLAUDE.md `_initialHost` TODO closeout draft. Acceptance gates G1~G10 (Plan 7 + Council 3). §9 Open Questions 7 항목 pending. | 노수장 (CTO Lead) |
| 0.1.1 | 2026-04-08 | **§9 Open Questions 7 항목 모두 resolved** (U-1 ~ U-7). §9 → §9 Resolved Decisions 로 대체. 6/7 council 권장 수락, **U-6 만 constraint** (사용자 hardware 협조 제한적 = 30회 1세트). **R2 risk mitigation 강화**: 4-attempt fallback chain (default → delay 단축 → iteration 100회 → 사용자 hardware 1세트 → stop + Plan revision). §9.3 확정 scope summary 추가 (6 source changes + 5 Phase 1 instrumentation). HC-3/5/6/7 design-only 기록 확정 (Option B 자연 cover). Do phase 진입 준비 완료. | 노수장 (CTO Lead) |
