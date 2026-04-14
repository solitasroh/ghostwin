#pragma once

// GhostWin Terminal — SessionManager
// Phase 5-A: Multi-session lifecycle management (Design v1.0)
//
// Thread safety:
//   - sessions_ vector modification: main thread only
//   - active_idx_: main thread store(release), render thread load(acquire)
//   - Individual Session fields: see session.h thread ownership legend

#include "session.h"

#include <condition_variable>
#include <memory>
#include <optional>
#include <thread>
#include <vector>

namespace ghostwin {

/// Session creation parameters.
struct SessionCreateParams {
    std::wstring shell_path;       // empty = auto-detect
    std::wstring initial_dir;      // empty = current directory
    uint16_t cols = 80;
    uint16_t rows = 24;
};

/// Session event callbacks — function pointer + context pattern.
/// All callbacks MUST be noexcept. SessionManager wraps calls in try-catch as safety net.
struct SessionEvents {
    using SessionFn = void(*)(void* ctx, SessionId id);
    using ExitFn    = void(*)(void* ctx, SessionId id, uint32_t exit_code);
    using TitleFn   = void(*)(void* ctx, SessionId id, const std::wstring& title);
    using CwdFn     = void(*)(void* ctx, SessionId id, const std::wstring& cwd);

    void* context = nullptr;
    SessionFn on_created   = nullptr;
    SessionFn on_closed    = nullptr;
    SessionFn on_activated = nullptr;
    TitleFn   on_title_changed = nullptr;
    CwdFn     on_cwd_changed   = nullptr;

    /// Called from I/O thread when child process exits.
    /// Handler MUST dispatch to UI thread before calling close_session.
    ExitFn on_child_exit = nullptr;
};

class SessionManager {
public:
    explicit SessionManager(SessionEvents events = {});
    ~SessionManager();

    /// Set or update event callbacks. Must be called before first create_session.
    void set_events(SessionEvents events) { events_ = events; }

    SessionManager(const SessionManager&) = delete;
    SessionManager& operator=(const SessionManager&) = delete;

    // ─── Session lifecycle [main thread only] ───

    /// Create a new session. Returns SessionId on success.
    /// Throws on ConPTY/RenderState failure. TSF failure → LOG_W, continues without IME.
    [[nodiscard]] SessionId create_session(
        const SessionCreateParams& params,
        HWND input_hwnd,
        SessionTsfAdapter::RectFn viewport_fn,
        SessionTsfAdapter::RectFn cursor_fn,
        void* fn_ctx);

    /// Close a session. Returns false if this was the last session (caller should exit app).
    [[nodiscard]] bool close_session(SessionId id);

    // ─── Active session ───

    /// Switch active session [main thread only]. Auto-switches TSF focus.
    void activate(SessionId id);

    /// Current active session [any thread, atomic read]. May be null.
    /// Returns a shared_ptr so callers (notably the render thread) can extend
    /// the Session lifetime across a critical section, preventing UAF when
    /// close_session runs concurrently. See session_manager.cpp comment block
    /// "shared_ptr ownership" for the rationale.
    [[nodiscard]] std::shared_ptr<Session> active_session();
    [[nodiscard]] std::shared_ptr<const Session> active_session() const;

    /// Active session ID [any thread].
    [[nodiscard]] SessionId active_id() const;

    // ─── Query ───

    /// Look up session by ID [any thread]. Returns a shared_ptr that keeps the
    /// Session alive as long as the caller holds it — mandatory for the render
    /// thread to avoid racing with cleanup_worker's destruction.
    [[nodiscard]] std::shared_ptr<Session> get(SessionId id);
    [[nodiscard]] std::shared_ptr<const Session> get(SessionId id) const;
    [[nodiscard]] size_t count() const;
    [[nodiscard]] std::vector<SessionId> ids() const;

    // ─── Resize [main thread only] ───

    /// Resize all sessions. Active = immediate, inactive = lazy (applied on activate).
    void resize_all(uint16_t cols, uint16_t rows);

    // ─── Index-based navigation (tab UI) [main thread only] ───

    [[nodiscard]] std::optional<SessionId> id_at(size_t index) const;
    [[nodiscard]] std::optional<size_t> index_of(SessionId id) const;
    void activate_next();
    void activate_prev();

    // ─── Phase 5-B/D extension API [main thread only] ───

    void move_session(size_t from_index, size_t to_index);
    void resize_session(SessionId id, uint16_t cols, uint16_t rows);

    /// Poll title/CWD for all sessions [main thread only].
    /// Title: read from VtCore (OSC 0/2 already parsed by ghostty).
    /// CWD: read from VtCore (OSC 7) or PEB fallback.
    /// Fires on_title_changed / on_cwd_changed events for changed values.
    void poll_titles_and_cwd();

    /// Shutdown all TSF instances on UI thread (STA). Must be called before
    /// engine destroy to prevent ITfThreadMgr::Deactivate count underflow.
    void shutdown_all_tsf();

private:
    // Ownership: shared_ptr (was unique_ptr). Render thread holds a shared_ptr
    // copy for the duration of start_paint so that a concurrent close_session
    // + cleanup_worker reset() cannot destroy the Session out from under it.
    // The manager itself still drops its strong reference when the session is
    // closed — lifetime ends when the last consumer releases its shared_ptr.
    std::vector<std::shared_ptr<Session>> sessions_;
    std::atomic<uint32_t> active_idx_{0};
    SessionId next_id_ = 0;
    SessionEvents events_;

    // Async cleanup — prevents UI freeze from ConPtySession destructor
    // (I/O thread join + child process wait up to 5s).
    // Policy: sequential processing on single cleanup thread.
    std::vector<std::shared_ptr<Session>> cleanup_queue_;
    std::thread cleanup_thread_;
    std::mutex cleanup_mutex_;
    std::condition_variable cleanup_cv_;
    std::atomic<bool> cleanup_running_{true};

    void cleanup_worker();
    void enqueue_cleanup(std::shared_ptr<Session> dying);

    // Internal helpers [main thread only]
    void switch_tsf_focus(Session* from, Session* to);
    void apply_pending_resize(Session* sess);

    using SessionIter = std::vector<std::shared_ptr<Session>>::iterator;
    using ConstSessionIter = std::vector<std::shared_ptr<Session>>::const_iterator;
    SessionIter find_by_id(SessionId id);
    ConstSessionIter find_by_id(SessionId id) const;

    [[nodiscard]] std::optional<SessionId> find_next_live_id(size_t exclude_index) const;

    void fire_event(SessionEvents::SessionFn fn, SessionId id);
    void fire_exit_event(SessionId id, uint32_t exit_code);
    void fire_title_event(SessionId id, const std::wstring& title);
    void fire_cwd_event(SessionId id, const std::wstring& cwd);
};

} // namespace ghostwin
