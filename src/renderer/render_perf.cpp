#include "render_perf.h"

#include <cstdlib>

namespace ghostwin {

namespace {

/// File-scope static: initialized once before main(). Subsequent
/// `perf_enabled()` calls are plain loads — no getenv, no atomic, no lock.
///
/// Enabled when `GHOSTWIN_RENDER_PERF` is set AND non-empty AND not "0".
const bool g_perf_enabled = []() {
    const char* env = std::getenv("GHOSTWIN_RENDER_PERF");
    if (!env || env[0] == '\0') return false;
    if (env[0] == '0' && env[1] == '\0') return false;
    return true;
}();

} // namespace

bool perf_enabled() { return g_perf_enabled; }

} // namespace ghostwin
