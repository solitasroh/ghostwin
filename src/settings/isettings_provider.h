#pragma once

/// @file isettings_provider.h
/// Interface: 설정 읽기 + Observer 등록.

#include "app_configuration.h"
#include "isettings_observer.h"

namespace ghostwin::settings {

class ISettingsProvider {
public:
    virtual ~ISettingsProvider() = default;
    virtual const AppConfiguration& settings() const = 0;
    virtual const ResolvedColors& resolved_colors() const = 0;
    virtual void register_observer(ISettingsObserver* obs) = 0;
    virtual void unregister_observer(ISettingsObserver* obs) = 0;
};

} // namespace ghostwin::settings
