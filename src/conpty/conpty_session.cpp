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

            {
                std::lock_guard lock(impl->vt_mutex);
                impl->vt_core->write({buf.get(), bytes_read});
            }
        }
    } catch (const std::exception& e) {
        fprintf(stderr, "[conpty] I/O thread exception: %s\n", e.what());
    }

    impl->running.store(false, std::memory_order_relaxed);

    if (impl->on_exit) {
        uint32_t exit_code = 0;
        if (impl->child_process) {
            DWORD code = 0;
            GetExitCodeProcess(impl->child_process.get(), &code);
            exit_code = code;
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

    // 1. Create VtCore
    impl->vt_core = VtCore::create(config.cols, config.rows, config.max_scrollback);
    if (!impl->vt_core) {
        fprintf(stderr, "[conpty] VtCore::create failed\n");
        return nullptr;
    }

    // 2. Create pipes (4 handles, all RAII-wrapped)
    HANDLE hInputRead = NULL, hInputWrite = NULL;
    HANDLE hOutputRead = NULL, hOutputWrite = NULL;

    if (!CreatePipe(&hInputRead, &hInputWrite, NULL, 0)) {
        log_win_error("CreatePipe(input)");
        return nullptr;
    }
    UniqueHandle inputReadSide = make_handle(hInputRead);
    impl->input_write = make_handle(hInputWrite);

    if (!CreatePipe(&hOutputRead, &hOutputWrite, NULL, 0)) {
        log_win_error("CreatePipe(output)");
        return nullptr;
    }
    impl->output_read = make_handle(hOutputRead);
    UniqueHandle outputWriteSide = make_handle(hOutputWrite);

    // 3. Create pseudo console
    COORD size;
    size.X = static_cast<SHORT>(config.cols);
    size.Y = static_cast<SHORT>(config.rows);

    HPCON hPC = NULL;
    HRESULT hr = CreatePseudoConsole(
        size,
        inputReadSide.get(),
        outputWriteSide.get(),
        0,
        &hPC
    );
    if (FAILED(hr)) {
        log_hresult("CreatePseudoConsole", hr);
        return nullptr;
    }
    impl->hpc = UniquePcon(hPC);

    // 4. Close ConPTY-side pipe handles (mandatory to avoid deadlock)
    inputReadSide.reset();
    outputWriteSide.reset();

    // 5. Prepare STARTUPINFOEX with pseudo console attribute
    size_t attr_size = 0;
    InitializeProcThreadAttributeList(NULL, 1, 0, &attr_size);

    auto attr_buf = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, attr_size);
    if (!attr_buf) {
        log_win_error("HeapAlloc(AttributeList)");
        return nullptr;
    }
    auto attr_list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attr_buf);
    UniqueAttrList attr_guard(attr_list);

    if (!InitializeProcThreadAttributeList(attr_list, 1, 0, &attr_size)) {
        log_win_error("InitializeProcThreadAttributeList");
        return nullptr;
    }

    if (!UpdateProcThreadAttribute(
            attr_list, 0,
            PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            impl->hpc.get(), sizeof(HPCON),
            NULL, NULL)) {
        log_win_error("UpdateProcThreadAttribute");
        return nullptr;
    }

    // 6. Resolve shell path
    std::wstring shell = resolve_shell_path(config.shell_path);

    // 7. Build environment block with TERM=xterm-256color
    auto env_block = build_environment_block();

    // 8. Create child process
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
        config.initial_dir.empty() ? NULL : config.initial_dir.c_str(),
        &siEx.StartupInfo,
        &pi
    );

    if (!created) {
        log_win_error("CreateProcessW");
        return nullptr;
    }

    impl->child_process = make_handle(pi.hProcess);
    impl->child_thread = make_handle(pi.hThread);

    // 9. attr_guard destructor handles DeleteProcThreadAttributeList + HeapFree

    // 10. Start I/O thread
    impl->running.store(true, std::memory_order_relaxed);
    impl->io_thread = std::thread(Impl::io_thread_func, impl);

    return session;
}

bool ConPtySession::send_input(std::span<const uint8_t> data) {
    if (!impl_->input_write || data.empty()) return false;

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
    const uint8_t ctrl_c = 0x03;
    return send_input({&ctrl_c, 1});
}

bool ConPtySession::resize(uint16_t cols, uint16_t rows) {
    if (!impl_->hpc) return false;

    COORD size;
    size.X = static_cast<SHORT>(cols);
    size.Y = static_cast<SHORT>(rows);

    HRESULT hr = ResizePseudoConsole(impl_->hpc.get(), size);
    if (FAILED(hr)) {
        log_hresult("ResizePseudoConsole", hr);
        return false;
    }

    {
        std::lock_guard lock(impl_->vt_mutex);
        impl_->vt_core->resize(cols, rows);
    }
    impl_->cols = cols;
    impl_->rows = rows;
    return true;
}

bool ConPtySession::is_alive() const {
    if (!impl_->child_process) return false;
    return WaitForSingleObject(impl_->child_process.get(), 0) == WAIT_TIMEOUT;
}

const VtCore& ConPtySession::vt_core() const { return *impl_->vt_core; }
VtCore& ConPtySession::vt_core() { return *impl_->vt_core; }
uint16_t ConPtySession::cols() const { return impl_->cols; }
uint16_t ConPtySession::rows() const { return impl_->rows; }

} // namespace ghostwin
