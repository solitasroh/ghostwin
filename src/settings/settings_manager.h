#pragma once

/// @file settings_manager.h
/// Service: JSON 설정 로드/저장/리로드 + Observer 통보.
/// Design Section 5.1 참조.

#include "isettings_provider.h"
#include <algorithm>
#include <filesystem>
#include <functional>
#include <memory>
#include <shared_mutex>
#include <vector>

namespace ghostwin::settings {

class FileWatcherRAII;

class SettingsManager : public ISettingsProvider {
public:
    explicit SettingsManager(std::filesystem::path config_path);
    ~SettingsManager();

    SettingsManager(const SettingsManager&) = delete;
    SettingsManager& operator=(const SettingsManager&) = delete;

    // ── Lifecycle ──
    bool load();
    bool save();

    /// Start file watching. on_file_changed is called on the watch thread.
    /// Caller should wrap reload() in DispatcherQueue.TryEnqueue() for UI safety.
    using FileChangedCallback = std::function<void()>;
    void start_watching(FileChangedCallback on_file_changed = {});
    void stop_watching();

    // ── ISettingsProvider ──
    const AppConfiguration& settings() const override;
    const ResolvedColors& resolved_colors() const override;
    void register_observer(ISettingsObserver* obs) override;
    void unregister_observer(ISettingsObserver* obs) override;

    // ── 외부 트리거 ──
    void reload();

    // ── 유틸리티 ──
    static std::filesystem::path default_config_path();

private:
    bool parse_json(const std::string& json_str);
    std::string serialize_json() const;
    ChangedFlags diff(const AppConfiguration& prev,
                      const AppConfiguration& next) const;
    void resolve_theme_colors();
    void notify_observers(ChangedFlags flags);
    void create_default_file();
    void populate_default_keybindings(AppConfiguration& config);

    mutable std::shared_mutex m_mutex;
    AppConfiguration m_config;
    ResolvedColors m_resolved;
    std::filesystem::path m_path;
    std::vector<ISettingsObserver*> m_observers;
    std::unique_ptr<FileWatcherRAII> m_watcher;
};

} // namespace ghostwin::settings
