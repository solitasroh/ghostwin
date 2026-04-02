#pragma once

/// @file glyph_atlas.h
/// DirectWrite glyph rasterizer + stb_rect_pack atlas.

#include "common/error.h"
#include "common/render_constants.h"
#include <cstdint>
#include <memory>

struct ID3D11Device;
struct ID3D11DeviceContext;
struct ID3D11ShaderResourceView;

namespace ghostwin {

struct GlyphEntry {
    float u, v;           // atlas position (pixels)
    float width, height;  // glyph size (pixels)
    float offset_x;       // bearing X from cell origin
    float offset_y;       // bearing Y from cell baseline
    float advance_x;      // font advance width in pixels (for CJK centering)
    bool  valid = false;
};

struct AtlasConfig {
    float font_size_pt = constants::kDefaultFontSizePt;
    const wchar_t* font_family = L"Cascadia Mono";
    const wchar_t* nerd_font_family = nullptr;  // nullptr = auto-detect
    uint32_t initial_size = constants::kInitialAtlasSize;
    uint32_t max_size = constants::kMaxAtlasSize;
    float dpi_scale = 1.0f;  // CompositionScaleX (1.0 = 96 DPI)
};

class GlyphAtlas {
public:
    [[nodiscard]] static std::unique_ptr<GlyphAtlas> create(
        ID3D11Device* device, const AtlasConfig& config,
        Error* out_error = nullptr);
    ~GlyphAtlas();

    /// Lookup or rasterize a glyph.
    GlyphEntry lookup_or_rasterize(
        ID3D11DeviceContext* ctx,
        uint32_t codepoint, uint8_t style_flags);

    [[nodiscard]] ID3D11ShaderResourceView* srv() const;
    [[nodiscard]] uint32_t cell_width() const;
    [[nodiscard]] uint32_t cell_height() const;
    [[nodiscard]] uint32_t baseline() const;  // ascent in pixels
    [[nodiscard]] uint32_t atlas_width() const;
    [[nodiscard]] uint32_t atlas_height() const;
    [[nodiscard]] uint32_t glyph_count() const;

    [[nodiscard]] float enhanced_contrast() const;
    [[nodiscard]] const float* gamma_ratios() const;

    /// Dump atlas texture to BMP file for diagnostic
    void dump_atlas(ID3D11DeviceContext* ctx, const char* path) const;

private:
    GlyphAtlas();
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace ghostwin
