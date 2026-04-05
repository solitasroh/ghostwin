/// @file key_map.cpp
/// KeyMap: 키 문자열 파싱 ("Ctrl+Shift+T") → packed uint32_t 룩업.

#include "key_map.h"
#include "common/log.h"

#include <algorithm>
#include <sstream>

namespace ghostwin::settings {

// ─── VK name → VK code 매핑 ───

static UINT vk_from_name(const std::string& name) {
    // Single character
    if (name.size() == 1) {
        char c = name[0];
        if (c >= 'A' && c <= 'Z') return static_cast<UINT>(c);
        if (c >= 'a' && c <= 'z') return static_cast<UINT>(c - 'a' + 'A');
        if (c >= '0' && c <= '9') return static_cast<UINT>(c);
    }

    // Named keys
    if (name == "Tab")      return VK_TAB;
    if (name == "Enter")    return VK_RETURN;
    if (name == "Escape")   return VK_ESCAPE;
    if (name == "Space")    return VK_SPACE;
    if (name == "Backspace") return VK_BACK;
    if (name == "Delete")   return VK_DELETE;
    if (name == "Insert")   return VK_INSERT;
    if (name == "Home")     return VK_HOME;
    if (name == "End")      return VK_END;
    if (name == "PageUp")   return VK_PRIOR;
    if (name == "PageDown") return VK_NEXT;
    if (name == "Up")       return VK_UP;
    if (name == "Down")     return VK_DOWN;
    if (name == "Left")     return VK_LEFT;
    if (name == "Right")    return VK_RIGHT;
    if (name == "F1")       return VK_F1;
    if (name == "F2")       return VK_F2;
    if (name == "F3")       return VK_F3;
    if (name == "F4")       return VK_F4;
    if (name == "F5")       return VK_F5;
    if (name == "F6")       return VK_F6;
    if (name == "F7")       return VK_F7;
    if (name == "F8")       return VK_F8;
    if (name == "F9")       return VK_F9;
    if (name == "F10")      return VK_F10;
    if (name == "F11")      return VK_F11;
    if (name == "F12")      return VK_F12;

    LOG_W("keymap", "Unknown key name: '%s'", name.c_str());
    return 0;
}

// ─── 키 문자열 파싱 ───

struct KeyCombo {
    bool ctrl = false;
    bool shift = false;
    bool alt = false;
    UINT vk = 0;
};

static KeyCombo parse_key_string(const std::string& str) {
    KeyCombo combo{};
    std::istringstream ss(str);
    std::string token;

    while (std::getline(ss, token, '+')) {
        // Trim whitespace
        while (!token.empty() && token.front() == ' ') token.erase(token.begin());
        while (!token.empty() && token.back() == ' ')  token.pop_back();

        if (token == "Ctrl")       combo.ctrl = true;
        else if (token == "Shift") combo.shift = true;
        else if (token == "Alt")   combo.alt = true;
        else                       combo.vk = vk_from_name(token);
    }
    return combo;
}

// ─── pack ───

uint32_t KeyMap::pack(bool ctrl, bool shift, bool alt, UINT vk) {
    return (static_cast<uint32_t>(ctrl)  << 24) |
           (static_cast<uint32_t>(shift) << 25) |
           (static_cast<uint32_t>(alt)   << 26) |
           (vk & 0xFFFF);
}

// ─── build ───

void KeyMap::build(const std::unordered_map<std::string, std::string>& bindings) {
    m_map.clear();
    for (auto& [action, key_str] : bindings) {
        auto combo = parse_key_string(key_str);
        if (combo.vk == 0) {
            LOG_W("keymap", "Invalid key '%s' for action '%s'", key_str.c_str(), action.c_str());
            continue;
        }
        uint32_t packed = pack(combo.ctrl, combo.shift, combo.alt, combo.vk);
        if (m_map.count(packed)) {
            LOG_W("keymap", "Duplicate key '%s': overwriting '%s' with '%s'",
                  key_str.c_str(), m_map[packed].c_str(), action.c_str());
        }
        m_map[packed] = action;
    }
    LOG_I("keymap", "Built %zu keybindings", m_map.size());
}

// ─── lookup ───

std::optional<std::string> KeyMap::lookup(
    bool ctrl, bool shift, bool alt, UINT vk) const {
    uint32_t packed = pack(ctrl, shift, alt, vk);
    auto it = m_map.find(packed);
    if (it != m_map.end()) return it->second;
    return std::nullopt;
}

} // namespace ghostwin::settings
