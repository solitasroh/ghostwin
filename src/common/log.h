#pragma once

/// @file log.h
/// Thread-safe minimal logger for GhostWin.
/// 4 threads share stderr -- mutex prevents interleaved output.

#include <cstdio>
#include <cstdarg>
#include <mutex>

namespace ghostwin {

enum class LogLevel { Debug, Info, Warn, Error };

inline void log(LogLevel level, const char* tag, const char* fmt, ...) {
    static std::mutex log_mutex;
    static constexpr const char* level_str[] = {"DBG", "INF", "WRN", "ERR"};

    va_list args;
    va_start(args, fmt);
    {
        std::lock_guard lock(log_mutex);
        fprintf(stderr, "[%s][%s] ", level_str[static_cast<int>(level)], tag);
        vfprintf(stderr, fmt, args);
        fputc('\n', stderr);
    }
    va_end(args);
}

#define LOG_D(tag, ...) ::ghostwin::log(::ghostwin::LogLevel::Debug, tag, __VA_ARGS__)
#define LOG_I(tag, ...) ::ghostwin::log(::ghostwin::LogLevel::Info,  tag, __VA_ARGS__)
#define LOG_W(tag, ...) ::ghostwin::log(::ghostwin::LogLevel::Warn,  tag, __VA_ARGS__)
#define LOG_E(tag, ...) ::ghostwin::log(::ghostwin::LogLevel::Error, tag, __VA_ARGS__)

} // namespace ghostwin
