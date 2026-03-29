#pragma once

/// @file conpty_session.h
/// ConPtySession -- Windows ConPTY session manager for GhostWin.
/// Creates a pseudo console, spawns a child process, and feeds
/// output to VtCore for VT parsing.

#include <cstdint>
#include <memory>
#include <span>
#include <string>
#include <functional>

namespace ghostwin {

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
    [[nodiscard]] bool resize(uint16_t cols, uint16_t rows);

    /// Check if the child process is still running.
    [[nodiscard]] bool is_alive() const;

    /// Access the VT parser.
    const VtCore& vt_core() const;
    VtCore& vt_core();

    /// Current terminal dimensions.
    uint16_t cols() const;
    uint16_t rows() const;

private:
    ConPtySession();
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace ghostwin
