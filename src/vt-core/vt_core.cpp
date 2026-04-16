/// @file vt_core.cpp
/// VtCore C++ wrapper over vt_bridge (pure C).
/// No ghostty headers are included here.

#include "vt_core.h"
#include "vt_bridge.h"

#include <algorithm>
#include <vector>

namespace ghostwin {

struct VtCore::Impl {
    // BC-11: typed opaque handles (was void*) so passing a render_state where a
    // terminal is expected fails to compile in C++. See vt_bridge.h.
    VtTerminal    terminal     = nullptr;
    VtRenderState render_state = nullptr;
    uint16_t cols = 0;
    uint16_t rows = 0;

    // Reusable iterators (avoid per-frame allocation)
    VtRowIterator  row_iter = nullptr;
    VtCellIterator cell_iter = nullptr;

    ~Impl() {
        if (cell_iter) { vt_bridge_cell_iterator_free(cell_iter); cell_iter = nullptr; }
        if (row_iter)  { vt_bridge_row_iterator_free(row_iter);  row_iter = nullptr; }
        if (render_state) { vt_bridge_render_state_free(render_state); render_state = nullptr; }
        if (terminal) { vt_bridge_terminal_free(terminal); terminal = nullptr; }
    }
};

VtCore::VtCore() : impl_(std::make_unique<Impl>()) {}
VtCore::~VtCore() = default;
VtCore::VtCore(VtCore&&) noexcept = default;
VtCore& VtCore::operator=(VtCore&&) noexcept = default;

std::unique_ptr<VtCore> VtCore::create(uint16_t cols, uint16_t rows, size_t max_scrollback) {
    auto vt = std::unique_ptr<VtCore>(new VtCore());

    vt->impl_->terminal = vt_bridge_terminal_new(cols, rows, max_scrollback);
    if (!vt->impl_->terminal) return nullptr;

    vt->impl_->render_state = vt_bridge_render_state_new();
    if (!vt->impl_->render_state) return nullptr;

    // Pre-allocate iterators for Phase 3
    vt->impl_->row_iter = vt_bridge_row_iterator_new();
    vt->impl_->cell_iter = vt_bridge_cell_iterator_new();

    vt->impl_->cols = cols;
    vt->impl_->rows = rows;
    return vt;
}

void VtCore::write(std::span<const uint8_t> data) {
    if (impl_->terminal && !data.empty()) {
        vt_bridge_write(impl_->terminal, data.data(), data.size());
    }
}

bool VtCore::resize(uint16_t cols, uint16_t rows) {
    if (!impl_->terminal) return false;
    int rc = vt_bridge_resize(impl_->terminal, cols, rows);
    if (rc == VT_OK) {
        impl_->cols = cols;
        impl_->rows = rows;
        return true;
    }
    return false;
}

uint16_t VtCore::cols() const { return impl_->cols; }
uint16_t VtCore::rows() const { return impl_->rows; }

// ─── Phase 3 ───

static uint32_t pack_rgba(VtColor c) {
    return c.r | (c.g << 8) | (c.b << 16) | (c.a << 24);
}

void VtCore::for_each_row(RowCallback callback) {
    if (!impl_->render_state || !impl_->row_iter || !impl_->cell_iter) return;

    int rc = vt_bridge_row_iterator_init(impl_->row_iter, impl_->render_state);
    if (rc != VT_OK) return;

    // Temporary buffer for cell data (reused across rows)
    std::vector<CellData> cells_buf;
    cells_buf.resize(impl_->cols);

    uint16_t row_index = 0;
    while (vt_bridge_row_iterator_next(impl_->row_iter)) {
        bool dirty = vt_bridge_row_is_dirty(impl_->row_iter);

        // Initialize cell iterator for this row
        rc = vt_bridge_cell_iterator_init(impl_->cell_iter, impl_->row_iter);
        if (rc != VT_OK) {
            row_index++;
            continue;
        }

        uint16_t col = 0;
        while (vt_bridge_cell_iterator_next(impl_->cell_iter) && col < impl_->cols) {
            CellData& cd = cells_buf[col];

            cd.cp_count = static_cast<uint8_t>(
                std::min(vt_bridge_cell_grapheme_count(impl_->cell_iter), 4u));

            if (cd.cp_count > 0) {
                vt_bridge_cell_graphemes(impl_->cell_iter, cd.codepoints, 4);
            } else {
                cd.codepoints[0] = 0;
            }

            cd.style_flags = vt_bridge_cell_style_flags(impl_->cell_iter);

            cd.fg_packed = pack_rgba(vt_bridge_cell_fg_color(impl_->cell_iter, impl_->render_state));
            cd.bg_packed = pack_rgba(vt_bridge_cell_bg_color(impl_->cell_iter, impl_->render_state));

            std::memset(cd._pad, 0, sizeof(cd._pad));
            col++;
        }

        // Zero remaining columns if cell iterator ran out early
        for (uint16_t c = col; c < impl_->cols; c++) {
            cells_buf[c] = CellData{};
        }

        callback(row_index, dirty, std::span<const CellData>(cells_buf.data(), impl_->cols));

        // Reset row-level dirty after reading
        if (dirty) {
            vt_bridge_row_set_clean(impl_->row_iter);
        }
        row_index++;
    }
}

CursorInfo VtCore::cursor_info() const {
    CursorInfo info{};
    if (!impl_->render_state) return info;

    VtCursorInfo raw = vt_bridge_get_cursor(impl_->render_state);
    info.x = raw.x;
    info.y = raw.y;
    info.style = static_cast<CursorStyle>(raw.style);
    info.visible = raw.visible;
    info.blink = raw.blink;
    info.in_viewport = raw.in_viewport;
    return info;
}

VtRenderState VtCore::raw_render_state() const {
    return impl_->render_state;
}

VtTerminal VtCore::raw_terminal() const {
    return impl_->terminal;
}

void VtCore::scroll_viewport(int32_t delta_rows) {
    if (impl_->terminal)
        vt_bridge_scroll_viewport(impl_->terminal, delta_rows);
}

bool VtCore::mode_get(uint16_t mode_value) const {
    if (!impl_->terminal) return false;
    bool value = false;
    int rc = vt_bridge_mode_get(impl_->terminal, mode_value, &value);
    return (rc == VT_OK) ? value : false;
}

// ─── Phase 5-B: OSC title/CWD ───

void VtCore::set_title_callback(TitleChangedFn fn, void* userdata) {
    if (!impl_->terminal) return;
    vt_bridge_set_title_callback(impl_->terminal, fn, userdata);
}

std::string VtCore::get_title() const {
    if (!impl_->terminal) return {};
    const char* ptr = nullptr;
    size_t len = 0;
    if (vt_bridge_get_title(impl_->terminal, &ptr, &len) == VT_OK)
        return {ptr, len};
    return {};
}

std::string VtCore::get_pwd() const {
    if (!impl_->terminal) return {};
    const char* ptr = nullptr;
    size_t len = 0;
    if (vt_bridge_get_pwd(impl_->terminal, &ptr, &len) == VT_OK)
        return {ptr, len};
    return {};
}

// ─── Phase 6-A: OSC 9/99/777 desktop notification ───

void VtCore::set_desktop_notify_callback(DesktopNotifyFn fn, void* userdata) {
    if (!impl_->terminal) return;
    vt_bridge_set_desktop_notify_callback(impl_->terminal,
        reinterpret_cast<VtDesktopNotifyFn>(fn), userdata);
}

} // namespace ghostwin
