# Pane-Split Concurrency Analysis Report

> **Project**: GhostWin Terminal
> **Feature**: pane-split (Phase 5-E)
> **Date**: 2026-04-06
> **Analyzer**: MCU Critical Analyzer (ConSynergy 4-Stage)
> **Scope**: `src/engine-api/ghostwin_engine.cpp` + design `docs/02-design/features/pane-split.design.md`

---

## Executive Summary

| Metric | Count |
|--------|:-----:|
| **Critical** | 3 issues (3 high confidence) |
| **Warning** | 3 issues |
| **Info** | 2 issues |

현재 `ghostwin_engine.cpp`의 surfaces 관련 코드는 **스레드 보호가 전혀 없는 상태**이다.
Design v0.3은 SurfaceManager + mutex 분리를 명시하고 있으나, 현재 구현은 v0.1 수준이므로
Design의 의도가 반영되기 전까지 아래 이슈가 실제 런타임에서 발생한다.

---

## Stage 1: Shared Resource Identification

| Resource | Type | Writers | Readers | Protection |
|----------|------|---------|---------|------------|
| `eng->surfaces` (vector) | `std::vector<unique_ptr<RenderSurface>>` | UI thread (create/destroy) | Render thread (iteration) | **NONE** |
| `RenderSurface::rtv` | ComPtr | UI thread (resize) | Render thread (draw) | **NONE** |
| `RenderSurface::swapchain` | ComPtr | UI thread (resize) | Render thread (Present) | **NONE** |
| `RenderSurface::width_px/height_px` | uint32_t | UI thread (resize) | Render thread (viewport) | **NONE** |
| `RenderSurface::needs_rtv_rebuild` | bool | UI thread (set) | Render thread (read+reset) | **NONE** |
| `eng->staging` | `vector<QuadInstance>` | Render thread (multi-surface) | Render thread only | single-writer (OK) |
| `session->vt_mutex` | std::mutex | I/O thread, render thread | render thread | mutex (OK per-session) |
| `ID3D11DeviceContext` | D3D11 ctx | Render thread (multi-surface) | Render thread only | single-thread (OK*) |

---

## Stage 2: Concurrency-Aware Slicing

### Slice 2.1: surfaces vector (render_loop vs gw_surface_create/destroy)

**Render thread** (`render_loop`, line 244-247):
```cpp
if (!surfaces.empty()) {                      // <-- read surfaces.size()
    for (auto& surf : surfaces) {             // <-- iterate surfaces
        render_surface(surf.get(), builder);  // <-- dereference unique_ptr
    }
}
```

**UI thread** (`gw_surface_create`, line 596):
```cpp
eng->surfaces.push_back(std::move(surf));     // <-- modify vector (may reallocate)
```

**UI thread** (`gw_surface_destroy`, line 610-615):
```cpp
auto it = std::find_if(eng->surfaces.begin(), eng->surfaces.end(), ...);
eng->surfaces.erase(it);                      // <-- invalidate iterators + shift elements
```

**Protection**: NONE. No mutex, no atomic, no snapshot.

### Slice 2.2: RenderSurface members during resize

**UI thread** (`gw_surface_resize` -> `resize_surface_swapchain`, line 139-147):
```cpp
void resize_surface_swapchain(RenderSurface* surf, uint32_t w, uint32_t h) {
    surf->rtv.Reset();                          // <-- rtv = nullptr
    surf->width_px = w > 0 ? w : 1;            // <-- width changes
    surf->height_px = h > 0 ? h : 1;           // <-- height changes
    surf->swapchain->ResizeBuffers(...);        // <-- swapchain internal state changes
    create_surface_rtv(surf);                   // <-- rtv = new RTV
}
```

**Render thread** (`render_surface`, line 188-228):
```cpp
if (surf->needs_rtv_rebuild) {
    create_surface_rtv(surf);                   // <-- reads/writes rtv
}
if (!surf->rtv) return;                         // <-- reads rtv
// ...
ctx->ClearRenderTargetView(surf->rtv.Get(), ...); // <-- uses rtv
// ...
vp.Width = (float)surf->width_px;               // <-- reads width
vp.Height = (float)surf->height_px;             // <-- reads height
// ...
surf->swapchain->Present(1, 0);                 // <-- uses swapchain
```

**Protection**: NONE.

### Slice 2.3: vt_mutex lock ordering

**Render thread** (`render_surface`, line 200-201):
```cpp
// For surface A (session 1):
bool dirty = state.start_paint(session->vt_mutex, vt);  // locks session1->vt_mutex
// ... render ...
// For surface B (session 2):
bool dirty = state.start_paint(session->vt_mutex, vt);  // locks session2->vt_mutex
```

**I/O thread** (per-session, `conpty_session.cpp` line 192):
```cpp
std::lock_guard lock(impl->vt_mutex);  // locks ConPtySession internal vt_mutex
```

**UI thread** (`session_manager.cpp:resize_session`, line 373):
```cpp
std::lock_guard lock(sess->vt_mutex);  // locks Session::vt_mutex
```

**Critical observation**: ConPtySession has its **own internal** `impl_->vt_mutex` (line 143 of conpty_session.cpp) that is **distinct** from `Session::vt_mutex` (session.h line 103). This is **two separate mutexes** for the same VtCore object.

### Slice 2.4: staging buffer reuse across surfaces

**Render thread** (`render_surface`, line 206-207):
```cpp
uint32_t count = builder.build(frame, *atlas, renderer->context(),
    std::span<QuadInstance>(staging), &bg_count);
```

Same `staging` buffer is used for each surface sequentially in the for-loop. Single-threaded, sequential reuse -- no concurrent issue here.

### Slice 2.5: D3D11 DeviceContext sharing

**Render thread** uses `renderer->context()` across multiple surfaces in the same render loop iteration. D3D11 Immediate Context is **not thread-safe** (Microsoft docs), but since only the render thread uses it, this is safe as-is. However, `gw_render_resize` (UI thread, line 372) calls `renderer->resize_swapchain()` which likely also uses the DeviceContext.

---

## Stage 3: Semantic Reasoning

### C-001: surfaces vector concurrent modification (Iterator Invalidation + UAF)

**Pattern**: CP-01 (shared resource without critical section) + CP-06 (non-atomic container modification)

**Thread interleaving scenario**:
1. Render thread enters `for (auto& surf : surfaces)`, holds iterator at index 1
2. UI thread calls `gw_surface_destroy()`, erases element at index 0
3. `std::vector::erase()` shifts all elements left, invalidates all iterators
4. Render thread dereferences invalidated iterator -> **undefined behavior**

Alternative scenario (push_back reallocation):
1. Render thread reads `surfaces[0]` pointer
2. UI thread calls `gw_surface_create()`, `push_back` triggers reallocation
3. Old memory is freed, render thread dereferences freed pointer -> **use-after-free**

**Impact**: Crash, heap corruption, or silent data corruption.

### C-002: RenderSurface member torn read during resize

**Pattern**: CP-01 + CP-02 (no synchronization on multi-field state)

**Thread interleaving scenario**:
1. UI thread in `resize_surface_swapchain()`: calls `surf->rtv.Reset()` (rtv = nullptr)
2. Render thread in `render_surface()`: passes `!surf->rtv` check (was valid a moment ago)
3. UI thread: `ResizeBuffers()` starts (swapchain internal state is inconsistent)
4. Render thread: `ctx->ClearRenderTargetView(surf->rtv.Get(), ...)` -- rtv was Reset, now nullptr
5. **D3D11 device removed** or **null pointer dereference**

Alternative: render thread reads old `width_px`/`height_px` while UI thread writes new values.
On x64, `uint32_t` reads are atomic, but the *combination* of width + height + rtv + swapchain
is not transactionally consistent.

**Impact**: D3D11 device lost, crash, or rendering artifacts.

### C-003: Dual vt_mutex deadlock risk

**Pattern**: CP-07 (lock ordering violation potential)

**Two separate mutexes protect the same VtCore**:
- `Session::vt_mutex` (session.h:103) -- used by render thread via `start_paint()` and by UI thread via `resize_session()`
- `ConPtySession::Impl::vt_mutex` (conpty_session.cpp:143) -- used by I/O thread for `write()` and by ConPtySession::resize() internally

**Current code flow for `resize_session()`** (session_manager.cpp:370-376):
```cpp
std::lock_guard lock(sess->vt_mutex);     // locks Session::vt_mutex
sess->conpty->resize(cols, rows);          // internally locks ConPtySession::Impl::vt_mutex
sess->state->resize(cols, rows);
```

**I/O thread flow** (conpty_session.cpp:192-200):
```cpp
std::lock_guard lock(impl->vt_mutex);     // locks ConPtySession::Impl::vt_mutex
impl->vt_core->write(...);                 // Session::vt_mutex is NOT held
```

**Render thread flow** (ghostwin_engine.cpp:200-201):
```cpp
state.start_paint(session->vt_mutex, vt);  // locks Session::vt_mutex only
```

**Analysis**: The lock ordering is:
- UI thread: `Session::vt_mutex` -> `ConPtySession::Impl::vt_mutex` (nested)
- I/O thread: `ConPtySession::Impl::vt_mutex` only
- Render thread: `Session::vt_mutex` only

Since I/O and render threads never hold both mutexes simultaneously, **classic ABBA deadlock cannot occur** with the current code. However, this dual-mutex design is fragile and error-prone. Any future code that changes the lock order could introduce deadlock.

### W-001: needs_rtv_rebuild flag without memory barrier

**Pattern**: CP-12 (shared flag without volatile AND without sync)

`needs_rtv_rebuild` is a plain `bool` written by UI thread (implicitly, in resize path) and read by render thread (line 192). On x86/x64, this is practically safe due to TSO memory model, but:
- No `std::atomic<bool>` or memory fence
- Compiler could optimize away the read (e.g., hoist out of loop)
- Violates C++ memory model (data race = UB per standard)

### W-002: D3D11 DeviceContext used from multiple threads

**Pattern**: CP-01 (shared resource without sync)

`renderer->context()` returns a raw `ID3D11DeviceContext*` (Immediate Context). Per Microsoft documentation, the Immediate Context is **not thread-safe**. Current code:
- Render thread: `render_surface()` uses ctx for ClearRTV, RSSetViewports, OMSetRenderTargets, upload_and_draw
- UI thread: `gw_render_resize()` calls `renderer->resize_swapchain()` which uses ctx
- UI thread: `create_surface_swapchain()` and `create_surface_rtv()` use `renderer->device()` (Device is thread-safe, but RTV creation may use ctx internally)

**Impact**: D3D11 runtime error or device removal if concurrent ctx calls overlap.

### W-003: staging buffer insufficient for multi-pane

**Pattern**: Logical sizing issue

`eng->staging` is allocated in `gw_render_init()` (line 361) based on the **initial single window size**:
```cpp
eng->staging.resize(cols * rows_count * constants::kInstanceMultiplier + 16);
```

When multiple panes exist, the largest pane's cell count may exceed this allocation. `QuadBuilder::build()` checks `max_instances` (line 50) and stops at the limit, but this silently truncates rendering.

**Impact**: Partial rendering in large panes after split.

### I-001: Single staging buffer limits parallelism

The `staging` vector is reused sequentially for each surface. This prevents future parallelization of the render loop. Design v0.3 addresses this by moving to per-surface rendering via `DX11Renderer::render_to_surface()`, which should also address staging ownership.

### I-002: SwapChain::Present(1, 0) blocks render thread

`Present(1, 0)` with VSync enabled blocks until the next vertical blank (~16ms at 60Hz). With N panes, the render loop could take up to N*16ms if each Present blocks sequentially. Design v0.3 mentions dirty-flag optimization to skip unchanged panes, and `frame_waitable` (DXGI waitable swapchain) exists in EngineImpl but is unused for surfaces.

---

## Stage 4: Verdict (Risk Classification)

### Critical Issues

| ID | Title | Location | Confidence | Pattern |
|----|-------|----------|:----------:|---------|
| **C-001** | surfaces vector concurrent modification -- iterator invalidation + UAF | ghostwin_engine.cpp:244-247 (read), :596 (push_back), :615 (erase) | **High** | CP-01, CP-06 |
| **C-002** | RenderSurface member torn read during resize | ghostwin_engine.cpp:139-147 (write), :188-228 (read) | **High** | CP-01, CP-02 |
| **C-003** | Dual vt_mutex on same VtCore -- fragile lock ordering | session.h:103, conpty_session.cpp:143, session_manager.cpp:373 | **High** | CP-07 (latent) |

### Warnings

| ID | Title | Location | Confidence | Pattern |
|----|-------|----------|:----------:|---------|
| **W-001** | needs_rtv_rebuild plain bool data race | ghostwin_engine.cpp:41, :192-194 | Medium | CP-12 |
| **W-002** | D3D11 Immediate Context cross-thread usage | ghostwin_engine.cpp:206,218-223 vs gw_render_resize:372 | Medium | CP-01 |
| **W-003** | staging buffer not resized for multi-pane | ghostwin_engine.cpp:361 | Medium | Logical |

### Info

| ID | Title | Location | Confidence | Pattern |
|----|-------|----------|:----------:|---------|
| **I-001** | Single staging buffer prevents render parallelism | ghostwin_engine.cpp:72, :206 | Low | Architecture |
| **I-002** | VSync Present blocking scales linearly with pane count | ghostwin_engine.cpp:228 | Low | Performance |

---

## Detailed Issue Reports

### C-001: surfaces vector concurrent modification

**Severity**: Critical
**Confidence**: High
**Affected code**:

Render thread (reader):
```cpp
// ghostwin_engine.cpp:244-247
if (!surfaces.empty()) {
    for (auto& surf : surfaces) {          // range-based for = iterator pair
        render_surface(surf.get(), builder);
    }
}
```

UI thread (writer):
```cpp
// ghostwin_engine.cpp:596
eng->surfaces.push_back(std::move(surf));  // may trigger reallocation

// ghostwin_engine.cpp:615
eng->surfaces.erase(it);                   // shifts elements, invalidates iterators
```

**Fix recommendation** (matches Design v0.3 SurfaceManager pattern):

```cpp
// Option A: Snapshot pattern (Design v0.3, Section 4.2)
// In SurfaceManager::active_surfaces():
std::vector<RenderSurface*> SurfaceManager::active_surfaces() {
    std::lock_guard lock(mutex_);
    std::vector<RenderSurface*> snapshot;
    snapshot.reserve(surfaces_.size());
    for (auto& s : surfaces_)
        snapshot.push_back(s.get());
    return snapshot;
}

// In render_loop:
auto active = surface_mgr->active_surfaces();  // short lock
for (auto* surf : active) {
    render_surface(surf, builder);              // no lock held
}

// In create/destroy:
{
    std::lock_guard lock(mutex_);
    surfaces_.push_back(std::move(surf));       // or erase
}
```

**Reference**: Design v0.3 Section 4.2 (SurfaceManager), ADR-006 (vt_mutex thread safety)

---

### C-002: RenderSurface member torn read during resize

**Severity**: Critical
**Confidence**: High
**Affected code**:

```cpp
// ghostwin_engine.cpp:139-147 (UI thread)
void resize_surface_swapchain(RenderSurface* surf, uint32_t w, uint32_t h) {
    surf->rtv.Reset();                    // rtv becomes nullptr HERE
    // ... any render thread read of rtv between here and create_surface_rtv is UB ...
    surf->swapchain->ResizeBuffers(...);  // swapchain internal state mutated
    create_surface_rtv(surf);             // rtv restored
}
```

```cpp
// ghostwin_engine.cpp:197,217 (render thread)
if (!surf->rtv) return;
// ...
ctx->ClearRenderTargetView(surf->rtv.Get(), clear_color);  // may read stale/null rtv
```

**Fix recommendation**:

```cpp
// Option A: Flag-based deferred resize (minimize lock time)
// UI thread sets resize request:
void gw_surface_resize(...) {
    std::lock_guard lock(surface_mutex_);
    surf->pending_resize = {w, h};
}

// Render thread applies resize at safe point:
void render_surface(RenderSurface* surf, ...) {
    if (auto resize = surf->consume_pending_resize()) {
        resize_surface_swapchain(surf, resize->w, resize->h);
    }
    // ... normal render ...
}

// Option B: Shared mutex (reader-writer lock)
// render_surface takes shared_lock, resize takes unique_lock.
```

**Reference**: Windows Terminal uses a similar "pending resize" pattern in its renderer. DXGI documentation: ResizeBuffers must not be called while any RTV is bound.

---

### C-003: Dual vt_mutex on same VtCore

**Severity**: Critical (latent -- currently safe by accident, fragile)
**Confidence**: High
**Affected code**:

```cpp
// session.h:103 -- Session-level mutex
std::mutex vt_mutex;  // used by render thread (start_paint) and UI thread (resize_session)

// conpty_session.cpp:143 -- ConPtySession internal mutex
std::mutex vt_mutex;  // used by I/O thread (write) and ConPtySession::resize
```

**Problem**: Two independent mutexes protect the **same** VtCore instance. This means:
1. I/O thread can call `vt_core->write()` while render thread calls `vt_core->for_each_row()` -- they hold **different** mutexes and do NOT exclude each other.
2. `resize_session()` holds Session::vt_mutex, then calls `conpty->resize()` which holds ConPtySession::Impl::vt_mutex. The I/O thread holds only ConPtySession::Impl::vt_mutex.

**Specifically**: `start_paint()` locks `Session::vt_mutex` and reads VtCore state via `vt.for_each_row()`. But the I/O thread locks `ConPtySession::Impl::vt_mutex` and calls `vt_core->write()`. These are **different mutexes** -- so the render thread and I/O thread can simultaneously access VtCore, which is **not thread-safe**.

**Fix recommendation**: Unify to a single mutex.

```cpp
// Option A: Remove ConPtySession::Impl::vt_mutex, expose Session::vt_mutex to I/O thread
// ConPtySession takes a reference to the external mutex at construction.

// Option B: Remove Session::vt_mutex, expose ConPtySession's mutex
// render_surface calls: state.start_paint(session->conpty->vt_mutex(), vt);

// Option C (Design v0.3 implicit): SurfaceManager holds per-surface lock
// that covers both I/O write and render read.
```

**Reference**: ADR-006 (vt_mutex thread safety), Alacritty uses a single `Arc<FairMutex<Term>>` shared between PTY reader and renderer.

---

### W-001: needs_rtv_rebuild plain bool data race

**Severity**: Warning
**Confidence**: Medium (practically safe on x86/x64 TSO, but UB per C++ standard)

```cpp
// ghostwin_engine.cpp:41
bool needs_rtv_rebuild = false;  // plain bool

// UI thread (implicit): could set this flag
// Render thread (line 192-194):
if (surf->needs_rtv_rebuild) {
    create_surface_rtv(surf);
    surf->needs_rtv_rebuild = false;
}
```

**Fix**: `std::atomic<bool> needs_rtv_rebuild{false};`

---

### W-002: D3D11 Immediate Context cross-thread usage

**Severity**: Warning
**Confidence**: Medium

The D3D11 Immediate Context (`renderer->context()`) is used by the render thread for all draw operations. However, `gw_render_resize()` (called from UI thread, line 372) calls `renderer->resize_swapchain()` which internally uses the same context.

**Fix**: Either:
1. Defer all context operations to the render thread (post messages/flags)
2. Use `ID3D11Multithread` to enable D3D11 runtime thread safety (performance cost)

**Reference**: [Microsoft D3D11 Threading](https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-render-multi-thread-intro)

---

### W-003: staging buffer not resized for multi-pane

**Severity**: Warning
**Confidence**: Medium

```cpp
// ghostwin_engine.cpp:361
eng->staging.resize(cols * rows_count * kInstanceMultiplier + 16);
```

This is sized for the initial window. After pane split, individual panes will be smaller, so this is likely sufficient. However, if a single pane is later resized to be **larger** than the initial window (e.g., closing other panes), truncation could occur.

**Fix**: Resize staging in `gw_surface_create()` or `gw_surface_resize()` if the new surface's cell count exceeds current staging capacity.

---

## Design v0.3 Gap Analysis

Design v0.3 explicitly addresses C-001 and partially addresses C-002:

| Issue | Design v0.3 Solution | Current Implementation |
|-------|---------------------|----------------------|
| C-001 | SurfaceManager with `std::mutex` + `active_surfaces()` snapshot | No SurfaceManager, no mutex, inline vector access |
| C-002 | `DX11Renderer::render_to_surface()` encapsulation | render_surface() directly manipulates ctx/rtv |
| C-003 | Not explicitly addressed | Dual vt_mutex remains in design |
| W-002 | Implied by renderer encapsulation | Not addressed |

**Recommendation**: Design v0.3 is sound for C-001. For C-003, the design should explicitly specify mutex unification strategy before implementation begins.

---

## Summary

```
Pane-Split Concurrency Analysis Complete
-----------------------------------------
Critical: 3 issues (3 high confidence)
Warning:  3 issues
Info:     2 issues
-----------------------------------------
Report: docs/03-analysis/concurrency/pane-split-concurrency-20260406.md
```

### Implementation Priority

1. **C-001 (surfaces mutex)** -- Must fix before ANY multi-pane testing. Single split will crash.
2. **C-003 (dual vt_mutex)** -- Must unify before multi-pane. Existing single-pane code already has this bug (I/O thread vs render thread access VtCore through different mutexes).
3. **C-002 (resize torn read)** -- Must fix before pane resize. Deferred resize pattern recommended.
4. **W-002 (D3D11 ctx)** -- Fix alongside C-002 by deferring all ctx operations to render thread.
5. **W-001, W-003** -- Fix during SurfaceManager implementation.
