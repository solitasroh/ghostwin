/// @file quad_builder.cpp
/// CellData -> QuadInstance conversion.

#include "quad_builder.h"
#include "render_state.h"
#include "glyph_atlas.h"
#include "vt_bridge.h"

namespace ghostwin {

QuadBuilder::QuadBuilder(uint32_t cell_w, uint32_t cell_h)
    : cell_w_(cell_w), cell_h_(cell_h) {}

void QuadBuilder::update_cell_size(uint32_t cell_w, uint32_t cell_h) {
    cell_w_ = cell_w;
    cell_h_ = cell_h;
}

static void unpack_color(uint32_t packed, float& r, float& g, float& b, float& a) {
    r = (packed & 0xFF) / 255.0f;
    g = ((packed >> 8) & 0xFF) / 255.0f;
    b = ((packed >> 16) & 0xFF) / 255.0f;
    a = ((packed >> 24) & 0xFF) / 255.0f;
}

uint32_t QuadBuilder::build(const RenderFrame& frame,
                            GlyphAtlas& atlas,
                            ID3D11DeviceContext* ctx,
                            std::span<QuadInstance> out) {
    uint32_t count = 0;
    const uint32_t max_instances = static_cast<uint32_t>(out.size());

    for (uint16_t r = 0; r < frame.rows_count; r++) {
        if (!frame.is_row_dirty(r)) continue;

        auto row = frame.row(r);

        for (uint16_t c = 0; c < frame.cols; c++) {
            const auto& cell = row[c];

            float px = (float)(c * cell_w_);
            float py = (float)(r * cell_h_);

            // Background quad
            if (count < max_instances) {
                auto& q = out[count++];
                q.shading_type = 0;  // TextBackground
                q.pos_x = px;
                q.pos_y = py;
                q.size_x = (float)cell_w_;
                q.size_y = (float)cell_h_;
                q.tex_u = 0; q.tex_v = 0; q.tex_w = 0; q.tex_h = 0;
                unpack_color(cell.bg_packed, q.bg_r, q.bg_g, q.bg_b, q.bg_a);
                q.fg_r = q.bg_r; q.fg_g = q.bg_g; q.fg_b = q.bg_b; q.fg_a = q.bg_a;
            }

            // Text glyph quad (only if cell has content)
            if (cell.cp_count > 0 && count < max_instances) {
                auto glyph = atlas.lookup_or_rasterize(ctx, cell.codepoints[0], cell.style_flags);
                if (glyph.valid && glyph.width > 0) {
                    auto& q = out[count++];
                    q.shading_type = 1;  // TextGrayscale
                    q.pos_x = px + glyph.offset_x;
                    q.pos_y = py + glyph.offset_y;
                    q.size_x = glyph.width;
                    q.size_y = glyph.height;
                    q.tex_u = glyph.u;
                    q.tex_v = glyph.v;
                    q.tex_w = glyph.width;
                    q.tex_h = glyph.height;
                    unpack_color(cell.fg_packed, q.fg_r, q.fg_g, q.fg_b, q.fg_a);
                    unpack_color(cell.bg_packed, q.bg_r, q.bg_g, q.bg_b, q.bg_a);
                }
            }

            // Underline (if style has it)
            if ((cell.style_flags & VT_STYLE_UNDERLINE) && count < max_instances) {
                auto& q = out[count++];
                q.shading_type = 3;  // SolidLine
                q.pos_x = px;
                q.pos_y = py + (float)cell_h_ - 1.0f;
                q.size_x = (float)cell_w_;
                q.size_y = 1.0f;
                q.tex_u = 0; q.tex_v = 0; q.tex_w = 0; q.tex_h = 0;
                unpack_color(cell.fg_packed, q.fg_r, q.fg_g, q.fg_b, q.fg_a);
                q.bg_r = 0; q.bg_g = 0; q.bg_b = 0; q.bg_a = 0;
            }
        }
    }

    // Cursor quad
    if (frame.cursor.visible && frame.cursor.in_viewport && count < max_instances) {
        auto& q = out[count++];
        q.shading_type = 2;  // Cursor
        q.pos_x = (float)(frame.cursor.x * cell_w_);
        q.pos_y = (float)(frame.cursor.y * cell_h_);
        q.size_x = (float)cell_w_;
        q.size_y = (float)cell_h_;
        q.tex_u = 0; q.tex_v = 0; q.tex_w = 0; q.tex_h = 0;
        // Cursor color: white
        q.fg_r = 1.0f; q.fg_g = 1.0f; q.fg_b = 1.0f; q.fg_a = 0.7f;
        q.bg_r = 0; q.bg_g = 0; q.bg_b = 0; q.bg_a = 0;
    }

    return count;
}

} // namespace ghostwin
