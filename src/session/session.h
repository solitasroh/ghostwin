#pragma once

// GhostWin Terminal — Session data structures
// Phase 5-A: Multi-session support (Design v1.0)
//
// Thread ownership legend (per field):
//   [main]         — main thread only
//   [main+IO]      — main thread + I/O thread, protected by vt_mutex
//   [main+render]  — main thread + render thread, protected by vt_mutex
//   [any/atomic]   — any thread, atomic access

#include "tsf/tsf_handle.h"
#include "conpty/conpty_session.h"
#include "renderer/render_state.h"
#include "common/log.h"

// ghostty mouse encoder/event — C API (extern "C" to prevent C++ name mangling)
extern "C" {
#include <ghostty/vt/mouse.h>
}

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>

namespace ghostwin {

class SessionManager;

/// Session ID — monotonically increasing, never reused.
using SessionId = uint32_t;

/// Session lifecycle state (CMUX PR #808 pattern).
/// Live → Closing → Closed. No reverse transitions.
enum class SessionState : uint8_t {
    Live,      // Normal operation. I/O thread running, input/render enabled.
    Closing,   // Shutdown in progress. New activate() rejected.
    Closed,    // Fully shut down. Awaiting erase from vector.
};

// Forward declare Session for SessionRef
struct Session;

/// Dangling-safe session reference.
/// Uses {id, generation} pair validated on each access.
struct SessionRef {
    SessionId id = 0;
    uint32_t generation = 0;
    SessionManager* mgr = nullptr;

    /// Returns valid Live session pointer, or nullptr.
    /// Implemented in session_manager.cpp (needs SessionManager::get).
    [[nodiscard]] Session* resolve() const;
};

/// TSF IDataProvider backed by SessionRef for safe session access.
///
/// Thread ownership: main thread only (TSF callbacks always on main thread).
/// Uses function pointers instead of std::function to avoid heap allocation.
///
/// Phase 5-D note: get_viewport/get_cursor_pos can be replaced with
/// Pane-aware versions when PaneSplit is implemented.
struct SessionTsfAdapter : IDataProvider {
    SessionRef session_ref;
    HWND input_hwnd = nullptr;

    using RectFn = RECT(*)(void* ctx);
    RectFn get_viewport = nullptr;
    RectFn get_cursor_pos = nullptr;
    void* fn_context = nullptr;

    HWND GetHwnd() override { return input_hwnd; }

    RECT GetViewport() override {
        return get_viewport ? get_viewport(fn_context) : RECT{};
    }

    RECT GetCursorPosition() override {
        return get_cursor_pos ? get_cursor_pos(fn_context) : RECT{};
    }

    // Implemented in session_manager.cpp (needs full Session definition)
    void HandleOutput(std::wstring_view text) override;
    void HandleCompositionUpdate(const CompositionPreview& preview) override;
};

/// Selection range for DX11 render-time overlay (M-10c).
/// Written by WndProc thread (via gw_session_set_selection C API),
/// read by render thread (render_surface). Protected by Session::vt_mutex
/// or single-writer guarantee (WndProc is single-threaded).
struct SelectionRange {
    int32_t start_row = 0, start_col = 0;
    int32_t end_row = 0, end_col = 0;
    std::atomic<bool> active{false};
};

/// Single terminal session — ConPTY + VT parser + render state + IME isolation.
struct Session {
    Session() = default;
    ~Session() {
        if (mouse_event)   ghostty_mouse_event_free(mouse_event);
        if (mouse_encoder) ghostty_mouse_encoder_free(mouse_encoder);
    }

    Session(const Session&) = delete;
    Session& operator=(const Session&) = delete;
    Session(Session&&) = delete;
    Session& operator=(Session&&) = delete;

    SessionId id = 0;

    // ─── Lifecycle [main: store(release), render/any: load(acquire)] ───
    std::atomic<SessionState> lifecycle{SessionState::Live};
    std::atomic<uint32_t> generation{1};

    // ─── Per-session isolated state ───
    std::unique_ptr<ConPtySession> conpty;               // [main+IO, vt_mutex]
    std::unique_ptr<TerminalRenderState> state;          // [main+render, vt_mutex]
    std::mutex vt_mutex;                                 // ADR-006 extension

    // ─── Mouse encoder/event cache (per-session, heap alloc 0 at runtime) ───
    GhosttyMouseEncoder mouse_encoder = nullptr;         // [WndProc thread, per-session]
    GhosttyMouseEvent   mouse_event   = nullptr;         // [WndProc thread, per-session]

    // ─── TSF/IME isolation [main only, except ime_mutex] ───
    TsfHandle tsf{};
    SessionTsfAdapter tsf_data;                          // [main only]
    std::wstring composition;                            // [main(W) + render(R), ime_mutex]
    std::mutex ime_mutex;

    // ─── Metadata [main only] ───
    std::wstring title;
    std::wstring cwd;

    // ─── Environment (Phase 6 hook integration) [main only] ───
    std::wstring env_session_id;

    // ─── Selection (M-10c: DX11 overlay) [WndProc write, render read] ───
    SelectionRange selection;

    // ─── Pending resize (lazy resize pattern) [main only] ───
    bool resize_pending = false;
    uint16_t pending_cols = 0;
    uint16_t pending_rows = 0;

    // ─── Surrogate pair state (per-session) [main only] ───
    wchar_t pending_high_surrogate = 0;

    // ─── Phase 5-B: VT title callback context (cpp.md: no raw new) ───
    struct TitleCallbackCtx {
        SessionManager* mgr = nullptr;
        SessionId sid = 0;
    };
    TitleCallbackCtx title_callback_ctx;

    /// State transition with generation bump.
    /// Call restriction: main thread only.
    void transition_to(SessionState new_state) {
        generation.fetch_add(1, std::memory_order_release);
        lifecycle.store(new_state, std::memory_order_release);
    }

    /// Check if session is in Live state.
    /// Safe to call from any thread (atomic read).
    [[nodiscard]] bool is_live() const {
        return lifecycle.load(std::memory_order_acquire) == SessionState::Live;
    }
};

} // namespace ghostwin
