#pragma once

/// @file quad_builder.h
/// Converts CellData from RenderFrame into QuadInstance arrays for GPU rendering.
/// Phase 4-E: 32B StructuredBuffer format (was 68B R32 Input Layout).

#include <cstdint>
#include <span>
#include <string>

struct ID3D11DeviceContext;

namespace ghostwin {

struct RenderFrame;
class GlyphAtlas;

/// QuadInstance 32B packed format for StructuredBuffer.
/// Layout matches HLSL PackedQuad (uint2 + uint2 + uint×4).
#pragma pack(push, 1)
struct QuadInstance {
    uint16_t pos_x, pos_y;       //  4B — pixel position
    uint16_t size_x, size_y;     //  4B — pixel size
    uint16_t tex_u, tex_v;       //  4B — atlas pixel coords
    uint16_t tex_w, tex_h;       //  4B — glyph pixel size
    uint32_t fg_packed;          //  4B — RGBA8
    uint32_t bg_packed;          //  4B — RGBA8
    uint32_t shading_type;       //  4B
    uint32_t reserved;           //  4B — alignment / future use
};                               // = 32B
#pragma pack(pop)
static_assert(sizeof(QuadInstance) == 32, "QuadInstance must be 32 bytes");

/// Builds QuadInstance arrays from RenderFrame cell data.
class QuadBuilder {
public:
    QuadBuilder(uint32_t cell_w, uint32_t cell_h, uint32_t baseline,
                float glyph_offset_x = 0.0f, float glyph_offset_y = 0.0f,
                float padding_left = 0.0f, float padding_top = 0.0f);

    /// Build QuadInstances from dirty rows.
    /// Returns: number of instances written. Sets bg_count to background instance count.
    uint32_t build(const RenderFrame& frame,
                   GlyphAtlas& atlas,
                   ID3D11DeviceContext* ctx,
                   std::span<QuadInstance> out,
                   uint32_t* bg_count = nullptr,
                   bool draw_cursor = true);

    void update_cell_size(uint32_t cell_w, uint32_t cell_h);
    [[nodiscard]] uint32_t cell_width() const { return cell_w_; }
    [[nodiscard]] uint32_t cell_height() const { return cell_h_; }

    /// M-13: Build composition overlay quads at cursor position.
    /// Appends background highlight + glyph + underline for each composition character.
    /// Returns total instances written (starting from out[start_offset]).
    uint32_t build_composition(
        const std::wstring& text,
        uint32_t caret_offset,
        uint16_t cursor_x, uint16_t cursor_y,
        GlyphAtlas& atlas,
        ID3D11DeviceContext* ctx,
        std::span<QuadInstance> out,
        uint32_t start_offset);

private:
    uint32_t cell_w_;
    uint32_t cell_h_;
    uint32_t baseline_;
    float    glyph_offset_x_ = 0.0f;  // FR-02
    float    glyph_offset_y_ = 0.0f;  // FR-02
    float    padding_left_ = 0.0f;     // FR-05
    float    padding_top_  = 0.0f;     // FR-05
};

} // namespace ghostwin
