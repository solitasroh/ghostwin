#pragma once

// GhostWin Terminal — Tab Sidebar (StackPanel-based)
// Passive View: StackPanel + TabItemUIRefs in-place update.
// No ListView, no IObservableVector, no SelectionGuard.

#include <optional>
#include <vector>

#undef GetCurrentTime
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.UI.Xaml.Controls.Primitives.h>
#include <winrt/Microsoft.UI.Xaml.Input.h>
#include <winrt/Microsoft.UI.Xaml.Media.h>
#include <winrt/Windows.Foundation.Collections.h>

#include "session/session_manager.h"

namespace ghostwin {

namespace winui = winrt::Microsoft::UI::Xaml;
namespace controls = winui::Controls;

struct TabItemData {
    SessionId session_id = 0;
    std::wstring title;
    std::wstring cwd_display;
    bool is_active = false;
};

struct TabItemUIRefs {
    controls::Grid root{nullptr};
    controls::Border accent_bar{nullptr};
    controls::TextBlock title_block{nullptr};
    controls::TextBlock cwd_block{nullptr};
    controls::StackPanel text_panel{nullptr};
};

struct TabSidebarConfig {
    float dpi_scale = 1.0f;
    SessionManager* mgr = nullptr;
    void(*new_tab_fn)(void* ctx) = nullptr;
    void* new_tab_ctx = nullptr;
};

class GhostWinApp;

class TabSidebar {
public:
    void initialize(const TabSidebarConfig& config);
    [[nodiscard]] winui::FrameworkElement root() const;
    void request_new_tab();
    void request_close_active();
    void update_dpi(float new_scale);
    void toggle_visibility();
    [[nodiscard]] bool is_visible() const;

    TabSidebar() = default;
    TabSidebar(const TabSidebar&) = delete;
    TabSidebar& operator=(const TabSidebar&) = delete;

private:
    friend class GhostWinApp;

    void on_session_created(SessionId id);
    void on_session_closed(SessionId id);
    void on_session_activated(SessionId id);
    void on_title_changed(SessionId id, const std::wstring& title);
    void on_cwd_changed(SessionId id, const std::wstring& cwd);

    SessionManager* mgr_ = nullptr;
    void(*new_tab_fn_)(void*) = nullptr;
    void* new_tab_ctx_ = nullptr;

    std::vector<TabItemData> items_;
    std::vector<TabItemUIRefs> item_refs_;

    controls::Grid root_panel_{nullptr};
    controls::ScrollViewer scroll_viewer_{nullptr};
    controls::StackPanel tabs_panel_{nullptr};
    controls::Button add_button_{nullptr};

    struct DragState {
        bool pending = false;
        bool active = false;
        int32_t source_idx = -1;
        int32_t insert_idx = -1;
        float start_y = 0;
    };
    DragState drag_;

    float dpi_scale_ = 1.0f;
    bool visible_ = true;

    static constexpr double kBaseWidth = 220.0;
    static constexpr float kDragThreshold = 5.0f;
    static constexpr double kAccentBarWidth = 3.0;
    static constexpr double kTabItemMinHeight = 40.0;
    static constexpr double kCwdOpacity = 0.6;
    static constexpr double kDragLiftOpacity = 0.85;
    static constexpr double kDragLiftScale = 1.03;

    void setup_tabs_panel();
    void setup_add_button();
    TabItemUIRefs create_tab_item_ui(const TabItemData& data);
    controls::StackPanel create_text_panel(const TabItemData& data, TabItemUIRefs& refs);
    controls::Button create_close_button(SessionId sid);
    void apply_active_visual(TabItemUIRefs& refs);
    void apply_inactive_visual(TabItemUIRefs& refs);
    void append_tab(TabItemData data, TabItemUIRefs refs);
    void remove_tab_at(size_t idx);
    [[nodiscard]] std::optional<size_t> find_index(SessionId id) const;
    void attach_drag_handlers(controls::Grid const& grid, SessionId sid);
    void attach_hover_handlers(controls::Grid const& grid);
    void apply_drag_reorder();
    void reset_drag_visuals();
    [[nodiscard]] int32_t calc_insert_index(float list_y) const;
    void rebuild_list();
    [[nodiscard]] double sidebar_width() const;
};

} // namespace ghostwin
