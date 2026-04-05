#pragma once

/// @file key_map.h
/// Action ID 기반 키바인딩 룩업.
/// Design Section 5.3 참조.

#include <cstdint>
#include <optional>
#include <string>
#include <unordered_map>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace ghostwin::settings {

class KeyMap {
public:
    void build(const std::unordered_map<std::string, std::string>& bindings);

    [[nodiscard]] std::optional<std::string> lookup(
        bool ctrl, bool shift, bool alt, UINT vk) const;

private:
    std::unordered_map<uint32_t, std::string> m_map;

    static uint32_t pack(bool ctrl, bool shift, bool alt, UINT vk);
};

} // namespace ghostwin::settings
