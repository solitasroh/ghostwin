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
    static FILE* logfile = nullptr;
    static bool logfile_init = false;

    {
        std::lock_guard lock(log_mutex);
        if (!logfile_init) {
            logfile_init = true;
            const char* path = getenv("GHOSTWIN_LOG_FILE");
            if (path && path[0] != '\0') {
                logfile = fopen(path, "w");
            }
        }
        // Write to both stderr and logfile
        FILE* outputs[] = { stderr, logfile };
        for (FILE* f : outputs) {
            if (!f) continue;
            va_list args;
            va_start(args, fmt);
            fprintf(f, "[%s][%s] ", level_str[static_cast<int>(level)], tag);
            vfprintf(f, fmt, args);
            fputc('\n', f);
            va_end(args);
            if (f == logfile) fflush(f);
        }
    }
}

#define LOG_D(tag, ...) ::ghostwin::log(::ghostwin::LogLevel::Debug, tag, __VA_ARGS__)
#define LOG_I(tag, ...) ::ghostwin::log(::ghostwin::LogLevel::Info,  tag, __VA_ARGS__)
#define LOG_W(tag, ...) ::ghostwin::log(::ghostwin::LogLevel::Warn,  tag, __VA_ARGS__)
#define LOG_E(tag, ...) ::ghostwin::log(::ghostwin::LogLevel::Error, tag, __VA_ARGS__)

} // namespace ghostwin
