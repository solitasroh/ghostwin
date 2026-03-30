#pragma once

/// @file quad_builder.h
/// Converts CellData from RenderFrame into QuadInstance arrays for GPU rendering.
/// Phase 4-E: 32B StructuredBuffer format (was 68B R32 Input Layout).

#include <cstdint>
#include <span>

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
    QuadBuilder(uint32_t cell_w, uint32_t cell_h, uint32_t baseline);

    /// Build QuadInstances from dirty rows.
    /// Returns: number of instances written.
    uint32_t build(const RenderFrame& frame,
                   GlyphAtlas& atlas,
                   ID3D11DeviceContext* ctx,
                   std::span<QuadInstance> out);

    void update_cell_size(uint32_t cell_w, uint32_t cell_h);
    [[nodiscard]] uint32_t cell_width() const { return cell_w_; }
    [[nodiscard]] uint32_t cell_height() const { return cell_h_; }

private:
    uint32_t cell_w_;
    uint32_t cell_h_;
    uint32_t baseline_;
};

} // namespace ghostwin
