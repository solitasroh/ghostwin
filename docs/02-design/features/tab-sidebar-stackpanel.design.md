# tab-sidebar-stackpanel Design Document

> **Summary**: ListView → StackPanel 전환. Passive View + TabItemUIRefs in-place 갱신. Triple-sync atomic 헬퍼. 자체 active/hover/pressed visual.
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-04
> **Status**: Draft (v1.0)
> **Planning Doc**: [tab-sidebar-stackpanel.plan.md](../../01-plan/features/tab-sidebar-stackpanel.plan.md)
> **Dependency**: Phase 5-B tab-sidebar 완료

---

## Executive Summary

| Perspective | Content |
|------------|---------|
| **Problem** | ListView 5대 기능 0개 사용, 6곳 SelectionGuard, SetAt selection 파괴 |
| **Solution** | StackPanel + Passive View + TabItemUIRefs + Triple-sync 헬퍼 + WinUI3 ThemeResource visual |
| **Function/UX** | active/hover/pressed 시각 피드백, TextTrimming, DPI-safe 드래그, 코드 복잡도 ~40줄 순감소 |
| **Core Value** | 우회 제로, Single Source of Truth, Passive View 3계층 |

---

## 1. Header Definition (tab_sidebar.h)

### 1.1 삭제 대상

```cpp
// 전부 삭제
#include <winrt/Windows.Foundation.Collections.h>     // IObservableVector 불필요
#include <winrt/Windows.UI.Xaml.Interop.h>            // ListView 전용

controls::ListView list_view_{nullptr};
winrt::Windows::Foundation::Collections::IObservableVector<
    winrt::Windows::Foundation::IInspectable> items_source_{nullptr};
bool updating_selection_ = false;
struct SelectionGuard { bool& flag; ... };

void setup_listview();
template <typename Mutator> void update_item(SessionId, Mutator&&);
void sync_items_from_listview();  // 이미 삭제됨
```

### 1.2 신규/변경 대상

```cpp
// ─── 신규 구조체 (cpp.md: Rule of Zero, WinRT value-type) ───

/// UI element references for in-place update
struct TabItemUIRefs {
    controls::Grid root{nullptr};
    controls::Border accent_bar{nullptr};
    controls::TextBlock title_block{nullptr};
    controls::TextBlock cwd_block{nullptr};       // nullable
    controls::StackPanel text_panel{nullptr};
};

// ─── 신규 constexpr (cpp.md: constexpr over #define) ───

static constexpr double kAccentBarWidth = 3.0;
static constexpr double kTabItemMinHeight = 40.0;
static constexpr double kCwdOpacity = 0.6;
static constexpr double kDragLiftOpacity = 0.85;
static constexpr double kDragLiftScale = 1.03;

// ─── 멤버 변경 ───

// 삭제: list_view_, items_source_, updating_selection_, SelectionGuard
// 추가:
controls::ScrollViewer scroll_viewer_{nullptr};
controls::StackPanel tabs_panel_{nullptr};
std::vector<TabItemUIRefs> item_refs_;     // items_와 1:1 병렬

// ─── Private methods 변경 ───

// 삭제: setup_listview, update_item 템플릿
// 추가:
void setup_tabs_panel();                                    // ScrollViewer + StackPanel 초기화
TabItemUIRefs create_tab_item_ui(const TabItemData& data);  // 반환 타입 변경
void apply_active_visual(TabItemUIRefs& refs);
void apply_inactive_visual(TabItemUIRefs& refs);
void append_tab(TabItemData data, TabItemUIRefs refs);      // triple-sync atomic
void remove_tab_at(size_t idx);                             // triple-sync atomic
[[nodiscard]] std::optional<size_t> find_index(SessionId id) const;
void attach_hover_handlers(controls::Grid const& grid, size_t idx);

// 유지 (시그니처 변경 없음):
void setup_add_button();
void attach_drag_handlers(controls::Grid const& grid, SessionId sid);
void apply_drag_reorder();
void reset_drag_visuals();
int32_t calc_insert_index(float list_y) const;
void rebuild_list();
double sidebar_width() const;
```

### 1.3 전체 클래스 개요

```cpp
class TabSidebar {
public:
    // ─── Public API (7 — 변경 없음) ───
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
    // on_* handlers (5개, 시그니처 변경 없음)

    // Data
    SessionManager* mgr_ = nullptr;
    void(*new_tab_fn_)(void*) = nullptr;
    void* new_tab_ctx_ = nullptr;
    std::vector<TabItemData> items_;           // Single Source of Truth
    std::vector<TabItemUIRefs> item_refs_;     // 1:1 view cache

    // UI elements
    controls::StackPanel root_panel_{nullptr};
    controls::ScrollViewer scroll_viewer_{nullptr};
    controls::StackPanel tabs_panel_{nullptr};
    controls::Button add_button_{nullptr};

    // Drag state (변경 없음)
    DragState drag_;
    float dpi_scale_ = 1.0f;
    bool visible_ = true;

    // constexpr
    static constexpr double kBaseWidth = 220.0;
    static constexpr float kDragThreshold = 5.0f;
    static constexpr double kAccentBarWidth = 3.0;
    static constexpr double kTabItemMinHeight = 40.0;
    static constexpr double kCwdOpacity = 0.6;
    static constexpr double kDragLiftOpacity = 0.85;
    static constexpr double kDragLiftScale = 1.03;

    // Internal helpers
    void setup_tabs_panel();
    void setup_add_button();
    TabItemUIRefs create_tab_item_ui(const TabItemData& data);
    void apply_active_visual(TabItemUIRefs& refs);
    void apply_inactive_visual(TabItemUIRefs& refs);
    void append_tab(TabItemData data, TabItemUIRefs refs);
    void remove_tab_at(size_t idx);
    [[nodiscard]] std::optional<size_t> find_index(SessionId id) const;
    void attach_drag_handlers(controls::Grid const& grid, SessionId sid);
    void attach_hover_handlers(controls::Grid const& grid, size_t idx);
    void apply_drag_reorder();
    void reset_drag_visuals();
    [[nodiscard]] int32_t calc_insert_index(float list_y) const;
    void rebuild_list();
    [[nodiscard]] double sidebar_width() const;
};
```

---

## 2. Implementation Details

### 2.1 setup_tabs_panel() (≤ 20 lines)

```cpp
void TabSidebar::setup_tabs_panel() {
    tabs_panel_ = controls::StackPanel();
    tabs_panel_.Orientation(controls::Orientation::Vertical);

    scroll_viewer_ = controls::ScrollViewer();
    scroll_viewer_.Content(tabs_panel_);
    scroll_viewer_.VerticalScrollBarVisibility(
        controls::ScrollBarVisibility::Auto);
    scroll_viewer_.HorizontalScrollBarVisibility(
        controls::ScrollBarVisibility::Disabled);
    scroll_viewer_.VerticalAlignment(winui::VerticalAlignment::Stretch);
}
```

### 2.2 create_tab_item_ui() → TabItemUIRefs (≤ 35 lines)

```cpp
TabItemUIRefs TabSidebar::create_tab_item_ui(const TabItemData& data) {
    TabItemUIRefs refs;

    refs.root = controls::Grid();
    refs.root.Tag(winrt::box_value(static_cast<int32_t>(data.session_id)));
    refs.root.MinHeight(kTabItemMinHeight);

    // 3-column: accent bar (Auto) | text (Star) | close (Auto)
    auto col0 = controls::ColumnDefinition();
    col0.Width(winui::GridLengthHelper::Auto());
    auto col1 = controls::ColumnDefinition();
    col1.Width(winui::GridLength{1, winui::GridUnitType::Star});
    auto col2 = controls::ColumnDefinition();
    col2.Width(winui::GridLengthHelper::Auto());
    refs.root.ColumnDefinitions().Append(col0);
    refs.root.ColumnDefinitions().Append(col1);
    refs.root.ColumnDefinitions().Append(col2);

    // Col 0: accent bar
    refs.accent_bar = controls::Border();
    refs.accent_bar.Width(kAccentBarWidth);
    refs.accent_bar.CornerRadius({2, 0, 0, 2});
    refs.accent_bar.VerticalAlignment(winui::VerticalAlignment::Stretch);
    refs.accent_bar.Visibility(winui::Visibility::Collapsed);
    controls::Grid::SetColumn(refs.accent_bar, 0);
    refs.root.Children().Append(refs.accent_bar);

    // Col 1: text panel
    refs.text_panel = create_text_panel(data, refs);
    controls::Grid::SetColumn(refs.text_panel, 1);
    refs.root.Children().Append(refs.text_panel);

    // Col 2: close button
    auto close = create_close_button(data.session_id);
    controls::Grid::SetColumn(close, 2);
    refs.root.Children().Append(close);

    // Active visual
    if (data.is_active) apply_active_visual(refs);

    // Drag + hover handlers
    attach_drag_handlers(refs.root, data.session_id);
    attach_hover_handlers(refs.root, items_.size());

    return refs;
}
```

> Note: `create_text_panel` 시그니처 변경 — `TabItemUIRefs& refs`를 받아 `title_block`/`cwd_block` 참조를 채움.

### 2.3 create_text_panel() (≤ 25 lines)

```cpp
controls::StackPanel TabSidebar::create_text_panel(
        const TabItemData& data, TabItemUIRefs& refs) {
    auto panel = controls::StackPanel();
    panel.Margin({8, 4, 0, 4});

    refs.title_block = controls::TextBlock();
    refs.title_block.Text(winrt::hstring(data.title));
    refs.title_block.FontSize(13);
    refs.title_block.TextTrimming(winui::TextTrimming::CharacterEllipsis);
    if (data.is_active) {
        refs.title_block.FontWeight(winrt::Windows::UI::Text::FontWeights::SemiBold());
    }
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
```

### 2.4 apply_active_visual / apply_inactive_visual (≤ 10 lines each)

```cpp
void TabSidebar::apply_active_visual(TabItemUIRefs& refs) {
    // WinUI3 ThemeResource — Dark/Light 자동 전환
    auto res = winui::Application::Current().Resources();
    auto brush = res.Lookup(winrt::box_value(L"SubtleFillColorSecondaryBrush"))
                    .as<winui::Media::Brush>();
    refs.root.Background(brush);
    refs.accent_bar.Background(/* SystemAccentColor brush */);
    refs.accent_bar.Visibility(winui::Visibility::Visible);
    refs.title_block.FontWeight(winrt::Windows::UI::Text::FontWeights::SemiBold());
}

void TabSidebar::apply_inactive_visual(TabItemUIRefs& refs) {
    refs.root.Background(nullptr);
    refs.accent_bar.Visibility(winui::Visibility::Collapsed);
    refs.title_block.FontWeight(winrt::Windows::UI::Text::FontWeights::Normal());
}
```

### 2.5 Triple-sync Atomic Helpers (≤ 10 lines each)

```cpp
void TabSidebar::append_tab(TabItemData data, TabItemUIRefs refs) {
    auto root = refs.root;  // copy before move
    items_.push_back(std::move(data));
    item_refs_.push_back(std::move(refs));
    tabs_panel_.Children().Append(root);
    assert(items_.size() == item_refs_.size());
}

void TabSidebar::remove_tab_at(size_t idx) {
    assert(idx < items_.size());
    // UI → refs → data 순서 (UI 먼저 제거, 실패 시 데이터 무변경)
    tabs_panel_.Children().RemoveAt(static_cast<uint32_t>(idx));
    item_refs_.erase(item_refs_.begin() + static_cast<ptrdiff_t>(idx));
    items_.erase(items_.begin() + static_cast<ptrdiff_t>(idx));
    assert(items_.size() == item_refs_.size());
}

std::optional<size_t> TabSidebar::find_index(SessionId id) const {
    auto it = std::ranges::find_if(items_,
        [id](const auto& d) { return d.session_id == id; });
    if (it == items_.end()) return std::nullopt;
    return static_cast<size_t>(std::distance(items_.begin(), it));
}
```

### 2.6 on_session_created (≤ 20 lines)

```cpp
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

    // Auto-scroll to new tab
    if (auto& last = item_refs_.back(); last.root) {
        last.root.StartBringIntoView();
    }
}
```

### 2.7 on_session_closed (≤ 15 lines)

```cpp
void TabSidebar::on_session_closed(SessionId id) {
    auto idx_opt = find_index(id);
    if (!idx_opt) return;

    // Drag 중이면 강제 취소
    if (drag_.active || drag_.pending) {
        reset_drag_visuals();
        drag_ = {};
    }

    remove_tab_at(*idx_opt);
}
```

### 2.8 on_session_activated (≤ 20 lines, rebuild 없음)

```cpp
void TabSidebar::on_session_activated(SessionId id) {
    // O(n) scan, O(2) visual change
    for (size_t i = 0; i < items_.size(); ++i) {
        bool was_active = items_[i].is_active;
        bool now_active = (items_[i].session_id == id);
        if (was_active && !now_active) {
            items_[i].is_active = false;
            apply_inactive_visual(item_refs_[i]);
        } else if (!was_active && now_active) {
            items_[i].is_active = true;
            apply_active_visual(item_refs_[i]);
        }
    }
}
```

### 2.9 on_title_changed / on_cwd_changed (in-place, ≤ 15 lines each)

```cpp
void TabSidebar::on_title_changed(SessionId id, const std::wstring& title) {
    auto idx_opt = find_index(id);
    if (!idx_opt) return;
    items_[*idx_opt].title = title;
    item_refs_[*idx_opt].title_block.Text(winrt::hstring(title));
}

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
```

### 2.10 rebuild_list (드래그 전용, ≤ 15 lines)

```cpp
void TabSidebar::rebuild_list() {
    tabs_panel_.Children().Clear();
    for (auto& ref : item_refs_) {
        tabs_panel_.Children().Append(ref.root);
    }
    // item_refs_의 기존 UI 요소를 재활용 — create_tab_item_ui 재호출 불필요
    // 이벤트 핸들러도 그대로 유지 (Grid 객체 불변)
}
```

### 2.11 attach_hover_handlers (≤ 15 lines)

```cpp
void TabSidebar::attach_hover_handlers(controls::Grid const& grid, size_t idx) {
    grid.PointerEntered([this](auto& sender, auto&&) {
        auto elem = sender.as<winui::FrameworkElement>();
        auto sid = static_cast<SessionId>(winrt::unbox_value<int32_t>(elem.Tag()));
        auto idx_opt = find_index(sid);
        if (!idx_opt || items_[*idx_opt].is_active) return;  // active는 이미 highlight
        auto res = winui::Application::Current().Resources();
        auto brush = res.Lookup(winrt::box_value(L"SubtleFillColorSecondaryBrush"))
                        .as<winui::Media::Brush>();
        sender.as<controls::Grid>().Background(brush);
    });

    grid.PointerExited([this](auto& sender, auto&&) {
        auto elem = sender.as<winui::FrameworkElement>();
        auto sid = static_cast<SessionId>(winrt::unbox_value<int32_t>(elem.Tag()));
        auto idx_opt = find_index(sid);
        if (!idx_opt || items_[*idx_opt].is_active) return;
        sender.as<controls::Grid>().Background(nullptr);
    });
}
```

### 2.12 apply_drag_reorder (≤ 20 lines)

```cpp
void TabSidebar::apply_drag_reorder() {
    if (!mgr_ || drag_.source_idx < 0 || drag_.insert_idx < 0) return;
    if (drag_.source_idx == drag_.insert_idx) return;

    auto src = static_cast<size_t>(drag_.source_idx);
    auto dst = static_cast<size_t>(drag_.insert_idx);
    if (src >= items_.size() || dst >= items_.size()) return;

    // items_ + item_refs_ 동시 이동
    auto moving_data = std::move(items_[src]);
    auto moving_refs = std::move(item_refs_[src]);
    items_.erase(items_.begin() + static_cast<ptrdiff_t>(src));
    item_refs_.erase(item_refs_.begin() + static_cast<ptrdiff_t>(src));
    items_.insert(items_.begin() + static_cast<ptrdiff_t>(dst), std::move(moving_data));
    item_refs_.insert(item_refs_.begin() + static_cast<ptrdiff_t>(dst), std::move(moving_refs));

    mgr_->move_session(src, dst);
    rebuild_list();  // Children 순서 재구성 (기존 UI 요소 재활용)
}
```

---

## 3. File Changes Summary

### 3.1 수정 파일

| File | Changes |
|------|---------|
| `src/ui/tab_sidebar.h` | TabItemUIRefs, constexpr, item_refs_, tabs_panel_, scroll_viewer_ 추가. ListView/IObservableVector/SelectionGuard 삭제 |
| `src/ui/tab_sidebar.cpp` | 전체 재구현 (~300줄 → ~280줄 예상, 순감소 ~20줄) |

### 3.2 변경 없는 파일

| File | 이유 |
|------|------|
| `src/app/winui_app.h` | TabSidebar public API 불변 |
| `src/app/winui_app.cpp` | friend on_* 호출 불변, root() 반환 타입 불변 |
| `CMakeLists.txt` | 파일 추가/삭제 없음 |
| `src/ui/titlebar_manager.*` | sidebar_width_fn 경로 불변 |

---

## 4. Include Changes

```cpp
// 삭제 (ListView 전용)
// #include <winrt/Windows.Foundation.Collections.h>  ← IObservableVector
// #include <winrt/Windows.UI.Xaml.Interop.h>         ← ListView interop

// 유지/추가 확인 필요
#include <winrt/Microsoft.UI.Xaml.Controls.h>  // ScrollViewer, StackPanel, Border 포함
#include <optional>                             // std::optional for find_index
```

---

## 5. Test Cases (Plan 섹션 9 참조)

| TC | 시나리오 | 검증 |
|----|---------|------|
| T1 | 앱 시작 | 첫 탭 accent bar + Background + SemiBold |
| T2 | + 클릭 | 새 탭 active, 이전 탭 inactive |
| T3 | 탭 클릭 | active 전환, 터미널 전환 |
| T4 | title 변경 | active visual 유지, 텍스트 갱신 |
| T5 | cwd 변경 | cwd 표시, active visual 유지 |
| T6 | 드래그 | DPI 100%/125%/150% 정상 |
| T7 | 탭 닫기 | 다음 탭 활성, 정합성 |
| T8 | 마지막 탭 | 앱 종료 |
| T9 | 키보드 | Ctrl+Tab/1~9 정상 |
| T10 | sidebar 토글 | Ctrl+Shift+B, titlebar 재계산 |
| T11 | 10개+ 탭 | ScrollViewer + BringIntoView |
| T12 | hover | 배경 변경/복원 |
| T13 | 긴 제목 | TextTrimming ellipsis |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.0 | 2026-04-04 | Plan v1.1 기반 초안. 코드 레벨 클래스/함수 시그니처, Visual Spec 구현, Triple-sync, cpp.md 준수 | 노수장 |
