/// @file glyph_atlas.cpp
/// DirectWrite glyph rasterizer + stb_rect_pack atlas + 2-tier cache.

#include "glyph_atlas.h"
#include "common/log.h"

#include <d3d11.h>
#include <dwrite_2.h>
#include <wrl/client.h>
#include <unordered_map>
#include <array>
#include <vector>
#include <cstring>

#define STB_RECT_PACK_IMPLEMENTATION
#include <stb_rect_pack.h>

using Microsoft::WRL::ComPtr;

namespace ghostwin {

// ─── Glyph cache key ───

struct GlyphKey {
    uint32_t codepoint;
    uint8_t  style_flags;
    bool operator==(const GlyphKey&) const = default;
};

struct GlyphKeyHash {
    size_t operator()(const GlyphKey& k) const noexcept {
        return k.codepoint ^ (static_cast<size_t>(k.style_flags) << 21);
    }
};

// ─── Impl ───

struct GlyphAtlas::Impl {
    ComPtr<ID3D11Device>             device;
    ComPtr<ID3D11Texture2D>          atlas_tex;
    ComPtr<ID3D11ShaderResourceView> atlas_srv;

    ComPtr<IDWriteFactory>           dwrite_factory;
    ComPtr<IDWriteFontFace>          font_face;  // primary font face
    ComPtr<IDWriteTextFormat>        text_format;

    uint32_t atlas_w = 0;
    uint32_t atlas_h = 0;
    uint32_t cell_w = 0;
    uint32_t cell_h = 0;
    uint32_t ascent_px = 0;  // baseline position from cell top
    float    dip_size = 0;   // font size in DIP
    uint32_t cached_count = 0;
    bool     cleartype_enabled = true;

    // stb_rect_pack context
    stbrp_context pack_ctx = {};
    std::vector<stbrp_node> pack_nodes;

    // 2-tier cache
    // Tier 1: ASCII direct (128 codepoints * 8 style variants)
    std::array<GlyphEntry, 128 * 8> ascii_cache{};
    // Tier 2: non-ASCII hashmap
    std::unordered_map<GlyphKey, GlyphEntry, GlyphKeyHash> complex_cache;

    bool init_dwrite(const AtlasConfig& config, Error* out_error);
    bool init_atlas_texture(Error* out_error);
    void compute_cell_metrics();
    GlyphEntry rasterize_glyph(ID3D11DeviceContext* ctx, uint32_t codepoint, uint8_t style_flags);
};

// ─── DirectWrite initialization ───

bool GlyphAtlas::Impl::init_dwrite(const AtlasConfig& config, Error* out_error) {
    dip_size = config.font_size_pt * (96.0f / 72.0f);

    HRESULT hr = DWriteCreateFactory(
        DWRITE_FACTORY_TYPE_SHARED,
        __uuidof(IDWriteFactory),
        reinterpret_cast<IUnknown**>(dwrite_factory.GetAddressOf()));
    if (FAILED(hr)) {
        LOG_E("atlas", "DWriteCreateFactory failed: 0x%08lX", (unsigned long)hr);
        if (out_error) *out_error = { ErrorCode::DeviceCreationFailed, "DWriteCreateFactory failed" };
        return false;
    }

    // Create text format for measuring
    hr = dwrite_factory->CreateTextFormat(
        config.font_family,
        nullptr,  // system font collection
        DWRITE_FONT_WEIGHT_REGULAR,
        DWRITE_FONT_STYLE_NORMAL,
        DWRITE_FONT_STRETCH_NORMAL,
        config.font_size_pt * (96.0f / 72.0f),  // pt -> DIP (96 DPI)
        L"en-us",
        &text_format);
    if (FAILED(hr)) {
        LOG_W("atlas", "Font '%ls' not found, falling back to Consolas", config.font_family);
        hr = dwrite_factory->CreateTextFormat(
            L"Consolas", nullptr,
            DWRITE_FONT_WEIGHT_REGULAR, DWRITE_FONT_STYLE_NORMAL,
            DWRITE_FONT_STRETCH_NORMAL,
            config.font_size_pt * (96.0f / 72.0f), L"en-us", &text_format);
    }
    if (FAILED(hr)) {
        if (out_error) *out_error = { ErrorCode::DeviceCreationFailed, "CreateTextFormat failed" };
        return false;
    }

    // Get font face from text format
    ComPtr<IDWriteFontCollection> collection;
    dwrite_factory->GetSystemFontCollection(&collection);

    UINT32 index = 0;
    BOOL exists = FALSE;
    collection->FindFamilyName(config.font_family, &index, &exists);
    if (!exists) {
        collection->FindFamilyName(L"Consolas", &index, &exists);
    }

    if (exists) {
        ComPtr<IDWriteFontFamily> family;
        collection->GetFontFamily(index, &family);
        ComPtr<IDWriteFont> font;
        family->GetFirstMatchingFont(
            DWRITE_FONT_WEIGHT_REGULAR, DWRITE_FONT_STRETCH_NORMAL,
            DWRITE_FONT_STYLE_NORMAL, &font);
        if (font) {
            font->CreateFontFace(&font_face);
        }
    }

    if (!font_face) {
        LOG_E("atlas", "Failed to get font face");
        if (out_error) *out_error = { ErrorCode::DeviceCreationFailed, "Font face creation failed" };
        return false;
    }

    compute_cell_metrics();

    // Detect ClearType availability (disabled on RDP, some accessibility settings)
    BOOL ct = FALSE;
    SystemParametersInfoW(SPI_GETCLEARTYPE, 0, &ct, 0);
    cleartype_enabled = (ct != FALSE);
    LOG_I("atlas", "DirectWrite init: cell=%ux%u, ClearType=%s",
          cell_w, cell_h, cleartype_enabled ? "on" : "off");
    return true;
}

void GlyphAtlas::Impl::compute_cell_metrics() {
    DWRITE_FONT_METRICS metrics;
    font_face->GetMetrics(&metrics);

    float scale = dip_size / metrics.designUnitsPerEm;

    float ascent  = metrics.ascent * scale;
    float descent = metrics.descent * scale;
    float gap     = metrics.lineGap * scale;

    ascent_px = static_cast<uint32_t>(ascent + 0.5f);
    cell_h = static_cast<uint32_t>(ascent + descent + gap + 0.5f);
    if (cell_h < 1) cell_h = 1;

    // Cell width: measure 'M' advance
    uint32_t cp = 'M';
    uint16_t glyph_index = 0;
    font_face->GetGlyphIndices(&cp, 1, &glyph_index);

    DWRITE_GLYPH_METRICS gm;
    font_face->GetDesignGlyphMetrics(&glyph_index, 1, &gm, FALSE);
    cell_w = static_cast<uint32_t>(gm.advanceWidth * scale + 0.5f);
    if (cell_w < 1) cell_w = 1;
}

// ─── Atlas texture ───

bool GlyphAtlas::Impl::init_atlas_texture(Error* out_error) {
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width  = atlas_w;
    desc.Height = atlas_h;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;  // ClearType subpixel RGBA
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

    HRESULT hr = device->CreateTexture2D(&desc, nullptr, &atlas_tex);
    if (FAILED(hr)) {
        if (out_error) *out_error = { ErrorCode::OutOfMemory, "Atlas texture creation failed" };
        return false;
    }

    hr = device->CreateShaderResourceView(atlas_tex.Get(), nullptr, &atlas_srv);
    if (FAILED(hr)) {
        if (out_error) *out_error = { ErrorCode::OutOfMemory, "Atlas SRV creation failed" };
        return false;
    }

    // Init stb_rect_pack
    pack_nodes.resize(atlas_w);
    stbrp_init_target(&pack_ctx, atlas_w, atlas_h, pack_nodes.data(), (int)atlas_w);

    LOG_I("atlas", "Atlas texture created (%ux%u, R8G8B8A8_UNORM)", atlas_w, atlas_h);
    return true;
}

// ─── Glyph rasterization ───

GlyphEntry GlyphAtlas::Impl::rasterize_glyph(ID3D11DeviceContext* ctx,
                                               uint32_t codepoint,
                                               uint8_t style_flags) {
    GlyphEntry entry{};

    // Get glyph index from primary font
    uint16_t glyph_index = 0;
    IDWriteFontFace* face_to_use = font_face.Get();
    ComPtr<IDWriteFontFace> fallback_face;

    font_face->GetGlyphIndices(&codepoint, 1, &glyph_index);
    if (glyph_index == 0 && codepoint > 0x7F) {
        // Try system font fallback for non-ASCII (CJK, etc.)
        ComPtr<IDWriteFontFallback> fallback;
        ComPtr<IDWriteFactory2> factory2;
        if (SUCCEEDED(dwrite_factory.As(&factory2))) {
            factory2->GetSystemFontFallback(&fallback);
        }
        if (fallback) {
            // Simple text analysis source for a single codepoint
            wchar_t wch[3] = {};
            int wlen = 0;
            if (codepoint <= 0xFFFF) {
                wch[0] = (wchar_t)codepoint;
                wlen = 1;
            } else {
                // Surrogate pair
                codepoint -= 0x10000;
                wch[0] = (wchar_t)(0xD800 + (codepoint >> 10));
                wch[1] = (wchar_t)(0xDC00 + (codepoint & 0x3FF));
                wlen = 2;
            }

            ComPtr<IDWriteTextLayout> layout;
            dwrite_factory->CreateTextLayout(wch, wlen, text_format.Get(),
                                             1000.0f, 1000.0f, &layout);
            if (layout) {
                ComPtr<IDWriteFont> mapped_font;
                UINT32 mapped_len = 0;
                float mapped_scale = 1.0f;

                // Use MapCharacters via text layout approach
                // Simpler: just try known fallback fonts
                const wchar_t* fallback_fonts[] = {
                    L"Malgun Gothic", L"Microsoft YaHei", L"Yu Gothic", L"Segoe UI Emoji", nullptr
                };
                ComPtr<IDWriteFontCollection> collection;
                dwrite_factory->GetSystemFontCollection(&collection);

                for (auto* ff = fallback_fonts; *ff; ++ff) {
                    UINT32 idx = 0;
                    BOOL exists = FALSE;
                    collection->FindFamilyName(*ff, &idx, &exists);
                    if (!exists) continue;

                    ComPtr<IDWriteFontFamily> family;
                    collection->GetFontFamily(idx, &family);
                    ComPtr<IDWriteFont> font;
                    family->GetFirstMatchingFont(
                        DWRITE_FONT_WEIGHT_REGULAR, DWRITE_FONT_STRETCH_NORMAL,
                        DWRITE_FONT_STYLE_NORMAL, &font);
                    if (!font) continue;

                    ComPtr<IDWriteFontFace> ff_face;
                    font->CreateFontFace(&ff_face);
                    if (!ff_face) continue;

                    uint16_t gi = 0;
                    ff_face->GetGlyphIndices(&codepoint, 1, &gi);
                    if (gi != 0) {
                        fallback_face = ff_face;
                        face_to_use = fallback_face.Get();
                        glyph_index = gi;
                        break;
                    }
                }
            }
        }
        if (glyph_index == 0) return entry;  // no font has this glyph
    } else if (glyph_index == 0 && codepoint != 0) {
        return entry;
    }

    // Get design metrics from the resolved font face
    DWRITE_GLYPH_METRICS gm;
    face_to_use->GetDesignGlyphMetrics(&glyph_index, 1, &gm, FALSE);

    DWRITE_FONT_METRICS fm;
    face_to_use->GetMetrics(&fm);

    // Fallback font: use same size as primary, scale down only if too tall.
    float em_size = dip_size;
    if (face_to_use != font_face.Get()) {
        float fb_cell_px = (float)(fm.ascent + fm.descent) * dip_size / fm.designUnitsPerEm;
        if (fb_cell_px > (float)cell_h) {
            em_size = dip_size * (float)cell_h / fb_cell_px;
        }
    }

    float scale = em_size / fm.designUnitsPerEm;

    // Create glyph run
    DWRITE_GLYPH_RUN glyph_run = {};
    glyph_run.fontFace = face_to_use;
    glyph_run.fontEmSize = em_size;
    glyph_run.glyphCount = 1;
    glyph_run.glyphIndices = &glyph_index;

    float advance = gm.advanceWidth * scale;
    glyph_run.glyphAdvances = &advance;

    DWRITE_GLYPH_OFFSET glyph_offset = { 0, 0 };
    glyph_run.glyphOffsets = &glyph_offset;

    // Create glyph run analysis
    ComPtr<IDWriteGlyphRunAnalysis> analysis;
    HRESULT hr = dwrite_factory->CreateGlyphRunAnalysis(
        &glyph_run,
        1.0f,  // pixels per DIP
        nullptr,  // transform
        DWRITE_RENDERING_MODE_NATURAL,
        DWRITE_MEASURING_MODE_NATURAL,
        0.0f, 0.0f,  // baseline origin
        &analysis);

    if (FAILED(hr)) {
        LOG_W("atlas", "CreateGlyphRunAnalysis failed for U+%04X", codepoint);
        return entry;
    }

    // Try ClearType 3x1 first (better quality), fallback to aliased 1x1
    RECT bounds;
    bool is_cleartype = false;
    if (cleartype_enabled) {
        hr = analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds);
        if (SUCCEEDED(hr) && bounds.right > bounds.left && bounds.bottom > bounds.top) {
            is_cleartype = true;
        }
    }
    if (!is_cleartype) {
        hr = analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_ALIASED_1x1, &bounds);
        if (FAILED(hr) || bounds.right <= bounds.left || bounds.bottom <= bounds.top) {
            entry.valid = true;
            entry.width = 0;
            entry.height = 0;
            return entry;
        }
    }

    int gw = bounds.right - bounds.left;
    int gh = bounds.bottom - bounds.top;

    // Pack into atlas
    stbrp_rect rect = {};
    rect.w = gw + 1;
    rect.h = gh + 1;
    if (!stbrp_pack_rects(&pack_ctx, &rect, 1) || !rect.was_packed) {
        LOG_W("atlas", "Atlas full, cannot pack U+%04X (%dx%d)", codepoint, gw, gh);
        return entry;
    }

    // Get alpha texture and store as RGBA for subpixel rendering
    std::vector<uint8_t> rgba(gw * gh * 4);
    if (is_cleartype) {
        // ClearType: 3 bytes per pixel (RGB subpixel coverage)
        std::vector<uint8_t> rgb(gw * gh * 3);
        hr = analysis->CreateAlphaTexture(DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds,
                                           rgb.data(), (UINT32)(gw * gh * 3));
        if (FAILED(hr)) {
            LOG_W("atlas", "CreateAlphaTexture(CT) failed for U+%04X", codepoint);
            return entry;
        }
        for (int i = 0; i < gw * gh; i++) {
            rgba[i * 4 + 0] = rgb[i * 3 + 0];  // R subpixel
            rgba[i * 4 + 1] = rgb[i * 3 + 1];  // G subpixel
            rgba[i * 4 + 2] = rgb[i * 3 + 2];  // B subpixel
            uint8_t r = rgb[i*3], g = rgb[i*3+1], b = rgb[i*3+2];
            rgba[i * 4 + 3] = r > g ? (r > b ? r : b) : (g > b ? g : b);  // A = max(R,G,B)
        }
    } else {
        // Aliased 1x1: replicate single channel to RGBA (grayscale equivalent)
        std::vector<uint8_t> alpha_1x1(gw * gh);
        hr = analysis->CreateAlphaTexture(DWRITE_TEXTURE_ALIASED_1x1, &bounds,
                                           alpha_1x1.data(), (UINT32)(gw * gh));
        if (FAILED(hr)) {
            LOG_W("atlas", "CreateAlphaTexture(1x1) failed for U+%04X", codepoint);
            return entry;
        }
        for (int i = 0; i < gw * gh; i++) {
            rgba[i * 4 + 0] = alpha_1x1[i];  // R = G = B = A
            rgba[i * 4 + 1] = alpha_1x1[i];
            rgba[i * 4 + 2] = alpha_1x1[i];
            rgba[i * 4 + 3] = alpha_1x1[i];
        }
    }

    // Upload RGBA to GPU texture
    D3D11_BOX box = {};
    box.left = rect.x;
    box.top = rect.y;
    box.right = rect.x + gw;
    box.bottom = rect.y + gh;
    box.front = 0;
    box.back = 1;
    ctx->UpdateSubresource(atlas_tex.Get(), 0, &box, rgba.data(), gw * 4, 0);

    entry.u = (float)rect.x;
    entry.v = (float)rect.y;
    entry.width = (float)gw;
    entry.height = (float)gh;
    entry.offset_x = (float)bounds.left;
    entry.offset_y = (float)bounds.top;
    entry.valid = true;
    cached_count++;

    return entry;
}

// ─── Public API ───

GlyphAtlas::GlyphAtlas() : impl_(std::make_unique<Impl>()) {}
GlyphAtlas::~GlyphAtlas() = default;

std::unique_ptr<GlyphAtlas> GlyphAtlas::create(
    ID3D11Device* device, const AtlasConfig& config, Error* out_error) {

    auto atlas = std::unique_ptr<GlyphAtlas>(new GlyphAtlas());
    atlas->impl_->device = device;
    atlas->impl_->atlas_w = config.initial_size;
    atlas->impl_->atlas_h = config.initial_size;

    if (!atlas->impl_->init_dwrite(config, out_error)) return nullptr;
    if (!atlas->impl_->init_atlas_texture(out_error)) return nullptr;

    return atlas;
}

GlyphEntry GlyphAtlas::lookup_or_rasterize(
    ID3D11DeviceContext* ctx, uint32_t codepoint, uint8_t style_flags) {

    // Tier 1: ASCII direct mapping
    uint8_t shape_key = (style_flags & 0x3) | ((style_flags >> 3) & 0x4);
    if (codepoint < 128 && shape_key < 8) {
        auto& e = impl_->ascii_cache[codepoint * 8 + shape_key];
        if (e.valid) return e;
        e = impl_->rasterize_glyph(ctx, codepoint, style_flags);
        return e;
    }

    // Tier 2: non-ASCII hashmap
    GlyphKey key{ codepoint, style_flags };
    auto it = impl_->complex_cache.find(key);
    if (it != impl_->complex_cache.end()) return it->second;

    auto entry = impl_->rasterize_glyph(ctx, codepoint, style_flags);
    impl_->complex_cache[key] = entry;
    return entry;
}

ID3D11ShaderResourceView* GlyphAtlas::srv() const { return impl_->atlas_srv.Get(); }
uint32_t GlyphAtlas::cell_width() const { return impl_->cell_w; }
uint32_t GlyphAtlas::cell_height() const { return impl_->cell_h; }
uint32_t GlyphAtlas::baseline() const { return impl_->ascent_px; }
uint32_t GlyphAtlas::atlas_width() const { return impl_->atlas_w; }
uint32_t GlyphAtlas::atlas_height() const { return impl_->atlas_h; }
uint32_t GlyphAtlas::glyph_count() const { return impl_->cached_count; }

} // namespace ghostwin
