// GhostWin Terminal — Platform CWD query implementation
// Phase 5-B: PEB-based CWD query + path shortening
// Include order: project first (cwd_query.h includes <windows.h> needed by winternl/tlhelp32)
// then Windows SDK extensions, then standard.

#include "platform/cwd_query.h"
#include "common/log.h"

#include <winternl.h>
#include <tlhelp32.h>

#include <algorithm>

// NtQueryInformationProcess — loaded once from ntdll
using NtQueryInfoFn = NTSTATUS(NTAPI*)(HANDLE, PROCESSINFOCLASS, PVOID, ULONG, PULONG);

static NtQueryInfoFn GetNtQueryFn() {
    static auto fn = reinterpret_cast<NtQueryInfoFn>(
        GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationProcess"));
    return fn;
}

// PEB offset constants (cpp.md: constexpr over #define)
#ifdef _WIN64
static constexpr SIZE_T kParamsOffset = 0x20;
static constexpr SIZE_T kCurDirOffset = 0x38;
#else
static constexpr SIZE_T kParamsOffset = 0x10;
static constexpr SIZE_T kCurDirOffset = 0x24;
#endif

namespace ghostwin {

// ─── PEB helper: read ProcessParameters pointer (cpp.md: ≤ 40 lines) ───

static PVOID ReadProcessParams(HANDLE proc, void* peb_base) {
    PVOID params = nullptr;
    ReadProcessMemory(proc,
        static_cast<const BYTE*>(peb_base) + kParamsOffset,
        &params, sizeof(params), nullptr);
    return params;
}

// ─── PEB helper: read CWD string from ProcessParameters ───

static std::wstring ReadCwdString(HANDLE proc, PVOID params_ptr) {
    struct CurDir { UNICODE_STRING dos_path; HANDLE handle; };
    CurDir cur_dir{};
    if (!ReadProcessMemory(proc,
            static_cast<const BYTE*>(params_ptr) + kCurDirOffset,
            &cur_dir, sizeof(cur_dir), nullptr))
        return {};

    if (cur_dir.dos_path.Length == 0 || !cur_dir.dos_path.Buffer) return {};

    std::wstring path(cur_dir.dos_path.Length / sizeof(WCHAR), L'\0');
    if (!ReadProcessMemory(proc, cur_dir.dos_path.Buffer,
            path.data(), cur_dir.dos_path.Length, nullptr))
        return {};

    // Remove trailing backslash (except root like C:\)
    if (path.size() > 3 && path.back() == L'\\')
        path.pop_back();
    return path;
}

// ─── GetProcessCwd (cpp.md: ≤ 40 lines — split into helpers above) ───

std::wstring GetProcessCwd(DWORD pid) {
    auto query_fn = GetNtQueryFn();
    if (!query_fn) return {};

    HandleGuard proc{OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid)};
    if (!proc) return {};

    PROCESS_BASIC_INFORMATION pbi{};
    if (query_fn(proc.get(), ProcessBasicInformation, &pbi, sizeof(pbi), nullptr) != 0)
        return {};
    if (!pbi.PebBaseAddress) return {};

    PVOID params = ReadProcessParams(proc.get(), pbi.PebBaseAddress);
    if (!params) return {};

    return ReadCwdString(proc.get(), params);
}

// ─── GetDeepestChildPid ───

DWORD GetDeepestChildPid(DWORD root_pid) {
    HandleGuard snap{CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)};
    if (!snap) return root_pid;

    DWORD current = root_pid;
    bool found = true;
    constexpr int kMaxDepth = 10;  // prevent infinite loop on PID reuse cycles
    int depth = 0;

    while (found && depth < kMaxDepth) {
        found = false;
        PROCESSENTRY32W pe{};
        pe.dwSize = sizeof(pe);
        if (!Process32FirstW(snap.get(), &pe)) break;
        do {
            if (pe.th32ParentProcessID == current) {
                current = pe.th32ProcessID;
                found = true;
                ++depth;
                break;
            }
        } while (Process32NextW(snap.get(), &pe));
    }

    return current;
}

// ─── GetShellCwd ───

std::wstring GetShellCwd(DWORD shell_pid) {
    DWORD deepest = GetDeepestChildPid(shell_pid);
    auto cwd = GetProcessCwd(deepest);
    if (cwd.empty() && deepest != shell_pid)
        cwd = GetProcessCwd(shell_pid);
    return cwd;
}

// ─── ShortenCwd (cpp.md: wstring_view parameter) ───

std::wstring ShortenCwd(std::wstring_view full_path) {
    if (full_path.empty()) return {};

    wchar_t home_buf[MAX_PATH]{};
    DWORD home_len = GetEnvironmentVariableW(L"USERPROFILE", home_buf, MAX_PATH);
    if (home_len == 0 || home_len >= MAX_PATH) {
        auto pos = full_path.find_last_of(L"\\/");
        if (pos != std::wstring_view::npos && pos + 1 < full_path.size())
            return std::wstring(full_path.substr(pos + 1));
        return std::wstring(full_path);
    }

    std::wstring_view home{home_buf, home_len};

    if (full_path == home) return L"~";

    if (full_path.size() > home.size() + 1 &&
        full_path.substr(0, home.size()) == home &&
        (full_path[home.size()] == L'\\' || full_path[home.size()] == L'/')) {
        return L"~/" + std::wstring(full_path.substr(home.size() + 1));
    }

    if (full_path.size() <= 3) return std::wstring(full_path);

    auto pos = full_path.find_last_of(L"\\/");
    if (pos != std::wstring_view::npos && pos + 1 < full_path.size())
        return std::wstring(full_path.substr(pos + 1));

    return std::wstring(full_path);
}

} // namespace ghostwin
