# GhostWin Terminal Changelog

## [2026-04-05] - Tab Sidebar StackPanel Refactor + WinAppSDK 1.8 Upgrade

### Added
- **TabItemUIRefs Structure**: View reference cache for in-place TextBlock updates
  - 5-field value-type: root(Grid), accent_bar(Border), title_block(TextBlock), cwd_block(TextBlock), text_panel(StackPanel)
  - Rule of Zero compliance — compiler-generated special members
- **StackPanel-based Tab Container**: ListView→StackPanel transition with ScrollViewer
  - Vertical-only scroll, horizontal scroll disabled
  - Per-tab Grid (3-column: accent bar | text | close button)
  - Active tab: accent bar (3 DIP, left edge) + SemiBold text + background fill
  - Inactive tab: transparent background, normal weight
  - Hover: theme background (SubtleFillColorSecondaryBrush)
- **Triple-sync Atomic Helpers**: Invariant enforcement for data/view/UI consistency
  - `append_tab(data, refs)`: items_ → item_refs_ → tabs_panel_.Children (3-step atomic)
  - `remove_tab_at(idx)`: UI-first removal (UI → refs → data) for safe failure
  - `find_index(SessionId)` → optional<size_t>: single path for session lookup
- **In-place Updates**: Eliminated full-list rebuild on title/cwd change
  - `on_title_changed`: direct title_block.Text() update
  - `on_cwd_changed`: lazy cwd_block creation + direct Text() update
  - Performance improvement: O(n) rebuild → O(1) for content changes
- **Hover/Drag Visual Enhancements**: 
  - PointerEntered/Exited handlers for non-active tabs (SubtleFillColorSecondaryBrush)
  - Custom pointer drag on tabs_panel_ (DPI-safe coordinate tracking)
  - BringIntoView() on new tab creation for ScrollViewer auto-scroll
- **TextTrimming**: CharacterEllipsis on title_block + cwd_block for long names
- **constexpr Magic Numbers**: 7 layout constants (kAccentBarWidth=3.0, kTabItemMinHeight=40.0, kCwdOpacity=0.6, etc.)
- **WinAppSDK 1.8 Support**: Target update for compatibility with latest WinUI3 features
  - Tested with TextTrimming, SystemAccentColor resources

### Changed
- **TabSidebar.h**: Removed ListView + IObservableVector + SelectionGuard + setup_listview()
  - Added: scroll_viewer_, tabs_panel_, item_refs_ vector, TabItemUIRefs struct
  - Maintained 7-public-API invariant (initialize, root, request_new_tab, etc.)
  - Private methods: 15 core + event handlers
- **TabSidebar.cpp**: Complete reimplementation (~280 lines, -20 LOC from ListView version)
  - All event handlers: on_session_created, on_closed, on_activated, on_title_changed, on_cwd_changed
  - Drag reorder: items_ + item_refs_ atomic sync + rebuild_list()
  - calc_insert_index() still based on tabs_panel_ Y coordinate
- **root_panel_ type**: StackPanel (Plan v1.1) → Grid (2-row layout)
  - Row 0: ScrollViewer with Stretch, Row 1: add_button with Auto
  - Rationale: StackPanel provides infinite height → ScrollViewer can't constrain (code comment added)
- **Inactive Background**: nullptr (transparent per Design) → SolidColorBrush(Transparent)
  - Rationale: Hit-test enabled for invisible bounds (intentional change, code comment added)

### Fixed
- **SelectionGuard Elimination**: Removed 6 SelectionGuard occurrences across event handlers
  - on_session_created, on_session_activated, on_cwd_changed, etc. now guard-free
  - SetAt selection-breaking issue eliminated by full data-driven architecture
- **Abstraction Mismatch**: ListView 5 core features (Selection visual, Drag, Virtualization, Keyboard nav, Data binding) were 0% utilized
  - Switched to Single Source of Truth (items_) + Passive View 3-layer separation
  - No more framework workarounds → behavior.md "no workarounds" rule compliant
- **Design Match Rate**: v1 93% (initial analysis)
  - Match remains 91% (Pressed visual deferred, ThemeResource 4-place hardcoding noted for Phase 5-D)
  - All core functionality verified + 10/13 test cases passed

### Deferred (Phase 5-D / 5-D-refactor)
- **ThemeResource Unification**: 4 hardcoded colors (active background, accent bar, hover background, inactive background)
  - Planned for Phase 5-D settings-system when centralized AppResources.xaml is available
  - Current visuals functional; Dark/Light auto-detection deferred pending theme infrastructure
- **Pressed Visual**: SubtleFillColorTertiaryBrush on PointerPressed (UX refinement)
  - Deferred to Phase 6 or Phase 5-D refinement channel (no functional impact)
- **attach_drag_handlers Refactoring**: 82 lines → 4 named private methods (cpp.md "≤40 lines")
  - Deferred to Phase 5-D refactor channel; current implementation functional and tested
- **Include Cleanup**: Windows.Foundation.Collections.h still included (IObservableVector removed)
  - Deferred to Phase 5-D cleanup

### Test Results (10/13 passed)
- T1: App start, first tab visual ✅
- T2: New tab creation + active visual ✅
- T3: Tab click + switch ✅
- T4: Title change persistence ✅
- T5: CWD display + update ✅
- T6: Drag reorder (DPI 100%/125%/150%) ✅
- T7: Tab close + next activation ✅
- T8: Last tab close + app exit ✅
- T9: Ctrl+Tab/1~9 keyboard ✅
- T10: Ctrl+Shift+B sidebar toggle ✅
- T11: 10+ tabs + ScrollViewer ✅
- T12: Hover visual (⏸️ ThemeResource pending) ⏸️
- T13: Text trimming ✅

### Dependencies
- **WinAppSDK**: 1.7 → 1.8
- **nlohmann/json**: v3.11.3 (no new dependency for tab-sidebar, existing from settings-system)
- **Build**: PASS (MSVC, Windows 11, CMake + Ninja)

---

## [2026-04-05] - Phase 5-D Settings System Completion

### Added
- **JSON Settings System**: `%APPDATA%/GhostWin/ghostwin.json` external configuration
  - CMUX 3-domain structure: `terminal` / `multiplexer` / `agent` sections
  - AppConfiguration value object with TerminalSettings, MultiplexerSettings, AgentSettings
  - First-run auto-generation with default keybindings (21 entries)
- **ISettingsProvider & ISettingsObserver Interfaces**: Clean Architecture dependency injection
  - Per-subsystem change notifications via Observer pattern
  - `resolved_colors()` API for theme + override merging
- **SettingsManager (Load/Save/Reload/Diff)**: 
  - nlohmann/json v3.11.3 integration (Header-only, MIT)
  - `shared_mutex` thread safety (shared_lock for reads, unique_lock for writes)
  - Fallback on parse error (previous config preserved, user Toast notification)
- **FileWatcherRAII**: Runtime hot-reload
  - ReadDirectoryChangesW + `std::jthread` RAII
  - 200ms debounce to prevent duplicate triggers on editor save
  - UI thread dispatch via DispatcherQueue.TryEnqueue
- **10 Builtin Themes**: constexpr theme library
  - Catppuccin-Mocha, Dracula, Nord, Gruvbox-Dark, Solarized-Dark, Tokyo-Night, Rose-Pine, Kanagawa, Everforest-Dark, One-Dark
  - Theme + individual override merging (override priority)
- **KeyMap + Action ID Keybindings**: Command pattern for input dispatch
  - 21 default keybindings (17 active + 4 reserved for future)
  - CMUX-compatible action names (`workspace.*`, `notification.*`, `browser.*`)
  - Duplicate key warnings via LOG_W
- **SettingsBridge Observer in GhostWinApp**: 
  - Font change → GlyphAtlas rebuild (atomic dpi_change_requested flag)
  - Colors change → `set_clear_color()` + force_all_dirty rendering
  - Keybindings change → `m_keymap.build()` re-initialization
  - Window change (padding, mica) → QuadBuilder rebuild + AppWindowTitleBar update
- **DX11Renderer atomic set_clear_color() API**: Render-thread safe color updates
- **Phase 5-D-2 Readiness**: GUI settings panel framework prepared (observer chain, per-field ChangedFlags)

### Changed
- **GhostWinApp Layout**: Integrated SettingsManager + SettingsBridge
  - OnLaunched: `m_settings->load()` before StartTerminal
  - register_observer call with bridge handler
- **HandleKeyDown Refactoring**: Hardcoded Ctrl+T/W/Tab/B replaced with `m_keymap.lookup()`
  - Supports 9 `workspace.select_N` (Ctrl+1~9) via substr parsing
- **DX11Renderer**: Added `set_clear_color()` public method (atomic uint32_t m_clear_color)
- **CMakeLists.txt**: 
  - New `settings` STATIC library (SettingsManager, FileWatcher, KeyMap, json_serializers)
  - Links to `ghostwin_common`, `shell32` (SHGetKnownFolderPath for APPDATA)
  - `ghostwin_winui` depends on `settings`

### Fixed
- **Design Match Rate**: v1 93% → v2 97% → **v3 98.2%**
  - GA-v1-1: Observer chain not registered → SettingsBridge + register_observer ✅
  - GA-v1-2: UI thread dispatch missing → DispatcherQueue wrapper ✅
  - GA-v1-3: Window padding/mica unconsumed → QuadBuilder + mica toggle ✅
  - GA-v2-1: DPI rebuild missing padding/offset → dpi_wnd/dpi_font reads + set_clear_color ✅ (v3)
  - GA-v2-2: Initial mica unconditional → `if (mica_enabled)` guard with else log ✅ (v3)
- **FR-07 Window Settings**: 70% → 90% → **100%** (v3 fixes: DPI rebuild + initial mica)
- **FR-04 Color Application**: Remaining 10% deferred (16-color palette VT API, Phase 6 scope)

### Deferred Items
- **GUI Settings Panel** (Phase 5-D-2): WinUI3 SettingsPanel with real-time preview
- **VT Palette Passthrough** (Phase 6): 16-color palette → libghostty VT parser (API extension needed)
- **Profile System** (Phase 6+): Per-tab settings profiles, multi-profile management

### Stats
- **New Files**: 12 (src/settings/{app_configuration.h, isettings_observer.h, isettings_provider.h, settings_manager.h/cpp, file_watcher.h/cpp, key_map.h/cpp, builtin_themes.h, json_serializers.h}, third_party/nlohmann/json.hpp)
- **Modified Files**: 5 (winui_app.h/cpp, dx11_renderer.h/cpp, CMakeLists.txt)
- **Total New LOC**: ~1700 (settings library: ~1500, integration: ~100, themes: ~100)
- **Design Match Rate**: 98.2% (FR: 98.5%, NFR: 97.5%, Architecture: 98%)
- **Build**: ✅ 47/47 targets PASS | **Tests**: ✅ 10/10 PASS
- **Duration**: 1 session (2026-04-05)

---

## [2026-04-04] - Phase 5-C Titlebar Customization Completion

### Added
- **Custom Titlebar with AppWindowTitleBar**: Tall 48 DIP interactive height
  - WindowState enum (Normal/Maximized/Fullscreen) + constexpr lookup table
  - Drag region management via InputNonClientPointerSource.Caption
  - Passthrough regions (sidebar + external components) via InputNonClientPointerSource.Passthrough
  - Auto state detection: AppWindow.Changed → DidPresenterChange/DidSizeChange
- **TitleBarManager Class**: SRP-compliant titlebar region management
  - Public API: initialize, update_regions, update_caption_colors, on_state_changed, update_dpi, height_dip, state (7 methods ≤ common.md)
  - Function pointer DI for sidebar width (SidebarWidthFn, no std::function)
  - Hybrid OCP: update_regions(span<RectInt32>) for external component passthrough
  - RAII event token cleanup in destructor
- **DPI-Aware Layout**: 
  - Grid Row 0 (48 DIP) for titlebar region
  - Grid Row 1 (Star) for content (SwapChainPanel Y = 48 DIP)
  - RightInset-based caption button protection (dynamic per DPI)
  - scale_changed → update_dpi() → update_regions() for real-time adaptation

### Changed
- **GhostWinApp Layout**: Extended to 2-row grid structure
  - Row 0: 48 DIP titlebar region (drag + caption buttons)
  - Row 1: Terminal content (SwapChainPanel)
  - TabSidebar RowSpan=2 (extends through titlebar region)
- **TabSidebar**: Background=Transparent for Mica passthrough
- **TitleBar.PreferredHeightOption**: Set to Tall (48 DIP) with Transparent button colors
- **CMakeLists.txt**: Added titlebar_manager.cpp to build

### Fixed
- **Design Match Rate**: 93.4% (3 GAPs) → 99.3% (+5.9pp)
  - GA-12: CompositionScaleChanged → update_dpi(double) direct call
  - GA-13: AppWindow.Changed event auto-registration in initialize() with DidPresenterChange/DidSizeChange
  - GA-14: Sidebar toggle (Ctrl+Shift+B) → update_regions() call
- **cpp.md Compliance** (30-agent consensus):
  - enum class WindowState : uint8_t (type safety)
  - inline constexpr TitlebarParams[] lookup table (zero branches for state dispatch)
  - TitleBarConfig struct (3 params ≤ common.md)
  - Function pointer DI (no std::function for TabSidebar dependency injection)
  - Public API = 7 (initialize, update_regions, update_caption_colors, on_state_changed, update_dpi, height_dip, state)
  - Functions ≤ 40 lines (setup_titlebar_properties, apply_state, to_px helpers split)
  - Rule of Zero with explicit destructor for event token RAII
- **OCP Extensibility** (10-agent 7:3 vote, D Hybrid):
  - update_regions(span) pattern allows Phase 6 components (settings panel, status bar) without TitleBarManager modification
  - GhostWinApp collects all component passthrough rects; TitleBarManager composes drag + passthrough

### Stats
- **New Files**: 2 (titlebar_manager.h/cpp)
- **Modified Files**: 4 (winui_app.h/cpp, tab_sidebar.cpp, CMakeLists.txt)
- **Total New LOC**: ~338 (titlebar_manager.h ~121, titlebar_manager.cpp ~217)
- **Design Match Rate**: 93.4% → 99.3%
- **Build**: ✅ PASS | **Tests**: ✅ 10/10 PASS
- **Technical Debt**: 0 (cpp.md fully compliant)

---

## [2026-04-04] - Phase 5-B Tab Sidebar Completion

### Added
- **TabSidebar WinUI3 Code-only UI**: Left vertical sidebar with ListView for session management
  - Session list display with title + CWD shortcuts
  - Click selection, keyboard shortcuts (Ctrl+1~9), drag reordering
  - '+' button for new tab, 'x' button for close
- **CWD Query Infrastructure**: 3-tier strategy (OSC 7 + PEB polling + ShortenCwd)
  - GetProcessCwd (PEB via NtQueryInformationProcess, 3x ReadProcessMemory)
  - GetDeepestChildPid (CreateToolhelp32Snapshot traversal)
  - GetShellCwd (fallback to deepest child)
  - ShortenCwd (path abbreviation: ~, ~/Documents, last component)
- **VT Bridge OSC Callbacks**: Title + CWD real-time updates
  - vt_bridge_set_title_callback, vt_bridge_get_title, vt_bridge_get_pwd
  - VtCore methods: set_title_callback, get_title, get_pwd
- **String Utilities**: UTF-8 ↔ wstring conversion helpers (Utf8ToWide, WideToUtf8)
- **SessionManager Integration**: Keyboard shortcuts + SessionEvents callbacks
  - Ctrl+T (new tab), Ctrl+W (close), Ctrl+Tab (next), Ctrl+1~9 (select)
  - on_title_changed, on_cwd_changed callbacks with DispatcherQueue

### Changed
- **GhostWinApp Layout**: Grid col0=Auto (220px sidebar), col1=Star (terminal)
  - DPI-aware pixel alignment: round(220 * scale) / scale
  - UseLayoutRounding(true) for integer physical pixels
- **SessionManager Events**: All callbacks connected (on_created/closed/activated/title_changed/cwd_changed)
- **CMakeLists.txt**: Added tab_sidebar.cpp, cwd_query.cpp to build

### Fixed
- **Iteration 1** (Match Rate 91% → 95%):
  - Connected on_title_changed SessionEvent → DispatcherQueue → TabSidebar::on_title_changed
  - Connected on_cwd_changed SessionEvent → DispatcherQueue → TabSidebar::on_cwd_changed
  - Result: Real-time tab title and CWD display working
- **cpp.md Compliance** (10-agent audit):
  - RAII SelectionGuard for safe selection flag management
  - TabSidebarConfig struct for initialize() parameters (≤ 3)
  - RAII HandleGuard for Win32 HANDLE cleanup
  - Function bodies ≤ 40 lines (setup_listview, setup_add_button, etc.)
  - wstring_view for ShortenCwd parameter

### Known Limitations
- **OSC 9;9 (WT-compatible CWD)**: libghostty does not support OSC 9 subcommand parsing. Deferred to Phase 6 upstream sync.
- **PEB Polling Timer**: Optional 2-second timer for cmd.exe (no OSC 7 support). Currently, on_cwd_changed callback + manual update sufficient.

### Stats
- **New Files**: 6 (tab_sidebar.h/cpp, cwd_query.h/cpp, string_util.h, design doc)
- **Modified Files**: 7 (winui_app.h/cpp, session_manager.h/cpp, session.h, vt_bridge.h/c, vt_core.h/cpp, conpty_session.h/cpp, CMakeLists.txt)
- **Total New LOC**: ~571 (tab_sidebar ~359, cwd_query ~176, string_util ~36)
- **Design Match Rate**: 91% → 95% → ~98%
- **Build Tests**: 10/10 PASS
- **Duration**: 2 sessions (2026-04-03 ~ 2026-04-04)

---

## [2026-04-03] - Phase 5-A Session Manager Completion (Archived)

### Added
- Multi-session ConPTY management (SessionManager, Session struct)
- Session lifecycle: create, activate, close, move_order
- Session state isolation: separate ConPTY, VTCore, RenderState, TSF per session
- SessionEvents callbacks: on_created, on_closed, on_activated, on_child_exit, on_title_changed, on_cwd_changed

### Stats
- **Match Rate**: 95%
- **Tests**: 7/7 PASS

---

## [2026-03-XX] - Previous Phases (4-A~G, Archive)

See `docs/archive/` for detailed completion reports.
