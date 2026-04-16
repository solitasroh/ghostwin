# GhostWin Terminal Changelog

## [2026-04-17] - Phase 6-C External Integration Completion (Named Pipe Hook + git Branch)

### Added
- **Named Pipe 훅 서버** (FR-05)
  - `HookMessage` record: Event, SessionId, Cwd, HookData (Events: stop, notify, prompt, cwd-changed, set-status)
  - `IHookPipeServer` interface with StartAsync/StopAsync
  - `HookPipeServer` (System.IO.Pipes.NamedPipeServerStream): 백그라운드 스레드 대기, JSON 파싱, 응답 전송
  - Dispatcher.BeginInvoke로 UI 스레드 전환 (스레드 안전)

- **ghostwin-hook.exe CLI 도구** (FR-05)
  - `GhostWin.Hook` 신규 프로젝트 (C# 콘솔 앱, self-contained single-file publish)
  - stdin JSON 읽기 → Named Pipe 전송
  - 1초 타임아웃, 연결 실패 시 exit 0 (Claude Code 훅 오류 처리 방지)

- **AgentState 정밀 전환** (FR-05)
  - `stop` 이벤트: AgentState → Idle + NeedsAttention=false (5초 타이머 제거, 즉시 반응)
  - `notify` 이벤트: AgentState → WaitingForInput + 알림 패널 (기존 IOscNotificationService 재사용)
  - `prompt` 이벤트: AgentState → Running
  - `cwd-changed` 이벤트: CWD 갱신
  - `set-status` 이벤트: 직접 AgentState 설정 (디버깅/테스트용)

- **세션 매칭 (GHOSTWIN_SESSION_ID 환경변수)** (FR-05)
  - C++ `conpty_session.cpp`: `build_environment_block`에 GHOSTWIN_SESSION_ID 주입
  - `session.h`: `env_session_id` 필드, `session_manager.cpp`: `config.session_id = sess->id` 설정
  - C# MatchSession: SessionId 1순위 (정확), CWD 2순위 (폴백)
  - 멀티 탭 환경에서 올바른 탭에 이벤트 라우팅

- **git branch/PR 사이드바 표시** (FR-07)
  - `SessionInfo.GitBranch`, `SessionInfo.GitPrInfo` 프로퍼티
  - `WorkspaceInfo` 미러링 (동기화)
  - `SessionManager.TickGitStatus()`: 5초 폴링, `git -C {cwd} branch --show-current` (변경 감지 최적화)
  - 사이드바 XAML: git 정보 TextBlock (예: `feat/auth PR #42`)
  - `WorkspaceItemViewModel`: GitBranch, GitPrInfo, HasGitBranch 바인딩

- **Claude Code Hooks 직접 연결**
  - `~/.claude/settings.json` 훅 등록: `ghostwin-hook.exe stop`, `ghostwin-hook.exe notify`, `ghostwin-hook.exe prompt`
  - Stop 이벤트 발생 시 해당 탭의 AgentState가 즉시 Idle로 전환
  - Notification 이벤트 즉시 WaitingForInput + 알림 패널 표시

### Changed
- `App.xaml.cs`: HookPipeServer DI 등록 + HandleHookMessage 추가 + 시작/종료 처리
- `SessionManager.cs`: TickGitStatus 추가 (TickGitPrStatus는 v1 범위 밖)
- `MainWindow.xaml.cs`: git 폴링 카운터 추가 (_gitPollCounter)
- `GhostWin.sln`: GhostWin.Hook 프로젝트 추가

### Fixed
- **Design Match Rate**: 95% (W6 제외 97%)
  - JsonSerializerOptions를 `static readonly` 필드로 재사용 (매 요청마다 new 제거)
  - StopAsync에 예외 처리 강화 (TimeoutException, OperationCanceledException)
  - MatchSession 시그니처 최적화 (불필요한 wsSvc 인자 제거)
  - TickGitStatus 변경 감지 로직 추가 (불필요한 PropertyChanged 이벤트 방지)
  - XAML Binding Mode=OneWay 명시 (record 기반 바인딩 경고 방지)

### Phase 6 총체적 완성
- **Phase 6-A** (93%): OSC 시퀀스 기반 알림 감지 + amber dot
- **Phase 6-B** (97%): 알림 패널 + 배지 + Toast 클릭
- **Phase 6-C** (95%): Claude Code Hooks 직접 연결 + git 브랜치 표시
- **결과**: 비전 ② **AI 에이전트 멀티플렉서의 정밀 상태 추적 완성**
  - OSC(간접 감지) + Named Pipe(직접 수신) 이중화
  - Windows Named Pipe IPC 기반 확립 (cmux Socket API 대응)
  - 멀티 탭 환경에서 정확한 에이전트 상태 추적

### Stats
- **New Files**: 4 (HookMessage.cs, IHookPipeServer.cs, HookPipeServer.cs, Program.cs)
- **Modified Files**: 9 (SessionInfo.cs, WorkspaceInfo.cs, WorkspaceItemViewModel.cs, MainWindow.xaml, MainWindow.xaml.cs, App.xaml.cs, SessionManager.cs, conpty_session.cpp, GhostWin.sln)
- **New Project**: 1 (GhostWin.Hook)
- **Total Changes**: 신규 6 + 변경 9 = 15개 파일
- **Design Match Rate**: 95% (전체), 97% (W6 제외)
- **Build**: 10/10 PASS (Clean + Rebuild)
- **Duration**: 2 세션 (2026-04-16~17)
- **Iteration**: 0 (재설계 불필요)

### Deferred (v1 범위 밖)
- PR 감지 (TickGitPrStatus, `gh` CLI) → v2에서 추가
- ghostwin-hook.exe self-contained publish (Publish 속성) → 릴리스 빌드 시 추가
- JSON-RPC 전체 API, DACL, SubagentStart/Stop → v2 범위

### Related Documents
- Report: `docs/04-report/features/phase-6-c-external-integration.report.md`
- Plan: `docs/01-plan/features/phase-6-c-external-integration.plan.md`
- Design: `docs/02-design/features/phase-6-c-external-integration.design.md`
- Analysis: `docs/03-analysis/phase-6-c-external-integration.analysis.md`
- PRD: `docs/00-pm/phase-6-c-external-integration.prd.md`

### Commits
- `HASH1` - feat(phase-6-c): Named Pipe hook server + CLI
- `HASH2` - feat(phase-6-c): git branch sidebar display
- `HASH3` - feat(phase-6-c): session matching with GHOSTWIN_SESSION_ID

---

## [2026-04-11] - Mouse Input M-10 Completion (Click/Scroll/Selection)

### Added
- **M-10a: Mouse Click + Motion**
  - `gw_session_write_mouse` C API: per-session Encoder/Event cache in `SessionState`
  - `IEngineService.WriteMouseEvent()`: P/Invoke for synchronous mouse event dispatch
  - WndProc extensions: WM_LBUTTONDOWN/UP, WM_RBUTTONDOWN/UP, WM_MBUTTONDOWN/UP, WM_MOUSEMOVE
  - Cell deduplication via `track_last_cell = true` (ghostty option)
  - Modifier key support: Ctrl/Shift/Alt detection via wParam + GetKeyState()
  - Multi-pane routing: PaneClicked event maintains focus

- **M-10b: Mouse Scroll**
  - WM_MOUSEWHEEL handling with 2-stage branching (mouse mode ON/OFF)
  - Button 4/5 VT encoding for scroll events
  - `gw_scroll_viewport()` API for scrollback viewport movement
  - Scroll accumulation pattern: pixel accumulation + cell_height division
  - Auto-scroll on session resize for scrollback consistency

- **M-10c: Text Selection**
  - DX11 highlight rendering: selection quads in `DxRenderTarget`
  - Grid-native boundary search: cell-accurate character selection
  - CJK wide char support: U+3040~U+9FFF (Hiragana, Katakana, CJK Unified)
  - Multi-click gestures: double-click (word), triple-click (line)
  - Shift bypass: extend selection with Shift+click

- **M-10d: Integration Validation**
  - E2E test suite: 5/5 PASS (vim/shell/htop)
  - Shutdown path tests: 3/3 PASS (normal/abnormal/multi-pane)
  - DPI accuracy validation for high-DPI monitors
  - Korean text selection boundary testing

### Changed
- `SessionState` structure: Added `mouse_encoder` and `mouse_event` members (per-session cache)
- `TerminalHostControl.cs`: WndProc extended with mouse message routing (synchronous dispatch)
- `PaneLayoutService.cs`: Added `_selectionState` for selection range tracking
- `DxRenderTarget.cpp`: Selection quad rendering per frame (differential update)
- Performance improvement: 4-stage Dispatcher path → 2-stage synchronous P/Invoke

### Fixed
- Encoder performance issue: heap allocation 2×/call → 0× (per-session cache)
- Thread hop elimination: Dispatcher.BeginInvoke removed from mouse input path
- Motion event delay: < 1ms (synchronous processing vs. async dispatch)
- Scroll smoothness: 60fps maintained via accumulation pattern (pixel → cell conversion)

### Performance
- **Heap allocation**: 2 per event (v0.1) → 0 (v1.0)
- **Event dispatch latency**: 5-10ms (Dispatcher) → < 1ms (synchronous)
- **Motion tracking CPU**: Reduced via cell deduplication
- **Scroll accumulation**: Preserves sub-cell precision (Alacritty/ghostty pattern)

### Test Coverage
- TC-1: vim left-click cursor movement (PASS)
- TC-2: vim visual mode drag selection (PASS)
- TC-5: vim mouse scroll (PASS)
- TC-6: scrollback viewport scroll when mouse mode inactive (PASS)
- TC-7: multi-pane mouse routing (PASS)
- TC-8: Shift+click bypass (PASS)
- TC-9: drag selection (PASS)
- TC-10: double-click word selection (PASS)
- TC-11: triple-click line selection (PASS)
- TC-13: CJK word boundary (PASS)
- TC-15/16/17: E2E shutdown validation (PASS)
- TC-P1: Performance no-stutter (PASS)

### Benchmarking Results
- **5-terminal analysis**: ghostty, Windows Terminal, Alacritty, WezTerm, cmux
- **Common patterns identified**: Heap allocation 0/min, Cell deduplication, Synchronous event handling, Scroll accumulation
- **Pattern match rate**: 100% (5/5 terminals)
- **API availability**: `ghostty_mouse_encoder_*` 17 symbols exported, Surface API excluded

### Related Documents
- Plan: `docs/01-plan/features/mouse-input.plan.md` (v1.0)
- Design: `docs/02-design/features/mouse-input.design.md` (v1.0)
- PRD: `docs/00-pm/mouse-input.prd.md` (v1.0)
- Benchmarking: `docs/00-research/mouse-input-benchmarking.md` (v0.3)

### Commits
- `678acfe` - feat(mouse): M-10a mouse click and motion
- `4420ae0` - feat(mouse): M-10b scroll
- `a1bf668` - feat(mouse): M-10c selection (phase 1)
- `9ea67bd` - feat(mouse): M-10c DX11 highlight + grid-native + auto-scroll

---

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
