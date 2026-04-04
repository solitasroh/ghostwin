// GhostWin Terminal — Tab Sidebar implementation (Phase 5-B)
// Presentation layer: WinUI3 Code-only ListView sidebar
// Include order (cpp.md): standard → third-party → project

#include <algorithm>
#include <cmath>

#include <winrt/Windows.UI.Text.h>
#include <winrt/Microsoft.UI.Input.h>

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
    // OLE drag disabled — DPI offset bug (WinUI3 #5520, #9717, OS-level)
    // Custom pointer drag used instead (PointerPressed/Moved/Released)
    list_view_.CanReorderItems(false);
    list_view_.AllowDrop(false);
    list_view_.VerticalAlignment(winui::VerticalAlignment::Stretch);

    list_view_.SelectionChanged([this](auto&&, auto&&) {
        if (updating_selection_ || drag_.active) return;
        int32_t idx = list_view_.SelectedIndex();
        if (idx < 0 || idx >= static_cast<int32_t>(items_.size())) return;
        if (mgr_) mgr_->activate(items_[static_cast<size_t>(idx)].session_id);
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
    grid.Tag(winrt::box_value(static_cast<int32_t>(data.session_id)));

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

    attach_drag_handlers(grid, data.session_id);
    return grid;
}

// ─── Custom Pointer Drag — OLE bypass (DPI safe) ───

void TabSidebar::attach_drag_handlers(controls::Grid const& grid, SessionId sid) {
    // Prepare TranslateTransform for drag movement (set once, reused)
    winui::Media::TranslateTransform transform;
    grid.RenderTransform(transform);

    grid.PointerPressed([this, sid](auto& sender, winui::Input::PointerRoutedEventArgs const& e) {
        auto it = std::ranges::find_if(items_,
            [sid](const auto& d) { return d.session_id == sid; });
        if (it == items_.end() || items_.size() < 2) return;

        auto pos = e.GetCurrentPoint(list_view_).Position();
        drag_ = {true, false, static_cast<int32_t>(std::distance(items_.begin(), it)),
                 -1, pos.Y};
        sender.as<winui::UIElement>().CapturePointer(e.Pointer());
        e.Handled(true);
    });

    grid.PointerMoved([this](auto& sender, winui::Input::PointerRoutedEventArgs const& e) {
        if (!drag_.pending && !drag_.active) return;
        auto pos = e.GetCurrentPoint(list_view_).Position();
        float delta_y = pos.Y - drag_.start_y;

        if (!drag_.active) {
            if (std::abs(delta_y) < kDragThreshold) return;
            drag_.active = true;
            // Lift effect: semi-transparent + scale
            auto elem = sender.as<winui::UIElement>();
            elem.Opacity(0.85);
            winrt::Microsoft::UI::Xaml::Media::ScaleTransform scale;
            scale.ScaleX(1.03);
            scale.ScaleY(1.03);
            auto group = winrt::Microsoft::UI::Xaml::Media::TransformGroup();
            auto translate = elem.RenderTransform().as<winui::Media::TranslateTransform>();
            group.Children().Append(translate);
            group.Children().Append(scale);
            elem.RenderTransform(group);
        }

        // Move dragged item with pointer
        auto elem = sender.as<winui::UIElement>();
        auto transform_group = elem.RenderTransform().try_as<winui::Media::TransformGroup>();
        if (transform_group && transform_group.Children().Size() > 0) {
            auto translate = transform_group.Children().GetAt(0).as<winui::Media::TranslateTransform>();
            translate.Y(static_cast<double>(delta_y));
        }

        drag_.insert_idx = calc_insert_index(pos.Y);

        // Visual hint: dim other items, highlight insertion target
        for (uint32_t i = 0; i < items_source_.Size(); ++i) {
            auto item = items_source_.GetAt(i).try_as<winui::UIElement>();
            if (!item || static_cast<int32_t>(i) == drag_.source_idx) continue;
            item.Opacity(static_cast<int32_t>(i) == drag_.insert_idx ? 0.6 : 1.0);
        }
        e.Handled(true);
    });

    grid.PointerReleased([this](auto& sender, winui::Input::PointerRoutedEventArgs const& e) {
        sender.as<winui::UIElement>().ReleasePointerCapture(e.Pointer());
        bool should_reorder = drag_.active && drag_.insert_idx >= 0
                              && drag_.insert_idx != drag_.source_idx;
        reset_drag_visuals();
        if (should_reorder) apply_drag_reorder();
        drag_ = {};
        e.Handled(true);
    });

    grid.PointerCanceled([this](auto& sender, winui::Input::PointerRoutedEventArgs const& e) {
        sender.as<winui::UIElement>().ReleasePointerCapture(e.Pointer());
        reset_drag_visuals();
        drag_ = {};
    });
}

// ─── reset_drag_visuals: restore opacity + transform on all items ───

void TabSidebar::reset_drag_visuals() {
    for (uint32_t i = 0; i < items_source_.Size(); ++i) {
        auto elem = items_source_.GetAt(i).try_as<winui::UIElement>();
        if (!elem) continue;
        elem.Opacity(1.0);
        // Reset to clean TranslateTransform (remove any TransformGroup)
        winui::Media::TranslateTransform clean;
        elem.RenderTransform(clean);
    }
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

// ─── calc_insert_index: Y position → item index ───

int32_t TabSidebar::calc_insert_index(float list_y) const {
    if (items_.empty()) return 0;
    float item_h = static_cast<float>(list_view_.ActualHeight()) /
                   static_cast<float>(items_.size());
    if (item_h < 1.0f) item_h = 40.0f;  // fallback
    int32_t idx = static_cast<int32_t>(list_y / item_h);
    return std::clamp(idx, 0, static_cast<int32_t>(items_.size()) - 1);
}

// ─── apply_drag_reorder: move item + sync SessionManager ───

void TabSidebar::apply_drag_reorder() {
    if (!mgr_ || drag_.source_idx < 0 || drag_.insert_idx < 0) return;
    if (drag_.source_idx == drag_.insert_idx) return;

    auto src = static_cast<size_t>(drag_.source_idx);
    auto dst = static_cast<size_t>(drag_.insert_idx);
    if (src >= items_.size() || dst >= items_.size()) return;

    // Move in items_ vector
    auto moving = items_[src];
    items_.erase(items_.begin() + static_cast<ptrdiff_t>(src));
    items_.insert(items_.begin() + static_cast<ptrdiff_t>(dst), moving);

    // Sync SessionManager
    mgr_->move_session(src, dst);

    // Rebuild UI to reflect new order
    rebuild_list();
    LOG_I("sidebar", "Drag reorder: %zu → %zu (%zu items)", src, dst, items_.size());
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
