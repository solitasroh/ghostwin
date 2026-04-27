/// @file glyph_atlas.cpp
/// DirectWrite glyph rasterizer + stb_rect_pack atlas + 2-tier cache.

#include "glyph_atlas.h"
#include "common/log.h"
#include "common/string_util.h"

#include <d3d11.h>
#include <dwrite_3.h>
#include <wrl/client.h>
#include <unordered_map>
#include <array>
#include <vector>
#include <cmath>
#include <cstring>
#include <string>

#define STB_RECT_PACK_IMPLEMENTATION
#include <stb_rect_pack.h>

using Microsoft::WRL::ComPtr;

namespace ghostwin {

// ─── IDWriteTextAnalysisSource minimal impl (for MapCharacters) ───

class SimpleTextAnalysisSource : public IDWriteTextAnalysisSource {
    const wchar_t* text_;
    UINT32 len_;
    ULONG ref_ = 1;
public:
    SimpleTextAnalysisSource(const wchar_t* t, UINT32 l) : text_(t), len_(l) {}
    ULONG STDMETHODCALLTYPE AddRef() override { return ++ref_; }
    ULONG STDMETHODCALLTYPE Release() override {
        if (--ref_ == 0) { delete this; return 0; } return ref_;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IDWriteTextAnalysisSource)) {
            *ppv = this; AddRef(); return S_OK;
        }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    HRESULT STDMETHODCALLTYPE GetTextAtPosition(UINT32 pos, const WCHAR** text, UINT32* len) override {
        if (pos >= len_) { *text = nullptr; *len = 0; }
        else { *text = text_ + pos; *len = len_ - pos; }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetTextBeforePosition(UINT32 pos, const WCHAR** text, UINT32* len) override {
        if (pos == 0 || pos > len_) { *text = nullptr; *len = 0; }
        else { *text = text_; *len = pos; }
        return S_OK;
    }
    DWRITE_READING_DIRECTION STDMETHODCALLTYPE GetParagraphReadingDirection() override {
        return DWRITE_READING_DIRECTION_LEFT_TO_RIGHT;
    }
    HRESULT STDMETHODCALLTYPE GetLocaleName(UINT32, UINT32*, const WCHAR** name) override {
        *name = L"en-us"; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetNumberSubstitution(UINT32, UINT32*, IDWriteNumberSubstitution** sub) override {
        *sub = nullptr; return S_OK;
    }
};

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
    ComPtr<IDWriteFontFallback>      custom_fallback;  // custom fallback chain
    std::wstring                     nerd_font_name;   // detected Nerd Font family
    ComPtr<IDWriteFontFace>          nerd_font_face;   // cached Nerd Font face for direct PUA lookup

    uint32_t atlas_w = 0;
    uint32_t atlas_h = 0;
    uint32_t cell_w = 0;
    uint32_t cell_h = 0;
    uint32_t ascent_px = 0;  // baseline position from cell top
    float    dip_size = 0;   // font size in DIP
    float    dpi_scale = 1.0f;  // display DPI scale (1.0 = 96 DPI)
    uint32_t cached_count = 0;
    bool     cleartype_enabled = true;
    float    dwrite_gamma = 1.8f;  // stored for logging only

    // FR-01/FR-03: scale and metrics state
    float    cell_width_scale = 1.0f;
    float    cell_height_scale = 1.0f;
    float    glyph_offset_x = 0.0f;   // FR-02
    float    glyph_offset_y = 0.0f;   // FR-02
    float    natural_cell_h = 0.0f;    // pre-scale cell height (for baseline calc)
    float    cap_height_px = 0.0f;     // FR-06: primary font cap height in px
    bool     use_cap_height_scaling = true;  // FR-06

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
    void compute_cell_metrics(const AtlasConfig& config);
    float measure_cell_advance(uint32_t codepoint);
    float measure_max_ascii_advance();
    float compute_cap_height(IDWriteFontFace* face, float scale, float dpi);
    bool build_fallback_chain(const AtlasConfig& config);
    GlyphEntry rasterize_glyph(ID3D11DeviceContext* ctx, uint32_t codepoint, uint8_t style_flags);
};

// ─── DirectWrite initialization ───

bool GlyphAtlas::Impl::init_dwrite(const AtlasConfig& config, Error* out_error) {
    dip_size = config.font_size_pt * (96.0f / 72.0f);
    dpi_scale = config.dpi_scale > 0.0f ? config.dpi_scale : 1.0f;

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

    compute_cell_metrics(config);
    build_fallback_chain(config);

    // Detect ClearType availability (disabled on RDP, some accessibility settings)
    BOOL ct = FALSE;
    SystemParametersInfoW(SPI_GETCLEARTYPE, 0, &ct, 0);
    cleartype_enabled = (ct != FALSE);

    // Get system gamma for logging
    ComPtr<IDWriteRenderingParams> params;
    hr = dwrite_factory->CreateRenderingParams(&params);
    if (SUCCEEDED(hr)) {
        dwrite_gamma = params->GetGamma();
    }

    LOG_I("atlas", "DirectWrite init: cell=%ux%u, dpi=%.2f, ClearType=%s, gamma=%.2f",
          cell_w, cell_h, dpi_scale, cleartype_enabled ? "on" : "off", dwrite_gamma);
    return true;
}

// FR-07: measure advance width of a single codepoint (physical px)
float GlyphAtlas::Impl::measure_cell_advance(uint32_t codepoint) {
    uint16_t glyph_index = 0;
    font_face->GetGlyphIndices(&codepoint, 1, &glyph_index);
    if (glyph_index == 0) return 0.0f;

    DWRITE_GLYPH_METRICS gm;
    font_face->GetDesignGlyphMetrics(&glyph_index, 1, &gm, FALSE);

    DWRITE_FONT_METRICS fm;
    font_face->GetMetrics(&fm);
    float scale = dip_size / fm.designUnitsPerEm;
    return gm.advanceWidth * scale * dpi_scale;
}

// FR-07 fallback: max advance of ASCII printable range (WezTerm pattern)
float GlyphAtlas::Impl::measure_max_ascii_advance() {
    float max_adv = 0.0f;
    for (uint32_t cp = 0x21; cp < 0x7F; cp++) {
        float adv = measure_cell_advance(cp);
        if (adv > max_adv) max_adv = adv;
    }
    return max_adv;
}

// FR-06: compute cap height in physical pixels using DWRITE_FONT_METRICS1
float GlyphAtlas::Impl::compute_cap_height(IDWriteFontFace* face, float scale, float dpi) {
    ComPtr<IDWriteFontFace1> face1;
    if (SUCCEEDED(face->QueryInterface(IID_PPV_ARGS(&face1)))) {
        DWRITE_FONT_METRICS1 m1;
        face1->GetMetrics(&m1);
        if (m1.capHeight > 0) {
            return m1.capHeight * scale * dpi;
        }
    }
    return 0.0f;
}

void GlyphAtlas::Impl::compute_cell_metrics(const AtlasConfig& config) {
    // Store config params
    cell_width_scale = config.cell_width_scale;
    cell_height_scale = config.cell_height_scale;
    glyph_offset_x = config.glyph_offset_x;
    glyph_offset_y = config.glyph_offset_y;
    use_cap_height_scaling = config.use_cap_height_scaling;

    DWRITE_FONT_METRICS metrics;
    font_face->GetMetrics(&metrics);
    float scale = dip_size / metrics.designUnitsPerEm;

    // Physical pixel values
    float ascent  = metrics.ascent  * scale * dpi_scale;
    float descent = metrics.descent * scale * dpi_scale;
    float gap     = metrics.lineGap * scale * dpi_scale;

    // FR-07: cell width — '0' advance (CSS ch unit, WT pattern), ASCII max fallback
    float advance_w = measure_cell_advance('0');
    if (advance_w <= 0.0f) {
        advance_w = measure_max_ascii_advance();
    }
    if (advance_w <= 0.0f) {
        // Last resort: 'M' advance (original behavior)
        advance_w = measure_cell_advance('M');
    }

    // Natural cell height (before user scale)
    float natural_h = ascent + descent + gap;
    natural_cell_h = natural_h;

    // FR-01: apply user scale
    float adjusted_w = advance_w * cell_width_scale;
    float adjusted_h = natural_h * cell_height_scale;

    // roundf() rounding (WT pattern — minimal visual error)
    float rw = std::roundf(adjusted_w);
    float rh = std::roundf(adjusted_h);
    cell_w = static_cast<uint32_t>(rw > 1.0f ? rw : 1.0f);
    cell_h = static_cast<uint32_t>(rh > 1.0f ? rh : 1.0f);

    // FR-03: baseline — lineGap + scale surplus distributed top/bottom (WT pattern)
    // baseline = ascent + (gap + adjustedH - naturalH) / 2
    float extra = gap + adjusted_h - natural_h;
    ascent_px = static_cast<uint32_t>(std::roundf(ascent + extra / 2.0f));

    // FR-06: store primary font cap height for fallback scaling
    cap_height_px = compute_cap_height(font_face.Get(), scale, dpi_scale);
}

// ─── Font fallback chain ───

bool GlyphAtlas::Impl::build_fallback_chain(const AtlasConfig& config) {
    ComPtr<IDWriteFactory2> factory2;
    if (FAILED(dwrite_factory.As(&factory2))) return false;

    ComPtr<IDWriteFontFallbackBuilder> builder;
    if (FAILED(factory2->CreateFontFallbackBuilder(&builder))) return false;

    ComPtr<IDWriteFontCollection> collection;
    dwrite_factory->GetSystemFontCollection(&collection);

    // 1. Nerd Font auto-detect
    const wchar_t* nerd_candidates[] = {
        L"CaskaydiaCove Nerd Font", L"CaskaydiaCove NF",
        L"JetBrainsMono Nerd Font", L"JetBrainsMono NF",
        L"Hack Nerd Font",          L"Hack NF",
        L"FiraCode Nerd Font",      L"FiraCode NF",
        nullptr
    };
    if (config.nerd_font_family) {
        // User-specified Nerd Font
        UINT32 idx = 0; BOOL exists = FALSE;
        collection->FindFamilyName(config.nerd_font_family, &idx, &exists);
        if (exists) nerd_font_name = config.nerd_font_family;
    }
    if (nerd_font_name.empty()) {
        for (auto* name = nerd_candidates; *name; ++name) {
            UINT32 idx = 0; BOOL exists = FALSE;
            collection->FindFamilyName(*name, &idx, &exists);
            if (exists) { nerd_font_name = *name; break; }
        }
    }

    // Cache Nerd Font face for direct PUA lookup (supplementary plane fallback)
    if (!nerd_font_name.empty()) {
        UINT32 idx = 0; BOOL exists = FALSE;
        collection->FindFamilyName(nerd_font_name.c_str(), &idx, &exists);
        if (exists) {
            ComPtr<IDWriteFontFamily> family;
            collection->GetFontFamily(idx, &family);
            ComPtr<IDWriteFont> font;
            family->GetFirstMatchingFont(
                DWRITE_FONT_WEIGHT_REGULAR, DWRITE_FONT_STRETCH_NORMAL,
                DWRITE_FONT_STYLE_NORMAL, &font);
            if (font) font->CreateFontFace(&nerd_font_face);
        }
    }

    // 2. CJK fallback
    const wchar_t* cjk_fonts[] = { L"Malgun Gothic", L"Microsoft YaHei", L"Yu Gothic" };
    DWRITE_UNICODE_RANGE cjk_ranges[] = {
        { 0x2E80, 0x9FFF },
        { 0x3000, 0x303F },
        { 0xAC00, 0xD7AF },
        { 0xF900, 0xFAFF },
    };
    builder->AddMapping(cjk_ranges, 4, cjk_fonts, 3, collection.Get(),
                        nullptr, nullptr, 1.0f);

    // 3. Nerd Font PUA (if installed)
    if (!nerd_font_name.empty()) {
        const wchar_t* nf_fonts[] = { nerd_font_name.c_str() };
        DWRITE_UNICODE_RANGE nerd_ranges[] = {
            { 0xE000, 0xE0FF },    // Powerline + Powerline Extra
            { 0xE200, 0xE2FF },    // Seti-UI + Custom
            { 0xE700, 0xE7FF },    // Devicons
            { 0xEA60, 0xEBEB },    // Codicons
            { 0xF000, 0xF2FF },    // Font Awesome
            { 0xF300, 0xF8FF },    // FA Extension + Weather + Material (BMP)
            { 0xF0001, 0xF1AF0 },  // Material Design Icons (Supplementary PUA-A)
        };
        builder->AddMapping(nerd_ranges, 7, nf_fonts, 1, collection.Get(),
                            nullptr, nullptr, 1.0f);
    }

    // 4. Emoji fallback
    const wchar_t* emoji_fonts[] = { L"Segoe UI Emoji", L"Segoe UI Symbol" };
    DWRITE_UNICODE_RANGE emoji_ranges[] = {
        { 0x2600, 0x27BF },
        { 0x1F300, 0x1F9FF },
    };
    builder->AddMapping(emoji_ranges, 2, emoji_fonts, 2, collection.Get(),
                        nullptr, nullptr, 1.0f);

    // 5. Append system default fallback
    ComPtr<IDWriteFontFallback> system_fallback;
    factory2->GetSystemFontFallback(&system_fallback);
    if (system_fallback)
        builder->AddMappings(system_fallback.Get());

    // 6. Build
    HRESULT hr = builder->CreateFontFallback(&custom_fallback);
    if (SUCCEEDED(hr)) {
        LOG_I("atlas", "Fallback chain built: NerdFont=%ls, CJK+Emoji+System",
              nerd_font_name.empty() ? L"(none)" : nerd_font_name.c_str());
    }
    return SUCCEEDED(hr);
}

// ─── Atlas texture ───

bool GlyphAtlas::Impl::init_atlas_texture(Error* out_error) {
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width  = atlas_w;
    desc.Height = atlas_h;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;  // ClearType BGRA
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

    LOG_I("atlas", "Atlas texture created (%ux%u, B8G8R8A8_UNORM)", atlas_w, atlas_h);
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
        // Use custom fallback chain (CJK → Nerd Font → Emoji → System)
        if (custom_fallback) {
            wchar_t wch[3] = {};
            int wlen = 0;
            if (codepoint <= 0xFFFF) {
                wch[0] = (wchar_t)codepoint;
                wlen = 1;
            } else {
                uint32_t cp = codepoint - 0x10000;
                wch[0] = (wchar_t)(0xD800 + (cp >> 10));
                wch[1] = (wchar_t)(0xDC00 + (cp & 0x3FF));
                wlen = 2;
            }

            SimpleTextAnalysisSource src(wch, wlen);
            ComPtr<IDWriteFont> mapped_font;
            UINT32 mapped_len = 0;
            float mapped_scale = 1.0f;

            HRESULT hr2 = custom_fallback->MapCharacters(
                &src, 0, wlen, nullptr, nullptr,
                DWRITE_FONT_WEIGHT_REGULAR, DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH_NORMAL,
                &mapped_len, &mapped_font, &mapped_scale);

            if (SUCCEEDED(hr2) && mapped_font) {
                ComPtr<IDWriteFontFace> ff_face;
                mapped_font->CreateFontFace(&ff_face);
                if (ff_face) {
                    uint16_t gi = 0;
                    ff_face->GetGlyphIndices(&codepoint, 1, &gi);
                    if (gi != 0) {
                        fallback_face = ff_face;
                        face_to_use = fallback_face.Get();
                        glyph_index = gi;
                    }
                }
            }
        }

        // Direct Nerd Font lookup for PUA codepoints (MapCharacters may fail for supplementary plane)
        if (glyph_index == 0 && nerd_font_face) {
            uint16_t gi = 0;
            nerd_font_face->GetGlyphIndices(&codepoint, 1, &gi);
            if (gi != 0) {
                fallback_face = nerd_font_face;
                face_to_use = fallback_face.Get();
                glyph_index = gi;
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

    // Fallback font sizing:
    //  - CJK wide: 높이 축소 안 함 → 자연 비율 유지, 수직 오버플로우는 quad_builder에서 클리핑
    //  - Non-CJK: 높이 초과 시 축소 (기존 동작)
    // CJK advance < 2*cell_w 잔여 gap은 quad_builder advance-centering으로 대칭 분배
    bool is_cjk_wide = is_wide_codepoint(codepoint);

    float em_size = dip_size;
    if (face_to_use != font_face.Get()) {
        // FR-06: cap-height based scaling for fallback fonts (WezTerm pattern)
        bool cap_scaled = false;
        if (use_cap_height_scaling && cap_height_px > 0.0f) {
            float fb_scale = dip_size / fm.designUnitsPerEm;
            float fb_cap = compute_cap_height(face_to_use, fb_scale, dpi_scale);
            if (fb_cap > 0.0f) {
                float ratio = cap_height_px / fb_cap;
                if (ratio != 1.0f) {
                    em_size *= ratio;
                    cap_scaled = true;
                }
            }
        }

        // Existing height-based shrink for non-CJK (when cap-height not available)
        if (!cap_scaled && !is_cjk_wide) {
            float fb_cell_dip = (float)(fm.ascent + fm.descent) * dip_size / fm.designUnitsPerEm;
            float target_cell_dip = (float)cell_h / dpi_scale;
            if (fb_cell_dip > target_cell_dip) {
                em_size = dip_size * target_cell_dip / fb_cell_dip;
            }
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

    // Bounds via CreateGlyphRunAnalysis (Factory v1, system gamma defaults).
    // TODO: Replace with ID2D1DeviceContext::GetGlyphRunWorldBounds (D2D 1.1)
    // CreateGlyphRunAnalysis provides glyph bounds for atlas packing.
    // Coverage is generated by CreateAlphaTexture (system gamma baked).
    ComPtr<IDWriteGlyphRunAnalysis> analysis;
    HRESULT hr = dwrite_factory->CreateGlyphRunAnalysis(
        &glyph_run,
        dpi_scale,
        nullptr,
        DWRITE_RENDERING_MODE_DEFAULT,  // DWrite auto-selects best mode
        DWRITE_MEASURING_MODE_NATURAL,
        0.0f, 0.0f,
        &analysis);
    if (FAILED(hr)) {
        hr = dwrite_factory->CreateGlyphRunAnalysis(
            &glyph_run, dpi_scale, nullptr,
            DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,
            DWRITE_MEASURING_MODE_NATURAL,
            0.0f, 0.0f, &analysis);
    }

    if (FAILED(hr)) {
        LOG_W("atlas", "CreateGlyphRunAnalysis failed for U+%04X", codepoint);
        return entry;
    }

    // ClearType 3x1 first, Grayscale 1x1 fallback
    RECT bounds;
    DWRITE_TEXTURE_TYPE tex_type = DWRITE_TEXTURE_CLEARTYPE_3x1;
    hr = analysis->GetAlphaTextureBounds(tex_type, &bounds);
    if (FAILED(hr) || bounds.right <= bounds.left || bounds.bottom <= bounds.top) {
        tex_type = DWRITE_TEXTURE_ALIASED_1x1;
        hr = analysis->GetAlphaTextureBounds(tex_type, &bounds);
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


    // CreateAlphaTexture: system gamma (~1.8) baked, ClearType 3x1
    // Coverage is display-ready. No shader gamma correction needed.
    // Dual Source Blending handles per-channel hardware blend.
    std::vector<uint8_t> bgra(gw * gh * 4, 0);
    if (tex_type == DWRITE_TEXTURE_CLEARTYPE_3x1) {
        std::vector<uint8_t> ct_3x1(gw * gh * 3);
        hr = analysis->CreateAlphaTexture(tex_type, &bounds,
                                           ct_3x1.data(), (UINT32)(gw * gh * 3));
        if (SUCCEEDED(hr)) {
            for (int i = 0; i < gw * gh; i++) {
                uint8_t r = ct_3x1[i * 3 + 0];
                uint8_t g = ct_3x1[i * 3 + 1];
                uint8_t b = ct_3x1[i * 3 + 2];
                bgra[i * 4 + 0] = b;  // B (B8G8R8A8 byte 0)
                bgra[i * 4 + 1] = g;  // G
                bgra[i * 4 + 2] = r;  // R
                bgra[i * 4 + 3] = (r > g) ? ((r > b) ? r : b) : ((g > b) ? g : b);
            }
        }
    } else {
        std::vector<uint8_t> a1(gw * gh);
        hr = analysis->CreateAlphaTexture(tex_type, &bounds, a1.data(), (UINT32)(gw * gh));
        if (SUCCEEDED(hr)) {
            for (int i = 0; i < gw * gh; i++) {
                bgra[i*4+0] = bgra[i*4+1] = bgra[i*4+2] = bgra[i*4+3] = a1[i];
            }
        }
    }
    D3D11_BOX box = {};
    box.left = rect.x; box.top = rect.y;
    box.right = rect.x + gw; box.bottom = rect.y + gh;
    box.front = 0; box.back = 1;
    ctx->UpdateSubresource(atlas_tex.Get(), 0, &box, bgra.data(), gw * 4, 0);

    entry.u = (float)rect.x;
    entry.v = (float)rect.y;
    entry.width = (float)gw;
    entry.height = (float)gh;
    entry.offset_x = (float)bounds.left;
    entry.offset_y = (float)bounds.top;
    entry.advance_x = gm.advanceWidth * scale * dpi_scale;

    // Center narrow fallback glyphs (Nerd Font icons, emoji) horizontally in cell
    if (face_to_use != font_face.Get()) {
        float glyph_advance = gm.advanceWidth * scale * dpi_scale;
        if (glyph_advance < (float)cell_w) {
            entry.offset_x += ((float)cell_w - glyph_advance) * 0.5f;
        }
    }

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
float GlyphAtlas::enhanced_contrast() const { return 0.0f; }  // unused (raw coverage)
const float* GlyphAtlas::gamma_ratios() const { static const float z[4]={}; return z; }

void GlyphAtlas::dump_atlas(ID3D11DeviceContext* ctx, const char* path) const {
    // Create staging texture for CPU readback
    D3D11_TEXTURE2D_DESC desc = {};
    impl_->atlas_tex->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;

    ComPtr<ID3D11Texture2D> staging;
    HRESULT hr = impl_->device->CreateTexture2D(&desc, nullptr, &staging);
    if (FAILED(hr)) { LOG_E("atlas", "dump: staging create failed"); return; }

    LOG_I("atlas", "dump: atlas_tex=%p, staging=%p, cached=%u",
          impl_->atlas_tex.Get(), staging.Get(), impl_->cached_count);
    ctx->CopyResource(staging.Get(), impl_->atlas_tex.Get());
    ctx->Flush();

    D3D11_MAPPED_SUBRESOURCE mapped;
    hr = ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr)) { LOG_E("atlas", "dump: map failed"); return; }

    // Write BMP (simple, no external dependency)
    FILE* f = fopen(path, "wb");
    if (f) {
        uint32_t w = desc.Width, h = desc.Height;
        uint32_t row_bytes = w * 3;
        uint32_t pad = (4 - (row_bytes % 4)) % 4;
        uint32_t data_size = (row_bytes + pad) * h;
        uint32_t file_size = 54 + data_size;

        // BMP header
        uint8_t hdr[54] = {};
        hdr[0] = 'B'; hdr[1] = 'M';
        memcpy(hdr + 2, &file_size, 4);
        uint32_t offset = 54; memcpy(hdr + 10, &offset, 4);
        uint32_t dib_size = 40; memcpy(hdr + 14, &dib_size, 4);
        memcpy(hdr + 18, &w, 4); memcpy(hdr + 22, &h, 4);
        uint16_t planes = 1; memcpy(hdr + 26, &planes, 2);
        uint16_t bpp = 24; memcpy(hdr + 28, &bpp, 2);
        fwrite(hdr, 1, 54, f);

        // Pixel data (bottom-up, BGR)
        auto* src = static_cast<uint8_t*>(mapped.pData);
        std::vector<uint8_t> row_buf(row_bytes + pad, 0);
        for (int y = h - 1; y >= 0; y--) {
            auto* row = src + y * mapped.RowPitch;
            for (uint32_t x = 0; x < w; x++) {
                row_buf[x * 3 + 0] = row[x * 4 + 2]; // B
                row_buf[x * 3 + 1] = row[x * 4 + 1]; // G
                row_buf[x * 3 + 2] = row[x * 4 + 0]; // R
            }
            fwrite(row_buf.data(), 1, row_bytes + pad, f);
        }
        fclose(f);
        LOG_I("atlas", "Atlas dumped to %s (%ux%u)", path, w, h);
    }
    ctx->Unmap(staging.Get(), 0);
}

} // namespace ghostwin
