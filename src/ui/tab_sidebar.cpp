// GhostWin Terminal — Tab Sidebar (StackPanel-based)
// Passive View: in-place TabItemUIRefs update, no ListView.

#include <algorithm>
#include <cassert>
#include <cmath>

#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Text.h>
#include <winrt/Microsoft.UI.Input.h>

#include "ui/tab_sidebar.h"
#include "platform/cwd_query.h"
#include "common/log.h"

namespace ghostwin {

// ─── sidebar_width ───

double TabSidebar::sidebar_width() const {
    return std::round(kBaseWidth * dpi_scale_) / dpi_scale_;
}

// ─── setup_tabs_panel ───

void TabSidebar::setup_tabs_panel() {
    tabs_panel_ = controls::StackPanel();
    tabs_panel_.Orientation(controls::Orientation::Vertical);
    // Transparent (not nullptr) — enables hit-testing for mouse wheel scroll
    tabs_panel_.Background(winui::Media::SolidColorBrush{
        winrt::Windows::UI::Colors::Transparent()});

    scroll_viewer_ = controls::ScrollViewer();
    scroll_viewer_.Content(tabs_panel_);
    scroll_viewer_.VerticalScrollBarVisibility(controls::ScrollBarVisibility::Auto);
    scroll_viewer_.HorizontalScrollBarVisibility(controls::ScrollBarVisibility::Disabled);
    scroll_viewer_.VerticalScrollMode(controls::ScrollMode::Enabled);
    scroll_viewer_.HorizontalScrollMode(controls::ScrollMode::Disabled);
    scroll_viewer_.VerticalAlignment(winui::VerticalAlignment::Stretch);

}

// ─── setup_add_button ───

void TabSidebar::setup_add_button() {
    add_button_ = controls::Button();
    add_button_.Content(winrt::box_value(L"+"));
    add_button_.HorizontalAlignment(winui::HorizontalAlignment::Stretch);
    add_button_.Margin({4, 4, 4, 4});
    add_button_.Click([this](auto&&, auto&&) { request_new_tab(); });
}

// ─── initialize ───

void TabSidebar::initialize(const TabSidebarConfig& config) {
    dpi_scale_ = config.dpi_scale;
    mgr_ = config.mgr;
    new_tab_fn_ = config.new_tab_fn;
    new_tab_ctx_ = config.new_tab_ctx;

    // Grid: Row 0=Star (ScrollViewer), Row 1=Auto (button)
    // StackPanel gives infinite height → ScrollViewer can't scroll
    root_panel_ = controls::Grid();
    root_panel_.Width(sidebar_width());
    root_panel_.VerticalAlignment(winui::VerticalAlignment::Stretch);
    root_panel_.Background(nullptr);

    auto row0 = controls::RowDefinition();
    row0.Height(winui::GridLength{1, winui::GridUnitType::Star});
    auto row1 = controls::RowDefinition();
    row1.Height(winui::GridLengthHelper::Auto());
    root_panel_.RowDefinitions().Append(row0);
    root_panel_.RowDefinitions().Append(row1);

    setup_tabs_panel();
    controls::Grid::SetRow(scroll_viewer_, 0);
    root_panel_.Children().Append(scroll_viewer_);

    setup_add_button();
    controls::Grid::SetRow(add_button_, 1);
    root_panel_.Children().Append(add_button_);


    LOG_I("sidebar", "Initialized (width=%.0f, dpi=%.2f)", sidebar_width(), dpi_scale_);
}

// ─── root ───

winui::FrameworkElement TabSidebar::root() const {
    return root_panel_.as<winui::FrameworkElement>();
}

// ─── request_new_tab / request_close_active ───

void TabSidebar::request_new_tab() {
    if (new_tab_fn_) new_tab_fn_(new_tab_ctx_);
}

void TabSidebar::request_close_active() {
    if (!mgr_) return;
    mgr_->close_session(mgr_->active_id());
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

// ─── find_index ───

std::optional<size_t> TabSidebar::find_index(SessionId id) const {
    auto it = std::ranges::find_if(items_,
        [id](const auto& d) { return d.session_id == id; });
    if (it == items_.end()) return std::nullopt;
    return static_cast<size_t>(std::distance(items_.begin(), it));
}

// ─── append_tab / remove_tab_at (triple-sync atomic) ───

void TabSidebar::append_tab(TabItemData data, TabItemUIRefs refs) {
    auto root = refs.root;
    items_.push_back(std::move(data));
    item_refs_.push_back(std::move(refs));
    tabs_panel_.Children().Append(root);
    assert(items_.size() == item_refs_.size());
}

void TabSidebar::remove_tab_at(size_t idx) {
    assert(idx < items_.size());
    tabs_panel_.Children().RemoveAt(static_cast<uint32_t>(idx));
    item_refs_.erase(item_refs_.begin() + static_cast<ptrdiff_t>(idx));
    items_.erase(items_.begin() + static_cast<ptrdiff_t>(idx));
    assert(items_.size() == item_refs_.size());
}

// ─── apply_active_visual / apply_inactive_visual ───

void TabSidebar::apply_active_visual(TabItemUIRefs& refs) {
    auto accent = winrt::Windows::UI::Colors::CornflowerBlue();
    refs.accent_bar.Background(winui::Media::SolidColorBrush{accent});
    refs.accent_bar.Visibility(winui::Visibility::Visible);
    refs.root.Background(winui::Media::SolidColorBrush{
        winrt::Windows::UI::ColorHelper::FromArgb(0x1A, 0xFF, 0xFF, 0xFF)});
    refs.title_block.FontWeight(winrt::Windows::UI::Text::FontWeights::SemiBold());
}

void TabSidebar::apply_inactive_visual(TabItemUIRefs& refs) {
    refs.accent_bar.Visibility(winui::Visibility::Collapsed);
    // Transparent (not nullptr) — hit-test visible for wheel/hover events
    refs.root.Background(winui::Media::SolidColorBrush{
        winrt::Windows::UI::Colors::Transparent()});
    refs.title_block.FontWeight(winrt::Windows::UI::Text::FontWeights::Normal());
}

// ─── create_text_panel ───

controls::StackPanel TabSidebar::create_text_panel(
        const TabItemData& data, TabItemUIRefs& refs) {
    auto panel = controls::StackPanel();
    panel.Margin({8, 4, 0, 4});

    refs.title_block = controls::TextBlock();
    refs.title_block.Text(winrt::hstring(data.title));
    refs.title_block.FontSize(13);
    refs.title_block.TextTrimming(winui::TextTrimming::CharacterEllipsis);
    panel.Children().Append(refs.title_block);

    if (!data.cwd_display.empty()) {
        refs.cwd_block = controls::TextBlock();
        refs.cwd_block.Text(winrt::hstring(data.cwd_display));
        refs.cwd_block.FontSize(11);
        refs.cwd_block.Opacity(kCwdOpacity);
        refs.cwd_block.TextTrimming(winui::TextTrimming::CharacterEllipsis);
        panel.Children().Append(refs.cwd_block);
    }

    return panel;
}

// ─── create_close_button ───

controls::Button TabSidebar::create_close_button(SessionId sid) {
    auto btn = controls::Button();
    btn.Content(winrt::box_value(L"\u00D7"));
    btn.Padding({4, 2, 4, 2});
    btn.Margin({0, 0, 4, 0});
    btn.VerticalAlignment(winui::VerticalAlignment::Center);
    btn.Click([this, sid](auto&&, auto&&) {
        if (mgr_) mgr_->close_session(sid);
    });
    return btn;
}

// ─── create_tab_item_ui ───

TabItemUIRefs TabSidebar::create_tab_item_ui(const TabItemData& data) {
    TabItemUIRefs refs;
    refs.root = controls::Grid();
    refs.root.Tag(winrt::box_value(static_cast<int32_t>(data.session_id)));
    refs.root.MinHeight(kTabItemMinHeight);

    auto col0 = controls::ColumnDefinition();
    col0.Width(winui::GridLengthHelper::Auto());
    auto col1 = controls::ColumnDefinition();
    col1.Width(winui::GridLength{1, winui::GridUnitType::Star});
    auto col2 = controls::ColumnDefinition();
    col2.Width(winui::GridLengthHelper::Auto());
    refs.root.ColumnDefinitions().Append(col0);
    refs.root.ColumnDefinitions().Append(col1);
    refs.root.ColumnDefinitions().Append(col2);

    refs.accent_bar = controls::Border();
    refs.accent_bar.Width(kAccentBarWidth);
    refs.accent_bar.CornerRadius({2, 0, 0, 2});
    refs.accent_bar.VerticalAlignment(winui::VerticalAlignment::Stretch);
    refs.accent_bar.Visibility(winui::Visibility::Collapsed);
    controls::Grid::SetColumn(refs.accent_bar, 0);
    refs.root.Children().Append(refs.accent_bar);

    refs.text_panel = create_text_panel(data, refs);
    controls::Grid::SetColumn(refs.text_panel, 1);
    refs.root.Children().Append(refs.text_panel);

    auto close = create_close_button(data.session_id);
    controls::Grid::SetColumn(close, 2);
    refs.root.Children().Append(close);

    if (data.is_active) apply_active_visual(refs);

    attach_drag_handlers(refs.root, data.session_id);
    attach_hover_handlers(refs.root);

    return refs;
}

// ─── attach_hover_handlers ───

void TabSidebar::attach_hover_handlers(controls::Grid const& grid) {
    grid.PointerEntered([this](auto& sender, auto&&) {
        auto elem = sender.as<winui::FrameworkElement>();
        auto sid = static_cast<SessionId>(winrt::unbox_value<int32_t>(elem.Tag()));
        auto idx = find_index(sid);
        if (!idx || items_[*idx].is_active || drag_.active) return;
        sender.as<controls::Grid>().Background(winui::Media::SolidColorBrush{
            winrt::Windows::UI::ColorHelper::FromArgb(0x0F, 0xFF, 0xFF, 0xFF)});
    });

    grid.PointerExited([this](auto& sender, auto&&) {
        auto elem = sender.as<winui::FrameworkElement>();
        auto sid = static_cast<SessionId>(winrt::unbox_value<int32_t>(elem.Tag()));
        auto idx = find_index(sid);
        if (!idx || items_[*idx].is_active) return;
        // Transparent (not nullptr) — preserve hit-test for wheel scroll
        sender.as<controls::Grid>().Background(winui::Media::SolidColorBrush{
            winrt::Windows::UI::Colors::Transparent()});
    });
}

// ─── attach_drag_handlers ───

void TabSidebar::attach_drag_handlers(controls::Grid const& grid, SessionId sid) {
    winui::Media::TranslateTransform transform;
    grid.RenderTransform(transform);

    grid.PointerPressed([this, sid](auto& sender, winui::Input::PointerRoutedEventArgs const& e) {
        auto it = find_index(sid);
        if (!it || items_.size() < 2) return;
        auto pos = e.GetCurrentPoint(tabs_panel_).Position();
        drag_ = {true, false, static_cast<int32_t>(*it), -1, pos.Y};
        // Do NOT CapturePointer or Handled here — deferred to PointerMoved
        // after drag threshold. Immediate capture blocks ScrollViewer wheel.
    });

    grid.PointerMoved([this](auto& sender, winui::Input::PointerRoutedEventArgs const& e) {
        if (!drag_.pending && !drag_.active) return;
        auto pos = e.GetCurrentPoint(tabs_panel_).Position();
        float delta_y = pos.Y - drag_.start_y;

        if (!drag_.active) {
            if (std::abs(delta_y) < kDragThreshold) return;
            drag_.active = true;
            // Capture only after threshold — allows ScrollViewer wheel before drag
            auto elem = sender.as<winui::UIElement>();
            elem.CapturePointer(e.Pointer());
            elem.Opacity(kDragLiftOpacity);
            winui::Media::ScaleTransform scale;
            scale.ScaleX(kDragLiftScale);
            scale.ScaleY(kDragLiftScale);
            auto group = winui::Media::TransformGroup();
            auto translate = elem.RenderTransform().as<winui::Media::TranslateTransform>();
            group.Children().Append(translate);
            group.Children().Append(scale);
            elem.RenderTransform(group);
        }

        auto elem = sender.as<winui::UIElement>();
        auto tg = elem.RenderTransform().try_as<winui::Media::TransformGroup>();
        if (tg && tg.Children().Size() > 0) {
            tg.Children().GetAt(0).as<winui::Media::TranslateTransform>().Y(
                static_cast<double>(delta_y));
        }

        drag_.insert_idx = calc_insert_index(pos.Y);

        for (size_t i = 0; i < item_refs_.size(); ++i) {
            if (static_cast<int32_t>(i) == drag_.source_idx) continue;
            bool is_target = (static_cast<int32_t>(i) == drag_.insert_idx);
            item_refs_[i].root.Opacity(is_target ? 0.6 : 1.0);
        }
        e.Handled(true);
    });

    grid.PointerReleased([this](auto& sender, winui::Input::PointerRoutedEventArgs const& e) {
        if (drag_.active) {
            try { sender.as<winui::UIElement>().ReleasePointerCapture(e.Pointer()); }
            catch (...) {}  // may not be captured if threshold not reached
        }
        bool was_drag = drag_.active;
        bool should_reorder = was_drag && drag_.insert_idx >= 0
                              && drag_.insert_idx != drag_.source_idx;
        reset_drag_visuals();
        if (should_reorder) {
            apply_drag_reorder();
        } else if (!was_drag) {
            auto tag = sender.as<winui::FrameworkElement>().Tag();
            auto sid = static_cast<SessionId>(winrt::unbox_value<int32_t>(tag));
            if (mgr_) mgr_->activate(sid);
        }
        drag_ = {};
        e.Handled(true);
    });

    grid.PointerCanceled([this](auto& sender, winui::Input::PointerRoutedEventArgs const& e) {
        if (drag_.active) {
            try { sender.as<winui::UIElement>().ReleasePointerCapture(e.Pointer()); }
            catch (...) {}
        }
        reset_drag_visuals();
        drag_ = {};
    });

}

// ─── reset_drag_visuals ───

void TabSidebar::reset_drag_visuals() {
    for (auto& ref : item_refs_) {
        ref.root.Opacity(1.0);
        winui::Media::TranslateTransform clean;
        ref.root.RenderTransform(clean);
    }
}

// ─── calc_insert_index ───

int32_t TabSidebar::calc_insert_index(float list_y) const {
    if (items_.empty()) return 0;
    float item_h = static_cast<float>(tabs_panel_.ActualHeight()) /
                   static_cast<float>(items_.size());
    if (item_h < 1.0f) item_h = static_cast<float>(kTabItemMinHeight);
    int32_t idx = static_cast<int32_t>(list_y / item_h);
    return std::clamp(idx, 0, static_cast<int32_t>(items_.size()) - 1);
}

// ─── apply_drag_reorder ───

void TabSidebar::apply_drag_reorder() {
    if (!mgr_ || drag_.source_idx < 0 || drag_.insert_idx < 0) return;
    if (drag_.source_idx == drag_.insert_idx) return;

    auto src = static_cast<size_t>(drag_.source_idx);
    auto dst = static_cast<size_t>(drag_.insert_idx);
    if (src >= items_.size() || dst >= items_.size()) return;

    auto moving_data = std::move(items_[src]);
    auto moving_refs = std::move(item_refs_[src]);
    items_.erase(items_.begin() + static_cast<ptrdiff_t>(src));
    item_refs_.erase(item_refs_.begin() + static_cast<ptrdiff_t>(src));
    items_.insert(items_.begin() + static_cast<ptrdiff_t>(dst), std::move(moving_data));
    item_refs_.insert(item_refs_.begin() + static_cast<ptrdiff_t>(dst), std::move(moving_refs));

    mgr_->move_session(src, dst);
    rebuild_list();
    LOG_I("sidebar", "Drag reorder: %zu -> %zu (%zu items)", src, dst, items_.size());
}

// ─── rebuild_list ───

void TabSidebar::rebuild_list() {
    tabs_panel_.Children().Clear();
    for (auto& ref : item_refs_) {
        tabs_panel_.Children().Append(ref.root);
    }
}

// ─── on_session_created ───

void TabSidebar::on_session_created(SessionId id) {
    if (!mgr_) return;
    auto* sess = mgr_->get(id);
    if (!sess) return;

    TabItemData data{};
    data.session_id = id;
    data.title = sess->title.empty()
        ? L"Session " + std::to_wstring(id) : sess->title;
    data.is_active = (mgr_->active_id() == id);

    auto refs = create_tab_item_ui(data);
    append_tab(std::move(data), std::move(refs));

    if (auto& last = item_refs_.back(); last.root) {
        last.root.StartBringIntoView();
    }

    LOG_I("sidebar", "Tab added: session %u (total: %zu)", id, items_.size());
}

// ─── on_session_closed ───

void TabSidebar::on_session_closed(SessionId id) {
    auto idx_opt = find_index(id);
    if (!idx_opt) return;

    if (drag_.active || drag_.pending) {
        reset_drag_visuals();
        drag_ = {};
    }

    remove_tab_at(*idx_opt);
    LOG_I("sidebar", "Tab removed: session %u (remaining: %zu)", id, items_.size());
}

// ─── on_session_activated ───

void TabSidebar::on_session_activated(SessionId id) {
    for (size_t i = 0; i < items_.size(); ++i) {
        bool was = items_[i].is_active;
        bool now = (items_[i].session_id == id);
        if (was && !now) {
            items_[i].is_active = false;
            apply_inactive_visual(item_refs_[i]);
        } else if (!was && now) {
            items_[i].is_active = true;
            apply_active_visual(item_refs_[i]);
        }
    }
}

// ─── on_title_changed ───

void TabSidebar::on_title_changed(SessionId id, const std::wstring& title) {
    auto idx_opt = find_index(id);
    if (!idx_opt) {
        LOG_W("sidebar", "on_title_changed: session %u not found", id);
        return;
    }
    items_[*idx_opt].title = title;
    item_refs_[*idx_opt].title_block.Text(winrt::hstring(title));
    LOG_I("sidebar", "Title updated: session %u idx=%zu", id, *idx_opt);
}

// ─── on_cwd_changed ───

void TabSidebar::on_cwd_changed(SessionId id, const std::wstring& cwd) {
    auto idx_opt = find_index(id);
    if (!idx_opt) return;
    auto idx = *idx_opt;
    items_[idx].cwd_display = ShortenCwd(cwd);
    auto& refs = item_refs_[idx];
    if (!refs.cwd_block) {
        refs.cwd_block = controls::TextBlock();
        refs.cwd_block.FontSize(11);
        refs.cwd_block.Opacity(kCwdOpacity);
        refs.cwd_block.TextTrimming(winui::TextTrimming::CharacterEllipsis);
        refs.text_panel.Children().Append(refs.cwd_block);
    }
    refs.cwd_block.Text(winrt::hstring(items_[idx].cwd_display));
}

} // namespace ghostwin
