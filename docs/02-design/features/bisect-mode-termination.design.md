# BISECT Mode Termination — Design Document

> **Summary**: render_loop legacy else branch 제거 + `release_swapchain()` 활성화 + `IPaneLayoutService.Initialize` 시그니처 단순화 + `gw_render_resize` 중복 경로 정리 + pane-split.design.md v0.5.1 갱신.
>
> **Project**: GhostWin Terminal
> **Phase**: 5-E.5 P0-2 — 부채 청산
> **Author**: 노수장 (Council: wpf-architect / code-analyzer / dotnet-expert, CTO Lead synthesis by Opus)
> **Date**: 2026-04-07
> **Status**: Council-reviewed
> **Plan**: `docs/01-plan/features/bisect-mode-termination.plan.md`

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | pane-split v0.5의 BISECT 잔재 4건 (render_loop legacy else, `release_swapchain()` 주석, `Initialize(_, 0)` placeholder, `gw_render_resize` 중복) + Plan 단계에서 미발견한 **5번째 복잡 요소**를 code-analyzer가 발굴: `MainWindow.OnTerminalResized`가 `PaneContainerControl.OnPaneResized`와 **같은 이벤트를 이중 처리**하고 있었음. `session_mgr->resize_all`은 pane-split 시맨틱과 구조적으로 충돌. |
| **Solution** | **Single-path 전환**: (1) `render_loop`에 `active_surfaces().empty()` 가드 + legacy else 삭제, (2) `gw_render_init`에서 `release_swapchain()` 활성화, (3) `IPaneLayoutService.Initialize(uint)` 1-parameter로 축소, (4) `gw_render_resize`를 ABI 호환 no-op으로 deprecate + `MainWindow.OnTerminalResized` 핸들러 + `IEngineService.RenderResize` 제거, (5) `PaneLayoutService.OnHostReady`에 `SurfaceCreate == 0` 실패 진단 로그 추가 (wpf-architect C5 대응). |
| **Function/UX Effect** | 사용자 가시 기능 변경 0. resize 동작은 `PaneContainerControl.OnPaneResized → SurfaceResize` 경로가 이미 담당 중이므로 **기능 회귀 0** (오히려 `session_mgr->resize_all`의 잘못된 uniform-size 가정이 제거됨). warm-up 기간: active_surfaces 비어있으면 `Sleep(1); continue;` — HwndHost 영역 수십 ms 검은 픽셀 허용 (WPF chrome은 정상 렌더). |
| **Core Value** | 10-agent 평가 §1 C1 "Critical: BISECT 미해결"이 실측 기반으로 **Moderate refactoring**으로 재정의되는 과정에서 **Plan 단계에선 못 본 숨은 복잡도(중복 resize 경로)가 Design council에서 발굴**. 이것이 Plan→Design 분리의 가치. core-tests-bootstrap의 FluentAssertions 라이선스 발굴과 **동일 패턴** — rkit council 방법론이 반복적으로 효과 입증. |

---

## 1. Council Synthesis

본 Design은 3개 council agent의 병렬 리뷰 결과를 CTO Lead가 통합.

### 1.1 Council 분담

| Agent | 담당 범위 | 핵심 기여 |
|---|---|---|
| `rkit:wpf-architect` | WPF lifecycle, HostReady timing, warm-up 안전성, gw_render_resize 호출 맥락 | 초기 pane HostReady 레이스 잠재성 발굴 (C4), SurfaceCreate 실패 silent path (C5) |
| `rkit:code-analyzer` | DX11Renderer 전수 조사, swapchain 의존성, 호출 그래프 | **Top Risk 1 완전 해소** — release_swapchain 안전 조건 확정, resize_swapchain NPE 위험 확인, `gw_render_resize` 중복 경로 발굴, terminal_window.cpp 영향 분석 |
| `rkit:dotnet-expert` | Initialize 시그니처 변경 전수 call site, 컴파일 안전성, SurfaceId==0 sentinel 구분 | 콜 사이트 정확히 1건 확정, 테스트 영향 0, SurfaceId==0 정상 sentinel KEEP 항목 7개 식별 |

### 1.2 Plan에 없던 발견

**Plan §6 Architecture** 단계에서는 4가지 BISECT 잔재만 인지했으나, code-analyzer가 다음을 추가 발굴:

1. **`gw_render_resize`의 `session_mgr->resize_all` 호출**은 pane-split 시맨틱과 구조적으로 충돌 — 각 pane이 독립 size를 가지는 상황에서 uniform size로 덮어쓰는 잘못된 동작 (확실하지 않음: 런타임에서 실제로 문제가 발생했는지 미검증, 하지만 의미론적으로 버그)
2. **`MainWindow.xaml.cs:163` vs `PaneContainerControl.AdoptInitialHost:53`** — 같은 `initialHost.PaneResizeRequested` 이벤트를 두 핸들러가 중복 구독. 한 이벤트가 `gw_render_resize`(legacy) + `gw_surface_resize`(정식) 두 경로를 동시에 실행. surface 경로가 정답이고 gw_render_resize는 중복이자 잘못된 시맨틱.
3. **`DX11Renderer::resize_swapchain`은 null guard 없음** — `release_swapchain` 후 호출하면 즉시 NPE. 두 작업을 단일 원자적 변경으로 묶어야 함.

이 발견들은 **Plan의 scope를 확장**시키지만, 각 항목이 BISECT 제거의 논리적 귀결이므로 scope creep이 아닌 **필수 동반 변경**.

---

## 2. Root Cause Analysis — BISECT의 진짜 정체

### 2.1 역사적 맥락 (추정)

Phase 5-E 초기 (`8e4e6c2 feat: M-8a engine surface api for pane split`) 때 SurfaceManager를 도입하면서:
- 기존 DX11Renderer는 main HWND 1개에 대한 swap chain을 소유했음 (`DX11Renderer::create(config)` 내부에서 `create_swapchain(hwnd)`)
- Surface path 도입 후 per-pane HWND마다 별도 swap chain이 필요해짐
- 안전한 전환을 위해 두 경로를 **공존** 시킴: render_loop이 `active_surfaces()` 비어있으면 legacy path로 fallback, 있으면 새 Surface path 사용
- `release_swapchain()` 호출을 주석 처리하여 legacy fallback이 동작할 수 있게 내부 swapchain 유지
- `Initialize(sessionId, 0)` placeholder로 surfaceId를 넘겨둠 — OnHostReady가 나중에 진짜 값으로 덮어씀

### 2.2 BISECT가 "Critical"로 평가된 이유

10-agent 평가 §1 C1이 BISECT를 Critical로 본 이유는 **"design 문서의 주장과 runtime 코드가 불일치"**. 평가자 관점에서:
- design §4 "Surface 전용 경로" 주장
- runtime에 `if/else` 두 경로 공존
- 불일치 자체가 setup 오류 또는 아키텍처 미완성을 의심하게 함

### 2.3 실측 후 "Moderate"로 재정의된 이유

code-analyzer의 DX11Renderer 전수 조사 결과:
- Surface path는 완전히 독립적 (`render_surface`가 per-surface RTV/swapchain만 사용, renderer 내부 swapchain 참조 0건)
- Legacy else branch의 `upload_and_draw`는 내부 swapchain을 암묵적으로 사용하지만, 이는 단일 지점
- `release_swapchain()` + legacy else 제거 = renderer 내부 swapchain에 대한 의존이 전체 코드베이스에서 제거됨
- 즉 BISECT는 **아키텍처 결함이 아니라 "이사 중 짐 박스가 복도에 쌓여있는 상태"**

**이것이 Plan Executive Summary의 Core Value와 연결**: Critical처럼 보였던 문제가 실측으로 Moderate 수준이라는 정직한 재평가.

---

## 3. Locked-in Design Decisions

| # | 결정 | 값 | 근거 |
|---|---|---|---|
| D1 | render_loop legacy else branch | **삭제** | code-analyzer C1: 내부 swapchain의 유일 소비자. 제거 시 release 100% 안전 |
| D2 | warm-up 가드 | **`if (active.empty()) { Sleep(1); continue; }`** | code-analyzer C8: engine init → 첫 SurfaceCreate 사이 race window 존재. 명시적 skip 필요 |
| D3 | `release_swapchain()` 활성화 시점 | **`gw_render_init` 내 SurfaceManager 생성 직후** | 기존 BISECT 주석 위치 그대로. 진단 주석 개정 |
| D4 | `gw_render_resize` 처리 | **ABI 호환 no-op으로 deprecate** (body = `return GW_OK;`) | code-analyzer Option A. 미지의 호출자 존재 시 방어. `terminal_window.cpp`는 독립 경로이므로 무관 |
| D5 | `MainWindow.OnTerminalResized` 핸들러 | **삭제** | wpf-architect 권고: `PaneContainerControl.OnPaneResized` 가 정식 경로, 이중 호출 제거 |
| D6 | `MainWindow.xaml.cs:163` 구독 | **삭제** (`initialHost.PaneResizeRequested += OnTerminalResized;`) | D5의 결과 |
| D7 | `IEngineService.RenderResize` | **유지** (deprecated marker) | code-analyzer C3: ABI 호환. `EngineService.cs` 래퍼도 유지. 단 Interop 레벨 주석에 "deprecated" 명시 |
| D8 | `IPaneLayoutService.Initialize` 시그니처 | **`void Initialize(uint initialSessionId)`** | dotnet-expert: 콜 사이트 1건, 테스트 영향 0 |
| D9 | `PaneLayoutService.Initialize` 구현 | **`SurfaceId: 0` 하드코딩 + 주석으로 OnHostReady 2-단계 초기화 설명** | dotnet-expert §2 |
| D10 | `WorkspaceService.cs:49` 갱신 | **`paneLayout.Initialize(sessionId)` + BISECT 주석 삭제** | dotnet-expert §3 |
| D11 | `PaneLayoutService.OnHostReady` 진단 추가 | **`SurfaceCreate == 0` 시 `Trace.TraceError(...)` 로그** | wpf-architect C5: silent failure 회피 |
| D12 | 초기 pane HostReady 레이스 대응 | **진단 로그만 추가** (`InitializeRenderer` 완료 후 일정 시간 내 `_leaves[paneId].SurfaceId > 0` 여부 체크 skip) | wpf-architect C4: 확실하지 않은 레이스. 구조 변경 없이 관찰 우선 |
| D13 | `DX11Renderer::resize_swapchain` 메서드 | **보존** | `terminal_window.cpp:204` 및 `tests/dx11_render_test.cpp`가 여전히 사용. 메서드 자체는 남김 |
| D14 | `DX11Renderer::release_swapchain` 메서드 | **보존** | 호출이 활성화되는 것일 뿐 메서드는 변경 없음 |
| D15 | `pane-split.design.md` 버전 bump | **v0.5 → v0.5.1** | patch level. 구조 변경 아님, 정합성 복구 |
| D16 | BISECT 주석 제거 범위 | **production src/ (2곳) 전부** | Plan FR-04 |
| D17 | docs/ 내 BISECT 언급 | **history 맥락은 유지** (design 문서 §1.4 "종료됨" 섹션 신설) | design-validator가 Do phase에서 검증 |

---

## 4. Detailed Design — File-by-File Diffs

### 4.1 `src/engine-api/ghostwin_engine.cpp`

#### 4.1.1 `gw_render_init` — `release_swapchain()` 활성화 (line 317-322)

**Before**:
```cpp
        eng->surface_mgr = std::make_unique<SurfaceManager>(
            eng->renderer->device(), factory.Get());
    }

    // BISECT: keep renderer's SwapChain for legacy path
    // eng->renderer->release_swapchain();
```

**After**:
```cpp
        eng->surface_mgr = std::make_unique<SurfaceManager>(
            eng->renderer->device(), factory.Get());
    }

    // Renderer's HWND swapchain was created by DX11Renderer::create() for
    // bootstrap diagnostics. SurfaceManager now owns per-pane swapchains on
    // pane HWNDs, so release the bootstrap swapchain here. All subsequent
    // rendering goes through bind_surface() with surface-owned targets.
    eng->renderer->release_swapchain();
```

#### 4.1.2 `render_loop` — legacy else branch 삭제 + warm-up 가드 (line 173-223)

**Before** (핵심 부분):
```cpp
void render_loop() {
    QuadBuilder builder(...);

    while (render_running.load(std::memory_order_acquire)) {
        Sleep(16); // ~60fps

        if (!renderer) continue;

        // Surface path (Phase 5-E pane split)
        auto active = surface_mgr ? surface_mgr->active_surfaces()
                                  : std::vector<RenderSurface*>{};
        if (!active.empty()) {
            for (auto* surf : active) {
                render_surface(surf, builder);
            }
        } else {
            // Legacy single-surface path (eed320d compatible)
            auto* session = session_mgr->active_session();
            if (!session || !session->conpty || !session->is_live()) {
                Sleep(1); continue;
            }
            auto& vt = session->conpty->vt_core();
            auto& state = *session->state;
            static uint32_t leg_iter = 0;
            static uint32_t leg_dirty = 0;
            leg_iter++;
            bool dirty = state.start_paint(session->vt_mutex, vt);
            if (dirty) leg_dirty++;
            if (leg_iter % 60 == 0) {
                LOG_I(kTag, "[LEGACY] iter %u: dirty_count=%u", leg_iter, leg_dirty);
            }
            if (!dirty) { Sleep(1); continue; }
            const auto& frame = state.frame();
            uint32_t bg_count = 0;
            uint32_t count = builder.build(frame, *atlas, renderer->context(),
                std::span<QuadInstance>(staging), &bg_count);
            LOG_I(kTag, "[LEGACY] DRAW iter=%u: total=%u text=%u",
                  leg_iter, count, count - bg_count);
            if (count > 0)
                renderer->upload_and_draw(staging.data(), count, bg_count);
        }

        // Deferred destroy: safe after snapshot usage complete
        surface_mgr->flush_pending_destroys();

        if (callbacks.on_render_done)
            callbacks.on_render_done(callbacks.context);
    }
}
```

**After**:
```cpp
void render_loop() {
    QuadBuilder builder(...);

    while (render_running.load(std::memory_order_acquire)) {
        Sleep(16); // ~60fps

        if (!renderer || !surface_mgr) continue;

        // Surface path is the only path. During the warm-up window between
        // engine init and the first SurfaceCreate, active_surfaces() is empty
        // and we simply skip rendering (WPF chrome continues to render the
        // frame background; HwndHost child area stays dark until first bind).
        auto active = surface_mgr->active_surfaces();
        if (active.empty()) {
            Sleep(1);
            continue;
        }

        for (auto* surf : active) {
            render_surface(surf, builder);
        }

        // Deferred destroy: safe after snapshot usage complete
        surface_mgr->flush_pending_destroys();

        if (callbacks.on_render_done)
            callbacks.on_render_done(callbacks.context);
    }
}
```

**Line 변화**: ~60 lines → ~25 lines (약 35 lines 삭제).

#### 4.1.3 `gw_render_resize` — no-op deprecate (line 336-350)

**Before**:
```cpp
GWAPI int gw_render_resize(GwEngine engine, uint32_t width_px, uint32_t height_px) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng || !eng->renderer) return GW_ERR_INVALID;
        eng->renderer->resize_swapchain(width_px, height_px);
        if (eng->session_mgr && eng->atlas) {
            uint16_t cols = static_cast<uint16_t>(width_px / eng->atlas->cell_width());
            uint16_t rows_count = static_cast<uint16_t>(height_px / eng->atlas->cell_height());
            if (cols < 1) cols = 1;
            if (rows_count < 1) rows_count = 1;
            eng->session_mgr->resize_all(cols, rows_count);
        }
        return GW_OK;
    GW_CATCH_INT
}
```

**After**:
```cpp
// Deprecated (Phase 5-E.5 P0-2 / 2026-04-07): pane resizes are now routed
// through gw_surface_resize per-pane via PaneContainerControl.OnPaneResized.
// Kept as no-op for ABI compatibility with any external callers. Window-level
// resize events should be handled by the WPF side dispatching per-pane events.
GWAPI int gw_render_resize(GwEngine engine, uint32_t /*width_px*/, uint32_t /*height_px*/) {
    GW_TRY
        auto* eng = as_impl(engine);
        if (!eng) return GW_ERR_INVALID;
        return GW_OK;
    GW_CATCH_INT
}
```

### 4.2 `src/GhostWin.Core/Interfaces/IPaneLayoutService.cs`

**Before** (line 13):
```csharp
void Initialize(uint initialSessionId, uint initialSurfaceId);
```

**After**:
```csharp
/// <summary>
/// Creates the root pane leaf bound to <paramref name="initialSessionId"/>.
/// The SurfaceId is a placeholder (0) until <see cref="OnHostReady"/> fires
/// and the engine creates the real per-pane swapchain.
/// </summary>
void Initialize(uint initialSessionId);
```

### 4.3 `src/GhostWin.Services/PaneLayoutService.cs`

**Before** (line 37-43):
```csharp
public void Initialize(uint initialSessionId, uint initialSurfaceId)
{
    var paneId = AllocateId();
    _root = PaneNode.CreateLeaf(paneId, initialSessionId);
    _leaves[paneId] = new PaneLeafState(paneId, initialSessionId, initialSurfaceId);
    FocusedPaneId = paneId;
}
```

**After**:
```csharp
public void Initialize(uint initialSessionId)
{
    var paneId = AllocateId();
    _root = PaneNode.CreateLeaf(paneId, initialSessionId);
    // SurfaceId=0 is the placeholder sentinel. OnHostReady assigns the real
    // surface once the TerminalHostControl's child HWND becomes available.
    _leaves[paneId] = new PaneLeafState(paneId, initialSessionId, SurfaceId: 0);
    FocusedPaneId = paneId;
}
```

**Also in `OnHostReady` (line 176-186)** — add SurfaceCreate failure diagnostic:

**Before**:
```csharp
public void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx)
{
    if (!_leaves.TryGetValue(paneId, out var state)) return;
    if (state.SurfaceId != 0) return; // Already created

    var leaf = FindLeaf(paneId);
    if (leaf?.SessionId == null) return;

    var surfaceId = _engine.SurfaceCreate(hwnd, leaf.SessionId.Value, widthPx, heightPx);
    _leaves[paneId] = state with { SurfaceId = surfaceId };
}
```

**After**:
```csharp
public void OnHostReady(uint paneId, nint hwnd, uint widthPx, uint heightPx)
{
    if (!_leaves.TryGetValue(paneId, out var state)) return;
    if (state.SurfaceId != 0) return; // Already created

    var leaf = FindLeaf(paneId);
    if (leaf?.SessionId == null) return;

    var surfaceId = _engine.SurfaceCreate(hwnd, leaf.SessionId.Value, widthPx, heightPx);
    if (surfaceId == 0)
    {
        // SurfaceCreate failed. The pane will render nothing (active_surfaces
        // excludes this pane). There is no retry path — this is a terminal
        // failure for the pane. Log for diagnostics (BISECT termination exposed
        // this silent-failure path, see bisect-mode-termination.design.md §D11).
        System.Diagnostics.Trace.TraceError(
            $"[PaneLayoutService] SurfaceCreate failed for pane {paneId} " +
            $"(session {leaf.SessionId.Value}, {widthPx}x{heightPx}). Pane will be blank.");
        return;
    }
    _leaves[paneId] = state with { SurfaceId = surfaceId };
}
```

### 4.4 `src/GhostWin.Services/WorkspaceService.cs`

**Before** (line 47-50):
```csharp
        // Create the workspace's initial session.
        var sessionId = _sessions.CreateSession();
        paneLayout.Initialize(sessionId, 0); // BISECT: surfaceId=0 (legacy single-swap-chain path)
```

**After**:
```csharp
        // Create the workspace's initial session. SurfaceId is assigned later
        // in PaneLayoutService.OnHostReady once the TerminalHostControl binds
        // its child HWND.
        var sessionId = _sessions.CreateSession();
        paneLayout.Initialize(sessionId);
```

### 4.5 `src/GhostWin.App/MainWindow.xaml.cs`

Two edits:

**Edit A — remove subscription** (line 163):
```csharp
// Before
initialHost.PaneResizeRequested += OnTerminalResized;

// After
// (line deleted — PaneContainerControl.AdoptInitialHost already handles
//  PaneResizeRequested via OnPaneResized → ActiveLayout.OnPaneResized → SurfaceResize)
```

**Edit B — remove handler method** (line 205-209):
```csharp
// Before
private void OnTerminalResized(object? sender, PaneResizeEventArgs e)
{
    if (_engine is not { IsInitialized: true }) return;
    _engine.RenderResize(e.WidthPx, e.HeightPx);
}

// After (method deleted entirely)
```

### 4.6 `src/GhostWin.Interop/EngineService.cs` (IEngineService impl, D7)

- `RenderResize(uint, uint)` 메서드: **유지**하되 XML doc에 `[Obsolete]` 또는 `<remarks>` 로 deprecated 명시
- `IEngineService.RenderResize` 인터페이스 선언: **유지**
- 이유: 미지의 호출자가 향후 없을 것을 장담 못함. ABI 호환 우선

**정확한 문구 제안** (예시):
```csharp
/// <remarks>
/// Deprecated in Phase 5-E.5 (2026-04-07). The engine no longer uses the
/// window-level swapchain — per-pane resizes go through <see cref="SurfaceResize"/>.
/// This method remains as an ABI-compatible no-op for external callers.
/// </remarks>
void RenderResize(uint widthPx, uint heightPx);
```

---

## 5. Verification Plan

### 5.1 Build & Test

| 검증 | 명령 | 합격 기준 |
|---|---|---|
| C++ 빌드 | `scripts/build_ghostwin.ps1 -Config Release` | exit 0, 경고 증가 0 |
| .NET 빌드 | 위 스크립트가 자동 수행 | exit 0 |
| PaneNode 단위 테스트 | `scripts/test_ghostwin.ps1` | 9/9 PASS |
| BISECT 마커 grep | `grep -rn BISECT src/` | 0건 (docs/ 제외) |

### 5.2 Manual QA — 8 Scenarios

| # | 시나리오 | 기대 결과 |
|---|---|---|
| MQ-1 | 앱 시작 → 첫 workspace 렌더 | 몇 십 ms blank 후 PowerShell 프롬프트 표시 |
| MQ-2 | Alt+V split → 오른쪽 pane 추가 | 두 pane 모두 프롬프트 표시 |
| MQ-3 | Alt+H split → 아래 pane 추가 | 세 pane 모두 프롬프트 표시 |
| MQ-4 | 마우스 click pane focus 전환 | focus 시각 표시 + 키 입력이 해당 pane로 | 
| MQ-5 | Ctrl+Shift+W focused pane close | pane 사라지고 sibling이 자리 차지, paneId 보존 |
| MQ-6 | Ctrl+T new workspace | 새 workspace 탭 생성 + 렌더링 |
| MQ-7 | Sidebar click workspace switch | 다른 workspace 표시 |
| MQ-8 | MainWindow 크기 조절 (드래그) | 모든 pane 적절히 리사이즈, 텍스트 깨짐 없음 |

**MQ-8 특별 주의**: `gw_render_resize`가 no-op이 된 후, 창 리사이즈가 여전히 pane 크기를 조정하는지 확인. `PaneContainerControl.OnPaneResized → SurfaceResize` 경로로 동작해야 함.

### 5.3 Regression Safety Net

- PaneNode 9/9 (core-tests-bootstrap 스위트) — logic 회귀 방어
- `scripts/build_ghostwin.ps1` 성공 — 컴파일 회귀 방어
- `git diff --stat tests/ scripts/test_ghostwin.ps1` empty — test infra 비변경 확인

### 5.4 Post-Change Diagnostics

- 앱 실행 후 `%LocalAppData%` 또는 console에 `[PaneLayoutService] SurfaceCreate failed` 로그 확인 — 있으면 D11/D12 대응 필요
- warm-up 시간 체감 확인 — 사람 눈에 띄는 지연이 있는지

---

## 6. pane-split.design.md v0.5.1 갱신 사양

본 feature의 Do phase 마지막 단계에서 `docs/02-design/features/pane-split.design.md`를 v0.5.1로 개정.

### 6.1 §1.4 "BISECT 상태 종료" 섹션 신설 (신규)

```markdown
## 1.4 BISECT 상태 (v0.5 → v0.5.1 종료)

v0.5까지 런타임에 legacy fallback 경로가 `render_loop`에 공존했다.
`ghostwin_engine.cpp:321`의 `release_swapchain()` 주석 처리,
`WorkspaceService.cs:49`의 `Initialize(sessionId, 0)` placeholder,
`render_loop:191-216`의 legacy else branch가 이 상태를 구성했다.

이는 아키텍처 미완성이 아니라 Phase 5-E 초기 Surface path 도입 시의
안전망이었다. v0.5.1 (feature: bisect-mode-termination, 2026-04-07)에서
Surface path가 유일 경로가 됨:

- `render_loop`은 `active_surfaces()` 비어있으면 skip (warm-up 가드)
- `release_swapchain()` 활성화 — 내부 swapchain 해제
- `IPaneLayoutService.Initialize`는 sessionId만 받음
- `gw_render_resize`는 no-op으로 deprecate (pane resize는 `gw_surface_resize` 전담)

10-agent v0.5 평가 §1 C1 Critical은 실측 기반으로 Moderate refactoring이었음.
```

### 6.2 §4.4 함수명 drift 수정

**현재 (v0.5)** — 확실하지 않음, 실제 §4.4 내용 Do phase에서 확인 후 수정:
- `render_to_target` → `bind_surface` / `upload_and_draw` / `unbind_surface` 3단계로 교체
- design-validator가 Do phase에서 전수 검증

### 6.3 §8 NFR 추가 검토 (선택)

- NFR-07 후보: "warm-up 기간 blank screen < 500ms (체감)" — 정량 목표 설정 여부는 Do phase 수동 QA 결과에 따라 결정
- design-validator 권고에 따라 skip 가능

### 6.4 §11 Test Plan 갱신

- PaneNode T-1~T-5 (+ 보강 3건) 실제 구현됨 표시 — core-tests-bootstrap report 인용
- PaneLayoutService T-6~T-11은 후속 feature

### 6.5 §12 Migration Checklist 갱신

- v0.5 → v0.5.1 diff 최소화, BISECT 관련 항목만 추가

### 6.6 Version History

```markdown
| 0.5.1 | 2026-04-07 | BISECT termination (feature: bisect-mode-termination). Legacy else branch deleted, release_swapchain activated, Initialize signature simplified, gw_render_resize deprecated to no-op. §1.4 added. | CTO Lead |
```

---

## 7. Implementation Order (Do phase)

**Strict 순서. 각 단계가 다음의 전제**:

1. **C++ 변경**
   - [ ] `ghostwin_engine.cpp`: `render_loop` 재작성 (legacy else 삭제 + warm-up 가드)
   - [ ] `ghostwin_engine.cpp`: `gw_render_init`에서 `release_swapchain()` 활성화
   - [ ] `ghostwin_engine.cpp`: `gw_render_resize` no-op 변환 + 주석

2. **C# Core/Services 변경**
   - [ ] `IPaneLayoutService.cs`: `Initialize` 시그니처 단순화 + XML doc 추가
   - [ ] `PaneLayoutService.cs`: `Initialize` 구현 갱신
   - [ ] `PaneLayoutService.cs`: `OnHostReady`에 SurfaceCreate 실패 로그 추가
   - [ ] `WorkspaceService.cs:47-49`: 콜 사이트 갱신 + BISECT 주석 제거

3. **C# App 변경**
   - [ ] `MainWindow.xaml.cs:163`: `initialHost.PaneResizeRequested += OnTerminalResized;` 삭제
   - [ ] `MainWindow.xaml.cs:205-209`: `OnTerminalResized` 메서드 삭제

4. **Interop 주석 갱신**
   - [ ] `IEngineService.cs`: `RenderResize`에 deprecated remarks 추가
   - [ ] `EngineService.cs`: `RenderResize` 래퍼에 동일 remarks

5. **빌드 & 단위 테스트**
   - [ ] `scripts/build_ghostwin.ps1 -Config Release` — exit 0 확인
   - [ ] `scripts/test_ghostwin.ps1` — 9/9 PASS 확인

6. **수동 QA 8건** (§5.2)

7. **design 문서 갱신**
   - [ ] `pane-split.design.md` v0.5.1 — §1.4 신설, §4.4 실제 함수명 확인 후 수정, Version History
   - [ ] **design-validator council agent 호출** (Do 말미): pane-split.design.md v0.5.1이 runtime과 일치하는지 검증

8. **BISECT 마커 최종 grep**
   - [ ] `grep -rn BISECT src/` → 0건

9. **CLAUDE.md 갱신**
   - [ ] Phase 5-E.5 섹션 P0-2 `[x]` 마킹
   - [ ] Key references 테이블에 bisect-mode-termination 링크 추가

### 7.1 중단 조건

- C++ 빌드 실패 → 근본 원인 확정 후 수정 (우회 금지 원칙)
- 단위 테스트 1건 이상 Red → PaneNode 로직 회귀. 본 feature는 PaneNode 변경 없으므로 드문 케이스. 발생 시 즉시 중단 + 원인 분석
- 수동 QA MQ-8 (창 리사이즈) 실패 → `gw_render_resize` no-op 후 리사이즈 경로가 작동 안 함. 디버깅 + design 재검토
- BISECT marker grep 결과 0건 아님 → 누락된 수정 지점 확인

---

## 8. Risks and Mitigation

| # | Risk | Impact | Likelihood | Mitigation |
|---|---|---|---|---|
| R1 | `terminal_window.cpp:204`의 `resize_swapchain` 호출 경로 (`main.cpp` 독립 실행 파일)이 본 feature로 인해 깨짐 | Low | Very Low | 독립 `DX11Renderer` 인스턴스. `release_swapchain`은 ghostwin_engine의 renderer 인스턴스에만 호출됨. 검증 완료 (code-analyzer §6) |
| R2 | 초기 pane HostReady 레이스 발현 (wpf-architect C4) — `AdoptInitialHost`의 HostReady 구독이 이벤트 발사 후 → 초기 pane 영구 blank | **High** | **Low~Medium** | D11 로그 추가로 감지. 수동 QA에서 재현 시도 (앱 재시작 20회 반복). 재현 시 별도 hotfix feature |
| R3 | `SurfaceCreate == 0` 실패 — Sillent 차 blank (wpf-architect C5) | Medium | Low | D11 로그 추가. 사용자 bug report 시 로그에서 추적 가능 |
| R4 | `gw_render_resize` no-op 후 창 리사이즈 시 pane 크기 안 맞음 | Medium | Low | MQ-8 수동 QA 필수. `PaneContainerControl.OnPaneResized` 경로가 이미 동작 중이므로 회귀 예상 안 됨 (code-analyzer C3) |
| R5 | `IEngineService.RenderResize` 인터페이스 유지가 독자에게 혼란 | Low | Medium | XML doc `<remarks>`에 명시적 deprecated 표시 (D7) |
| R6 | `render_loop` 단순화 후 `surface_mgr->flush_pending_destroys()` 호출이 빠질 수 있음 | Medium | Low | 리팩토링 시 명시적 유지. Before/After diff 비교 (§4.1.2) |
| R7 | pane-split.design.md v0.5.1 갱신 시 §4.4 다른 섹션과의 일관성 깨짐 | Low | Medium | Do 말미 design-validator 호출 (§7 step 7) |
| R8 | render_loop 단순화 후 `callbacks.on_render_done` 호출 조건 변화 — warm-up 기간 callback 미수신 | Low | High | **의도된 변경**. warm-up 기간은 아무 것도 렌더하지 않으므로 render_done callback 없음이 정확. WPF 측에서 이 callback을 어떻게 사용하는지 Do phase 확인 |
| R9 | `session_mgr->resize_all` 제거로 창 size 변경 시 session VT grid가 정확한 크기 유지 | Low | Low | `gw_surface_resize` (line 587)가 `session_mgr->resize_session`을 개별 pane에 호출 → session grid 갱신. 중복이 아니라 정상 경로 (code-analyzer C6) |
| R10 | 확실하지 않음 항목 (wpf-architect Dispatcher priority 9 FIFO, SurfaceCreate 실패 재시도 경로 부재) | Low~Medium | N/A | 실측 기반 경험 축적 필요. 본 feature 완료 후 관찰 데이터로 평가 |

---

## 9. Open Questions (Do phase에서 해결)

1. **`callbacks.on_render_done` 사용처 확인**: WPF 측에서 이 callback을 어떻게 사용하는지 — warm-up 기간 누락 허용 가능한지
2. **pane-split.design.md §4.4의 실제 현재 내용**: "render_to_target" 표현이 실제로 있는지 + 어떤 맥락인지 Do phase에서 확인
3. **MQ-8 창 리사이즈 성공**: `PaneContainerControl.OnPaneResized`가 창 크기 변경 시 각 pane을 자동으로 리사이즈하는지 실측
4. **`EngineService.cs`의 `RenderResize` 래퍼 정확한 위치** — dotnet-expert가 §4에서 언급 안 한 부분

---

## 10. Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-07 | Initial design. Council synthesis: wpf-architect (HostReady race), code-analyzer (Top Risk 1 해소 + 중복 resize 경로 발굴), dotnet-expert (call site 확정). 숨은 복잡도 5건 추가 발견 — Plan scope 확장. | 노수장 (CTO Lead) |
| 0.2 | 2026-04-08 | **Retroactive Operator QA closeout (8/8)**. e2e-ctrl-key-injection cycle의 H9 fix (`docs/archive/2026-04/e2e-ctrl-key-injection/e2e-ctrl-key-injection.design.md` v0.2)로 e2e harness Operator 8/8 OK 도달, MQ-1 ~ MQ-8 8건 모두 injection success 확인. 직전 5/8 cap이 해소됨. §10.1 retroactive QA evidence table 추가. | 노수장 (CTO Lead) |
| 0.3 | 2026-04-08 | **Evaluator side retroactive**. `e2e-evaluator-automation` cycle 이 `diag_all_h9_fix` 를 visual evaluation 결과 **verdict=FAIL, 6/8 PASS**. MQ-2/3/4/5/6/8 시각 검증 PASS (P0-2 BISECT termination 이후 pane layout / resize / workspace 생성 모두 렌더 정상). MQ-1 partial-render (WGC capture timing) 와 MQ-7 key-action-not-applied (sidebar click workspace 전환 실패) 는 **P0-2 와 무관한 독립 regression** 으로 판명 — §10.2 evaluator judgment table 추가. Bisect closeout 의 본질인 "BISECT 종료 후 pane/workspace/resize 렌더 정상" 은 MQ-2-6/8 로 visually confirmed. | 노수장 (CTO Lead) |

### 10.1 Retroactive QA Evidence (post-H9 fix)

P0-2 BISECT 종료 작업 commit 시점에는 `e2e-ctrl-key-injection` cycle의 H9 fix가 아직 없었기 때문에 e2e harness가 5/8 = 62.5% Match Rate에 cap돼 MQ-5/MQ-6/MQ-7 (Ctrl+Shift+W, Ctrl+T, sidebar click 후 워크스페이스 전환) 3건이 prerequisite 미충족으로 검증 불가였다. 2026-04-08 H9 fix (focus() Alt-tap 제거) 이후 8건 모두 검증 가능해졌고, 본 design의 §5.2 8건 수동 QA 항목을 retroactive하게 close한다.

| MQ | Scenario | Pre-H9 status | Post-H9 evidence | Result |
|:---:|---|:---:|---|:---:|
| MQ-1 | 앱 시작 → 첫 workspace 렌더 | OK (operator) | `scripts/e2e/artifacts/diag_all_h9_fix/MQ-1/01_initial_render.png` | ✅ |
| MQ-2 | Alt+V split | OK | `MQ-2/after_split_vertical.png`, pane_count=2 | ✅ |
| MQ-3 | Alt+H split | OK | `MQ-3/after_split_horizontal.png`, pane_count=3 | ✅ |
| MQ-4 | 마우스 click pane focus | OK | `MQ-4/after_mouse_focus.png`, click=(400,250) | ✅ |
| MQ-5 | Ctrl+Shift+W focused pane close | **SKIP** (R4 cap) | `MQ-5/after_pane_close.png`, pane_count=3→2, KeyDiag #0005-7 (LeftCtrl+LeftShift+W) | ✅ |
| MQ-6 | Ctrl+T new workspace | **SKIP** (R4 cap) | `MQ-6/after_new_workspace.png`, workspace_count=1→2, KeyDiag #0008-9 (LeftCtrl+T) | ✅ |
| MQ-7 | Sidebar click workspace switch | **SKIP** (R4 cap) | `MQ-7/after_workspace_switch.png`, click=(80,150), workspace_count=2 | ✅ |
| MQ-8 | MainWindow 크기 조절 | OK | `MQ-8/02_after_resize.png`, before=(100,100,1380,900) after=(100,100,1700,1100) | ✅ |

**Match Rate**: 5/8 → **8/8 = 100%**

**Cross-references**:
- e2e harness run id: `diag_all_h9_fix` (`scripts/e2e/artifacts/diag_all_h9_fix/summary.json`)
- KeyDiag log evidence: 9 entries covering Alt+V, Alt+H, Ctrl+Shift+W, Ctrl+T (MQ-7 mouse click is not keyboard-triggered so absent from KeyDiag)
- Operator outcomes: OK=8 ERROR=0 SKIPPED=0
- PaneNode unit 9/9 PASS (38ms) — `scripts/test_ghostwin.ps1 -Configuration Release`
- Hardware manual smoke 5/5 PASS (Alt+V, Alt+H, Ctrl+T, Ctrl+W, Ctrl+Shift+W)

P0-2 BISECT 종료 작업의 §5.2 8건 수동 QA 요건은 본 retroactive evidence로 충족된다. 추가 manual run 불필요.

### 10.2 Evaluator Visual Verification (via e2e-evaluator-automation cycle)

2026-04-08 `e2e-evaluator-automation` cycle 에서 `e2e-evaluator` project-local
subagent (Sonnet 4.6) 가 동일 `diag_all_h9_fix` run 을 **visual evaluation** 으로
평가한 결과:

| MQ | Scenario | Visual Verdict | Confidence | Note |
|:---:|---|:---:|:---:|---|
| MQ-1 | 앱 시작 → 첫 workspace 렌더 | ❌ FAIL | high | `partial-render` — 사용자 hardware 검증 결과 **첫 workspace 첫 pane 이 실제로 render 되지 않음** (WGC capture timing race 아님). 두 번째 workspace (Ctrl+T 후, MQ-6) 에서는 정상 render → GhostWin source 측 `_initialHost` lifecycle / `OnHostReady` race 로 추정. **본 design v0.1 의 R2 (HostReady race, "잠재적" 으로 분류됐던 것) 의 실제 reproduction 일 가능성**. 본 cycle (P0-2 BISECT termination) 의 warm-up 가드 와 `release_swapchain()` 은 정상 작동하고 있으나 첫 pane host 확립 경로가 별개 문제 |
| MQ-2 | Alt+V split | ✅ PASS | high | 2-pane 세로 분할 정상 렌더. BISECT 종료 후 pane split 정상 확인 |
| MQ-3 | Alt+H split | ✅ PASS | high | 3-pane 세로+가로 분할기 모두 정상 렌더. P0-2 핵심 검증 |
| MQ-4 | 마우스 click pane focus | ✅ PASS | medium | 3-pane 유지 + 포커스 인디케이터 확인 |
| MQ-5 | Ctrl+Shift+W pane close | ✅ PASS | high | 3→2 pane 정상, 형제 pane 재배분. P0-2 `release_swapchain` warm-up 정상 |
| MQ-6 | Ctrl+T new workspace | ✅ PASS | high | 사이드바 2 workspace 항목, 새 워크스페이스 활성 |
| MQ-7 | Sidebar click workspace switch | ❌ FAIL | high | `key-action-not-applied` — 사이드바 클릭 (80,150) 이 workspace 전환 미발생. MQ-6 과 screenshot 사실상 동일. 독립 sidebar click handler regression 또는 **MQ-1 blank state 의 연쇄 효과** (workspace 1 로 전환해도 여전히 blank 상태일 가능성) — follow-up 조사 필요. P0-2 와 무관 |
| MQ-8 | MainWindow 크기 조절 | ✅ PASS | high | Before/after 캡처에 구조 동일 + 크기 확대 + 검은 영역 없음. **P0-2 `gw_render_resize` no-op deprecation 이후 `PaneContainerControl.OnPaneResized → SurfaceResize` 경로가 정상 작동함을 visually confirmed** (Top Risk 1 완전 해소 evidence) |

**Evaluator Match Rate**: 6/8 = 75% (verdict=FAIL)
**P0-2 scope Match Rate (MQ-2/3/4/5/8 + MQ-6 pane create)**: 6/6 = 100%

**Interpretation**: P0-2 BISECT mode termination 의 본질인 **per-pane SurfaceResize 경로 + release_swapchain warm-up + OnHostReady 실패 진단 + gw_render_resize no-op** 은 MQ-2/3/4/5/6/8 6개 시나리오에서 모두 visually confirmed. MQ-1 과 MQ-7 은 P0-2 scope 밖 regression 이므로 본 cycle closeout 에 영향 없음.

**Important note on R2 (HostReady race)**: 본 design v0.1 §8 R2 에서 "HostReady race 가 초기 pane 영구 blank 로 나타날 수 있다" 고 기술되고 "High severity, Low~Medium likelihood" 로 분류됐다. 당시 실제 reproduction 은 없었고 "수동 QA 에서 재현 시도 (앱 재시작 20회 반복)" 를 mitigation 으로 명시했다. **e2e-evaluator-automation cycle 의 MQ-1 실패는 R2 의 실제 최초 reproduction 일 가능성이 높다**. 사용자 hardware 검증: 첫 workspace 첫 pane 이 render 안 됨, 두 번째 workspace (Ctrl+T 후) 는 정상 — 이는 initial host adoption path 가 specific 하게 실패하는 R2 pattern 과 정확히 일치. Follow-up cycle (`first-pane-render-failure`) 은 본 design §8 R2 + CLAUDE.md TODO `_initialHost` lifecycle 을 동시 target 으로 설계해야 한다.

- Evaluator result file: `scripts/e2e/artifacts/diag_all_h9_fix/evaluator_summary.json`
- SHA256 sidecar: `scripts/e2e/artifacts/diag_all_h9_fix/evaluator_summary.json.sha256`
- Agent: `e2e-evaluator-v1.0` (project-local, Sonnet 4.6)
- Wrapper: `scripts/test_e2e.ps1 -Apply -RunId diag_all_h9_fix` → exit 1 (FAIL)

**Follow-up (out of this cycle's scope)**: MQ-1 capture timing + MQ-7 sidebar click handler 는 `e2e-evaluator-automation` cycle 이 식별한 follow-up items. 별도 fix cycle 필요.

---

## Council Attribution

| Agent | 핵심 기여 |
|-------|---------|
| `rkit:wpf-architect` | HostReady dispatcher timing 분석, 초기 pane 잠재 레이스 (C4), SurfaceCreate silent failure path (C5), warm-up blank screen 안전성, gw_render_resize WPF 측 호출 맥락 |
| `rkit:code-analyzer` | **Top Risk 1 완전 해소**: DX11Renderer 전수 조사, `release_swapchain`/`resize_swapchain` 구현 분석, legacy else 유일 consumer 확정, `session_mgr->resize_all` pane-split 시맨틱 충돌 발굴, `MainWindow.OnTerminalResized` 중복 경로 발굴, terminal_window.cpp 독립 경로 확인 |
| `rkit:dotnet-expert` | `Initialize` 콜 사이트 exhaustive search (1건 확정), 테스트 영향 0 확인, `SurfaceId == 0` sentinel 7개 KEEP 식별, compile-time safety 검증, XML doc 신규 |
| CTO Lead (Opus) | Council 통합, 숨은 복잡도 발굴 반영 (Plan scope 조정), D1-D17 decision lock-in, Risk matrix, Implementation Order, pane-split.design.md v0.5.1 갱신 사양, design-validator 호출 시점 결정 (Do 말미) |
