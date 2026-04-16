#pragma once

/// @file conpty_session.h
/// ConPtySession -- Windows ConPTY session manager for GhostWin.
/// Creates a pseudo console, spawns a child process, and feeds
/// output to VtCore for VT parsing.

#include <cstdint>
#include <memory>
#include <mutex>
#include <span>
#include <string>
#include <functional>

namespace ghostwin {

// 테스트 tap 콜백 (--test-ime 모드에서만 설정)
// g_tap_active가 false면 mutex 진입 자체를 건너뜀 (프로덕션 zero-cost).
// 설정 시: lock → 콜백 설정 → g_tap_active=true
// 해제 시: lock → 콜백=nullptr → g_tap_active=false
extern std::atomic<bool> g_tap_active;
extern std::mutex g_tap_mutex;
extern std::function<void(std::span<const uint8_t>)> g_tap_input;
extern std::function<void(std::span<const uint8_t>)> g_tap_echo;

class VtCore;

/// Callback invoked when the child process exits.
/// Called from the I/O thread -- do NOT call ConPtySession methods inside.
using ExitCallback = std::function<void(uint32_t exit_code)>;

/// ConPTY session configuration.
struct SessionConfig {
    uint16_t cols = 80;
    uint16_t rows = 24;
    size_t max_scrollback = 10000;
    std::wstring shell_path;      // empty = auto-detect (pwsh -> powershell -> cmd)
    std::wstring initial_dir;     // empty = current directory
    ExitCallback on_exit;         // optional, called from I/O thread

    uint32_t io_buffer_size = 65536;      // I/O read buffer (default 64KB)
    uint32_t shutdown_timeout_ms = 5000;  // shutdown wait timeout (default 5s)

    // Phase 5-B: VT title/CWD change notification from I/O thread.
    // Called after write() when title/pwd changed. Caller holds NO locks.
    using VtNotifyFn = void(*)(void* ctx, const std::string& utf8_value);
    VtNotifyFn on_vt_title_changed = nullptr;
    VtNotifyFn on_vt_cwd_changed = nullptr;
    void* vt_notify_ctx = nullptr;

    // Phase 6-A: OSC 9/99/777 desktop notification from I/O thread.
    using VtDesktopNotifyFn = void(*)(void* ctx,
                                      const std::string& title,
                                      const std::string& body);
    VtDesktopNotifyFn on_vt_desktop_notify = nullptr;
};

/// ConPTY session -- owns ConPTY handle, I/O thread, and VtCore.
class ConPtySession {
public:
    [[nodiscard]] static std::unique_ptr<ConPtySession> create(const SessionConfig& config);

    ~ConPtySession();

    ConPtySession(const ConPtySession&) = delete;
    ConPtySession& operator=(const ConPtySession&) = delete;

    /// Send keyboard input to the child process (UTF-8 bytes).
    [[nodiscard]] bool send_input(std::span<const uint8_t> data);

    /// Send Ctrl+C signal (0x03) to the child process.
    [[nodiscard]] bool send_ctrl_c();

    /// Resize the terminal. Updates both ConPTY and VtCore.
    /// Thin wrapper: calls resize_pty_only() then locks vt_mutex() and calls vt_resize_locked().
    /// Exists for callers that do NOT need to atomically pair VT resize with other lock-held work
    /// (e.g. TerminalRenderState::resize).
    [[nodiscard]] bool resize(uint16_t cols, uint16_t rows);

    /// PTY syscall only (ResizePseudoConsole). Does NOT touch VtCore or internal cols/rows.
    /// Caller must NOT hold vt_mutex() (syscall may block; keep lock hold time minimal).
    /// Returns false if the PTY handle is gone or syscall failed.
    [[nodiscard]] bool resize_pty_only(uint16_t cols, uint16_t rows);

    /// Update VtCore and internal cols/rows members.
    /// PRECONDITION: caller MUST hold vt_mutex() (same mutex returned by vt_mutex()).
    /// Intended to be called inside a caller's lock scope that also covers TerminalRenderState::resize,
    /// to guarantee atomicity of "VtCore dimensions" and "RenderState dimensions".
    void vt_resize_locked(uint16_t cols, uint16_t rows);

    /// Check if the child process is still running.
    [[nodiscard]] bool is_alive() const;

    /// Child process PID (for CWD query via PEB). Returns 0 if not available.
    [[nodiscard]] uint32_t child_pid() const;

    /// Access the VT parser.
    const VtCore& vt_core() const;
    VtCore& vt_core();

    /// Access the single VT lock used by the I/O thread, the render thread, and
    /// SessionManager's resize path (ADR-006). Callers that need to atomically
    /// pair VT updates with TerminalRenderState::resize should hold this mutex
    /// around both vt_resize_locked() and state->resize().
    std::mutex& vt_mutex();

    /// Current terminal dimensions.
    uint16_t cols() const;
    uint16_t rows() const;

private:
    ConPtySession();
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace ghostwin
