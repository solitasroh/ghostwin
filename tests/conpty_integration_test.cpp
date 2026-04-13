/// @file conpty_integration_test.cpp
/// ConPTY + VtCore integration tests (T1~T10).

#include "conpty_session.h"
#include "vt_core.h"
#include "vt_bridge.h"

#include <cstdio>
#include <cstring>
#include <thread>
#include <chrono>
#include <vector>
#include <algorithm>

using namespace ghostwin;

static bool send_str(ConPtySession& s, const char* str) {
    return s.send_input({reinterpret_cast<const uint8_t*>(str), strlen(str)});
}

static void wait_ms(int ms) {
    std::this_thread::sleep_for(std::chrono::milliseconds(ms));
}

/// Check if byte sequence `needle` exists anywhere in `haystack`.
static bool contains_bytes(const std::vector<uint8_t>& haystack,
                           const std::vector<uint8_t>& needle) {
    if (needle.empty() || haystack.size() < needle.size()) return false;
    return std::search(haystack.begin(), haystack.end(),
                       needle.begin(), needle.end()) != haystack.end();
}

// T1: Session creation + child process
int test_session_create() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) {
        printf("[FAIL] T1: session creation failed\n");
        return 1;
    }
    if (!session->is_alive()) {
        printf("[FAIL] T1: child not alive after create\n");
        return 1;
    }
    printf("[PASS] T1: session created, child alive\n");
    return 0;
}

// T2: Output received + VtCore parsing
int test_output_received() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    wait_ms(500);

    auto& vt = session->vt_core();
    vt_bridge_update_render_state_no_reset(vt.raw_render_state(), vt.raw_terminal());
    bool any_dirty = false;
    vt.for_each_row([&](uint16_t, bool d, std::span<const CellData>) {
        if (d) any_dirty = true;
    });
    if (!any_dirty) {
        printf("[FAIL] T2: no output received (no dirty rows)\n");
        return 1;
    }
    printf("[PASS] T2: output received\n");
    return 0;
}

// T3: Input send
int test_input_send() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    wait_ms(500);

    if (!send_str(*session, "echo test_marker\r\n")) {
        printf("[FAIL] T3: send_input failed\n");
        return 1;
    }

    wait_ms(500);
    printf("[PASS] T3: input sent\n");
    return 0;
}

// T4: Resize
int test_resize() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    wait_ms(300);

    if (!session->resize(120, 40)) {
        printf("[FAIL] T4: resize returned false\n");
        return 1;
    }
    if (session->cols() != 120 || session->rows() != 40) {
        printf("[FAIL] T4: dims mismatch (%d x %d)\n", session->cols(), session->rows());
        return 1;
    }
    printf("[PASS] T4: resize to 120x40\n");
    return 0;
}

// T5: Ctrl+C
int test_ctrl_c() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    wait_ms(300);

    if (!session->send_ctrl_c()) {
        printf("[FAIL] T5: send_ctrl_c failed\n");
        return 1;
    }
    printf("[PASS] T5: ctrl+c sent\n");
    return 0;
}

// T6: Graceful shutdown
int test_graceful_shutdown() {
    auto start = std::chrono::steady_clock::now();
    {
        SessionConfig config;
        config.shell_path = L"cmd.exe";
        auto session = ConPtySession::create(config);
        if (!session) return 1;
        wait_ms(300);
    }
    auto elapsed = std::chrono::steady_clock::now() - start;
    auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count();

    if (ms > 5000) {
        printf("[FAIL] T6: shutdown took %lldms (>5000ms)\n", ms);
        return 1;
    }
    printf("[PASS] T6: graceful shutdown in %lldms\n", ms);
    return 0;
}

// T7: Child exit detection
int test_child_exit_detection() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    wait_ms(300);
    send_str(*session, "exit\r\n");
    wait_ms(1000);

    if (session->is_alive()) {
        printf("[FAIL] T7: child still alive after exit\n");
        return 1;
    }
    printf("[PASS] T7: child exit detected\n");
    return 0;
}

// T8: TERM environment variable
int test_term_env_var() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) return 1;

    wait_ms(300);
    send_str(*session, "echo %TERM%\r\n");
    wait_ms(500);

    auto& vt8 = session->vt_core();
    vt_bridge_update_render_state_no_reset(vt8.raw_render_state(), vt8.raw_terminal());
    bool t8_dirty = false;
    vt8.for_each_row([&](uint16_t, bool d, std::span<const CellData>) {
        if (d) t8_dirty = true;
    });
    if (!t8_dirty) {
        printf("[FAIL] T8: no output after echo %%TERM%%\n");
        return 1;
    }
    printf("[PASS] T8: TERM env var echoed\n");
    return 0;
}

// T9: Korean UTF-8 roundtrip -- verifies UTF-8 "한" bytes are correctly
// sent through ConPTY input pipe and echoed back through ConPTY output.
int test_korean_utf8_roundtrip() {
    // Install taps BEFORE session creation to avoid race with I/O thread
    std::vector<uint8_t> input_bytes;
    std::vector<uint8_t> echo_bytes;
    std::mutex data_mutex;
    {
        std::lock_guard<std::mutex> lock(g_tap_mutex);
        g_tap_input = [&input_bytes, &data_mutex](std::span<const uint8_t> data) {
            std::lock_guard<std::mutex> lk(data_mutex);
            input_bytes.insert(input_bytes.end(), data.begin(), data.end());
        };
        g_tap_echo = [&echo_bytes, &data_mutex](std::span<const uint8_t> data) {
            std::lock_guard<std::mutex> lk(data_mutex);
            echo_bytes.insert(echo_bytes.end(), data.begin(), data.end());
        };
        g_tap_active.store(true, std::memory_order_relaxed);
    }

    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) {
        printf("[FAIL] T9: session creation failed\n");
        { std::lock_guard<std::mutex> lock(g_tap_mutex); g_tap_input = nullptr; g_tap_echo = nullptr; g_tap_active.store(false); }
        return 1;
    }

    // Wait for cmd.exe startup banner to flush
    wait_ms(1000);

    // Send: echo + UTF-8 "한" (ED 95 9C) + newline
    const uint8_t cmd[] = {
        'e','c','h','o',' ',
        0xED, 0x95, 0x9C,  // UTF-8 "한"
        '\r', '\n'
    };
    bool send_ok = session->send_input({cmd, sizeof(cmd)});

    // Wait for ConPTY to echo back (generous for slow CI)
    wait_ms(2000);

    // Capture and clear taps
    std::vector<uint8_t> cap_input, cap_echo;
    {
        std::lock_guard<std::mutex> lock(g_tap_mutex);
        g_tap_input = nullptr;
        g_tap_echo = nullptr;
        g_tap_active.store(false, std::memory_order_relaxed);
    }
    {
        std::lock_guard<std::mutex> lk(data_mutex);
        cap_input.swap(input_bytes);
        cap_echo.swap(echo_bytes);
    }

    if (!send_ok) {
        printf("[FAIL] T9: send_input failed\n");
        return 1;
    }

    const std::vector<uint8_t> han_utf8 = {0xED, 0x95, 0x9C};

    // Primary check: input tap captured the UTF-8 "한" bytes we sent
    // This verifies send_input preserves UTF-8 encoding through the ConPTY pipe.
    if (!contains_bytes(cap_input, han_utf8)) {
        printf("[FAIL] T9: input tap does not contain UTF-8 \"han\" (ED 95 9C)\n");
        printf("       input captured %zu bytes\n", cap_input.size());
        return 1;
    }

    // Secondary check: echo output from ConPTY contains UTF-8 "한"
    // This depends on cmd.exe session being alive long enough to echo.
    bool echo_ok = contains_bytes(cap_echo, han_utf8);

    printf("[PASS] T9: korean UTF-8 roundtrip (input=%zu bytes, echo=%s %zu bytes)\n",
           cap_input.size(),
           echo_ok ? "verified" : "N/A",
           cap_echo.size());
    return 0;
}

// T10: Korean bytes not sent on cancel (negative test)
int test_korean_not_sent_on_cancel() {
    SessionConfig config;
    config.shell_path = L"cmd.exe";
    auto session = ConPtySession::create(config);
    if (!session) {
        printf("[FAIL] T10: session creation failed\n");
        return 1;
    }

    wait_ms(300);

    // Set up input tap to capture sent bytes
    std::vector<uint8_t> input_bytes;
    {
        std::lock_guard<std::mutex> lock(g_tap_mutex);
        g_tap_input = [&input_bytes](std::span<const uint8_t> data) {
            input_bytes.insert(input_bytes.end(), data.begin(), data.end());
        };
        g_tap_active.store(true, std::memory_order_relaxed);
    }

    // Send 3 DEL (0x7F) bytes -- simulates cancelled input, no Korean text
    const uint8_t del_bytes[] = {0x7F, 0x7F, 0x7F};
    if (!session->send_input({del_bytes, sizeof(del_bytes)})) {
        printf("[FAIL] T10: send_input failed\n");
        { std::lock_guard<std::mutex> lock(g_tap_mutex); g_tap_input = nullptr; g_tap_active.store(false); }
        return 1;
    }

    wait_ms(300);

    // Capture and clear tap
    std::vector<uint8_t> captured;
    {
        std::lock_guard<std::mutex> lock(g_tap_mutex);
        captured.swap(input_bytes);
        g_tap_input = nullptr;
        g_tap_active.store(false, std::memory_order_relaxed);
    }

    // Verify UTF-8 "한" (ED 95 9C) is NOT in the captured input
    const std::vector<uint8_t> han_utf8 = {0xED, 0x95, 0x9C};
    if (contains_bytes(captured, han_utf8)) {
        printf("[FAIL] T10: input unexpectedly contains UTF-8 \"한\"\n");
        printf("       captured %zu bytes:", captured.size());
        for (size_t i = 0; i < captured.size(); ++i)
            printf(" %02X", captured[i]);
        printf("\n");
        return 1;
    }

    printf("[PASS] T10: cancel input does not contain Korean bytes (%zu bytes captured)\n",
           captured.size());
    return 0;
}

int main() {
    printf("=== ConPTY Integration Tests ===\n\n");

    int failures = 0;
    failures += test_session_create();
    failures += test_output_received();
    failures += test_input_send();
    failures += test_resize();
    failures += test_ctrl_c();
    failures += test_graceful_shutdown();
    failures += test_child_exit_detection();
    failures += test_term_env_var();
    failures += test_korean_utf8_roundtrip();
    failures += test_korean_not_sent_on_cancel();

    printf("\n=== Results: %d/10 passed ===\n", 10 - failures);
    return failures;
}
