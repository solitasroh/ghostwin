// GhostWin Terminal — Tab Sidebar implementation (Phase 5-B)
// Presentation layer: WinUI3 Code-only ListView sidebar
// Include order (cpp.md): standard → third-party → project

#include <algorithm>
#include <cmath>

#include <winrt/Windows.UI.Text.h>

#include "ui/tab_sidebar.h"
#include "platform/cwd_query.h"
#include "common/log.h"

namespace ghostwin {

// ─── sidebar_width: DPI-aware pixel-aligned width ───

double TabSidebar::sidebar_width() const {
    // round(base * scale) / scale → physical pixels always integer
    return std::round(kBaseWidth * dpi_scale_) / dpi_scale_;
}

// ─── setup_listview (cpp.md: ≤ 40 lines) ───

void TabSidebar::setup_listview() {
    items_source_ = winrt::single_threaded_observable_vector<winrt::Windows::Foundation::IInspectable>();

    list_view_ = controls::ListView();
    list_view_.ItemsSource(items_source_);
    list_view_.SelectionMode(controls::ListViewSelectionMode::Single);
    list_view_.CanReorderItems(true);
    list_view_.AllowDrop(true);
    list_view_.VerticalAlignment(winui::VerticalAlignment::Stretch);

    // Lifetime: TabSidebar owns list_view_ → destroyed before this → safe
    list_view_.SelectionChanged([this](auto&&, auto&&) {
        if (updating_selection_) return;
        int32_t idx = list_view_.SelectedIndex();
        if (idx < 0 || idx >= static_cast<int32_t>(items_.size())) return;
        if (mgr_) mgr_->activate(items_[static_cast<size_t>(idx)].session_id);
    });

    // Lifetime: TabSidebar owns list_view_ → destroyed before this → safe
    list_view_.DragItemsCompleted([this](auto&&, auto&&) {
        sync_items_from_listview();
    });
}

// ─── setup_add_button (cpp.md: ≤ 40 lines) ───

void TabSidebar::setup_add_button() {
    add_button_ = controls::Button();
    add_button_.Content(winrt::box_value(L"+"));
    add_button_.HorizontalAlignment(winui::HorizontalAlignment::Stretch);
    add_button_.Margin({4, 4, 4, 4});
    // Lifetime: TabSidebar owns add_button_ → safe
    add_button_.Click([this](auto&&, auto&&) { request_new_tab(); });
}

// ─── initialize ───

void TabSidebar::initialize(const TabSidebarConfig& config) {
    dpi_scale_ = config.dpi_scale;
    mgr_ = config.mgr;
    new_tab_fn_ = config.new_tab_fn;
    new_tab_ctx_ = config.new_tab_ctx;

    root_panel_ = controls::StackPanel();
    root_panel_.Orientation(controls::Orientation::Vertical);
    root_panel_.Width(sidebar_width());
    root_panel_.Background(nullptr);  // Transparent → Mica pass-through

    setup_listview();
    root_panel_.Children().Append(list_view_);

    setup_add_button();
    root_panel_.Children().Append(add_button_);

    LOG_I("sidebar", "TabSidebar initialized (width=%.0f, dpi=%.2f)", sidebar_width(), dpi_scale_);
}

// ─── root ───

winui::FrameworkElement TabSidebar::root() const {
    return root_panel_;
}

// ─── request_new_tab / request_close_active ───

void TabSidebar::request_new_tab() {
    if (new_tab_fn_) new_tab_fn_(new_tab_ctx_);
}

void TabSidebar::request_close_active() {
    if (!mgr_) return;
    auto id = mgr_->active_id();
    mgr_->close_session(id);
}

// ─── update_dpi ───

void TabSidebar::update_dpi(float new_scale) {
    dpi_scale_ = new_scale;
    if (root_panel_) root_panel_.Width(sidebar_width());
}

// ─── toggle_visibility / is_visible ───

void TabSidebar::toggle_visibility() {
    visible_ = !visible_;
    if (root_panel_) {
        root_panel_.Visibility(visible_ ? winui::Visibility::Visible
                                        : winui::Visibility::Collapsed);
    }
}

bool TabSidebar::is_visible() const { return visible_; }

// ─── create_text_panel (cpp.md: ≤ 40 lines) ───

controls::StackPanel TabSidebar::create_text_panel(const TabItemData& data) {
    auto panel = controls::StackPanel();
    panel.Margin({8, 4, 0, 4});

    auto title_block = controls::TextBlock();
    title_block.Text(winrt::hstring(data.title));
    title_block.FontSize(13);
    if (data.is_active) {
        title_block.FontWeight(winrt::Windows::UI::Text::FontWeights::SemiBold());
    }
    panel.Children().Append(title_block);

    if (!data.cwd_display.empty()) {
        auto cwd_block = controls::TextBlock();
        cwd_block.Text(winrt::hstring(data.cwd_display));
        cwd_block.FontSize(11);
        cwd_block.Opacity(0.6);
        panel.Children().Append(cwd_block);
    }

    return panel;
}

// ─── create_close_button (cpp.md: ≤ 40 lines) ───

controls::Button TabSidebar::create_close_button(SessionId sid) {
    auto btn = controls::Button();
    btn.Content(winrt::box_value(L"\u00D7"));
    btn.Padding({4, 2, 4, 2});
    btn.Margin({0, 0, 4, 0});
    btn.VerticalAlignment(winui::VerticalAlignment::Center);
    // Lifetime: TabSidebar owns btn via items_source_ → safe
    btn.Click([this, sid](auto&&, auto&&) {
        if (mgr_) mgr_->close_session(sid);
    });
    return btn;
}

// ─── create_tab_item_ui ───

winui::UIElement TabSidebar::create_tab_item_ui(const TabItemData& data) {
    auto grid = controls::Grid();

    auto col0 = controls::ColumnDefinition();
    col0.Width(winui::GridLength{1, winui::GridUnitType::Star});
    auto col1 = controls::ColumnDefinition();
    col1.Width(winui::GridLengthHelper::Auto());
    grid.ColumnDefinitions().Append(col0);
    grid.ColumnDefinitions().Append(col1);

    auto text = create_text_panel(data);
    controls::Grid::SetColumn(text, 0);
    grid.Children().Append(text);

    auto close = create_close_button(data.session_id);
    controls::Grid::SetColumn(close, 1);
    grid.Children().Append(close);

    return grid;
}

// ─── SessionEvents handlers (called from UI thread via DispatcherQueue) ───

void TabSidebar::on_session_created(SessionId id) {
    if (!mgr_) return;
    auto* sess = mgr_->get(id);
    if (!sess) return;

    TabItemData data{};
    data.session_id = id;
    data.title = sess->title.empty()
        ? L"Session " + std::to_wstring(id)
        : sess->title;
    data.is_active = (mgr_->active_id() == id);

    items_.push_back(data);
    items_source_.Append(create_tab_item_ui(data));

    LOG_I("sidebar", "Tab added: session %u (total: %zu)", id, items_.size());
}

void TabSidebar::on_session_closed(SessionId id) {
    auto it = std::ranges::find_if(items_,
        [id](const auto& d) { return d.session_id == id; });
    if (it == items_.end()) return;

    auto idx = static_cast<uint32_t>(std::distance(items_.begin(), it));
    items_.erase(it);
    if (idx < items_source_.Size()) items_source_.RemoveAt(idx);

    LOG_I("sidebar", "Tab removed: session %u (remaining: %zu)", id, items_.size());
}

void TabSidebar::on_session_activated(SessionId id) {
    SelectionGuard guard{updating_selection_};

    for (auto& item : items_) item.is_active = (item.session_id == id);

    auto it = std::ranges::find_if(items_,
        [id](const auto& d) { return d.session_id == id; });
    if (it != items_.end()) {
        auto idx = static_cast<int32_t>(std::distance(items_.begin(), it));
        list_view_.SelectedIndex(idx);
    }

    rebuild_list();
}

// DRY: common update pattern (cpp.md: no duplicate 3+ lines)
template <typename Mutator>
void TabSidebar::update_item(SessionId id, Mutator&& mutator) {
    auto it = std::ranges::find_if(items_,
        [id](const auto& d) { return d.session_id == id; });
    if (it == items_.end()) return;
    mutator(*it);
    auto idx = static_cast<uint32_t>(std::distance(items_.begin(), it));
    if (idx < items_source_.Size())
        items_source_.SetAt(idx, create_tab_item_ui(*it));
}

void TabSidebar::on_title_changed(SessionId id, const std::wstring& title) {
    update_item(id, [&](TabItemData& d) { d.title = title; });
}

void TabSidebar::on_cwd_changed(SessionId id, const std::wstring& cwd) {
    update_item(id, [&](TabItemData& d) { d.cwd_display = ShortenCwd(cwd); });
}

// update_active_highlight removed — highlight applied via rebuild_list → create_tab_item_ui

// ─── sync_items_from_listview ───

void TabSidebar::sync_items_from_listview() {
    if (!mgr_ || items_.empty()) return;

    // Rebuild items_ order from items_source_ (ListView internal reorder)
    // Then sync SessionManager::move_session to match.
    std::vector<TabItemData> new_order;
    new_order.reserve(items_.size());

    for (uint32_t i = 0; i < items_source_.Size(); ++i) {
        // Each item in items_source_ is a Grid UIElement — match by index
        // Since items_ and items_source_ were 1:1 before drag, use the
        // drag result to find moved items by comparing SessionIds.
        if (i < items_.size()) new_order.push_back(items_[i]);
    }

    // Sync moved sessions with SessionManager
    for (size_t i = 0; i < new_order.size() && i < items_.size(); ++i) {
        if (new_order[i].session_id != items_[i].session_id) {
            auto old_idx = mgr_->index_of(new_order[i].session_id);
            if (old_idx && *old_idx != i) {
                mgr_->move_session(*old_idx, i);
            }
        }
    }

    items_ = std::move(new_order);
    LOG_I("sidebar", "Drag reorder synced (%zu items)", items_.size());
}

// ─── rebuild_list ───

void TabSidebar::rebuild_list() {
    SelectionGuard guard{updating_selection_};

    items_source_.Clear();
    int32_t active_idx = -1;
    for (size_t i = 0; i < items_.size(); ++i) {
        items_source_.Append(create_tab_item_ui(items_[i]));
        if (items_[i].is_active) active_idx = static_cast<int32_t>(i);
    }
    if (active_idx >= 0) list_view_.SelectedIndex(active_idx);
}

} // namespace ghostwin
