#pragma once

/// @file quad_builder.h
/// Converts CellData from RenderFrame into QuadInstance arrays for GPU rendering.

#include <cstdint>
#include <span>

struct ID3D11DeviceContext;

namespace ghostwin {

struct RenderFrame;
class GlyphAtlas;

/// QuadInstance matches the R32-based GPU layout (68 bytes).
struct QuadInstance {
    uint32_t shading_type;
    float    pos_x, pos_y;
    float    size_x, size_y;
    float    tex_u, tex_v;
    float    tex_w, tex_h;
    float    fg_r, fg_g, fg_b, fg_a;
    float    bg_r, bg_g, bg_b, bg_a;
};
static_assert(sizeof(QuadInstance) == 68, "QuadInstance must be 68 bytes");

/// Builds QuadInstance arrays from RenderFrame cell data.
class QuadBuilder {
public:
    QuadBuilder(uint32_t cell_w, uint32_t cell_h);

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
};

} // namespace ghostwin
