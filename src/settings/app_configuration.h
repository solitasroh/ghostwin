#pragma once

/// @file app_configuration.h
/// Domain: CMUX 3-domain 설정 구조체 + ChangedFlags.
/// Phase 5-D settings-system (Design Section 3.1)

#include <cstdint>
#include <optional>
#include <string>
#include <unordered_map>

namespace ghostwin::settings {

// ─── Terminal Domain ───

struct TerminalSettings {
    struct Font {
        std::wstring family = L"JetBrainsMono NF";
        float size_pt = 11.25f;
        float cell_width_scale = 1.0f;
        float cell_height_scale = 1.0f;
        float glyph_offset_x = 0.0f;
        float glyph_offset_y = 0.0f;
    } font;

    struct Colors {
        std::string theme = "catppuccin-mocha";
        std::optional<uint32_t> background;
        std::optional<uint32_t> foreground;
        std::optional<uint32_t> cursor;
        std::optional<uint32_t> palette[16];
        float background_opacity = 1.0f;
    } colors;

    struct Cursor {
        std::string style = "block";
        bool blinking = true;
    } cursor;

    struct Window {
        float padding_left = 8.f;
        float padding_top = 4.f;
        float padding_right = 8.f;
        float padding_bottom = 4.f;
        bool mica_enabled = true;
        bool dynamic_padding = true;
    } window;
};

// ─── Multiplexer Domain ───

struct MultiplexerSettings {
    struct Sidebar {
        bool visible = true;
        int width = 200;
        bool show_git = true;
        bool show_ports = true;
        bool show_pr = true;
        bool show_cwd = true;
        bool show_latest_alert = true;
    } sidebar;

    struct Behavior {
        struct AutoRestore {
            bool layout = true;
            bool cwd = true;
            bool scrollback = false;
            bool browser_history = false;
        } auto_restore;
    } behavior;
};

// ─── Agent Domain ───

struct AgentSettings {
    struct Socket {
        bool enabled = true;
        std::string path = "\\\\.\\pipe\\ghostwin";
        std::string mode = "process_only";
    } socket;

    struct Notifications {
        float ring_width = 2.5f;
        struct StateColors {
            uint32_t waiting   = 0x89b4fa;
            uint32_t running   = 0xa6e3a1;
            uint32_t error     = 0xf38ba8;
            uint32_t completed = 0xa6adc8;
        } colors;
        struct Panel {
            std::string position = "right";
            bool auto_hide = false;
        } panel;
        struct DesktopToast {
            bool enabled = true;
            bool suppress_when_focused = true;
        } desktop_toast;
    } notifications;

    struct Progress {
        bool visible = true;
        uint32_t color = 0xf9e2af;
    } progress;

    struct Browser {
        bool enabled = true;
        bool automation_allowed = true;
    } browser;
};

// ─── Resolved Colors (테마 + 오버라이드 병합 후) ───

struct ResolvedColors {
    uint32_t background = 0x1E1E2E;
    uint32_t foreground = 0xCDD6F4;
    uint32_t cursor     = 0xF5E0DC;
    uint32_t palette[16] = {};
    float background_opacity = 1.0f;
};

// ─── Top-level Configuration ───

struct AppConfiguration {
    TerminalSettings terminal;
    MultiplexerSettings multiplexer;
    AgentSettings agent;
    std::unordered_map<std::string, std::string> keybindings;
};

// ─── ChangedFlags (bitmask) ───

enum class ChangedFlags : uint16_t {
    None               = 0,
    TerminalFont       = 1 << 0,
    TerminalColors     = 1 << 1,
    TerminalCursor     = 1 << 2,
    TerminalWindow     = 1 << 3,
    MultiplexerSidebar = 1 << 4,
    MultiplexerBehavior= 1 << 5,
    AgentConfig        = 1 << 6,
    Keybindings        = 1 << 7,
    All                = 0xFFFF,
};

inline ChangedFlags operator|(ChangedFlags a, ChangedFlags b) {
    return static_cast<ChangedFlags>(
        static_cast<uint16_t>(a) | static_cast<uint16_t>(b));
}
inline bool has_flag(ChangedFlags flags, ChangedFlags test) {
    return (static_cast<uint16_t>(flags) & static_cast<uint16_t>(test)) != 0;
}

} // namespace ghostwin::settings
