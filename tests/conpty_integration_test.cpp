/// @file conpty_integration_test.cpp
/// ConPTY + VtCore integration tests (T1~T8).

#include "conpty_session.h"
#include "vt_core.h"

#include <cstdio>
#include <cstring>
#include <thread>
#include <chrono>

using namespace ghostwin;

static bool send_str(ConPtySession& s, const char* str) {
    return s.send_input({reinterpret_cast<const uint8_t*>(str), strlen(str)});
}

static void wait_ms(int ms) {
    std::this_thread::sleep_for(std::chrono::milliseconds(ms));
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

    auto info = session->vt_core().update_render_state();
    if (info.dirty == DirtyState::Clean) {
        printf("[FAIL] T2: no output received (dirty=Clean)\n");
        return 1;
    }
    printf("[PASS] T2: output received (dirty=%d)\n", static_cast<int>(info.dirty));
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

    auto info = session->vt_core().update_render_state();
    if (info.dirty == DirtyState::Clean) {
        printf("[FAIL] T8: no output after echo %%TERM%%\n");
        return 1;
    }
    printf("[PASS] T8: TERM env var echoed (dirty=%d)\n", static_cast<int>(info.dirty));
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

    printf("\n=== Results: %d/8 passed ===\n", 8 - failures);
    return failures;
}
