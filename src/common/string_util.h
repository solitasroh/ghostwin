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

} // namespace ghostwin
