#pragma once

/// @file render_perf.h
/// M-14 W1 — Render path performance instrumentation.
///
/// Emits one structured log line per render_surface() call when
/// `GHOSTWIN_RENDER_PERF` env var is set (non-empty and not "0"). The flag is
/// read once at process startup; `perf_enabled()` is a single plain read per
/// frame so the disabled path adds negligible overhead.
///
/// Log schema (single line, LOG_I tag "render-perf"):
///   frame=<u64> sid=<u32> panes=<usize> vt_dirty=<0|1> visual_dirty=<0|1>
///   resize=<0|1> start_us=<f> build_us=<f> draw_us=<f> present_us=<f>
///   total_us=<f> quads=<u32>
///
/// Consumed by `scripts/measure_render_baseline.ps1` (idle/load/resize
/// scenarios) and cross-referenced with PresentMon CSV output.

#include <cstdint>

namespace ghostwin {

/// Per-surface per-frame timing sample. All durations in microseconds.
/// `visual_dirty` reflects the SessionVisualState epoch snapshot comparison
/// introduced in M-14 W3 follow-up (non-VT visual change: selection / IME /
/// activate).
struct RenderPerfSample {
    uint64_t frame_id = 0;
    uint32_t surface_id = 0;
    uint64_t pane_count = 0;
    bool vt_dirty = false;
    bool visual_dirty = false;
    bool resize_applied = false;
    double start_us = 0.0;     // start_paint() duration
    double build_us = 0.0;     // QuadBuilder + overlays
    double draw_us = 0.0;      // upload + DrawIndexedInstanced
    double present_us = 0.0;   // Present(1, 0) block
    double total_us = 0.0;     // render_surface total
    uint32_t quad_count = 0;
};

/// Returned by DX11Renderer::upload_and_draw_timed() — splits the Present
/// blocking time from the rest so engine.cpp can populate draw_us/present_us
/// separately in the perf sample.
struct DrawPerfResult {
    double upload_draw_us = 0.0;
    double present_us = 0.0;
    bool presented = false;
};

/// Process-level flag. Reads `GHOSTWIN_RENDER_PERF` once at static init.
/// Subsequent calls are plain loads (no syscall, no lock).
bool perf_enabled();

} // namespace ghostwin
