/// @file vt_core.cpp
/// VtCore C++ wrapper over vt_bridge (pure C).
/// No ghostty headers are included here.

#include "vt_core.h"
#include "vt_bridge.h"

namespace ghostwin {

struct VtCore::Impl {
    void* terminal = nullptr;
    void* render_state = nullptr;
    uint16_t cols = 0;
    uint16_t rows = 0;

    ~Impl() {
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

    vt->impl_->cols = cols;
    vt->impl_->rows = rows;
    return vt;
}

void VtCore::write(std::span<const uint8_t> data) {
    if (impl_->terminal && !data.empty()) {
        vt_bridge_write(impl_->terminal, data.data(), data.size());
    }
}

RenderInfo VtCore::update_render_state() {
    RenderInfo info{};
    if (!impl_->terminal || !impl_->render_state) return info;

    VtRenderInfo raw = vt_bridge_update_render_state(impl_->render_state, impl_->terminal);

    info.dirty = static_cast<DirtyState>(raw.dirty);
    info.cols = raw.cols;
    info.rows = raw.rows;
    info.cursor_x = raw.cursor_x;
    info.cursor_y = raw.cursor_y;
    info.cursor_visible = raw.cursor_visible != 0;
    info.cursor_style = static_cast<CursorStyle>(raw.cursor_style);
    return info;
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

} // namespace ghostwin
