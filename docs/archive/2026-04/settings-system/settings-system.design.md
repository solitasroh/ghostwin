# settings-system Design Document

> **Summary**: Phase 5-D — JSON 설정 시스템. CMUX 3-domain 구조(`terminal`, `multiplexer`, `agent`)로 하드코딩 제거 + 런타임 리로드 + 10개 내장 테마 + Action ID 기반 키바인딩.
>
> **Project**: GhostWin Terminal
> **Author**: 노수장
> **Date**: 2026-04-05
> **Status**: Draft
> **Planning Doc**: [settings-system.plan.md](../../01-plan/features/settings-system.plan.md)
> **Dependency**: 없음 (독립)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 폰트(`JetBrainsMono NF`, `11.25f`), 배경색(`#1E1E2E`), 키바인딩(`Ctrl+T/W/Tab`)이 `winui_app.cpp`/`dx11_renderer.cpp`에 하드코딩. CMUX 도메인(알림 링, 사이드바 메타데이터, Socket API)을 제어할 설정 구조 부재 |
| **Solution** | `%APPDATA%/GhostWin/ghostwin.json` 단일 JSON. ISettingsProvider 인터페이스 + Observer 패턴으로 서브시스템 분리. nlohmann/json + ReadDirectoryChangesW 런타임 리로드 |
| **Function/UX Effect** | JSON 편집 → 저장 즉시 반영. 폰트/테마/키바인딩/사이드바/알림 색상 모두 런타임 변경 가능 |
| **Core Value** | WT/cmux 수준 커스터마이징. Clean Architecture 기반으로 Phase 5-D-2 GUI 패널 + Phase 6 에이전트 기능의 설정 인프라 확보 |

---

## 1. Overview

### 1.1 Design Goals

1. **하드코딩 제거**: `winui_app.cpp`의 폰트/색상 리터럴 → `AppConfiguration` 구조체에서 읽기
2. **CMUX 3-domain**: `terminal` / `multiplexer` / `agent` 섹션 분리 (단일 JSON)
3. **런타임 리로드**: ReadDirectoryChangesW + 200ms debounce → 파일 변경 즉시 반영
4. **Observer 패턴**: ISettingsObserver로 Renderer/Sidebar/KeyMap 등 독립 통보
5. **10개 내장 테마**: constexpr 배열로 소스 내장. `theme` 필드로 선택
6. **Action ID 키바인딩**: HandleKeyDown의 하드코딩 → KeyMap 룩업으로 전환
7. **에러 내성**: 잘못된 JSON → LOG_W + 이전 설정 유지 (크래시 방지)

### 1.2 Design Principles (Aligned with Plan)

- **Clean Architecture**: Domain(Entity), Interface, Infrastructure 계층 엄격 분리. C 스타일 절차적 코드 지양 및 의존성 역전(ISettingsProvider) 활용
- **RAII**: `FileWatcherRAII`를 통한 파일 핸들(`HANDLE`) 및 백그라운드 스레드(`std::jthread`)의 완벽한 수명 주기 관리 보장
- **Design Patterns**: 
  - **Observer Pattern**: `ISettingsObserver`를 통해 각 렌더러/UI 컴포넌트가 변경 통보를 받을 수 있도록 구현
  - **Command Pattern**: `KeyMap`과 Action ID를 통해 하드코딩 분기를 제거하고 키 입력을 명령 처리기로 매핑
- **Modern C++ & Thread Safety**: C++20/23 적극 활용. `std::shared_mutex`로 읽기는 lock-free 수준, 설정 변경 시에만 독점 lock 획득

### 1.3 현재 하드코딩 위치 (제거 대상)

| 위치 | 하드코딩 값 | 설정 키 |
|------|------------|---------|
| `winui_app.cpp:1632` | `L"JetBrainsMono NF"` | `terminal.font.family` |
| `winui_app.cpp:1633` | `11.25f` | `terminal.font.size` |
| `winui_app.cpp:1733-34` | 같은 값 (DPI 재생성) | 같은 키 |
| `dx11_renderer.cpp:502` | `30/255, 30/255, 46/255` (#1E1E2E) | `terminal.colors.background` |
| `winui_app.cpp:318` | `ctrl && vk == 'T'` | `keybindings.workspace.create` |
| `winui_app.cpp:325` | `ctrl && vk == 'W'` | `keybindings.workspace.close` |
| `winui_app.cpp:332` | `ctrl && vk == VK_TAB` | `keybindings.workspace.next` |
| `winui_app.cpp:351` | `ctrl && shift && vk == 'B'` | `keybindings.sidebar.toggle` |

---

## 2. Architecture

### 2.1 Class Diagram

```
  ┌──────────────────────┐
  │  ISettingsProvider    │ (인터페이스)
  ├──────────────────────┤
  │ + settings() const   │
  │ + register_observer()│
  │ + unregister_obs()   │
  └──────────┬───────────┘
             │ implements
  ┌──────────▼───────────┐     ┌─────────────────────┐
  │   SettingsManager    │────▶│  FileWatcherRAII    │
  ├──────────────────────┤     ├─────────────────────┤
  │ - m_config           │     │ - m_dir_handle      │
  │ - m_mutex (shared)   │     │ - m_watch_thread    │
  │ - m_observers[]      │     │   (std::jthread)    │
  │ - m_watcher (RAII)   │     │ - m_stop_event      │
  ├──────────────────────┤     └─────────────────────┘
  │ + load() → parse     │
  │ + save() → serialize │
  │ + reload() → diff    │
  └──────────────────────┘
             │ notifies
  ┌──────────▼───────────┐
  │ ISettingsObserver     │ (인터페이스)
  ├──────────────────────┤
  │ + on_settings_changed│
  │   (config, flags)    │
  └──────────────────────┘
       △          △
       │          │
  ┌────┴───┐  ┌──┴──────────┐
  │ App*   │  │ KeyMap       │
  │(bridge)│  │(HandleKeyDown│
  └────────┘  │ 리팩토링)    │
              └──────────────┘
  * GhostWinApp은 직접 Observer 구현하지 않고
    bridge lambda로 font/color/window 변경 처리
```

### 2.2 데이터 흐름

```
ghostwin.json 저장
       │
       ▼
FileWatcherRAII (ReadDirectoryChangesW, 별도 스레드)
       │ 200ms debounce
       ▼
DispatcherQueue.TryEnqueue() → UI 스레드
       │
       ▼
SettingsManager::reload()
  1. JSON 파싱 → 새 AppConfiguration
  2. diff(old, new) → ChangedFlags
  3. shared_lock 해제 후 unique_lock으로 교체
  4. m_observers 순회 → on_settings_changed(config, flags)
       │
       ├── Font 변경 → GlyphAtlas 재생성 (atomic swap)
       ├── Colors 변경 → clear_color 업데이트 + 전체 dirty
       ├── Window 변경 → padding/mica 재적용
       └── Keybindings 변경 → KeyMap::rebuild()
```

### 2.3 설정 적용 시점

| 타이밍 | 처리 |
|--------|------|
| 앱 시작 | `SettingsManager::load()` → 없으면 기본 생성 → `StartTerminal()` 진입 전 완료 |
| 런타임 리로드 | FileWatcher → UI 스레드 dispatch → `reload()` → Observer 통보 |
| DPI 변경 | 기존 DPI 로직에서 `m_settings->settings().terminal.font` 읽기 |

---

## 3. Domain Components

### 3.1 AppConfiguration (Value Object)

```cpp
// src/settings/app_configuration.h
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <unordered_map>

namespace ghostwin::settings {

struct TerminalSettings {
    struct Font {
        std::wstring family = L"JetBrainsMono NF";
        float size_pt = 11.25f;
        float cell_width_scale = 1.0f;
        float cell_height_scale = 1.0f;
        float glyph_offset_x = 0.0f;
        float glyph_offset_y = 0.0f;
    } font;

    struct Colors {
        std::string theme = "catppuccin-mocha";  // 내장 테마 이름
        // 개별 오버라이드 (theme보다 우선)
        std::optional<uint32_t> background;  // RGB (no alpha)
        std::optional<uint32_t> foreground;
        std::optional<uint32_t> cursor;
        std::optional<uint32_t> palette[16];
        float background_opacity = 1.0f;     // 0.0~1.0
    } colors;

    struct Cursor {
        std::string style = "block";   // "block", "bar", "underline"
        bool blinking = true;
    } cursor;

    struct Window {
        float padding_left = 8.f;
        float padding_top = 4.f;
        float padding_right = 8.f;
        float padding_bottom = 4.f;
        bool mica_enabled = true;
        bool dynamic_padding = true;
    } window;
};

struct MultiplexerSettings {
    struct Sidebar {
        bool visible = true;
        int width = 200;
        bool show_git = true;
        bool show_ports = true;
        bool show_pr = true;
        bool show_cwd = true;
        bool show_latest_alert = true;
    } sidebar;

    struct Behavior {
        struct AutoRestore {
            bool layout = true;
            bool cwd = true;
            bool scrollback = false;
            bool browser_history = false;
        } auto_restore;
    } behavior;
};

struct AgentSettings {
    struct Socket {
        bool enabled = true;
        std::string path = "\\\\.\\pipe\\ghostwin";
        std::string mode = "process_only"; // "off", "process_only", "allow_all"
    } socket;

    struct Notifications {
        float ring_width = 2.5f;
        struct StateColors {
            uint32_t waiting   = 0x89b4fa;
            uint32_t running   = 0xa6e3a1;
            uint32_t error     = 0xf38ba8;
            uint32_t completed = 0xa6adc8;
        } colors;
        struct Panel {
            std::string position = "right"; // "right", "bottom"
            bool auto_hide = false;
        } panel;
        struct DesktopToast {
            bool enabled = true;
            bool suppress_when_focused = true;
        } desktop_toast;
    } notifications;

    struct Progress {
        bool visible = true;
        uint32_t color = 0xf9e2af;
    } progress;

    struct Browser {
        bool enabled = true;
        bool automation_allowed = true;
    } browser;
};

struct AppConfiguration {
    TerminalSettings terminal;
    MultiplexerSettings multiplexer;
    AgentSettings agent;
    std::unordered_map<std::string, std::string> keybindings;

    bool operator==(const AppConfiguration&) const = default;
};

/// 변경 영역 플래그 (bitmask)
enum class ChangedFlags : uint16_t {
    None               = 0,
    TerminalFont       = 1 << 0, // 폰트 변경 (아틀라스 재생성 필요)
    TerminalColors     = 1 << 1, // 배경/팔레트 색상 변경 (즉시 렌더 업데이트)
    TerminalCursor     = 1 << 2,
    TerminalWindow     = 1 << 3, // 패딩/투명도 변경 (레이아웃 재계산)
    MultiplexerSidebar = 1 << 4, // 사이드바 메타데이터 토글/사이즈
    MultiplexerBehavior= 1 << 5, // 세션 복원 정책 규칙
    AgentConfig        = 1 << 6, // 소켓, 알림 판넬, 브라우저 속성 변경
    Keybindings        = 1 << 7, // KeyMap 리빌드 필요
    All                = 0xFFFF,
};
inline ChangedFlags operator|(ChangedFlags a, ChangedFlags b) {
    return static_cast<ChangedFlags>(
        static_cast<uint16_t>(a) | static_cast<uint16_t>(b));
}
inline bool operator&(ChangedFlags a, ChangedFlags b) {
    return (static_cast<uint16_t>(a) & static_cast<uint16_t>(b)) != 0;
}

} // namespace ghostwin::settings
```

### 3.2 해결된 색상 구조체 (테마 + 오버라이드 병합 후)

```cpp
// 테마 적용 + 개별 오버라이드 병합 결과
struct ResolvedColors {
    uint32_t background;
    uint32_t foreground;
    uint32_t cursor;
    uint32_t palette[16];
    float background_opacity;
};

// SettingsManager 내부에서 resolve
ResolvedColors resolve_colors(const TerminalSettings::Colors& colors);
```

---

## 4. Interfaces

### 4.1 ISettingsObserver

```cpp
// src/settings/isettings_observer.h
#pragma once
#include "app_configuration.h"

namespace ghostwin::settings {

class ISettingsObserver {
public:
    virtual ~ISettingsObserver() = default;
    virtual void on_settings_changed(
        const AppConfiguration& config, ChangedFlags flags) = 0;
};

} // namespace ghostwin::settings
```

### 4.2 ISettingsProvider

```cpp
// src/settings/isettings_provider.h
#pragma once
#include "app_configuration.h"
#include "isettings_observer.h"

namespace ghostwin::settings {

class ISettingsProvider {
public:
    virtual ~ISettingsProvider() = default;
    virtual const AppConfiguration& settings() const = 0;
    virtual void register_observer(ISettingsObserver* obs) = 0;
    virtual void unregister_observer(ISettingsObserver* obs) = 0;
};

} // namespace ghostwin::settings
```

---

## 5. Implementation Components

### 5.1 SettingsManager

```cpp
// src/settings/settings_manager.h
#pragma once
#include "isettings_provider.h"
#include <filesystem>
#include <memory>
#include <mutex>
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
    bool load();                   // 앱 시작 시. false = 파일 없음 → 기본 생성
    bool save();                   // GUI 패널용 (Phase 5-D-2)
    void start_watching();         // FileWatcher 시작
    void stop_watching();          // 앱 종료 시

    // ── ISettingsProvider ──
    const AppConfiguration& settings() const override;
    void register_observer(ISettingsObserver* obs) override;
    void unregister_observer(ISettingsObserver* obs) override;

    // ── 외부 트리거 ──
    void reload();                 // FileWatcher 콜백 또는 수동 호출

    // ── 유틸리티 ──
    static std::filesystem::path default_config_path();
    const ResolvedColors& resolved_colors() const;

private:
    bool parse_json(const std::string& json_str);
    std::string serialize_json() const;
    ChangedFlags diff(const AppConfiguration& a, const AppConfiguration& b) const;
    void apply_theme();
    void notify_observers(ChangedFlags flags);
    void create_default_file();

    mutable std::shared_mutex m_mutex;
    AppConfiguration m_config;
    ResolvedColors m_resolved_colors;
    std::filesystem::path m_path;
    std::vector<ISettingsObserver*> m_observers;
    std::unique_ptr<FileWatcherRAII> m_watcher;
};

} // namespace ghostwin::settings
```

### 5.2 FileWatcherRAII

```cpp
// src/settings/file_watcher.h
#pragma once
#include <atomic>
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
```

**구현 핵심**:
- `ReadDirectoryChangesW` + `FILE_NOTIFY_CHANGE_LAST_WRITE`
- `WaitForMultipleObjects(2, {ov.hEvent, m_stop_event}, FALSE, INFINITE)`
- 변경 감지 → `Sleep(200)` debounce → `m_on_change()` 콜백
- 소멸자: `SetEvent(m_stop_event)` → `m_thread.join()` (jthread 자동) → `CloseHandle`

### 5.3 KeyMap

```cpp
// src/settings/key_map.h
#pragma once
#include <cstdint>
#include <optional>
#include <string>
#include <unordered_map>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace ghostwin::settings {

struct KeyCombo {
    bool ctrl = false;
    bool shift = false;
    bool alt = false;
    UINT vk = 0;
};

class KeyMap {
public:
    /// keybindings 맵에서 KeyMap 빌드.
    /// 중복 키 → 마지막 선언 우선 + LOG_W.
    void build(const std::unordered_map<std::string, std::string>& bindings);

    /// 현재 키 조합에 매칭되는 action 반환.
    [[nodiscard]] std::optional<std::string> lookup(
        bool ctrl, bool shift, bool alt, UINT vk) const;

private:
    // packed key (ctrl|shift|alt|vk) → action name
    std::unordered_map<uint32_t, std::string> m_map;

    static uint32_t pack(bool ctrl, bool shift, bool alt, UINT vk);
    static KeyCombo parse_key_string(const std::string& str);
    static UINT vk_from_name(const std::string& name);
};

} // namespace ghostwin::settings
```

**키 문자열 파싱 규칙**:
```
"Ctrl+T"           → {ctrl=true, vk='T'}
"Ctrl+Shift+Tab"   → {ctrl=true, shift=true, vk=VK_TAB}
"Ctrl+Shift+B"     → {ctrl=true, shift=true, vk='B'}
"Ctrl+1"           → {ctrl=true, vk='1'}
"Ctrl+Shift+PageUp" → {ctrl=true, shift=true, vk=VK_PRIOR}
```

**기본 키바인딩 (HandleKeyDown에서 추출)**:

| Action ID | 기본 키 | HandleKeyDown 위치 |
|-----------|--------|-------------------|
| `workspace.create` | `Ctrl+T` | :318 |
| `workspace.close` | `Ctrl+W` | :325 |
| `workspace.next` | `Ctrl+Tab` | :332 |
| `workspace.prev` | `Ctrl+Shift+Tab` | :332 |
| `workspace.select_1`~`9` | `Ctrl+1`~`9` | :340 |
| `sidebar.toggle` | `Ctrl+Shift+B` | :351 |
| `workspace.move_up` | `Ctrl+Shift+PageUp` | :359 |
| `workspace.move_down` | `Ctrl+Shift+PageDown` | :359 |
| `edit.paste` | `Ctrl+V` | :377 |

**Phase 6 예약 (JSON 구조만 정의, 소비자 미구현)**:

| Action ID | 기본 키 | 소비자 |
|-----------|--------|--------|
| `notification.toggle_panel` | `Ctrl+Shift+I` | Phase 6 알림 패널 |
| `notification.jump_unread` | `Ctrl+Shift+U` | Phase 6 미읽음 점프 |
| `surface.split_right` | `Alt+V` | Phase 5-E pane-split |
| `surface.split_down` | `Alt+H` | Phase 5-E pane-split |

### 5.4 내장 테마

```cpp
// src/settings/builtin_themes.h
#pragma once
#include <cstdint>

namespace ghostwin::settings {

struct ColorTheme {
    const char* name;
    uint32_t bg, fg, cursor;
    uint32_t ansi[16];
};

// 10개 테마 — 공식 리포지토리 기반 (Plan 문서 출처 참조)
inline constexpr ColorTheme kBuiltinThemes[] = {
  { "catppuccin-mocha",
    0x1E1E2E, 0xCDD6F4, 0xF5E0DC,
    { 0x45475A, 0xF38BA8, 0xA6E3A1, 0xF9E2AF,
      0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xBAC2DE,
      0x585B70, 0xF38BA8, 0xA6E3A1, 0xF9E2AF,
      0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xA6ADC8 }},
  { "dracula",
    0x282A36, 0xF8F8F2, 0xF8F8F2,
    { 0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C,
      0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2,
      0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5,
      0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF }},
  { "one-dark",
    0x282C34, 0xABB2BF, 0x528BFF,
    { 0x282C34, 0xE06C75, 0x98C379, 0xE5C07B,
      0x61AFEF, 0xC678DD, 0x56B6C2, 0xABB2BF,
      0x5C6370, 0xE06C75, 0x98C379, 0xE5C07B,
      0x61AFEF, 0xC678DD, 0x56B6C2, 0xFFFFFF }},
  { "nord",
    0x2E3440, 0xD8DEE9, 0xD8DEE9,
    { 0x3B4252, 0xBF616A, 0xA3BE8C, 0xEBCB8B,
      0x81A1C1, 0xB48EAD, 0x88C0D0, 0xE5E9F0,
      0x4C566A, 0xBF616A, 0xA3BE8C, 0xEBCB8B,
      0x81A1C1, 0xB48EAD, 0x8FBCBB, 0xECEFF4 }},
  { "gruvbox-dark",
    0x282828, 0xEBDBB2, 0xEBDBB2,
    { 0x282828, 0xCC241D, 0x98971A, 0xD79921,
      0x458588, 0xB16286, 0x689D6A, 0xA89984,
      0x928374, 0xFB4934, 0xB8BB26, 0xFABD2F,
      0x83A598, 0xD3869B, 0x8EC07C, 0xEBDBB2 }},
  { "solarized-dark",
    0x002B36, 0x839496, 0x839496,
    { 0x073642, 0xDC322F, 0x859900, 0xB58900,
      0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
      0x002B36, 0xCB4B16, 0x586E75, 0x657B83,
      0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3 }},
  { "tokyo-night",
    0x1A1B26, 0xC0CAF5, 0xC0CAF5,
    { 0x15161E, 0xF7768E, 0x9ECE6A, 0xE0AF68,
      0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xA9B1D6,
      0x414868, 0xF7768E, 0x9ECE6A, 0xE0AF68,
      0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xC0CAF5 }},
  { "rose-pine",
    0x191724, 0xE0DEF4, 0x524F67,
    { 0x26233A, 0xEB6F92, 0x31748F, 0xF6C177,
      0x9CCFD8, 0xC4A7E7, 0xEBBCBA, 0xE0DEF4,
      0x6E6A86, 0xEB6F92, 0x31748F, 0xF6C177,
      0x9CCFD8, 0xC4A7E7, 0xEBBCBA, 0xE0DEF4 }},
  { "kanagawa",
    0x1F1F28, 0xDCD7BA, 0xC8C093,
    { 0x16161D, 0xC34043, 0x76946A, 0xC0A36E,
      0x7E9CD8, 0x957FB8, 0x6A9589, 0xC8C093,
      0x727169, 0xE82424, 0x98BB6C, 0xE6C384,
      0x7FB4CA, 0x938AA9, 0x7AA89F, 0xDCD7BA }},
  { "everforest-dark",
    0x2D353B, 0xD3C6AA, 0xD3C6AA,
    { 0x343F44, 0xE67E80, 0xA7C080, 0xDBBC7F,
      0x7FBBB3, 0xD699B6, 0x83C092, 0xD3C6AA,
      0x859289, 0xE67E80, 0xA7C080, 0xDBBC7F,
      0x7FBBB3, 0xD699B6, 0x83C092, 0xD3C6AA }},
};

inline constexpr size_t kBuiltinThemeCount =
    sizeof(kBuiltinThemes) / sizeof(kBuiltinThemes[0]);

/// 이름으로 테마 찾기. 없으면 nullptr.
inline const ColorTheme* find_theme(const char* name) {
    for (size_t i = 0; i < kBuiltinThemeCount; ++i)
        if (std::string_view(kBuiltinThemes[i].name) == name)
            return &kBuiltinThemes[i];
    return nullptr;
}

} // namespace ghostwin::settings
```

---

## 6. JSON Schema (전체 명세)

```jsonc
// %APPDATA%/GhostWin/ghostwin.json
{
  "terminal": {
    "font": {
      "family": "JetBrainsMono NF",
      "size": 11.25,
      "cell_width_scale": 1.0,
      "cell_height_scale": 1.0,
      "glyph_offset_x": 0.0,
      "glyph_offset_y": 0.0
    },
    "colors": {
      "theme": "catppuccin-mocha",
      "background": "#1e1e2e",
      "foreground": "#cdd6f4",
      "cursor": "#f5e0dc",
      "palette": [
        "#45475a", "#f38ba8", "#a6e3a1", "#f9e2af",
        "#89b4fa", "#f5c2e7", "#94e2d5", "#bac2de",
        "#585b70", "#f38ba8", "#a6e3a1", "#f9e2af",
        "#89b4fa", "#f5c2e7", "#94e2d5", "#a6adc8"
      ],
      "background_opacity": 1.0
    },
    "cursor": {
      "style": "block",
      "blinking": true
    },
    "window": {
      "padding": { "left": 8, "top": 4, "right": 8, "bottom": 4 },
      "mica_enabled": true,
      "dynamic_padding": true
    }
  },
  "multiplexer": {
    "sidebar": {
      "visible": true,
      "width": 200,
      "show_git": true,
      "show_ports": true,
      "show_pr": true,
      "show_cwd": true,
      "show_latest_alert": true
    },
    "behavior": {
      "auto_restore": {
        "layout": true,
        "cwd": true,
        "scrollback": false,
        "browser_history": false
      }
    }
  },
  "agent": {
    "socket": {
      "enabled": true,
      "path": "\\\\.\\pipe\\ghostwin",
      "mode": "process_only"
    },
    "notifications": {
      "ring_width": 2.5,
      "colors": {
        "waiting": "#89b4fa",
        "running": "#a6e3a1",
        "error": "#f38ba8",
        "completed": "#a6adc8"
      },
      "panel": { "position": "right", "auto_hide": false },
      "desktop_toast": { "enabled": true, "suppress_when_focused": true }
    },
    "progress": { "visible": true, "color": "#f9e2af" },
    "browser": { "enabled": true, "automation_allowed": true }
  },
  "keybindings": {
    "workspace.create": "Ctrl+T",
    "workspace.close": "Ctrl+W",
    "workspace.next": "Ctrl+Tab",
    "workspace.prev": "Ctrl+Shift+Tab",
    "workspace.select_1": "Ctrl+1",
    "workspace.select_2": "Ctrl+2",
    "workspace.select_3": "Ctrl+3",
    "workspace.select_4": "Ctrl+4",
    "workspace.select_5": "Ctrl+5",
    "workspace.select_6": "Ctrl+6",
    "workspace.select_7": "Ctrl+7",
    "workspace.select_8": "Ctrl+8",
    "workspace.select_9": "Ctrl+9",
    "workspace.move_up": "Ctrl+Shift+PageUp",
    "workspace.move_down": "Ctrl+Shift+PageDown",
    "sidebar.toggle": "Ctrl+Shift+B",
    "edit.paste": "Ctrl+V",
    "notification.toggle_panel": "Ctrl+Shift+I",
    "notification.jump_unread": "Ctrl+Shift+U",
    "surface.split_right": "Alt+V",
    "surface.split_down": "Alt+H"
  }
}
```

**JSON 파싱 규칙**:
- 누락된 키 → 기본값 사용 (partial JSON 허용)
- `colors.background` 등 개별 지정 → `theme`보다 우선
- `theme` 지정 + 개별 미지정 → 테마 값 사용
- 색상 값 형식: `"#RRGGBB"` 문자열 → `uint32_t` 변환

---

## 7. Integration Plan (기존 코드 변경)

### 7.1 GhostWinApp 변경

```cpp
// winui_app.h — 추가 멤버
#include "settings/settings_manager.h"
#include "settings/key_map.h"

class GhostWinApp : ... {
    // 추가
    std::unique_ptr<settings::SettingsManager> m_settings;
    settings::KeyMap m_keymap;

    void on_settings_changed(const settings::AppConfiguration& config,
                             settings::ChangedFlags flags);
};
```

### 7.2 StartTerminal 변경

```cpp
// Before:
AtlasConfig acfg;
acfg.font_family = L"JetBrainsMono NF";   // 삭제
acfg.font_size_pt = 11.25f;                // 삭제

// After:
const auto& font = m_settings->settings().terminal.font;
AtlasConfig acfg;
acfg.font_family = font.family.c_str();
acfg.font_size_pt = font.size_pt;
acfg.cell_width_scale = font.cell_width_scale;
acfg.cell_height_scale = font.cell_height_scale;
acfg.glyph_offset_x = font.glyph_offset_x;
acfg.glyph_offset_y = font.glyph_offset_y;
```

### 7.3 DX11Renderer clear_color 변경

```cpp
// Before:
float clear_color[4] = { 30.f/255.f, 30.f/255.f, 46.f/255.f, 1.0f };

// After: SettingsManager에서 resolved_colors 읽기
// clear_color는 render loop 시작 시 매 프레임 또는 변경 시 업데이트
```

### 7.4 HandleKeyDown 리팩토링

```cpp
// Before (하드코딩):
if (ctrl && !shift && vk == 'T') {
    m_tab_sidebar.request_new_tab();
    return true;
}

// After (KeyMap 룩업):
auto action = m_keymap.lookup(ctrl, shift, alt, (UINT)vk);
if (!action) {
    // 커스텀 키바인딩에 없는 키 → 기존 VT 시퀀스 처리 계속
} else if (*action == "workspace.create") {
    cancelComposition();
    m_tab_sidebar.request_new_tab();
    return true;
} else if (*action == "workspace.close") {
    cancelComposition();
    m_tab_sidebar.request_close_active();
    return true;
}
// ... action dispatch table
```

### 7.5 CMakeLists.txt 변경

```cmake
# ── Settings library (Phase 5-D) ──
add_library(settings STATIC
    src/settings/settings_manager.cpp
    src/settings/key_map.cpp
    src/settings/file_watcher.cpp
)
target_include_directories(settings PUBLIC src)
target_link_libraries(settings PUBLIC ghostwin_common)

# ghostwin_winui에 settings 링크 추가
target_link_libraries(ghostwin_winui PRIVATE
    session renderer conpty settings  # ← settings 추가
    ...
)
```

---

## 8. File Structure

```
src/settings/
├── app_configuration.h       // Domain: 구조체 + ChangedFlags
├── isettings_observer.h      // Interface: Observer
├── isettings_provider.h      // Interface: Provider
├── settings_manager.h        // Service: 선언
├── settings_manager.cpp      // Service: JSON 로드/저장/리로드/diff
├── file_watcher.h            // Infra: RAII FileWatcher 선언
├── file_watcher.cpp          // Infra: ReadDirectoryChangesW 구현
├── key_map.h                 // Service: KeyMap 선언
├── key_map.cpp               // Service: 키 문자열 파싱 + 룩업
├── builtin_themes.h          // Domain: 10개 내장 테마 constexpr
└── json_serializers.h        // Infra: nlohmann from_json/to_json

third_party/nlohmann/
└── json.hpp                  // nlohmann/json v3.x single-header
```

---

## 9. Functional Requirements Mapping

| FR | 요구사항 | 구현 컴포넌트 | 검증 방법 |
|----|---------|-------------|----------|
| FR-01 | JSON 로드 → AppConfiguration | SettingsManager::load() | 앱 시작 시 LOG_I 출력 |
| FR-02 | 첫 실행 기본 파일 생성 | SettingsManager::create_default_file() | 파일 부재 시 자동 생성 확인 |
| FR-03 | 폰트 적용 | on_settings_changed → AtlasConfig | 폰트 변경 후 글리프 확인 |
| FR-04 | 색상 적용 (clear_color + palette) | resolve_colors() + Renderer | 테마 변경 후 배경색 확인 |
| FR-05 | 10개 내장 테마 | builtin_themes.h + find_theme() | theme 필드 변경 → 색상 반영 |
| FR-06 | KeyMap + HandleKeyDown 리팩토링 | KeyMap::build/lookup | 키바인딩 변경 후 동작 확인 |
| FR-07 | 윈도우 설정 (padding, mica) | on_settings_changed → QuadBuilder | padding 변경 → 여백 확인 |
| FR-08 | 런타임 리로드 (< 100ms) | FileWatcherRAII + reload() | JSON 저장 → 화면 반영 시간 |
| FR-09 | 에러 핸들링 (잘못된 JSON) | try/catch in parse_json | 잘못된 JSON → 이전 설정 유지 |
| FR-10 | colors 우선순위 (직접 > 테마) | resolve_colors() | theme + background 동시 지정 테스트 |

---

## 10. Non-Functional Requirements

| NFR | 목표 | 측정 방법 |
|-----|------|-----------|
| NFR-01 | JSON 로드 < 10ms | LOG_I 타임스탬프 |
| NFR-02 | 런타임 리로드 < 100ms | 파일 저장 → 화면 반영 |
| NFR-03 | 기존 테스트 유지 | 10/10 PASS |
| NFR-04 | 감시 스레드 idle CPU < 0.1% | Task Manager |
| NFR-05 | nlohmann/json single-header | 추가 DLL 없음 |
| NFR-06 | 200ms debounce | 에디터 임시파일 중복 방지 |

---

## 11. Implementation Order

```
Step 1: 기반 구조 (Day 1)
  ├─ nlohmann/json 추가 (third_party/)
  ├─ app_configuration.h (도메인 구조체)
  ├─ isettings_observer.h / isettings_provider.h
  └─ builtin_themes.h (10개 테마)

Step 2: SettingsManager 코어 (Day 1-2)
  ├─ settings_manager.h/cpp (load/save/parse/serialize)
  ├─ json_serializers.h (from_json/to_json)
  ├─ diff() + resolve_colors()
  └─ create_default_file() (첫 실행)

Step 3: GhostWinApp 연동 (Day 2)
  ├─ winui_app.h에 m_settings 멤버 추가
  ├─ OnLaunched에서 load() 호출
  ├─ StartTerminal에서 settings 읽기 (하드코딩 제거)
  ├─ DPI 재생성에서도 settings 읽기
  └─ clear_color를 resolved_colors에서 읽기

Step 4: KeyMap + HandleKeyDown (Day 2-3)
  ├─ key_map.h/cpp
  ├─ HandleKeyDown을 KeyMap 룩업으로 리팩토링
  └─ 기본 키바인딩 동작 검증

Step 5: FileWatcher + 런타임 리로드 (Day 3)
  ├─ file_watcher.h/cpp (RAII)
  ├─ SettingsManager::start_watching()
  ├─ on_settings_changed() 콜백 체인
  └─ 폰트/테마/키바인딩 런타임 변경 검증

Step 6: 에러 핸들링 + CMake 통합 (Day 3)
  ├─ 잘못된 JSON → LOG_W + 이전 설정 유지
  ├─ CMakeLists.txt에 settings 라이브러리 추가
  └─ 전체 빌드 + 기존 테스트 PASS 확인
```

---

## 12. Risks

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| GlyphAtlas 재생성 깜빡임 | 중 | 중 | 새 Atlas 생성 완료 후 atomic swap |
| ReadDirectoryChangesW 에디터 임시파일 | 하 | 상 | 200ms debounce + 파일명 필터 |
| nlohmann/json 컴파일 시간 증가 | 하 | 중 | `json_serializers.cpp` 단일 TU로 격리 |
| 잘못된 JSON 크래시 | 상 | 중 | try/catch + 이전 설정 유지 |
| 키바인딩 충돌 | 하 | 하 | 마지막 선언 우선 + LOG_W |
| std::shared_mutex 오버헤드 | 하 | 하 | 설정 읽기 빈도 낮음 (프레임당 0~1회) |
| Multiplexer/Agent 설정 소비자 미구현 | — | — | JSON 구조만 정의, 미구현 섹션은 파싱만 수행 |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.0 | 2026-04-05 | Initial skeletal design (Clean Architecture) | AI Agent |
| 2.0 | 2026-04-05 | CMUX 확장 Plan 기반 상세 설계 (FR/NFR, JSON 전체 스키마, 테마 데이터, KeyMap, 통합 계획) | 노수장 |
| 2.1 | 2026-04-05 | Plan 제약사항(패턴/C++) 동기화 및 ChangedFlags 비트마스크 오버랩 최적화 | AI Agent |
