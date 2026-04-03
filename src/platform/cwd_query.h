#pragma once

// GhostWin Terminal — Platform CWD query utilities
// Phase 5-B: Process CWD query via PEB + CWD shortening
//
// Infrastructure layer (common.md): OS-specific process introspection.
// cpp.md: RAII HandleGuard, wstring_view for non-owning strings.

#include <cstdint>
#include <string>
#include <string_view>

#include <windows.h>

namespace ghostwin {

/// RAII wrapper for Win32 HANDLE (cpp.md: RAII for C APIs)
struct HandleGuard {
    HANDLE h = nullptr;

    explicit HandleGuard(HANDLE handle) : h(handle) {}
    ~HandleGuard() {
        if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h);
    }

    HandleGuard(const HandleGuard&) = delete;
    HandleGuard& operator=(const HandleGuard&) = delete;

    [[nodiscard]] explicit operator bool() const { return h && h != INVALID_HANDLE_VALUE; }
    [[nodiscard]] HANDLE get() const { return h; }
};

/// Query CWD of a process by PID via NtQueryInformationProcess + PEB.
/// ReadProcessMemory 3x. Returns empty string on failure.
/// Requires PROCESS_QUERY_INFORMATION | PROCESS_VM_READ.
[[nodiscard]] std::wstring GetProcessCwd(DWORD pid);

/// Find the deepest child process in a process tree.
/// Uses CreateToolhelp32Snapshot. Returns root_pid if no children found.
[[nodiscard]] DWORD GetDeepestChildPid(DWORD root_pid);

/// Combined: shell PID → deepest child's CWD (fallback: shell's own CWD).
[[nodiscard]] std::wstring GetShellCwd(DWORD shell_pid);

/// Shorten a Windows path for tab display.
/// cpp.md: wstring_view for non-owning parameter.
///
/// Rules:
///   C:\Users\<user>           → ~
///   C:\Users\<user>\Documents → ~/Documents
///   C:\Users\<user>\...\foo  → foo (last component)
///   C:\                       → C:\
///   (empty)                   → (empty)
[[nodiscard]] std::wstring ShortenCwd(std::wstring_view full_path);

} // namespace ghostwin
