#pragma once

/// @file render_constants.h
/// Renderer-wide default constants.

#include <cstdint>

namespace ghostwin::constants {

/// Default background color (Catppuccin Mocha #1E1E2E).
/// Overridable from Settings at runtime via DX11Renderer::set_clear_color().
constexpr uint32_t kDefaultBgColor = 0x1E1E2E;

} // namespace ghostwin::constants
