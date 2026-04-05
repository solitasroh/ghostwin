#pragma once

/// @file file_watcher.h
/// RAII wrapper for ReadDirectoryChangesW.
/// Design Section 5.2 참조.

#include <filesystem>
#include <functional>
#include <thread>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace ghostwin::settings {

class FileWatcherRAII {
public:
    using Callback = std::function<void()>;

    FileWatcherRAII(std::filesystem::path watch_dir, Callback on_change);
    ~FileWatcherRAII();

    FileWatcherRAII(const FileWatcherRAII&) = delete;
    FileWatcherRAII& operator=(const FileWatcherRAII&) = delete;

private:
    void watch_thread_func();

    std::filesystem::path m_dir;
    Callback m_on_change;
    HANDLE m_dir_handle = INVALID_HANDLE_VALUE;
    HANDLE m_stop_event = nullptr;
    std::jthread m_thread;
};

} // namespace ghostwin::settings
