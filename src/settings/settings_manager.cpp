/// @file settings_manager.cpp
/// SettingsManager: JSON 로드/저장/리로드/diff + Observer 통보.

#include "settings_manager.h"
#include "file_watcher.h"
#include "json_serializers.h"
#include "builtin_themes.h"
#include "common/log.h"

#include <fstream>
#include <sstream>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <shlobj.h>

namespace ghostwin::settings {

// ─── Construction / Destruction ───

SettingsManager::SettingsManager(std::filesystem::path config_path)
    : m_path(std::move(config_path)) {
    populate_default_keybindings(m_config);
    resolve_theme_colors();
}

SettingsManager::~SettingsManager() {
    stop_watching();
}

// ─── default_config_path ─��─

std::filesystem::path SettingsManager::default_config_path() {
    wchar_t* appdata = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_RoamingAppData, 0, nullptr, &appdata))) {
        std::filesystem::path p(appdata);
        CoTaskMemFree(appdata);
        return p / L"GhostWin" / L"ghostwin.json";
    }
    return L"ghostwin.json";
}

// ─── load ───

bool SettingsManager::load() {
    if (!std::filesystem::exists(m_path)) {
        LOG_I("settings", "Config not found, creating default: %ls", m_path.c_str());
        create_default_file();
        return false;
    }

    std::ifstream ifs(m_path);
    if (!ifs.is_open()) {
        LOG_W("settings", "Cannot open config: %ls", m_path.c_str());
        return false;
    }

    std::ostringstream ss;
    ss << ifs.rdbuf();
    return parse_json(ss.str());
}

// ─── save ───

bool SettingsManager::save() {
    std::shared_lock lock(m_mutex);
    auto j = to_json_config(m_config);
    lock.unlock();

    std::filesystem::create_directories(m_path.parent_path());
    std::ofstream ofs(m_path);
    if (!ofs.is_open()) {
        LOG_W("settings", "Cannot write config: %ls", m_path.c_str());
        return false;
    }
    ofs << j.dump(2);
    LOG_I("settings", "Config saved: %ls", m_path.c_str());
    return true;
}

// ��── parse_json ───

bool SettingsManager::parse_json(const std::string& json_str) {
    try {
        auto j = nlohmann::json::parse(json_str);
        AppConfiguration new_config;
        populate_default_keybindings(new_config);
        from_json(j, new_config);

        std::unique_lock lock(m_mutex);
        auto flags = diff(m_config, new_config);
        m_config = std::move(new_config);
        resolve_theme_colors();
        lock.unlock();

        if (flags != ChangedFlags::None) {
            LOG_I("settings", "Config loaded (changed flags=0x%x)",
                  static_cast<uint16_t>(flags));
            notify_observers(flags);
        }
        return true;
    } catch (const nlohmann::json::parse_error& e) {
        LOG_W("settings", "JSON parse error: %s", e.what());
        return false;
    } catch (const std::exception& e) {
        LOG_W("settings", "Config load error: %s", e.what());
        return false;
    }
}

// ─── serialize_json ───

std::string SettingsManager::serialize_json() const {
    std::shared_lock lock(m_mutex);
    return to_json_config(m_config).dump(2);
}

// ─── reload ───

void SettingsManager::reload() {
    if (!std::filesystem::exists(m_path)) return;

    std::ifstream ifs(m_path);
    if (!ifs.is_open()) return;

    std::ostringstream ss;
    ss << ifs.rdbuf();
    parse_json(ss.str());
}

// ─── ISettingsProvider ───

const AppConfiguration& SettingsManager::settings() const {
    std::shared_lock lock(m_mutex);
    return m_config;
}

const ResolvedColors& SettingsManager::resolved_colors() const {
    std::shared_lock lock(m_mutex);
    return m_resolved;
}

void SettingsManager::register_observer(ISettingsObserver* obs) {
    if (obs) m_observers.push_back(obs);
}

void SettingsManager::unregister_observer(ISettingsObserver* obs) {
    m_observers.erase(
        std::remove(m_observers.begin(), m_observers.end(), obs),
        m_observers.end());
}

// ─── diff ───

ChangedFlags SettingsManager::diff(
    const AppConfiguration& prev, const AppConfiguration& next) const {

    ChangedFlags flags = ChangedFlags::None;

    // Terminal Font
    auto& pf = prev.terminal.font;
    auto& nf = next.terminal.font;
    if (pf.family != nf.family || pf.size_pt != nf.size_pt ||
        pf.cell_width_scale != nf.cell_width_scale ||
        pf.cell_height_scale != nf.cell_height_scale ||
        pf.glyph_offset_x != nf.glyph_offset_x ||
        pf.glyph_offset_y != nf.glyph_offset_y)
        flags = flags | ChangedFlags::TerminalFont;

    // Terminal Colors
    auto& pc = prev.terminal.colors;
    auto& nc = next.terminal.colors;
    if (pc.theme != nc.theme ||
        pc.background != nc.background ||
        pc.foreground != nc.foreground ||
        pc.cursor != nc.cursor ||
        pc.background_opacity != nc.background_opacity)
        flags = flags | ChangedFlags::TerminalColors;
    // Palette check
    for (int i = 0; i < 16; ++i) {
        if (pc.palette[i] != nc.palette[i]) {
            flags = flags | ChangedFlags::TerminalColors;
            break;
        }
    }

    // Cursor
    if (prev.terminal.cursor.style != next.terminal.cursor.style ||
        prev.terminal.cursor.blinking != next.terminal.cursor.blinking)
        flags = flags | ChangedFlags::TerminalCursor;

    // Window
    auto& pw = prev.terminal.window;
    auto& nw = next.terminal.window;
    if (pw.padding_left != nw.padding_left || pw.padding_top != nw.padding_top ||
        pw.padding_right != nw.padding_right || pw.padding_bottom != nw.padding_bottom ||
        pw.mica_enabled != nw.mica_enabled || pw.dynamic_padding != nw.dynamic_padding)
        flags = flags | ChangedFlags::TerminalWindow;

    // Keybindings
    if (prev.keybindings != next.keybindings)
        flags = flags | ChangedFlags::Keybindings;

    // Multiplexer sidebar
    auto& ps = prev.multiplexer.sidebar;
    auto& ns = next.multiplexer.sidebar;
    if (ps.visible != ns.visible || ps.width != ns.width ||
        ps.show_git != ns.show_git || ps.show_ports != ns.show_ports ||
        ps.show_pr != ns.show_pr || ps.show_cwd != ns.show_cwd ||
        ps.show_latest_alert != ns.show_latest_alert)
        flags = flags | ChangedFlags::MultiplexerSidebar;

    // Agent (coarse check)
    auto& pa = prev.agent;
    auto& na = next.agent;
    if (pa.socket.enabled != na.socket.enabled ||
        pa.socket.path != na.socket.path ||
        pa.socket.mode != na.socket.mode ||
        pa.notifications.ring_width != na.notifications.ring_width)
        flags = flags | ChangedFlags::AgentConfig;

    return flags;
}

// ���── resolve_theme_colors ───

void SettingsManager::resolve_theme_colors() {
    auto& colors = m_config.terminal.colors;
    const ColorTheme* theme = find_theme(colors.theme.c_str());

    if (theme) {
        m_resolved.background = colors.background.value_or(theme->bg);
        m_resolved.foreground = colors.foreground.value_or(theme->fg);
        m_resolved.cursor     = colors.cursor.value_or(theme->cursor);
        for (int i = 0; i < 16; ++i)
            m_resolved.palette[i] = colors.palette[i].value_or(theme->ansi[i]);
    } else {
        // Unknown theme → use catppuccin-mocha as fallback
        const ColorTheme* fallback = find_theme("catppuccin-mocha");
        m_resolved.background = colors.background.value_or(fallback->bg);
        m_resolved.foreground = colors.foreground.value_or(fallback->fg);
        m_resolved.cursor     = colors.cursor.value_or(fallback->cursor);
        for (int i = 0; i < 16; ++i)
            m_resolved.palette[i] = colors.palette[i].value_or(fallback->ansi[i]);
        if (!colors.theme.empty())
            LOG_W("settings", "Unknown theme '%s', using catppuccin-mocha", colors.theme.c_str());
    }
    m_resolved.background_opacity = colors.background_opacity;
}

// ─── notify_observers ───

void SettingsManager::notify_observers(ChangedFlags flags) {
    for (auto* obs : m_observers) {
        try {
            obs->on_settings_changed(m_config, flags);
        } catch (const std::exception& e) {
            LOG_W("settings", "Observer exception: %s", e.what());
        }
    }
}

// ─── create_default_file ───

void SettingsManager::create_default_file() {
    std::filesystem::create_directories(m_path.parent_path());

    AppConfiguration default_config;
    populate_default_keybindings(default_config);
    auto j = to_json_config(default_config);

    std::ofstream ofs(m_path);
    if (ofs.is_open()) {
        ofs << j.dump(2);
        LOG_I("settings", "Default config created: %ls", m_path.c_str());
    }
}

// ─── populate_default_keybindings ───

void SettingsManager::populate_default_keybindings(AppConfiguration& config) {
    if (!config.keybindings.empty()) return;
    config.keybindings = {
        {"workspace.create",    "Ctrl+T"},
        {"workspace.close",     "Ctrl+W"},
        {"workspace.next",      "Ctrl+Tab"},
        {"workspace.prev",      "Ctrl+Shift+Tab"},
        {"workspace.select_1",  "Ctrl+1"},
        {"workspace.select_2",  "Ctrl+2"},
        {"workspace.select_3",  "Ctrl+3"},
        {"workspace.select_4",  "Ctrl+4"},
        {"workspace.select_5",  "Ctrl+5"},
        {"workspace.select_6",  "Ctrl+6"},
        {"workspace.select_7",  "Ctrl+7"},
        {"workspace.select_8",  "Ctrl+8"},
        {"workspace.select_9",  "Ctrl+9"},
        {"workspace.move_up",   "Ctrl+Shift+PageUp"},
        {"workspace.move_down", "Ctrl+Shift+PageDown"},
        {"sidebar.toggle",      "Ctrl+Shift+B"},
        {"edit.paste",          "Ctrl+V"},
        {"notification.toggle_panel", "Ctrl+Shift+I"},
        {"notification.jump_unread",  "Ctrl+Shift+U"},
        {"surface.split_right", "Alt+V"},
        {"surface.split_down",  "Alt+H"},
    };
}

// ─── FileWatcher stubs (Step 5에서 구현) ───

void SettingsManager::start_watching(FileChangedCallback on_file_changed) {
    if (m_watcher) return;
    auto dir = m_path.parent_path();
    if (!std::filesystem::exists(dir)) return;

    if (on_file_changed) {
        // Caller provides UI-thread-safe dispatch wrapper
        m_watcher = std::make_unique<FileWatcherRAII>(dir, std::move(on_file_changed));
    } else {
        // Direct reload (caller ensures thread safety)
        m_watcher = std::make_unique<FileWatcherRAII>(dir, [this]() { reload(); });
    }
}

void SettingsManager::stop_watching() {
    m_watcher.reset();
}

} // namespace ghostwin::settings
