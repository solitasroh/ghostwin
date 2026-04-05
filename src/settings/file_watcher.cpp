/// @file file_watcher.cpp
/// ReadDirectoryChangesW RAII wrapper with 200ms debounce.

#include "file_watcher.h"
#include "common/log.h"

namespace ghostwin::settings {

FileWatcherRAII::FileWatcherRAII(
    std::filesystem::path watch_dir, Callback on_change)
    : m_dir(std::move(watch_dir))
    , m_on_change(std::move(on_change)) {

    m_stop_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!m_stop_event) {
        LOG_W("filewatcher", "CreateEvent failed: %lu", GetLastError());
        return;
    }

    m_dir_handle = CreateFileW(
        m_dir.c_str(),
        FILE_LIST_DIRECTORY,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OVERLAPPED,
        nullptr);

    if (m_dir_handle == INVALID_HANDLE_VALUE) {
        LOG_W("filewatcher", "Cannot open dir for watching: %ls (err=%lu)",
              m_dir.c_str(), GetLastError());
        return;
    }

    m_thread = std::jthread([this](std::stop_token) { watch_thread_func(); });
    LOG_I("filewatcher", "Watching: %ls", m_dir.c_str());
}

FileWatcherRAII::~FileWatcherRAII() {
    if (m_stop_event) SetEvent(m_stop_event);
    if (m_thread.joinable()) m_thread.join();
    if (m_dir_handle != INVALID_HANDLE_VALUE) CloseHandle(m_dir_handle);
    if (m_stop_event) CloseHandle(m_stop_event);
}

void FileWatcherRAII::watch_thread_func() {
    if (m_dir_handle == INVALID_HANDLE_VALUE) return;

    BYTE buf[4096];
    OVERLAPPED ov{};
    ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!ov.hEvent) return;

    while (true) {
        ResetEvent(ov.hEvent);
        DWORD bytes_returned = 0;

        BOOL ok = ReadDirectoryChangesW(
            m_dir_handle, buf, sizeof(buf), FALSE,
            FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_FILE_NAME,
            &bytes_returned, &ov, nullptr);

        if (!ok && GetLastError() != ERROR_IO_PENDING) break;

        HANDLE events[] = { ov.hEvent, m_stop_event };
        DWORD wait = WaitForMultipleObjects(2, events, FALSE, INFINITE);

        if (wait == WAIT_OBJECT_0) {
            // File change detected — 200ms debounce
            Sleep(200);
            if (m_on_change) m_on_change();
        } else {
            // Stop event or error
            CancelIo(m_dir_handle);
            break;
        }
    }

    CloseHandle(ov.hEvent);
}

} // namespace ghostwin::settings
