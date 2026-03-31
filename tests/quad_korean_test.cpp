/// @file quad_korean_test.cpp
/// Headless QuadBuilder test: CellData(Korean) -> build() -> QuadInstance validation.
/// Uses WARP D3D11 device (no HWND required).

#include "renderer/quad_builder.h"
#include "renderer/render_state.h"
#include "renderer/glyph_atlas.h"
#include "common/error.h"

#include <cstdio>
#include <cstdlib>
#include <vector>
#include <span>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;
using namespace ghostwin;

// ─── Test infrastructure ───

static int g_pass = 0;
static int g_fail = 0;

#define CHECK(cond, msg) \
    do { \
        if (cond) { \
            printf("  [OK]   %s\n", msg); \
            g_pass++; \
        } else { \
            printf("  [FAIL] %s\n", msg); \
            g_fail++; \
        } \
    } while (0)

// ─── Shared test fixtures ───

struct TestFixture {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    std::unique_ptr<GlyphAtlas> atlas;
    bool valid = false;
};

static TestFixture create_fixture() {
    TestFixture f;

    // WARP device: software rasterizer, no GPU/HWND needed
    HRESULT hr = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_WARP,
        nullptr,
        0,
        nullptr, 0,
        D3D11_SDK_VERSION,
        &f.device, nullptr, &f.context);

    if (FAILED(hr)) {
        printf("[FATAL] D3D11CreateDevice(WARP) failed: 0x%08lX\n", hr);
        return f;
    }

    AtlasConfig cfg;
    cfg.font_family = L"Cascadia Mono";
    cfg.font_size_pt = 12.0f;

    Error err{};
    f.atlas = GlyphAtlas::create(f.device.Get(), cfg, &err);
    if (!f.atlas) {
        printf("[FATAL] GlyphAtlas::create failed: %s\n",
               err.message ? err.message : "unknown");
        return f;
    }

    f.valid = true;
    return f;
}

// ─── Helper: build a frame with single character at (0,0) ───

static RenderFrame make_single_char_frame(uint32_t codepoint, bool wide) {
    RenderFrame frame;
    frame.allocate(80, 24);

    auto row = frame.row(0);
    row[0].codepoints[0] = codepoint;
    row[0].cp_count = 1;
    row[0].style_flags = 0;
    row[0].fg_packed = 0xFFFFFFFF;  // white
    row[0].bg_packed = 0xFF000000;  // black

    if (wide) {
        // col 1: spacer cell (wide char occupies 2 cells)
        row[1].cp_count = 0;
        row[1].fg_packed = 0xFFFFFFFF;
        row[1].bg_packed = 0xFF000000;
    }

    frame.set_row_dirty(0);
    return frame;
}

// ─── Q1: korean_glyph_exists ───
// "한" (U+D55C) glyph rasterization produces a valid text quad.

static void test_korean_glyph_exists(TestFixture& f) {
    printf("\n--- Q1: korean_glyph_exists ---\n");

    auto frame = make_single_char_frame(0xD55C, true);  // "한" is wide

    uint32_t cell_w = f.atlas->cell_width();
    uint32_t cell_h = f.atlas->cell_height();
    uint32_t baseline = f.atlas->baseline();

    QuadBuilder builder(cell_w, cell_h, baseline);
    // 80*24*3 = 5760 (bg + text + decoration per cell, worst case)
    std::vector<QuadInstance> staging(80 * 24 * 3);
    uint32_t count = builder.build(frame, *f.atlas, f.context.Get(), std::span(staging));

    CHECK(count > 0, "build() produced quads");

    // Find text quad (shading_type == 1)
    const QuadInstance* text_quad = nullptr;
    for (uint32_t i = 0; i < count; i++) {
        if (staging[i].shading_type == 1) {
            text_quad = &staging[i];
            break;
        }
    }

    CHECK(text_quad != nullptr, "text quad (shading_type=1) found");

    if (text_quad) {
        CHECK(text_quad->tex_w > 0, "glyph tex_w > 0 (rasterized)");
        CHECK(text_quad->tex_h > 0, "glyph tex_h > 0 (rasterized)");

        // Verify atlas consistency: lookup the same glyph directly
        auto entry = f.atlas->lookup_or_rasterize(f.context.Get(), 0xD55C, 0);
        CHECK(entry.valid, "atlas lookup_or_rasterize valid");

        if (entry.valid) {
            CHECK(text_quad->tex_u == (uint16_t)entry.u,
                  "tex_u matches atlas entry");
            CHECK(text_quad->tex_v == (uint16_t)entry.v,
                  "tex_v matches atlas entry");
        }

        // pos_x should be near column 0 (glyph bearing offset applied)
        CHECK(text_quad->pos_x < cell_w * 2,
              "pos_x within first wide cell range");
    }
}

// ─── Q2: korean_wide_char ───
// "한" background quad spans 2 cells (size_x == cell_w * 2).

static void test_korean_wide_char(TestFixture& f) {
    printf("\n--- Q2: korean_wide_char ---\n");

    auto frame = make_single_char_frame(0xD55C, true);  // "한"

    uint32_t cell_w = f.atlas->cell_width();
    uint32_t cell_h = f.atlas->cell_height();
    uint32_t baseline = f.atlas->baseline();

    QuadBuilder builder(cell_w, cell_h, baseline);
    std::vector<QuadInstance> staging(80 * 24 * 3);
    uint32_t count = builder.build(frame, *f.atlas, f.context.Get(), std::span(staging));

    // Find the background quad at col 0 (shading_type == 0, pos_x == 0)
    const QuadInstance* bg_quad = nullptr;
    for (uint32_t i = 0; i < count; i++) {
        if (staging[i].shading_type == 0 && staging[i].pos_x == 0) {
            bg_quad = &staging[i];
            break;
        }
    }

    CHECK(bg_quad != nullptr, "background quad at col 0 found");

    if (bg_quad) {
        uint16_t expected_width = (uint16_t)(cell_w * 2);
        CHECK(bg_quad->size_x == expected_width,
              "bg size_x == cell_w * 2 (wide char spans 2 cells)");

        printf("  [INFO] cell_w=%u, bg.size_x=%u, expected=%u\n",
               cell_w, bg_quad->size_x, expected_width);

        CHECK(bg_quad->size_y == (uint16_t)cell_h,
              "bg size_y == cell_h");
    }
}

// ─── Q3: korean_backspace_empty ───
// Empty CellData produces no text quads (only background quads).

static void test_korean_backspace_empty(TestFixture& f) {
    printf("\n--- Q3: korean_backspace_empty ---\n");

    // All-empty frame: default-initialized CellData has cp_count=0
    RenderFrame frame;
    frame.allocate(80, 24);
    frame.set_row_dirty(0);

    uint32_t cell_w = f.atlas->cell_width();
    uint32_t cell_h = f.atlas->cell_height();
    uint32_t baseline = f.atlas->baseline();

    QuadBuilder builder(cell_w, cell_h, baseline);
    std::vector<QuadInstance> staging(10000);
    uint32_t count = builder.build(frame, *f.atlas, f.context.Get(), std::span(staging));

    CHECK(count > 0, "build() produced quads (backgrounds exist)");

    // No text quads should be present
    uint32_t text_count = 0;
    for (uint32_t i = 0; i < count; i++) {
        if (staging[i].shading_type == 1) {
            text_count++;
        }
    }

    CHECK(text_count == 0, "no text quads for empty cells");
    printf("  [INFO] total quads=%u, text quads=%u\n", count, text_count);
}

// ─── main ───

int main() {
    printf("=== QuadBuilder Korean Glyph Test (Headless WARP) ===\n");

    auto fixture = create_fixture();
    if (!fixture.valid) {
        printf("\n[FATAL] Fixture creation failed. Aborting.\n");
        return 1;
    }

    printf("[INFO] WARP device + GlyphAtlas ready (cell=%ux%u, baseline=%u)\n",
           fixture.atlas->cell_width(),
           fixture.atlas->cell_height(),
           fixture.atlas->baseline());

    test_korean_glyph_exists(fixture);
    test_korean_wide_char(fixture);
    test_korean_backspace_empty(fixture);

    printf("\n=== Results: %d PASS, %d FAIL ===\n", g_pass, g_fail);
    return g_fail > 0 ? 1 : 0;
}
