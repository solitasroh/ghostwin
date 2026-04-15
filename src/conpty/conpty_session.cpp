/// @file conpty_session.cpp
/// ConPTY session implementation -- creates pseudo console, I/O thread,
/// and feeds output to VtCore. No Windows headers leak into the public API.

#include "conpty_session.h"
#include "vt_core.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <thread>
#include <mutex>
#include <atomic>
#include <vector>
#include <string_view>
#include <cstdio>
#include <functional>
#include <optional>

namespace ghostwin {

// ─── RAII wrappers (anonymous namespace, cpp-internal only) ───

namespace {

struct HandleCloser {
    void operator()(HANDLE h) const {
        if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h);
    }
};
using UniqueHandle = std::unique_ptr<void, HandleCloser>;

UniqueHandle make_handle(HANDLE h) {
    return UniqueHandle((h == INVALID_HANDLE_VALUE) ? nullptr : h);
}

struct PseudoConsoleCloser {
    void operator()(HPCON h) const {
        if (h) ClosePseudoConsole(h);
    }
};
using UniquePcon = std::unique_ptr<std::remove_pointer_t<HPCON>, PseudoConsoleCloser>;

struct AttrListDeleter {
    void operator()(LPPROC_THREAD_ATTRIBUTE_LIST list) const {
        if (list) {
            DeleteProcThreadAttributeList(list);
            HeapFree(GetProcessHeap(), 0, list);
        }
    }
};
using UniqueAttrList = std::unique_ptr<
    std::remove_pointer_t<LPPROC_THREAD_ATTRIBUTE_LIST>, AttrListDeleter>;

// ─── Logging helpers ───

void log_win_error(const char* context, DWORD error = GetLastError()) {
    fprintf(stderr, "[conpty] %s failed: error=%lu\n", context, error);
}

void log_hresult(const char* context, HRESULT hr) {
    fprintf(stderr, "[conpty] %s failed: HRESULT=0x%08lX\n",
            context, static_cast<unsigned long>(hr));
}

// ─── Shell path resolution ───

std::wstring resolve_shell_path(const std::wstring& user_path) {
    if (!user_path.empty()) return user_path;

    wchar_t found[MAX_PATH];
    if (SearchPathW(NULL, L"pwsh.exe", NULL, MAX_PATH, found, NULL))
        return found;
    if (SearchPathW(NULL, L"powershell.exe", NULL, MAX_PATH, found, NULL))
        return found;
    return L"cmd.exe";
}

// ─── Environment block helpers ───

void remove_env_var(std::vector<wchar_t>& block, const wchar_t* prefix, size_t prefix_len) {
    size_t pos = 0;
    while (pos < block.size() && block[pos] != L'\0') {
        size_t start = pos;
        while (pos < block.size() && block[pos] != L'\0') ++pos;
        size_t entry_len = pos - start;
        if (entry_len >= prefix_len &&
            _wcsnicmp(&block[start], prefix, prefix_len) == 0) {
            block.erase(block.begin() + start, block.begin() + pos + 1);
            pos = start;
        } else {
            ++pos;
        }
    }
}

std::vector<wchar_t> build_environment_block() {
    wchar_t* parent_env = GetEnvironmentStringsW();
    if (!parent_env) return {};

    // Calculate block size (double-null terminated)
    const wchar_t* p = parent_env;
    while (*p) {
        while (*p) ++p;
        ++p;
    }
    ++p; // include final null
    size_t env_size = static_cast<size_t>(p - parent_env);

    std::vector<wchar_t> block(parent_env, parent_env + env_size);
    FreeEnvironmentStringsW(parent_env);

    // Remove existing TERM= to avoid duplicates
    remove_env_var(block, L"TERM=", 5);

    // Insert TERM=xterm-256color before the final double-null
    const std::wstring term_var = L"TERM=xterm-256color";
    // block ends with \0\0 -- replace last \0 with our var
    if (!block.empty()) block.pop_back(); // remove final \0
    block.insert(block.end(), term_var.begin(), term_var.end());
    block.push_back(L'\0');
    block.push_back(L'\0');

    return block;
}

// ─── create() helper: pipe creation ───

struct PipeHandles {
    UniqueHandle input_write;
    UniqueHandle output_read;
    UniqueHandle input_read_side;   // ConPTY-side, closed after CreatePseudoConsole
    UniqueHandle output_write_side; // ConPTY-side, closed after CreatePseudoConsole
};

std::optional<PipeHandles> create_pipes() {
    HANDLE hInputRead = NULL, hInputWrite = NULL;
    HANDLE hOutputRead = NULL, hOutputWrite = NULL;

    if (!CreatePipe(&hInputRead, &hInputWrite, NULL, 0)) {
        log_win_error("CreatePipe(input)");
        return std::nullopt;
    }
    auto inputReadSide = make_handle(hInputRead);
    auto inputWrite = make_handle(hInputWrite);

    if (!CreatePipe(&hOutputRead, &hOutputWrite, NULL, 0)) {
        log_win_error("CreatePipe(output)");
        return std::nullopt;
    }
    auto outputRead = make_handle(hOutputRead);
    auto outputWriteSide = make_handle(hOutputWrite);

    PipeHandles result;
    result.input_write = std::move(inputWrite);
    result.output_read = std::move(outputRead);
    result.input_read_side = std::move(inputReadSide);
    result.output_write_side = std::move(outputWriteSide);
    return result;
}

// ─── create() helper: pseudo console creation ───

std::optional<UniquePcon> create_pseudo_console(
    uint16_t cols, uint16_t rows, HANDLE input_read, HANDLE output_write)
{
    COORD size;
    size.X = static_cast<SHORT>(cols);
    size.Y = static_cast<SHORT>(rows);

    HPCON hPC = NULL;
    HRESULT hr = CreatePseudoConsole(size, input_read, output_write, 0, &hPC);
    if (FAILED(hr)) {
        log_hresult("CreatePseudoConsole", hr);
        return std::nullopt;
    }
    return UniquePcon(hPC);
}

// ─── create() helper: child process spawn ───

struct ChildHandles {
    UniqueHandle process;
    UniqueHandle thread;
};

std::optional<ChildHandles> spawn_child_process(
    HPCON hpc, const std::wstring& shell,
    const std::wstring& initial_dir, std::vector<wchar_t>& env_block)
{
    // Prepare STARTUPINFOEX with pseudo console attribute
    size_t attr_size = 0;
    InitializeProcThreadAttributeList(NULL, 1, 0, &attr_size);

    auto attr_buf = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, attr_size);
    if (!attr_buf) {
        log_win_error("HeapAlloc(AttributeList)");
        return std::nullopt;
    }
    auto attr_list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attr_buf);
    UniqueAttrList attr_guard(attr_list);

    if (!InitializeProcThreadAttributeList(attr_list, 1, 0, &attr_size)) {
        log_win_error("InitializeProcThreadAttributeList");
        return std::nullopt;
    }

    if (!UpdateProcThreadAttribute(
            attr_list, 0,
            PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            hpc, sizeof(HPCON),
            NULL, NULL)) {
        log_win_error("UpdateProcThreadAttribute");
        return std::nullopt;
    }

    STARTUPINFOEXW siEx = {};
    siEx.StartupInfo.cb = sizeof(STARTUPINFOEXW);
    siEx.lpAttributeList = attr_list;

    // CreateProcessW needs a mutable command line buffer
    std::wstring cmd_line = shell;
    PROCESS_INFORMATION pi = {};

    BOOL created = CreateProcessW(
        NULL,
        cmd_line.data(),
        NULL, NULL,
        FALSE,
        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
        env_block.empty() ? NULL : env_block.data(),
        initial_dir.empty() ? NULL : initial_dir.c_str(),
        &siEx.StartupInfo,
        &pi
    );

    if (!created) {
        log_win_error("CreateProcessW");
        return std::nullopt;
    }

    // attr_guard destructor handles DeleteProcThreadAttributeList + HeapFree

    ChildHandles result;
    result.process = make_handle(pi.hProcess);
    result.thread = make_handle(pi.hThread);
    return result;
}

} // anonymous namespace

// ─── Impl ───

struct ConPtySession::Impl {
    UniquePcon hpc;
    UniqueHandle input_write;
    UniqueHandle output_read;
    UniqueHandle child_process;
    UniqueHandle child_thread;

    std::unique_ptr<VtCore> vt_core;

    std::thread io_thread;
    std::atomic<bool> running{false};
    std::mutex vt_mutex;

    ExitCallback on_exit;
    SessionConfig::VtNotifyFn on_vt_title_changed = nullptr;
    SessionConfig::VtNotifyFn on_vt_cwd_changed = nullptr;
    void* vt_notify_ctx = nullptr;
    uint16_t cols = 80;
    uint16_t rows = 24;
    DWORD io_buffer_size = 65536;
    DWORD shutdown_timeout_ms = 5000;

    static void io_thread_func(Impl* impl);
};

// ─── I/O thread function ───

void ConPtySession::Impl::io_thread_func(Impl* impl) {
    const DWORD buf_size = impl->io_buffer_size;
    auto buf = std::make_unique<uint8_t[]>(buf_size);
    DWORD bytes_read = 0;

    try {
        while (impl->running.load(std::memory_order_relaxed)) {
            BOOL ok = ReadFile(
                impl->output_read.get(),
                buf.get(),
                buf_size,
                &bytes_read,
                NULL
            );

            if (!ok || bytes_read == 0) {
                if (!ok) {
                    DWORD err = GetLastError();
                    if (err != ERROR_BROKEN_PIPE) {
                        log_win_error("ReadFile", err);
                    }
                }
                break;
            }

            if (g_tap_active.load(std::memory_order_relaxed)) {
                std::lock_guard lock(g_tap_mutex);
                if (g_tap_echo) g_tap_echo({buf.get(), bytes_read});
            }

            // write() before/after comparison: ~0ms title/CWD detection
            // ghostty contract: "title can be queried after callback returns"
            // write() returns → callback has returned → get_title()/get_pwd() safe
            std::string new_title, new_cwd;
            bool title_changed = false, cwd_changed = false;
            {
                std::lock_guard lock(impl->vt_mutex);
                auto old_title = impl->vt_core->get_title();
                auto old_cwd = impl->vt_core->get_pwd();
                impl->vt_core->write({buf.get(), bytes_read});
                new_title = impl->vt_core->get_title();
                new_cwd = impl->vt_core->get_pwd();
                title_changed = (!new_title.empty() && new_title != old_title);
                cwd_changed = (!new_cwd.empty() && new_cwd != old_cwd);
            }
            // Fire outside lock — callbacks dispatch to UI thread via DispatcherQueue
            if (title_changed && impl->on_vt_title_changed)
                impl->on_vt_title_changed(impl->vt_notify_ctx, new_title);
            if (cwd_changed && impl->on_vt_cwd_changed)
                impl->on_vt_cwd_changed(impl->vt_notify_ctx, new_cwd);
        }
    } catch (const std::exception& e) {
        fprintf(stderr, "[conpty] I/O thread exception: %s\n", e.what());
    }

    impl->running.store(false, std::memory_order_relaxed);

    if (impl->on_exit) {
        uint32_t exit_code = 0;
        if (impl->child_process) {
            DWORD code = 0;
            if (GetExitCodeProcess(impl->child_process.get(), &code)) {
                exit_code = code;
            } else {
                log_win_error("GetExitCodeProcess");
                exit_code = UINT32_MAX;  // sentinel: unknown exit code
            }
        }
        impl->on_exit(exit_code);
    }
}

// ─── ConPtySession ───

ConPtySession::ConPtySession() : impl_(std::make_unique<Impl>()) {}

ConPtySession::~ConPtySession() {
    // Shutdown sequence -- order matters!

    // 1. Close input pipe -> child sees EOF
    impl_->input_write.reset();

    // 2. Close ConPTY -> sends CTRL_CLOSE_EVENT to child
    //    Called from main thread (not I/O thread) to avoid deadlock
    impl_->hpc.reset();

    // 3. I/O thread's ReadFile returns failure -> loop exits -> joinable
    //    hpc.reset() (step 2) breaks the ConPTY pipe, causing ReadFile to fail
    //    and the I/O loop to exit. join() should return quickly after that.
    if (impl_->io_thread.joinable()) {
        impl_->io_thread.join();
    }

    // 4. Close output pipe (after I/O thread exits)
    impl_->output_read.reset();

    // 5. Wait for child process with timeout
    if (impl_->child_process) {
        DWORD wait = WaitForSingleObject(
            impl_->child_process.get(), impl_->shutdown_timeout_ms);
        if (wait == WAIT_TIMEOUT) {
            TerminateProcess(impl_->child_process.get(), 1);
            fprintf(stderr, "[conpty] shutdown timeout, force-terminated child\n");
        }
    }
    impl_->child_process.reset();
    impl_->child_thread.reset();
}

std::unique_ptr<ConPtySession> ConPtySession::create(const SessionConfig& config) {
    auto session = std::unique_ptr<ConPtySession>(new ConPtySession());
    auto* impl = session->impl_.get();

    impl->cols = config.cols;
    impl->rows = config.rows;
    impl->io_buffer_size = config.io_buffer_size;
    impl->shutdown_timeout_ms = config.shutdown_timeout_ms;
    impl->on_exit = config.on_exit;
    impl->on_vt_title_changed = config.on_vt_title_changed;
    impl->on_vt_cwd_changed = config.on_vt_cwd_changed;
    impl->vt_notify_ctx = config.vt_notify_ctx;

    // 1. Create VtCore
    impl->vt_core = VtCore::create(config.cols, config.rows, config.max_scrollback);
    if (!impl->vt_core) {
        fprintf(stderr, "[conpty] VtCore::create failed\n");
        return nullptr;
    }

    // 2. Create pipes (4 handles, all RAII-wrapped)
    auto pipes = create_pipes();
    if (!pipes) return nullptr;

    impl->input_write = std::move(pipes->input_write);
    impl->output_read = std::move(pipes->output_read);

    // 3. Create pseudo console
    auto hpc = create_pseudo_console(
        config.cols, config.rows,
        pipes->input_read_side.get(), pipes->output_write_side.get());
    if (!hpc) return nullptr;

    impl->hpc = std::move(*hpc);

    // 4. Close ConPTY-side pipe handles (mandatory to avoid deadlock)
    pipes->input_read_side.reset();
    pipes->output_write_side.reset();

    // 5-8. Resolve shell, build env block, spawn child process
    std::wstring shell = resolve_shell_path(config.shell_path);
    auto env_block = build_environment_block();

    auto child = spawn_child_process(
        impl->hpc.get(), shell, config.initial_dir, env_block);
    if (!child) return nullptr;

    impl->child_process = std::move(child->process);
    impl->child_thread = std::move(child->thread);

    // 9. Start I/O thread
    impl->running.store(true, std::memory_order_relaxed);
    impl->io_thread = std::thread(Impl::io_thread_func, impl);

    return session;
}

// 테스트 tap 콜백 (--test-ime 모드에서만 설정, 프로덕션은 nullptr)
// g_tap_active가 false면 I/O hot path에서 mutex 진입을 건너뜀.
std::atomic<bool> g_tap_active{false};
std::mutex g_tap_mutex;
std::function<void(std::span<const uint8_t>)> g_tap_input;
std::function<void(std::span<const uint8_t>)> g_tap_echo;

bool ConPtySession::send_input(std::span<const uint8_t> data) {
    if (!impl_->input_write || data.empty()) return false;
    if (g_tap_active.load(std::memory_order_relaxed)) {
        std::lock_guard lock(g_tap_mutex);
        if (g_tap_input) g_tap_input(data);
    }

    const uint8_t* ptr = data.data();
    DWORD remaining = static_cast<DWORD>(data.size());

    while (remaining > 0) {
        DWORD bytes_written = 0;
        BOOL ok = WriteFile(
            impl_->input_write.get(),
            ptr, remaining,
            &bytes_written, NULL
        );
        if (!ok) {
            log_win_error("WriteFile(input)");
            return false;
        }
        ptr += bytes_written;
        remaining -= bytes_written;
    }
    return true;
}

bool ConPtySession::send_ctrl_c() {
    static constexpr uint8_t kCtrlC = 0x03;
    return send_input({&kCtrlC, 1});
}

bool ConPtySession::resize_pty_only(uint16_t cols, uint16_t rows) {
    if (!impl_->hpc) return false;

    COORD size;
    size.X = static_cast<SHORT>(cols);
    size.Y = static_cast<SHORT>(rows);

    HRESULT hr = ResizePseudoConsole(impl_->hpc.get(), size);
    if (FAILED(hr)) {
        log_hresult("ResizePseudoConsole", hr);
        return false;
    }
    return true;
}

void ConPtySession::vt_resize_locked(uint16_t cols, uint16_t rows) {
    // PRECONDITION: caller holds impl_->vt_mutex.
    // std::mutex has no owner query so we cannot assert; rely on contract + code review.
    impl_->vt_core->resize(cols, rows);
    impl_->cols = cols;
    impl_->rows = rows;
}

bool ConPtySession::resize(uint16_t cols, uint16_t rows) {
    if (!resize_pty_only(cols, rows)) return false;

    std::lock_guard lock(impl_->vt_mutex);
    vt_resize_locked(cols, rows);
    return true;
}

bool ConPtySession::is_alive() const {
    if (!impl_->child_process) return false;
    return WaitForSingleObject(impl_->child_process.get(), 0) == WAIT_TIMEOUT;
}

uint32_t ConPtySession::child_pid() const {
    if (!impl_->child_process) return 0;
    return GetProcessId(impl_->child_process.get());
}

const VtCore& ConPtySession::vt_core() const { return *impl_->vt_core; }
VtCore& ConPtySession::vt_core() { return *impl_->vt_core; }
std::mutex& ConPtySession::vt_mutex() { return impl_->vt_mutex; }
uint16_t ConPtySession::cols() const { return impl_->cols; }
uint16_t ConPtySession::rows() const { return impl_->rows; }

} // namespace ghostwin
