#pragma once

/// @file json_serializers.h
/// Infra: nlohmann/json ADL from_json/to_json for AppConfiguration.
/// Design Section 5 JSON Schema 참조.

#include "app_configuration.h"
#include <nlohmann/json.hpp>
#include <string>

namespace ghostwin::settings {

// ─── Color hex parsing utilities ───

inline uint32_t parse_hex_color(const std::string& s) {
    if (s.size() >= 7 && s[0] == '#')
        return static_cast<uint32_t>(std::stoul(s.substr(1, 6), nullptr, 16));
    return 0;
}

inline std::string to_hex_color(uint32_t rgb) {
    char buf[8];
    snprintf(buf, sizeof(buf), "#%06x", rgb & 0xFFFFFF);
    return buf;
}

// ─── wstring <-> UTF-8 string helpers ───

inline std::string to_utf8(const std::wstring& ws) {
    if (ws.empty()) return {};
    int len = WideCharToMultiByte(CP_UTF8, 0, ws.data(),
        static_cast<int>(ws.size()), nullptr, 0, nullptr, nullptr);
    std::string s(len, '\0');
    WideCharToMultiByte(CP_UTF8, 0, ws.data(),
        static_cast<int>(ws.size()), s.data(), len, nullptr, nullptr);
    return s;
}

inline std::wstring from_utf8(const std::string& s) {
    if (s.empty()) return {};
    int len = MultiByteToWideChar(CP_UTF8, 0, s.data(),
        static_cast<int>(s.size()), nullptr, 0);
    std::wstring ws(len, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(),
        static_cast<int>(s.size()), ws.data(), len);
    return ws;
}

// ─── Safe JSON accessors (missing key → default) ───

template<typename T>
T get_or(const nlohmann::json& j, const char* key, const T& def) {
    if (j.contains(key)) {
        try { return j[key].get<T>(); }
        catch (...) { return def; }
    }
    return def;
}

inline std::optional<uint32_t> get_opt_color(
    const nlohmann::json& j, const char* key) {
    if (j.contains(key) && j[key].is_string())
        return parse_hex_color(j[key].get<std::string>());
    return std::nullopt;
}

// ─── from_json: JSON → AppConfiguration ───

inline void from_json(const nlohmann::json& j, TerminalSettings::Font& f) {
    if (!j.is_object()) return;
    if (j.contains("family")) f.family = from_utf8(j["family"].get<std::string>());
    f.size_pt          = get_or(j, "size", f.size_pt);
    f.cell_width_scale = get_or(j, "cell_width_scale", f.cell_width_scale);
    f.cell_height_scale= get_or(j, "cell_height_scale", f.cell_height_scale);
    f.glyph_offset_x   = get_or(j, "glyph_offset_x", f.glyph_offset_x);
    f.glyph_offset_y   = get_or(j, "glyph_offset_y", f.glyph_offset_y);
}

inline void from_json(const nlohmann::json& j, TerminalSettings::Colors& c) {
    if (!j.is_object()) return;
    c.theme = get_or<std::string>(j, "theme", c.theme);
    c.background = get_opt_color(j, "background");
    c.foreground = get_opt_color(j, "foreground");
    c.cursor     = get_opt_color(j, "cursor");
    c.background_opacity = get_or(j, "background_opacity", c.background_opacity);
    if (j.contains("palette") && j["palette"].is_array()) {
        auto& arr = j["palette"];
        for (size_t i = 0; i < 16 && i < arr.size(); ++i) {
            if (arr[i].is_string())
                c.palette[i] = parse_hex_color(arr[i].get<std::string>());
        }
    }
}

inline void from_json(const nlohmann::json& j, TerminalSettings::Cursor& c) {
    if (!j.is_object()) return;
    c.style    = get_or<std::string>(j, "style", c.style);
    c.blinking = get_or(j, "blinking", c.blinking);
}

inline void from_json(const nlohmann::json& j, TerminalSettings::Window& w) {
    if (!j.is_object()) return;
    if (j.contains("padding") && j["padding"].is_object()) {
        auto& p = j["padding"];
        w.padding_left   = get_or(p, "left",   w.padding_left);
        w.padding_top    = get_or(p, "top",    w.padding_top);
        w.padding_right  = get_or(p, "right",  w.padding_right);
        w.padding_bottom = get_or(p, "bottom", w.padding_bottom);
    }
    w.mica_enabled    = get_or(j, "mica_enabled", w.mica_enabled);
    w.dynamic_padding = get_or(j, "dynamic_padding", w.dynamic_padding);
}

inline void from_json(const nlohmann::json& j, TerminalSettings& t) {
    if (!j.is_object()) return;
    if (j.contains("font"))   from_json(j["font"],   t.font);
    if (j.contains("colors")) from_json(j["colors"], t.colors);
    if (j.contains("cursor")) from_json(j["cursor"], t.cursor);
    if (j.contains("window")) from_json(j["window"], t.window);
}

inline void from_json(const nlohmann::json& j, MultiplexerSettings::Sidebar& s) {
    if (!j.is_object()) return;
    s.visible           = get_or(j, "visible", s.visible);
    s.width             = get_or(j, "width", s.width);
    s.show_git          = get_or(j, "show_git", s.show_git);
    s.show_ports        = get_or(j, "show_ports", s.show_ports);
    s.show_pr           = get_or(j, "show_pr", s.show_pr);
    s.show_cwd          = get_or(j, "show_cwd", s.show_cwd);
    s.show_latest_alert = get_or(j, "show_latest_alert", s.show_latest_alert);
}

inline void from_json(const nlohmann::json& j, MultiplexerSettings::Behavior& b) {
    if (!j.is_object()) return;
    if (j.contains("auto_restore") && j["auto_restore"].is_object()) {
        auto& ar = j["auto_restore"];
        b.auto_restore.layout          = get_or(ar, "layout", b.auto_restore.layout);
        b.auto_restore.cwd             = get_or(ar, "cwd", b.auto_restore.cwd);
        b.auto_restore.scrollback      = get_or(ar, "scrollback", b.auto_restore.scrollback);
        b.auto_restore.browser_history = get_or(ar, "browser_history", b.auto_restore.browser_history);
    }
}

inline void from_json(const nlohmann::json& j, MultiplexerSettings& m) {
    if (!j.is_object()) return;
    if (j.contains("sidebar"))  from_json(j["sidebar"],  m.sidebar);
    if (j.contains("behavior")) from_json(j["behavior"], m.behavior);
}

inline void from_json(const nlohmann::json& j, AgentSettings::Socket& s) {
    if (!j.is_object()) return;
    s.enabled = get_or(j, "enabled", s.enabled);
    s.path    = get_or<std::string>(j, "path", s.path);
    s.mode    = get_or<std::string>(j, "mode", s.mode);
}

inline void from_json(const nlohmann::json& j, AgentSettings::Notifications& n) {
    if (!j.is_object()) return;
    n.ring_width = get_or(j, "ring_width", n.ring_width);
    if (j.contains("colors") && j["colors"].is_object()) {
        auto& c = j["colors"];
        auto oc = [&](const char* k, uint32_t& v) {
            auto opt = get_opt_color(c, k);
            if (opt) v = *opt;
        };
        oc("waiting",   n.colors.waiting);
        oc("running",   n.colors.running);
        oc("error",     n.colors.error);
        oc("completed", n.colors.completed);
    }
    if (j.contains("panel") && j["panel"].is_object()) {
        auto& p = j["panel"];
        n.panel.position  = get_or<std::string>(p, "position", n.panel.position);
        n.panel.auto_hide = get_or(p, "auto_hide", n.panel.auto_hide);
    }
    if (j.contains("desktop_toast") && j["desktop_toast"].is_object()) {
        auto& dt = j["desktop_toast"];
        n.desktop_toast.enabled = get_or(dt, "enabled", n.desktop_toast.enabled);
        n.desktop_toast.suppress_when_focused =
            get_or(dt, "suppress_when_focused", n.desktop_toast.suppress_when_focused);
    }
}

inline void from_json(const nlohmann::json& j, AgentSettings& a) {
    if (!j.is_object()) return;
    if (j.contains("socket"))        from_json(j["socket"],        a.socket);
    if (j.contains("notifications")) from_json(j["notifications"], a.notifications);
    if (j.contains("progress") && j["progress"].is_object()) {
        auto& p = j["progress"];
        a.progress.visible = get_or(p, "visible", a.progress.visible);
        auto oc = get_opt_color(p, "color");
        if (oc) a.progress.color = *oc;
    }
    if (j.contains("browser") && j["browser"].is_object()) {
        auto& b = j["browser"];
        a.browser.enabled            = get_or(b, "enabled", a.browser.enabled);
        a.browser.automation_allowed = get_or(b, "automation_allowed", a.browser.automation_allowed);
    }
}

inline void from_json(const nlohmann::json& j, AppConfiguration& cfg) {
    if (!j.is_object()) return;
    if (j.contains("terminal"))    from_json(j["terminal"],    cfg.terminal);
    if (j.contains("multiplexer")) from_json(j["multiplexer"], cfg.multiplexer);
    if (j.contains("agent"))       from_json(j["agent"],       cfg.agent);
    if (j.contains("keybindings") && j["keybindings"].is_object()) {
        cfg.keybindings.clear();
        for (auto& [k, v] : j["keybindings"].items()) {
            if (v.is_string())
                cfg.keybindings[k] = v.get<std::string>();
        }
    }
}

// ─── to_json: AppConfiguration → JSON ───

inline nlohmann::json to_json_config(const AppConfiguration& cfg) {
    nlohmann::json j;

    // terminal.font
    auto& tf = cfg.terminal.font;
    j["terminal"]["font"] = {
        {"family", to_utf8(tf.family)},
        {"size", tf.size_pt},
        {"cell_width_scale", tf.cell_width_scale},
        {"cell_height_scale", tf.cell_height_scale},
        {"glyph_offset_x", tf.glyph_offset_x},
        {"glyph_offset_y", tf.glyph_offset_y},
    };

    // terminal.colors
    auto& tc = cfg.terminal.colors;
    j["terminal"]["colors"]["theme"] = tc.theme;
    if (tc.background) j["terminal"]["colors"]["background"] = to_hex_color(*tc.background);
    if (tc.foreground) j["terminal"]["colors"]["foreground"] = to_hex_color(*tc.foreground);
    if (tc.cursor)     j["terminal"]["colors"]["cursor"]     = to_hex_color(*tc.cursor);
    j["terminal"]["colors"]["background_opacity"] = tc.background_opacity;
    nlohmann::json palette_arr = nlohmann::json::array();
    for (int i = 0; i < 16; ++i) {
        if (tc.palette[i])
            palette_arr.push_back(to_hex_color(*tc.palette[i]));
        else
            palette_arr.push_back(nullptr);
    }
    j["terminal"]["colors"]["palette"] = palette_arr;

    // terminal.cursor
    j["terminal"]["cursor"] = {
        {"style", cfg.terminal.cursor.style},
        {"blinking", cfg.terminal.cursor.blinking},
    };

    // terminal.window
    auto& tw = cfg.terminal.window;
    j["terminal"]["window"] = {
        {"padding", {{"left", tw.padding_left}, {"top", tw.padding_top},
                     {"right", tw.padding_right}, {"bottom", tw.padding_bottom}}},
        {"mica_enabled", tw.mica_enabled},
        {"dynamic_padding", tw.dynamic_padding},
    };

    // multiplexer
    auto& ms = cfg.multiplexer.sidebar;
    j["multiplexer"]["sidebar"] = {
        {"visible", ms.visible}, {"width", ms.width},
        {"show_git", ms.show_git}, {"show_ports", ms.show_ports},
        {"show_pr", ms.show_pr}, {"show_cwd", ms.show_cwd},
        {"show_latest_alert", ms.show_latest_alert},
    };
    auto& mb = cfg.multiplexer.behavior.auto_restore;
    j["multiplexer"]["behavior"]["auto_restore"] = {
        {"layout", mb.layout}, {"cwd", mb.cwd},
        {"scrollback", mb.scrollback}, {"browser_history", mb.browser_history},
    };

    // agent
    auto& as = cfg.agent.socket;
    j["agent"]["socket"] = {
        {"enabled", as.enabled}, {"path", as.path}, {"mode", as.mode},
    };
    auto& an = cfg.agent.notifications;
    j["agent"]["notifications"] = {
        {"ring_width", an.ring_width},
        {"colors", {
            {"waiting", to_hex_color(an.colors.waiting)},
            {"running", to_hex_color(an.colors.running)},
            {"error", to_hex_color(an.colors.error)},
            {"completed", to_hex_color(an.colors.completed)},
        }},
        {"panel", {{"position", an.panel.position}, {"auto_hide", an.panel.auto_hide}}},
        {"desktop_toast", {{"enabled", an.desktop_toast.enabled},
                           {"suppress_when_focused", an.desktop_toast.suppress_when_focused}}},
    };
    j["agent"]["progress"] = {
        {"visible", cfg.agent.progress.visible},
        {"color", to_hex_color(cfg.agent.progress.color)},
    };
    j["agent"]["browser"] = {
        {"enabled", cfg.agent.browser.enabled},
        {"automation_allowed", cfg.agent.browser.automation_allowed},
    };

    // keybindings
    j["keybindings"] = nlohmann::json::object();
    for (auto& [k, v] : cfg.keybindings)
        j["keybindings"][k] = v;

    return j;
}

} // namespace ghostwin::settings
