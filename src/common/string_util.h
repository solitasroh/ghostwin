#pragma once

// GhostWin Terminal — UTF-8 ↔ wstring conversion utilities
// cpp.md DRY: extracted from 3+ duplicate MultiByteToWideChar call sites

#include <string>
#include <string_view>

#include <windows.h>

namespace ghostwin {

/// UTF-8 → std::wstring. Returns empty on failure.
[[nodiscard]] inline std::wstring Utf8ToWide(std::string_view utf8) {
    if (utf8.empty()) return {};
    int wlen = MultiByteToWideChar(CP_UTF8, 0,
        utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    if (wlen <= 0) return {};
    std::wstring result(static_cast<size_t>(wlen), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.data(),
        static_cast<int>(utf8.size()), result.data(), wlen);
    return result;
}

/// std::wstring → UTF-8. Returns empty on failure.
[[nodiscard]] inline std::string WideToUtf8(std::wstring_view wide) {
    if (wide.empty()) return {};
    int len = WideCharToMultiByte(CP_UTF8, 0,
        wide.data(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
    if (len <= 0) return {};
    std::string result(static_cast<size_t>(len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.data(),
        static_cast<int>(wide.size()), result.data(), len, nullptr, nullptr);
    return result;
}

/// East Asian Width: true if codepoint is typically displayed as wide (2 cells).
[[nodiscard]] inline bool is_wide_codepoint(uint32_t cp) {
    if (cp >= 0x1100 && cp <= 0x115F) return true;
    if (cp >= 0x2E80 && cp <= 0x303E) return true;
    if (cp >= 0x3040 && cp <= 0x30FF) return true;
    if (cp >= 0x3400 && cp <= 0x9FFF) return true;
    if (cp >= 0xAC00 && cp <= 0xD7AF) return true;
    if (cp >= 0xF900 && cp <= 0xFAFF) return true;
    if (cp >= 0xFF01 && cp <= 0xFF60) return true;
    if (cp >= 0x20000 && cp <= 0x2FA1F) return true;
    return false;
}

} // namespace ghostwin
