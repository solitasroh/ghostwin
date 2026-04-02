/// @file glyph_atlas.cpp
/// DirectWrite glyph rasterizer + stb_rect_pack atlas + 2-tier cache.

#include "glyph_atlas.h"
#include "common/log.h"

#include <d3d11.h>
#include <dwrite_3.h>
#include <wrl/client.h>
#include <unordered_map>
#include <array>
#include <vector>
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
    float    dwrite_gamma = 1.8f;
    float    dwrite_enhanced_contrast = 0.5f;  // Grayscale 전용
    float    dwrite_ct_contrast = 0.5f;       // ClearType 전용 (P3)
    float    gamma_ratios[4] = {0.0f, 0.0f, 0.0f, 0.0f};
    ComPtr<IDWriteRenderingParams> linear_params;  // gamma=1.0 for shader-based correction
    DWRITE_RENDERING_MODE1 recommended_rendering_mode = DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC;
    DWRITE_GRID_FIT_MODE   recommended_grid_fit_mode = DWRITE_GRID_FIT_MODE_DEFAULT;

    // Compute gamma ratios from DirectWrite gamma value
    // Source: lhecker/dwrite-hlsl DWrite_GetGammaRatios() (Microsoft license)
    void compute_gamma_ratios() {
        // Lookup table for sRGB encoded render target (DXGI_FORMAT_B8G8R8A8_UNORM)
        static constexpr float table[13][4] = {
            { 0.0000f/4.f, -0.0000f/4.f, 0.0000f/4.f, -0.0000f/4.f }, // 1.0
            { 0.0166f/4.f, -0.0807f/4.f, 0.2227f/4.f, -0.0751f/4.f }, // 1.1
            { 0.0350f/4.f, -0.1760f/4.f, 0.4325f/4.f, -0.1370f/4.f }, // 1.2
            { 0.0543f/4.f, -0.2821f/4.f, 0.6302f/4.f, -0.1876f/4.f }, // 1.3
            { 0.0739f/4.f, -0.3963f/4.f, 0.8167f/4.f, -0.2287f/4.f }, // 1.4
            { 0.0933f/4.f, -0.5161f/4.f, 0.9926f/4.f, -0.2616f/4.f }, // 1.5
            { 0.1121f/4.f, -0.6395f/4.f, 1.1588f/4.f, -0.2877f/4.f }, // 1.6
            { 0.1300f/4.f, -0.7649f/4.f, 1.3159f/4.f, -0.3080f/4.f }, // 1.7
            { 0.1469f/4.f, -0.8911f/4.f, 1.4644f/4.f, -0.3234f/4.f }, // 1.8
            { 0.1627f/4.f, -1.0170f/4.f, 1.6051f/4.f, -0.3347f/4.f }, // 1.9
            { 0.1773f/4.f, -1.1420f/4.f, 1.7385f/4.f, -0.3426f/4.f }, // 2.0
            { 0.1908f/4.f, -1.2652f/4.f, 1.8650f/4.f, -0.3476f/4.f }, // 2.1
            { 0.2031f/4.f, -1.3864f/4.f, 1.9851f/4.f, -0.3501f/4.f }, // 2.2
        };

        static constexpr float norm13 =
            static_cast<float>(static_cast<double>(0x10000) / (255.0 * 255.0) * 4.0);
        static constexpr float norm24 =
            static_cast<float>(static_cast<double>(0x100) / 255.0 * 4.0);

        float g = dwrite_gamma;
        if (g < 1.0f) g = 1.0f;
        if (g > 2.2f) g = 2.2f;

        int index = static_cast<int>(g * 10.0f + 0.5f) - 10;
        if (index < 0) index = 0;
        if (index > 12) index = 12;

        gamma_ratios[0] = norm13 * table[index][0];
        gamma_ratios[1] = norm24 * table[index][1];
        gamma_ratios[2] = norm13 * table[index][2];
        gamma_ratios[3] = norm24 * table[index][3];

        LOG_I("atlas", "Gamma ratios: [%.4f, %.4f, %.4f, %.4f] (gamma=%.2f, index=%d)",
              gamma_ratios[0], gamma_ratios[1], gamma_ratios[2], gamma_ratios[3], g, index);
    }

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

    compute_cell_metrics();
    build_fallback_chain(config);

    // Detect ClearType availability (disabled on RDP, some accessibility settings)
    BOOL ct = FALSE;
    SystemParametersInfoW(SPI_GETCLEARTYPE, 0, &ct, 0);
    cleartype_enabled = (ct != FALSE);

    // Get DirectWrite rendering params for gamma correction
    ComPtr<IDWriteRenderingParams> params;
    hr = dwrite_factory->CreateRenderingParams(&params);
    if (SUCCEEDED(hr)) {
        dwrite_gamma = params->GetGamma();

        // Grayscale 전용 contrast: RenderingParams1이 있으면 독립 값 사용
        ComPtr<IDWriteRenderingParams1> params1;
        if (SUCCEEDED(params.As(&params1))) {
            float gs_contrast = params1->GetGrayscaleEnhancedContrast();
            dwrite_enhanced_contrast = (gs_contrast > 0.01f) ? gs_contrast
                : params->GetEnhancedContrast() + 0.25f;
        } else {
            dwrite_enhanced_contrast = params->GetEnhancedContrast() + 0.25f;
        }
        compute_gamma_ratios();

        // Factory3: 7-param CreateCustomRenderingParams
        // P1: clearTypeLevel = 1.0 (was 0.0 = Grayscale fallback)
        // P2: enhancedContrast = system ClearType value (was 0.0 = no contrast)
        float ct_contrast = params->GetEnhancedContrast();
        dwrite_ct_contrast = ct_contrast;
        ComPtr<IDWriteFactory3> factory3;
        if (SUCCEEDED(dwrite_factory.As(&factory3))) {
            ComPtr<IDWriteRenderingParams3> linear3;
            hr = factory3->CreateCustomRenderingParams(
                1.0f,                              // gamma = 1.0 (linear, shader handles correction)
                ct_contrast,                       // enhancedContrast (ClearType) — P2 fix
                dwrite_enhanced_contrast,          // grayscaleEnhancedContrast
                1.0f,                              // clearTypeLevel = full ClearType — P1 fix
                params->GetPixelGeometry(),
                DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC,
                DWRITE_GRID_FIT_MODE_ENABLED,
                &linear3);
            if (SUCCEEDED(hr)) {
                linear_params = linear3;
                LOG_I("atlas", "Factory3 params: ctContrast=%.2f, gsContrast=%.2f, ctLevel=1.0, gridFit=ENABLED",
                      ct_contrast, dwrite_enhanced_contrast);
            }
        }

        // Factory3 실패 시 기존 Factory1 경로 폴백
        if (!linear_params) {
            hr = dwrite_factory->CreateCustomRenderingParams(
                1.0f, 0.0f, 0.0f,
                params->GetPixelGeometry(),
                params->GetRenderingMode(),
                &linear_params);
            if (FAILED(hr)) {
                LOG_W("atlas", "CreateCustomRenderingParams failed, using system defaults");
            }
        }

        // FontFace3: GetRecommendedRenderingMode for optimal per-font settings
        ComPtr<IDWriteFontFace3> face3;
        if (SUCCEEDED(font_face.As(&face3))) {
            DWRITE_RENDERING_MODE1 recMode;
            DWRITE_GRID_FIT_MODE recGrid;
            // Use system default params (like Alacritty) instead of linear_params
            hr = face3->GetRecommendedRenderingMode(
                dip_size, 96.0f * dpi_scale, 96.0f * dpi_scale,
                nullptr, FALSE,
                DWRITE_OUTLINE_THRESHOLD_ANTIALIASED,
                DWRITE_MEASURING_MODE_NATURAL,
                params.Get(),  // system default (gamma=1.8) — not linear_params
                &recMode, &recGrid);
            if (SUCCEEDED(hr)) {
                recommended_rendering_mode = recMode;
                recommended_grid_fit_mode = (recGrid == DWRITE_GRID_FIT_MODE_DEFAULT)
                    ? DWRITE_GRID_FIT_MODE_ENABLED : recGrid;
                LOG_I("atlas", "Recommended mode: %d, gridFit: %d",
                      (int)recMode, (int)recommended_grid_fit_mode);
            }
        }
    }

    LOG_I("atlas", "DirectWrite init: cell=%ux%u, dpi=%.2f, ClearType=%s, gamma=%.2f, contrast=%.2f",
          cell_w, cell_h, dpi_scale, cleartype_enabled ? "on" : "off",
          dwrite_gamma, dwrite_enhanced_contrast);
    return true;
}

void GlyphAtlas::Impl::compute_cell_metrics() {
    DWRITE_FONT_METRICS metrics;
    font_face->GetMetrics(&metrics);

    float scale = dip_size / metrics.designUnitsPerEm;

    float ascent  = metrics.ascent * scale;
    float descent = metrics.descent * scale;
    float gap     = metrics.lineGap * scale;

    // DIP -> physical pixel conversion via dpi_scale
    ascent_px = static_cast<uint32_t>(ascent * dpi_scale + 0.5f);
    cell_h = static_cast<uint32_t>((ascent + descent + gap) * dpi_scale + 0.5f);
    if (cell_h < 1) cell_h = 1;

    // Cell width: measure 'M' advance
    uint32_t cp = 'M';
    uint16_t glyph_index = 0;
    font_face->GetGlyphIndices(&cp, 1, &glyph_index);

    DWRITE_GLYPH_METRICS gm;
    font_face->GetDesignGlyphMetrics(&glyph_index, 1, &gm, FALSE);
    cell_w = static_cast<uint32_t>(gm.advanceWidth * scale * dpi_scale + 0.5f);
    if (cell_w < 1) cell_w = 1;
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
    bool is_cjk_wide = (codepoint >= 0x1100 && codepoint <= 0x115F) ||
                       (codepoint >= 0x2E80 && codepoint <= 0x303E) ||
                       (codepoint >= 0x3040 && codepoint <= 0x30FF) ||
                       (codepoint >= 0x3400 && codepoint <= 0x9FFF) ||
                       (codepoint >= 0xAC00 && codepoint <= 0xD7AF) ||
                       (codepoint >= 0xF900 && codepoint <= 0xFAFF) ||
                       (codepoint >= 0xFF01 && codepoint <= 0xFF60) ||
                       (codepoint >= 0x20000 && codepoint <= 0x2FA1F);

    float em_size = dip_size;
    if (face_to_use != font_face.Get() && !is_cjk_wide) {
        float fb_cell_dip = (float)(fm.ascent + fm.descent) * dip_size / fm.designUnitsPerEm;
        float target_cell_dip = (float)cell_h / dpi_scale;  // physical -> DIP
        if (fb_cell_dip > target_cell_dip) {
            em_size = dip_size * target_cell_dip / fb_cell_dip;
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

    // Alacritty pattern: use Factory v1 CreateGlyphRunAnalysis
    // v1 has NO gridFitMode / antialiasMode params — DirectWrite decides internally
    // This produces sharper glyphs than v2 with forced GRID_FIT_MODE_ENABLED
    // Use NATURAL (mode 4) instead of NATURAL_SYMMETRIC (mode 5)
    // NATURAL: horizontal-only AA → narrow edge transitions (sharper)
    // NATURAL_SYMMETRIC: horizontal + vertical AA → wider edges (blurrier)
    // Alacritty's v1 get_recommended_rendering_mode_default_params likely returns NATURAL
    ComPtr<IDWriteGlyphRunAnalysis> analysis;
    HRESULT hr = dwrite_factory->CreateGlyphRunAnalysis(
        &glyph_run,
        dpi_scale,
        nullptr,
        DWRITE_RENDERING_MODE_NATURAL,
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

    // Try ClearType 3x1 first, fall back to Grayscale 1x1
    RECT bounds;
    DWRITE_TEXTURE_TYPE tex_type = DWRITE_TEXTURE_CLEARTYPE_3x1;
    hr = analysis->GetAlphaTextureBounds(tex_type, &bounds);
    if (FAILED(hr) || bounds.right <= bounds.left || bounds.bottom <= bounds.top) {
        // ClearType bounds failed — try Grayscale
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


    // Rasterize glyph to RGBA
    std::vector<uint8_t> rgba(gw * gh * 4);
    if (tex_type == DWRITE_TEXTURE_CLEARTYPE_3x1) {
        // ClearType 3x1: 3 bytes/pixel (R,G,B subpixel coverage)
        std::vector<uint8_t> ct_3x1(gw * gh * 3);
        hr = analysis->CreateAlphaTexture(tex_type, &bounds,
                                           ct_3x1.data(), (UINT32)(gw * gh * 3));
        if (FAILED(hr)) {
            LOG_W("atlas", "CreateAlphaTexture(3x1) failed for U+%04X", codepoint);
            return entry;
        }
        for (int i = 0; i < gw * gh; i++) {
            uint8_t r = ct_3x1[i * 3 + 0];
            uint8_t g = ct_3x1[i * 3 + 1];
            uint8_t b = ct_3x1[i * 3 + 2];
            rgba[i * 4 + 0] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = (r > g) ? ((r > b) ? r : b) : ((g > b) ? g : b);
        }
    } else {
        // Grayscale 1x1: 1 byte/pixel (single alpha)
        std::vector<uint8_t> alpha_1x1(gw * gh);
        hr = analysis->CreateAlphaTexture(tex_type, &bounds,
                                           alpha_1x1.data(), (UINT32)(gw * gh));
        if (FAILED(hr)) {
            LOG_W("atlas", "CreateAlphaTexture(1x1) failed for U+%04X", codepoint);
            return entry;
        }
        for (int i = 0; i < gw * gh; i++) {
            rgba[i * 4 + 0] = alpha_1x1[i];
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
float GlyphAtlas::enhanced_contrast() const {
    // ClearType: system contrast + boost for sharper edges (Chromium: 1.0 = native level)
    if (impl_->cleartype_enabled) {
        float boosted = impl_->dwrite_ct_contrast + 0.5f;  // 0.50 + 0.50 = 1.0
        return boosted > 1.0f ? 1.0f : boosted;
    }
    return impl_->dwrite_enhanced_contrast;
}
const float* GlyphAtlas::gamma_ratios() const { return impl_->gamma_ratios; }

} // namespace ghostwin
