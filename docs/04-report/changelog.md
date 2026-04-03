# GhostWin Terminal Changelog

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
