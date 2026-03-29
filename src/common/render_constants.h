#pragma once

/// @file render_constants.h
/// Compile-time constants for the Phase 3 renderer.
/// Replaces magic numbers throughout the codebase.

#include <cstdint>

namespace ghostwin::constants {

// Terminal defaults
constexpr uint16_t kDefaultCols = 80;
constexpr uint16_t kDefaultRows = 24;

// CellData
constexpr uint8_t kMaxCodepoints = 4;

// Resize debounce
constexpr uint32_t kResizeDebounceMs = 100;

// Swapchain
constexpr uint32_t kSwapchainBufferCount = 2;

// Glyph atlas
constexpr uint32_t kInitialAtlasSize = 1024;
constexpr uint32_t kMaxAtlasSize = 4096;

// Font
constexpr float kDefaultFontSizePt = 12.0f;

// Instance buffer
constexpr uint32_t kQuadInstanceSize = 32;
constexpr uint32_t kIndexCount = 6;
constexpr uint32_t kInstanceMultiplier = 3;  // bg + text + decoration

// Dirty row tracking
constexpr uint16_t kMaxRows = 256;

// Cursor blink (GetCaretBlinkTime fallback)
constexpr uint32_t kDefaultBlinkIntervalMs = 530;

} // namespace ghostwin::constants
