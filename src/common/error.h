#pragma once

/// @file error.h
/// Lightweight error reporting for GhostWin.
/// Phase 3: used by renderer factory functions.

#include <cstdint>

namespace ghostwin {

enum class ErrorCode : uint32_t {
    Ok = 0,
    DeviceCreationFailed,
    SwapchainCreationFailed,
    ShaderCompilationFailed,
    AtlasOverflow,
    OutOfMemory,
    InvalidArgument,
    DeviceRemoved,
};

struct Error {
    ErrorCode code = ErrorCode::Ok;
    const char* message = nullptr;

    [[nodiscard]] bool ok() const { return code == ErrorCode::Ok; }
};

} // namespace ghostwin
