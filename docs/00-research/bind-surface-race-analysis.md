# bind_surface Data Race — Analysis (Tech Debt #16)

> **Status (2026-04-15)**: Current code path has no observable race. Deferred-destroy + mutex already serialize all access. Converting `SurfaceManager` to `shared_ptr` ownership to match `SessionManager` for defense-in-depth and pattern consistency.

## Current Protection

### Deferred-destroy pattern (`SurfaceManager`)

```
main thread destroy()  → move surface to pending_destroy_  (still alive)
render thread frame    → render using snapshot of raw ptrs
render thread frame-end→ flush_pending_destroys() releases unique_ptrs
```

All three steps serialize via `SurfaceManager::mutex_`. `active_surfaces()` returns a snapshot taken under lock.

### Resize race between main + render

```
main thread:  SurfaceManager::resize()
                std::lock_guard mutex_
                set surf->pending_w/h
                needs_resize.store(true, release)

render thread:  if (surf->needs_resize.load(acquire))
                    read surf->pending_w/h  (happens-after release)
                    surf->swapchain->ResizeBuffers(...)
                    surf->rtv.Reset() + assign new
                    needs_resize.store(false)
```

Release-acquire pairing correctly transfers ownership of `pending_w/h` writes. No race.

## Hypothetical Concern

The original Tech Debt #16 entry flagged "surface handle 교체 시점의 race". After detailed walkthrough of destroy/flush/resize paths, we cannot reproduce a genuine race under the current locking discipline. Possible origins of the concern:

1. **Worry that future features introduce `find_locked` use on render thread** — then the pattern breaks.
2. **Worry about ComPtr member assignment during flush** — but flush only runs on render thread between frames.
3. **General lack of explicit shared ownership** — mirrored the issue fixed in SessionManager (commit 4e989f9).

## Remediation

Convert `surfaces_` and `pending_destroy_` from `vector<unique_ptr>` to `vector<shared_ptr>`, and return `shared_ptr<RenderSurface>` from `find_*` / `active_surfaces`. Same reasoning as the SessionManager UAF fix:

- Render thread that snapshots an `active_surfaces()` list holds its own `shared_ptr` copies
- Concurrent destroy/flush on main thread drops manager refs only
- Surface lives until all strong references released — automatically, no explicit synchronization needed

This is **not strictly required** to fix a current bug but:
- Matches the `SessionManager` pattern (post-UAF-fix)
- Eliminates the hypothetical class of bugs
- Simplifies future feature additions (no need to reason about find vs find_locked)

## Non-goals

- Changing the deferred-destroy timing (still render-thread driven)
- Removing `mutex_` (needed for the vector ops themselves)
- Changing the resize release-acquire protocol

## References

- `src/engine-api/surface_manager.h`, `src/engine-api/surface_manager.cpp`
- `src/engine-api/ghostwin_engine.cpp` — render_loop, render_surface
- `src/renderer/dx11_renderer.cpp::bind_surface` — consumer of rtv/swapchain
- Commit `4e989f9` — SessionManager shared_ptr pattern we mirror
