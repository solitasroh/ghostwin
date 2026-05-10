/// @file surface_manager_state_test.cpp
/// Terminal Display Hardening: RenderSurface local state contracts.

#include "surface_manager.h"

#include <cstdio>

#define CHECK(cond, msg) do { if (!(cond)) { std::printf("[FAIL] %s\n", msg); return false; } } while (0)

static bool test_dim_factor_bumps_surface_visual_epoch_only_on_change() {
    RenderSurface surface{};
    const auto initial = surface.surface_visual_epoch.load();

    CHECK(surface.set_dim_factor(0.4f), "first dim change should report changed");
    CHECK(surface.surface_visual_epoch.load() == initial + 1,
          "dim change should bump surface visual epoch");

    CHECK(!surface.set_dim_factor(0.4f), "same dim value should not report changed");
    CHECK(surface.surface_visual_epoch.load() == initial + 1,
          "same dim value should not bump surface visual epoch");

    CHECK(surface.set_dim_factor(0.0f), "clearing dim should report changed");
    CHECK(surface.surface_visual_epoch.load() == initial + 2,
          "clearing dim should bump surface visual epoch");

    return true;
}

static bool test_pending_resize_consumes_latest_coherent_request_once() {
    RenderSurface surface{};

    surface.set_pending_resize(80, 24);
    surface.set_pending_resize(120, 40);

    SurfaceResizeRequest request{};
    CHECK(surface.consume_pending_resize(request), "pending resize should be consumed");
    CHECK(request.width_px == 120, "latest resize width should win");
    CHECK(request.height_px == 40, "latest resize height should win");
    CHECK(request.sequence == 2, "resize sequence should increment per request");
    CHECK(!surface.consume_pending_resize(request), "resize should only be consumed once");

    return true;
}

static bool test_applied_size_is_read_as_one_coherent_pair() {
    RenderSurface surface{};
    surface.set_applied_size(101, 55);

    const auto applied = surface.applied_size();
    CHECK(applied.width_px == 101, "applied width mismatch");
    CHECK(applied.height_px == 55, "applied height mismatch");

    return true;
}

int main() {
    int failed = 0;
    auto run = [&](const char* name, bool (*fn)()) {
        std::printf("[ RUN ] %s\n", name);
        if (fn()) {
            std::printf("[ OK  ] %s\n", name);
        } else {
            ++failed;
        }
    };

    run("dim_factor_bumps_surface_visual_epoch_only_on_change",
        test_dim_factor_bumps_surface_visual_epoch_only_on_change);
    run("pending_resize_consumes_latest_coherent_request_once",
        test_pending_resize_consumes_latest_coherent_request_once);
    run("applied_size_is_read_as_one_coherent_pair",
        test_applied_size_is_read_as_one_coherent_pair);

    if (failed != 0) {
        std::printf("[FAIL] %d surface manager state test(s) failed\n", failed);
        return 1;
    }

    std::printf("[PASS] surface_manager_state_test\n");
    return 0;
}
