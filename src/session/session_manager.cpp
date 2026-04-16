// GhostWin Terminal — SessionManager implementation
// Phase 5-A: Multi-session lifecycle management (Design v1.0)

#include "session/session_manager.h"
#include "common/log.h"
#include "common/string_util.h"
#include "platform/cwd_query.h"  // GetShellCwd — PEB-based fallback for shells without OSC 7
#include "vt-core/vt_bridge.h"  // vt_bridge_get_title (C API, thread-safe)

#include <algorithm>
#include <cassert>

namespace ghostwin {

// ─── SessionTsfAdapter methods ───

void SessionTsfAdapter::HandleOutput(std::wstring_view text) {
    auto* s = session_ref.resolve();
    if (!s || text.empty()) return;

    int len = WideCharToMultiByte(CP_UTF8, 0,
        text.data(), static_cast<int>(text.size()),
        nullptr, 0, nullptr, nullptr);
    if (len <= 0) return;

    constexpr int kStackBufSize = 128;
    char stack_buf[kStackBufSize];
    char* buf = (len <= kStackBufSize) ? stack_buf : new char[len];

    WideCharToMultiByte(CP_UTF8, 0,
        text.data(), static_cast<int>(text.size()),
        buf, len, nullptr, nullptr);

    // send_input failure = pipe closed / child exited; logged inside ConPtySession.
    // IME text loss on pipe closure is acceptable (session is tearing down).
    (void)s->conpty->send_input({reinterpret_cast<const uint8_t*>(buf),
                                 static_cast<size_t>(len)});

    if (buf != stack_buf) delete[] buf;
}

void SessionTsfAdapter::HandleCompositionUpdate(const CompositionPreview& preview) {
    auto* s = session_ref.resolve();
    if (!s) return;
    std::lock_guard lock(s->ime_mutex);
    s->composition = preview.text;
}

// ─── SessionRef::resolve ───

Session* SessionRef::resolve() const {
    if (!mgr) return nullptr;
    // BC-11/UAF-fix: mgr->get() now returns shared_ptr<Session>. SessionRef
    // callers (TSF adapter, UI thread) use the result transiently within a
    // single callback, so returning the raw pointer here preserves the
    // original dangling-safe semantics without forcing every caller to change.
    // The underlying Session cannot be destroyed mid-call because the caller
    // is on the main thread (same thread as close_session), so no race.
    auto s = mgr->get(id);
    if (!s) return nullptr;
    if (s->generation.load(std::memory_order_acquire) != generation) return nullptr;
    if (!s->is_live()) return nullptr;
    return s.get();
}

// ─── SessionManager lifecycle ───

SessionManager::SessionManager(SessionEvents events)
    : events_(events)
{
    cleanup_thread_ = std::thread([this] { cleanup_worker(); });
}

SessionManager::~SessionManager() {
    // Transition all sessions to Closed and enqueue for cleanup
    for (auto& sess : sessions_) {
        if (sess->is_live()) {
            sess->transition_to(SessionState::Closing);
        }
        sess->transition_to(SessionState::Closed);
        enqueue_cleanup(std::move(sess));
    }
    sessions_.clear();

    // Signal cleanup thread to finish
    cleanup_running_.store(false, std::memory_order_release);
    cleanup_cv_.notify_one();
    if (cleanup_thread_.joinable()) cleanup_thread_.join();
}

// ─── create_session ───

SessionId SessionManager::create_session(
    const SessionCreateParams& params,
    HWND input_hwnd,
    SessionTsfAdapter::RectFn viewport_fn,
    SessionTsfAdapter::RectFn cursor_fn,
    void* fn_ctx)
{
    auto sess = std::make_unique<Session>();
    sess->id = next_id_++;
    sess->env_session_id = L"GHOSTWIN_SESSION_ID=" + std::to_wstring(sess->id);

    // TsfDataAdapter setup (function pointers, no heap allocation)
    sess->tsf_data.session_ref = SessionRef{sess->id,
        sess->generation.load(std::memory_order_relaxed), this};
    sess->tsf_data.input_hwnd = input_hwnd;
    sess->tsf_data.get_viewport = viewport_fn;
    sess->tsf_data.get_cursor_pos = cursor_fn;
    sess->tsf_data.fn_context = fn_ctx;

    // TSF handle (failure → IME disabled, session continues)
    try {
        sess->tsf = TsfHandle::Create();
    } catch (...) {
        LOG_W("session", "TSF init failed for session %u, IME disabled", sess->id);
    }

    // RenderState
    sess->state = std::make_unique<TerminalRenderState>(params.cols, params.rows);

    // Mouse encoder/event cache (per-session, Design v1.0 pattern 1+2)
    ghostty_mouse_encoder_new(nullptr, &sess->mouse_encoder);
    ghostty_mouse_event_new(nullptr, &sess->mouse_event);
    if (sess->mouse_encoder) {
        bool track = true;
        ghostty_mouse_encoder_setopt(sess->mouse_encoder,
            GHOSTTY_MOUSE_ENCODER_OPT_TRACK_LAST_CELL, &track);
    }

    // ConPTY session (I/O thread starts automatically)
    // this capture safety: ~SessionManager joins all I/O threads via cleanup_thread
    SessionConfig config{};
    config.cols = params.cols;
    config.rows = params.rows;
    config.shell_path = params.shell_path;
    config.initial_dir = params.initial_dir;
    // on_exit: I/O thread → fire_exit_event → caller dispatches to UI thread
    // this capture safety: ~SessionManager joins all I/O threads via cleanup_thread,
    // so on_exit completes before SessionManager is destroyed.
    config.on_exit = [this, id = sess->id](uint32_t exit_code) {
        fire_exit_event(id, exit_code);
    };

    // VT title/CWD callbacks MUST be set BEFORE create() — create() copies config
    sess->title_callback_ctx = {this, sess->id};
    config.vt_notify_ctx = &sess->title_callback_ctx;
    config.on_vt_title_changed = [](void* ctx, const std::string& utf8) {
        auto* c = static_cast<Session::TitleCallbackCtx*>(ctx);
        auto wtitle = Utf8ToWide(utf8);
        if (!wtitle.empty()) c->mgr->fire_title_event(c->sid, wtitle);
    };
    config.on_vt_cwd_changed = [](void* ctx, const std::string& utf8) {
        auto* c = static_cast<Session::TitleCallbackCtx*>(ctx);
        auto wcwd = Utf8ToWide(utf8);
        if (!wcwd.empty()) c->mgr->fire_cwd_event(c->sid, wcwd);
    };
    config.on_vt_desktop_notify = [](void* ctx,
                                     const std::string& title,
                                     const std::string& body) {
        auto* c = static_cast<Session::TitleCallbackCtx*>(ctx);
        c->mgr->fire_osc_notify_event(c->sid, title, body);
    };

    sess->conpty = ConPtySession::create(config);

    SessionId id = sess->id;
    // unique_ptr → shared_ptr conversion (sess is a local unique_ptr built in
    // create_session; move into shared_ptr to transfer ownership into the
    // manager's shared_ptr vector).
    std::shared_ptr<Session> shared(std::move(sess));
    Session* raw = shared.get();
    sessions_.push_back(std::move(shared));

    if (sessions_.size() == 1) {
        // First session: activate_idx_ is already 0 matching the new index,
        // so activate() would early-return. Manually set up TSF focus.
        active_idx_.store(0, std::memory_order_release);
        switch_tsf_focus(nullptr, raw);
        if (raw->state) raw->state->force_all_dirty();
        fire_event(events_.on_activated, id);
    } else {
        activate(id);
    }

    fire_event(events_.on_created, id);
    LOG_I("session", "Created session %u (total: %zu)", id, sessions_.size());
    return id;
}

// ─── close_session ───

bool SessionManager::close_session(SessionId id) {
    auto it = find_by_id(id);
    if (it == sessions_.end() || !(*it)->is_live()) return true;

    // Hold a shared_ptr alias across the close sequence. When sessions_.erase
    // runs below, the manager drops its strong reference — but render threads
    // may still be using this Session, and enqueue_cleanup needs a valid
    // shared_ptr to hand to the cleanup worker.
    Session* sess = it->get();
    size_t closing_index = static_cast<size_t>(std::distance(sessions_.begin(), it));
    uint32_t current_active = active_idx_.load(std::memory_order_relaxed);
    bool was_active = (closing_index == current_active);

    LOG_I("session", "Closing session %u (index=%zu, was_active=%d)", id,
          closing_index, was_active);

    // Phase 1: Closing — reject new activate
    sess->transition_to(SessionState::Closing);

    if (was_active && sess->tsf) {
        sess->tsf.Unfocus(&sess->tsf_data);
    }

    // Phase 2: Find next active session BEFORE erase
    if (was_active) {
        auto next = find_next_live_id(closing_index);
        if (next) {
            activate(*next);
        }
    }

    // Phase 3: Closed + erase from vector
    sess->transition_to(SessionState::Closed);

    auto dying = std::move(*it);
    sessions_.erase(it);

    // Adjust active_idx_ after erase shifts indices
    uint32_t adj = active_idx_.load(std::memory_order_relaxed);
    if (!sessions_.empty()) {
        if (was_active) {
            // activate() set active_idx_ to pre-erase index.
            // After erase, if closing_index was before or at the activated index, shift down.
            auto activated_id = active_id();
            auto new_it = find_by_id(activated_id);
            if (new_it != sessions_.end()) {
                size_t corrected = static_cast<size_t>(
                    std::distance(sessions_.begin(), new_it));
                active_idx_.store(static_cast<uint32_t>(corrected),
                                  std::memory_order_release);
            }
        } else if (closing_index < adj) {
            active_idx_.store(adj - 1, std::memory_order_release);
        }
    }

    enqueue_cleanup(std::move(dying));
    fire_event(events_.on_closed, id);

    LOG_I("session", "Session %u closed (remaining: %zu)", id, sessions_.size());
    return !sessions_.empty();
}

// ─── activate ───

void SessionManager::activate(SessionId id) {
    auto it = find_by_id(id);
    if (it == sessions_.end()) return;
    if (!(*it)->is_live()) return;

    size_t new_index = static_cast<size_t>(std::distance(sessions_.begin(), it));
    uint32_t current = active_idx_.load(std::memory_order_relaxed);
    if (new_index == current && !sessions_.empty()) return;

    auto old_active = active_session();

    apply_pending_resize(it->get());
    switch_tsf_focus(old_active.get(), it->get());

    active_idx_.store(static_cast<uint32_t>(new_index), std::memory_order_release);

    if ((*it)->state) {
        (*it)->state->force_all_dirty();
    }

    fire_event(events_.on_activated, id);
    LOG_I("session", "Activated session %u (index=%zu)", id, new_index);
}

// ─── active_session ───

std::shared_ptr<Session> SessionManager::active_session() {
    if (sessions_.empty()) return nullptr;
    uint32_t idx = active_idx_.load(std::memory_order_acquire);
    if (idx >= sessions_.size()) return nullptr;
    return sessions_[idx];
}

std::shared_ptr<const Session> SessionManager::active_session() const {
    if (sessions_.empty()) return nullptr;
    uint32_t idx = active_idx_.load(std::memory_order_acquire);
    if (idx >= sessions_.size()) return nullptr;
    return sessions_[idx];
}

SessionId SessionManager::active_id() const {
    auto s = active_session();
    return s ? s->id : 0;
}

// ─── Query ───

std::shared_ptr<Session> SessionManager::get(SessionId id) {
    auto it = find_by_id(id);
    return (it != sessions_.end()) ? *it : nullptr;
}

std::shared_ptr<const Session> SessionManager::get(SessionId id) const {
    auto it = find_by_id(id);
    return (it != sessions_.end()) ? *it : nullptr;
}

size_t SessionManager::count() const {
    return sessions_.size();
}

std::vector<SessionId> SessionManager::ids() const {
    std::vector<SessionId> result;
    result.reserve(sessions_.size());
    for (const auto& s : sessions_) {
        result.push_back(s->id);
    }
    return result;
}

// ─── Index-based navigation ───

std::optional<SessionId> SessionManager::id_at(size_t index) const {
    if (index >= sessions_.size()) return std::nullopt;
    return sessions_[index]->id;
}

std::optional<size_t> SessionManager::index_of(SessionId id) const {
    for (size_t i = 0; i < sessions_.size(); ++i) {
        if (sessions_[i]->id == id) return i;
    }
    return std::nullopt;
}

void SessionManager::activate_next() {
    if (sessions_.size() <= 1) return;
    uint32_t idx = active_idx_.load(std::memory_order_relaxed);
    size_t next = (idx + 1) % sessions_.size();
    activate(sessions_[next]->id);
}

void SessionManager::activate_prev() {
    if (sessions_.size() <= 1) return;
    uint32_t idx = active_idx_.load(std::memory_order_relaxed);
    size_t prev = (idx == 0) ? sessions_.size() - 1 : idx - 1;
    activate(sessions_[prev]->id);
}

// ─── move_session ───

void SessionManager::move_session(size_t from, size_t to) {
    if (from >= sessions_.size() || to >= sessions_.size() || from == to) return;

    uint32_t current_active = active_idx_.load(std::memory_order_relaxed);

    auto moving = std::move(sessions_[from]);
    sessions_.erase(sessions_.begin() + static_cast<ptrdiff_t>(from));
    sessions_.insert(sessions_.begin() + static_cast<ptrdiff_t>(to), std::move(moving));

    uint32_t new_active = current_active;
    if (current_active == from) {
        new_active = static_cast<uint32_t>(to);
    } else {
        if (from < current_active && to >= current_active) new_active--;
        else if (from > current_active && to <= current_active) new_active++;
    }
    if (new_active != current_active) {
        active_idx_.store(new_active, std::memory_order_release);
    }
}

// ─── resize_session ───

void SessionManager::resize_session(SessionId id, uint16_t cols, uint16_t rows) {
    auto sess = get(id);
    if (!sess || !sess->is_live()) return;

    // PTY syscall outside the VT lock (ResizePseudoConsole may block briefly;
    // ConPTY output pipe is a separate kernel object — no race with I/O thread).
    // Skip VT/RenderState update on PTY failure to preserve the original invariant
    // (ConPtySession::resize() wrapper aborted VT update on PTY failure).
    if (!sess->conpty->resize_pty_only(cols, rows)) return;

    // VT + RenderState update atomically under the SAME mutex that the I/O and
    // render threads use. This protects against torn reads of TerminalRenderState
    // during _api.reshape() (see ADR-006 revision, 2026-04-15).
    {
        std::lock_guard lock(sess->conpty->vt_mutex());
        sess->conpty->vt_resize_locked(cols, rows);
        sess->state->resize(cols, rows);
    }
}

// ─── Internal helpers ───

void SessionManager::switch_tsf_focus(Session* from, Session* to) {
    if (from && from->tsf) {
        from->tsf.Unfocus(&from->tsf_data);
    }
    if (to && to->tsf) {
        to->tsf.Focus(&to->tsf_data);
    }
}

void SessionManager::apply_pending_resize(Session* sess) {
    if (!sess || !sess->resize_pending) return;

    const uint16_t cols = sess->pending_cols;
    const uint16_t rows = sess->pending_rows;

    // Skip VT/RenderState update on PTY failure (see resize_session for rationale).
    // Keep resize_pending = true so the next activation retries — do NOT clear the flag.
    if (!sess->conpty->resize_pty_only(cols, rows)) return;

    {
        std::lock_guard lock(sess->conpty->vt_mutex());
        sess->conpty->vt_resize_locked(cols, rows);
        sess->state->resize(cols, rows);
    }
    sess->resize_pending = false;
}

SessionManager::SessionIter SessionManager::find_by_id(SessionId id) {
    return std::find_if(sessions_.begin(), sessions_.end(),
        [id](const auto& s) { return s->id == id; });
}

SessionManager::ConstSessionIter SessionManager::find_by_id(SessionId id) const {
    return std::find_if(sessions_.begin(), sessions_.end(),
        [id](const auto& s) { return s->id == id; });
}

std::optional<SessionId> SessionManager::find_next_live_id(size_t exclude_index) const {
    // Search forward from exclude_index+1, then wrap around
    for (size_t i = 1; i < sessions_.size(); ++i) {
        size_t idx = (exclude_index + i) % sessions_.size();
        if (sessions_[idx]->is_live()) {
            return sessions_[idx]->id;
        }
    }
    return std::nullopt;
}

void SessionManager::fire_event(SessionEvents::SessionFn fn, SessionId id) {
    if (!fn || !events_.context) return;
    try {
        fn(events_.context, id);
    } catch (...) {
        LOG_E("session", "Event callback threw for session %u", id);
    }
}

void SessionManager::fire_exit_event(SessionId id, uint32_t exit_code) {
    if (!events_.on_child_exit || !events_.context) return;
    try {
        events_.on_child_exit(events_.context, id, exit_code);
    } catch (...) {
        LOG_E("session", "on_child_exit callback threw for session %u", id);
    }
}

void SessionManager::fire_title_event(SessionId id, const std::wstring& title) {
    if (!events_.on_title_changed || !events_.context) return;
    try {
        events_.on_title_changed(events_.context, id, title);
    } catch (...) {
        LOG_E("session", "on_title_changed callback threw for session %u", id);
    }
}

void SessionManager::fire_cwd_event(SessionId id, const std::wstring& cwd) {
    if (!events_.on_cwd_changed || !events_.context) return;
    try {
        events_.on_cwd_changed(events_.context, id, cwd);
    } catch (...) {
        LOG_E("session", "on_cwd_changed callback threw for session %u", id);
    }
}

void SessionManager::fire_osc_notify_event(SessionId id,
                                            const std::string& title,
                                            const std::string& body) {
    if (!events_.on_osc_notify || !events_.context) return;
    try {
        events_.on_osc_notify(events_.context, id,
            title.c_str(), title.size(),
            body.c_str(), body.size());
    } catch (...) {
        LOG_E("session", "on_osc_notify callback threw for session %u", id);
    }
}

// ─── Phase 5-B + M-11 followup: PEB CWD polling (cpp.md: ≤ 40 lines) ───
// Title + OSC 7 CWD: event-driven via I/O thread write() before/after comparison (~0ms).
// PEB CWD: fallback for shells without OSC 7 (cmd.exe, default PowerShell prompt).
// Re-introduced 2026-04-15 after WinUI3→WPF migration removed winui_app.cpp timer.
// Caller (WPF DispatcherTimer in MainWindow) invokes this ~1s. Cost: 1 PEB read per live session.
// Thread: main thread only. Reads sess->cwd / writes sess->cwd / fires on_cwd_changed.

void SessionManager::poll_titles_and_cwd() {
    for (auto& sess : sessions_) {
        if (!sess) continue;
        if (sess->lifecycle.load(std::memory_order_acquire) != SessionState::Live) continue;
        if (!sess->conpty) continue;

        const uint32_t pid = sess->conpty->child_pid();
        if (pid == 0) continue;  // child not running yet or already exited

        std::wstring new_cwd = GetShellCwd(pid);
        if (new_cwd.empty()) continue;  // PEB read failed (permission/race) — keep last known

        if (new_cwd != sess->cwd) {
            sess->cwd = new_cwd;
            fire_cwd_event(sess->id, new_cwd);
        }
    }
}

// ─── Async cleanup ───

void SessionManager::cleanup_worker() {
    while (true) {
        std::unique_lock lock(cleanup_mutex_);
        cleanup_cv_.wait(lock, [this] {
            return !cleanup_queue_.empty() || !cleanup_running_.load(std::memory_order_acquire);
        });

        if (!cleanup_running_.load(std::memory_order_acquire) && cleanup_queue_.empty())
            return;

        std::shared_ptr<Session> dying;
        if (!cleanup_queue_.empty()) {
            dying = std::move(cleanup_queue_.front());
            cleanup_queue_.erase(cleanup_queue_.begin());
        }
        lock.unlock();

        if (dying) {
            SessionId id = dying->id;
            // dying.reset() here only drops the cleanup worker's strong ref.
            // If a render thread still holds its own shared_ptr copy, the
            // actual destructor (ConPtySession I/O join + child wait) runs
            // when that last reference is released — fully race-free.
            dying.reset();
            LOG_I("session", "Cleanup completed for session %u (may still hold render refs)", id);
        }
    }
}

void SessionManager::enqueue_cleanup(std::shared_ptr<Session> dying) {
    {
        std::lock_guard lock(cleanup_mutex_);
        cleanup_queue_.push_back(std::move(dying));
    }
    cleanup_cv_.notify_one();
}

void SessionManager::shutdown_all_tsf() {
    for (auto& sess : sessions_) {
        if (sess && sess->tsf) {
            sess->tsf.Shutdown();
            LOG_I("session", "TSF shutdown for session %u", sess->id);
        }
    }
}

} // namespace ghostwin
