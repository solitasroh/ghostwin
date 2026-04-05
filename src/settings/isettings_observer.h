#pragma once

/// @file isettings_observer.h
/// Interface: 설정 변경 통보 수신자.

#include "app_configuration.h"

namespace ghostwin::settings {

class ISettingsObserver {
public:
    virtual ~ISettingsObserver() = default;
    virtual void on_settings_changed(
        const AppConfiguration& config, ChangedFlags flags) = 0;
};

} // namespace ghostwin::settings
