#pragma once

// GhostWin Terminal — Tab Sidebar (Phase 5-B)
// Presentation layer (common.md): WinUI3 Code-only left vertical sidebar.
//
// cpp.md compliance:
//   - Rule of Zero (WinRT value-type members → copy deleted, move/dtor compiler-generated)
//   - Public methods ≤ 7 (on_* handlers are private, GhostWinApp is friend)
//   - Function bodies ≤ 40 lines (split into setup_*/create_* helpers)
//   - Function pointer + context pattern (no std::function, no #include <functional>)
//   - RAII SelectionGuard for guard flag
//   - Parameters ≤ 3 (initialize takes TabSidebarConfig struct)
//   - Lambda [this] captures: TabSidebar owns all WinUI3 elements → elements destroyed first → safe
//
// Include order (cpp.md IWYU): standard → third-party → project

#include <vector>

#undef GetCurrentTime
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Xaml.Interop.h>
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.UI.Xaml.Controls.Primitives.h>
#include <winrt/Microsoft.UI.Xaml.Input.h>
#include <winrt/Microsoft.UI.Xaml.Media.h>

#include "session/session_manager.h"

namespace ghostwin {

namespace winui = winrt::Microsoft::UI::Xaml;
namespace controls = winui::Controls;

/// Tab item display data (UI thread only)
struct TabItemData {
    SessionId session_id = 0;
    std::wstring title;
    std::wstring cwd_display;
    bool is_active = false;
};

/// Initialization config (cpp.md: parameters ≤ 3 → struct for 4+)
struct TabSidebarConfig {
    float dpi_scale = 1.0f;
    SessionManager* mgr = nullptr;           // non-owning observer
    void(*new_tab_fn)(void* ctx) = nullptr;  // function pointer (no std::function)
    void* new_tab_ctx = nullptr;             // non-owning
};

// Forward declare — GhostWinApp calls private on_* handlers via friend
class GhostWinApp;

/// Left vertical tab sidebar — WinUI3 Code-only ListView
///
/// Thread ownership: all methods UI thread (main thread) only.
/// SessionManager events are dispatched to UI thread by GhostWinApp
/// before calling on_* handlers (private, accessed via friend).
class TabSidebar {
public:
    // ─── Public API (7 methods — common.md limit) ───

    void initialize(const TabSidebarConfig& config);
    [[nodiscard]] winui::FrameworkElement root() const;
    void request_new_tab();
    void request_close_active();
    void update_dpi(float new_scale);
    void toggle_visibility();
    [[nodiscard]] bool is_visible() const;

    // Non-copyable (WinRT com_ptr members → Rule of Zero, copy deleted)
    TabSidebar() = default;
    TabSidebar(const TabSidebar&) = delete;
    TabSidebar& operator=(const TabSidebar&) = delete;

private:
    // GhostWinApp dispatches SessionEvents to these private handlers
    friend class GhostWinApp;

    void on_session_created(SessionId id);
    void on_session_closed(SessionId id);
    void on_session_activated(SessionId id);
    void on_title_changed(SessionId id, const std::wstring& title);
    void on_cwd_changed(SessionId id, const std::wstring& cwd);

    // ─── Data ───

    SessionManager* mgr_ = nullptr;
    void(*new_tab_fn_)(void*) = nullptr;
    void* new_tab_ctx_ = nullptr;

    controls::StackPanel root_panel_{nullptr};
    controls::ListView list_view_{nullptr};
    controls::Button add_button_{nullptr};

    winrt::Windows::Foundation::Collections::IObservableVector<winrt::Windows::Foundation::IInspectable>
        items_source_{nullptr};

    std::vector<TabItemData> items_;

    // RAII guard (cpp.md: constructor sets, destructor resets)
    bool updating_selection_ = false;
    struct SelectionGuard {
        bool& flag;
        SelectionGuard(bool& f) : flag(f) { flag = true; }
        ~SelectionGuard() { flag = false; }
    };

    float dpi_scale_ = 1.0f;
    bool visible_ = true;
    static constexpr double kBaseWidth = 220.0;
    static constexpr float kDragThreshold = 5.0f;  // DIP

    // ─── Custom Pointer Drag state (OLE drag bypass — DPI safe) ───

    struct DragState {
        bool pending = false;     // pointer down, threshold not yet passed
        bool active = false;      // threshold passed, dragging
        int32_t source_idx = -1;
        int32_t insert_idx = -1;
        float start_y = 0;
    };
    DragState drag_;

    // ─── Internal helpers (cpp.md: function ≤ 40 lines → split) ───

    void setup_listview();
    void setup_add_button();
    controls::StackPanel create_text_panel(const TabItemData& data);
    controls::Button create_close_button(SessionId sid);
    winui::UIElement create_tab_item_ui(const TabItemData& data);
    void attach_drag_handlers(controls::Grid const& grid, SessionId sid);

    /// Common update pattern for on_title_changed/on_cwd_changed (DRY)
    template <typename Mutator>
    void update_item(SessionId id, Mutator&& mutator);

    void apply_drag_reorder();
    void reset_drag_visuals();
    [[nodiscard]] int32_t calc_insert_index(float list_y) const;
    void rebuild_list();
    [[nodiscard]] double sidebar_width() const;
};

} // namespace ghostwin
