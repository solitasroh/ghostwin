/// @file quad_builder.cpp
/// CellData -> QuadInstance 32B packed conversion.

#include "quad_builder.h"
#include "render_state.h"
#include "glyph_atlas.h"
#include "vt_bridge.h"
#include "common/string_util.h"

namespace ghostwin {

QuadBuilder::QuadBuilder(uint32_t cell_w, uint32_t cell_h, uint32_t baseline,
                         float glyph_offset_x, float glyph_offset_y,
                         float padding_left, float padding_top)
    : cell_w_(cell_w), cell_h_(cell_h), baseline_(baseline),
      glyph_offset_x_(glyph_offset_x), glyph_offset_y_(glyph_offset_y),
      padding_left_(padding_left), padding_top_(padding_top) {}

void QuadBuilder::update_cell_size(uint32_t cell_w, uint32_t cell_h) {
    cell_w_ = cell_w;
    cell_h_ = cell_h;
}

// Pack RGBA8 color from 0.0-1.0 floats
static uint32_t pack_color(float r, float g, float b, float a) {
    return ((uint32_t)(r * 255.0f)) |
           ((uint32_t)(g * 255.0f) << 8) |
           ((uint32_t)(b * 255.0f) << 16) |
           ((uint32_t)(a * 255.0f) << 24);
}

struct DecodedCodepoint {
    uint32_t value;
    size_t next_index;
};

static DecodedCodepoint decode_utf16_codepoint(const std::wstring& text, size_t index) {
    uint32_t cp = static_cast<uint32_t>(static_cast<uint16_t>(text[index]));
    if (cp >= 0xD800 && cp <= 0xDBFF && index + 1 < text.size()) {
        uint32_t low = static_cast<uint16_t>(text[index + 1]);
        if (low >= 0xDC00 && low <= 0xDFFF) {
            cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
            return { cp, index + 2 };
        }
    }

    if (cp >= 0xDC00 && cp <= 0xDFFF)
        cp = 0xFFFD;

    return { cp, index + 1 };
}

static uint16_t codepoint_cell_span(uint32_t cp) {
    return is_wide_codepoint(cp) ? 2 : 1;
}

static uint16_t visual_cells_until(const std::wstring& text, uint32_t utf16_offset) {
    const size_t limit = utf16_offset > text.size() ? text.size() : utf16_offset;
    uint16_t cells = 0;
    for (size_t i = 0; i < limit;) {
        auto decoded = decode_utf16_codepoint(text, i);
        cells = static_cast<uint16_t>(cells + codepoint_cell_span(decoded.value));
        i = decoded.next_index;
    }
    return cells;
}

uint32_t QuadBuilder::build(const RenderFrame& frame,
                            GlyphAtlas& atlas,
                            ID3D11DeviceContext* ctx,
                            std::span<QuadInstance> out,
                            uint32_t* bg_count_out,
                            bool draw_cursor) {
    uint32_t count = 0;
    const uint32_t max_instances = static_cast<uint32_t>(out.size());

    // 2-pass rendering: all backgrounds first, then all text on top.

    // Pass 1: Backgrounds
    for (uint16_t r = 0; r < frame.rows_count; r++) {
        auto row = frame.row(r);
        if (row.empty()) continue;  // guard: resize race (see render_state.h)
        for (uint16_t c = 0; c < row.size(); c++) {
            if (count >= max_instances) goto done;
            const auto& cell = row[c];

            // Skip spacer cell (2nd cell of wide char)
            if (cell.cp_count == 0 && c > 0) {
                const auto& prev = row[c - 1];
                if (prev.cp_count > 0 && is_wide_codepoint(prev.codepoints[0]))
                    continue;
            }

            bool wide = (cell.cp_count > 0 && is_wide_codepoint(cell.codepoints[0]));
            uint16_t px = (uint16_t)(c * cell_w_ + padding_left_);
            uint16_t py = (uint16_t)(r * cell_h_ + padding_top_);

            auto& q = out[count++];
            q.shading_type = 0;
            q.pos_x = px;
            q.pos_y = py;
            q.size_x = wide ? (uint16_t)(cell_w_ * 2) : (uint16_t)cell_w_;
            q.size_y = (uint16_t)cell_h_;
            q.tex_u = 0; q.tex_v = 0; q.tex_w = 0; q.tex_h = 0;
            q.fg_packed = cell.bg_packed;
            q.bg_packed = cell.bg_packed;
            q.reserved = 0;
        }
    }

    // Record background instance count
    if (bg_count_out) *bg_count_out = count;

    // Pass 2: Text glyphs + decorations
    for (uint16_t r = 0; r < frame.rows_count; r++) {
        auto row = frame.row(r);
        if (row.empty()) continue;  // guard: resize race
        for (uint16_t c = 0; c < row.size(); c++) {
            const auto& cell = row[c];
            if (cell.cp_count == 0) continue;
            if (count >= max_instances) goto done;

            uint16_t px = (uint16_t)(c * cell_w_ + padding_left_);
            uint16_t py = (uint16_t)(r * cell_h_ + padding_top_);

            auto glyph = atlas.lookup_or_rasterize(ctx, cell.codepoints[0], cell.style_flags);
            if (glyph.valid && glyph.width > 0) {
                auto& q = out[count++];
                q.shading_type = 1;
                // CJK wide: advance-centered positioning (Alacritty/Ghostty 패턴)
                // advance width(bearing 포함)를 2-cell span 내에서 센터링하여
                // bearing 비율을 보존하면서 gap을 균등 분배
                bool wide = is_wide_codepoint(cell.codepoints[0]);
                float glyph_x;
                if (wide && glyph.advance_x > 0.0f) {
                    // FR-04: CJK advance forced to cell_span (WT pattern)
                    float cell_span = (float)(cell_w_ * 2);
                    float centering = (cell_span - glyph.advance_x) * 0.5f;
                    if (centering < 0.0f) centering = 0.0f;
                    glyph_x = (float)px + centering + glyph.offset_x + glyph_offset_x_;
                } else {
                    glyph_x = (float)px + glyph.offset_x + glyph_offset_x_;
                }
                q.pos_x = (uint16_t)(glyph_x + 0.5f);

                // Y 위치 + 셀 높이 클리핑 (CJK advance 스케일링으로 세로 오버플로우 대응)
                float gy = (float)py + (float)baseline_ + glyph.offset_y + glyph_offset_y_;
                float gh = glyph.height;
                float tv = glyph.v;  // atlas texture V offset
                float cell_top = (float)py;
                float cell_bot = (float)(py + cell_h_);
                // 위쪽 클리핑: 글리프가 셀 위로 넘치면 잘라냄
                if (gy < cell_top) {
                    float clip = cell_top - gy;
                    tv += clip;
                    gh -= clip;
                    gy = cell_top;
                }
                // 아래쪽 클리핑: 글리프가 셀 아래로 넘치면 잘라냄
                if (gy + gh > cell_bot) {
                    gh = cell_bot - gy;
                }
                if (gh < 1.0f) gh = 1.0f;

                q.pos_y = (uint16_t)(gy + 0.5f);
                q.size_x = (uint16_t)glyph.width;
                q.size_y = (uint16_t)(gh + 0.5f);
                q.tex_u = (uint16_t)glyph.u;
                q.tex_v = (uint16_t)(tv + 0.5f);
                q.tex_w = (uint16_t)glyph.width;
                q.tex_h = (uint16_t)(gh + 0.5f);
                q.fg_packed = cell.fg_packed;
                q.bg_packed = cell.bg_packed;
                q.reserved = 0;
            }

            // Underline
            if ((cell.style_flags & VT_STYLE_UNDERLINE) && count < max_instances) {
                auto& q = out[count++];
                q.shading_type = 3;
                q.pos_x = px;
                q.pos_y = (uint16_t)(py + cell_h_ - 1);
                q.size_x = (uint16_t)cell_w_;
                q.size_y = 1;
                q.tex_u = 0; q.tex_v = 0; q.tex_w = 0; q.tex_h = 0;
                q.fg_packed = cell.fg_packed;
                q.bg_packed = 0;
                q.reserved = 0;
            }
        }
    }

done:

    // Cursor quad
    if (draw_cursor && frame.cursor.visible && frame.cursor.in_viewport && count < max_instances) {
        auto& q = out[count++];
        q.shading_type = 2;
        q.pos_x = (uint16_t)(frame.cursor.x * cell_w_ + padding_left_);
        q.pos_y = (uint16_t)(frame.cursor.y * cell_h_ + padding_top_);
        q.size_x = (uint16_t)cell_w_;
        q.size_y = (uint16_t)cell_h_;
        q.tex_u = 0; q.tex_v = 0; q.tex_w = 0; q.tex_h = 0;
        q.fg_packed = pack_color(0.8f, 0.8f, 0.8f, 1.0f);
        q.bg_packed = 0;
        q.reserved = 0;
    }

    return count;
}

// M-13 FR-01: Build composition overlay quads at cursor position.
// Analysis §4.3 risks addressed:
//  - is_wide_codepoint() reuse (no duplicated CJK ranges)
//  - UTF-16 surrogate pair handling (BMP+ codepoints recombined)
//  - CJK advance-centering reuses build()'s pattern for visual consistency
//  - Diagnostic fallback rect when glyph rasterization fails (data-path verify)
uint32_t QuadBuilder::build_composition(
    const std::wstring& text,
    uint32_t caret_offset,
    uint16_t cursor_x, uint16_t cursor_y,
    GlyphAtlas& atlas,
    ID3D11DeviceContext* ctx,
    std::span<QuadInstance> out,
    uint32_t start_offset) {

    uint32_t count = start_offset;
    const uint32_t max = static_cast<uint32_t>(out.size());
    uint16_t cx = cursor_x;
    uint16_t cy = cursor_y;

    // Composition overlay color tokens (Plan D-2 / D-3).
    // TODO(M-12 theme): pull from ISettingsService once exposed to engine.
    constexpr uint32_t kCompBgColor   = 0x60FF8844;  // semi-transparent blue (RGBA: B=0x44 G=0x88 R=0xFF A=0x60 packed LE)
    constexpr uint32_t kCompFgColor   = 0xFFFFFFFF;  // opaque white glyph
    constexpr uint32_t kCompUnderline = 0xFFFFFFFF;  // opaque white underline
    constexpr uint32_t kCompCaret     = 0xFFFFFFFF;  // opaque white caret

    for (size_t i = 0; i < text.size() && count < max;) {
        auto decoded = decode_utf16_codepoint(text, i);
        uint32_t cp = decoded.value;
        i = decoded.next_index;

        const uint16_t span = codepoint_cell_span(cp);
        const bool wide = span == 2;

        const float px      = (float)(cx * cell_w_) + padding_left_;
        const float py_cell = (float)(cy * cell_h_) + padding_top_;

        // ── Background highlight (per cell within span) ──
        for (uint16_t s = 0; s < span && count < max; ++s) {
            auto& q = out[count++];
            q.shading_type = 2;
            q.pos_x = (uint16_t)(px + s * cell_w_);
            q.pos_y = (uint16_t)py_cell;
            q.size_x = (uint16_t)cell_w_;
            q.size_y = (uint16_t)cell_h_;
            q.tex_u = 0; q.tex_v = 0; q.tex_w = 0; q.tex_h = 0;
            q.fg_packed = kCompBgColor;
            q.bg_packed = 0;
            q.reserved = 0;
        }

        // ── Glyph (CJK advance-centering reused from build(), analysis §4.3) ──
        auto glyph = atlas.lookup_or_rasterize(ctx, cp, 0);
        if (glyph.valid && glyph.width > 0 && count < max) {
            float glyph_x;
            if (wide && glyph.advance_x > 0.0f) {
                float cell_span = (float)(cell_w_ * 2);
                float centering = (cell_span - glyph.advance_x) * 0.5f;
                if (centering < 0.0f) centering = 0.0f;
                glyph_x = px + centering + glyph.offset_x + glyph_offset_x_;
            } else {
                glyph_x = px + glyph.offset_x + glyph_offset_x_;
            }

            float gy = py_cell + (float)baseline_ + glyph.offset_y + glyph_offset_y_;
            float gh = glyph.height;
            float tv = glyph.v;

            // Vertical clipping (same policy as build())
            if (gy < py_cell) {
                float clip = py_cell - gy;
                tv += clip; gh -= clip; gy = py_cell;
            }
            float cell_bot = py_cell + (float)cell_h_;
            if (gy + gh > cell_bot) gh = cell_bot - gy;
            if (gh < 1.0f) gh = 1.0f;

            auto& q = out[count++];
            q.shading_type = 1;
            q.pos_x = (uint16_t)(glyph_x + 0.5f);
            q.pos_y = (uint16_t)(gy + 0.5f);
            q.size_x = (uint16_t)glyph.width;
            q.size_y = (uint16_t)(gh + 0.5f);
            q.tex_u = (uint16_t)glyph.u;
            q.tex_v = (uint16_t)(tv + 0.5f);
            q.tex_w = (uint16_t)glyph.width;
            q.tex_h = (uint16_t)(gh + 0.5f);
            q.fg_packed = kCompFgColor;
            q.bg_packed = 0;
            q.reserved = 0;
        }

        // ── Underline (1px at cell bottom, full span width) ──
        if (count < max) {
            auto& q = out[count++];
            q.shading_type = 2;
            q.pos_x = (uint16_t)px;
            q.pos_y = (uint16_t)(py_cell + cell_h_ - 1);
            q.size_x = (uint16_t)(cell_w_ * span);
            q.size_y = 1;
            q.tex_u = 0; q.tex_v = 0; q.tex_w = 0; q.tex_h = 0;
            q.fg_packed = kCompUnderline;
            q.bg_packed = 0;
            q.reserved = 0;
        }

        cx += span;
    }

    // ── Composition caret (separate from the terminal cursor) ──
    if (count < max) {
        const uint16_t caret_cells = visual_cells_until(text, caret_offset);
        const float px = (float)((cursor_x + caret_cells) * cell_w_) + padding_left_;
        const float py = (float)(cursor_y * cell_h_) + padding_top_;

        auto& q = out[count++];
        q.shading_type = 2;
        q.pos_x = (uint16_t)px;
        q.pos_y = (uint16_t)py;
        q.size_x = 2;
        q.size_y = (uint16_t)cell_h_;
        q.tex_u = 0; q.tex_v = 0; q.tex_w = 0; q.tex_h = 0;
        q.fg_packed = kCompCaret;
        q.bg_packed = 0;
        q.reserved = 0;
    }

    return count;
}

} // namespace ghostwin
