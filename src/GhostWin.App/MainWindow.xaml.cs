using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using GhostWin.App.Controls;
using GhostWin.App.Diagnostics;
using GhostWin.App.ViewModels;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using GhostWin.Interop;

namespace GhostWin.App;

public partial class MainWindow : Window
{
    private IEngineService _engine = null!;
    private ISessionManager _sessionManager = null!;
    private IWorkspaceService _workspaceService = null!;
    private TsfBridge? _tsfBridge;
    private bool _shuttingDown;

    // ──────────────────────────────────────────────────────────
    // M-11 followup: PEB CWD polling timer (2026-04-15)
    // WinUI3→WPF 이행 시 winui_app.cpp 의 폴링 타이머가 사라짐 → cmd.exe / 기본
    // PowerShell 처럼 OSC 7 안 보내는 쉘에서 cwd 가 비어 옴. 이 타이머가 1초마다
    // _engine.PollTitles() 호출 → native SessionManager::poll_titles_and_cwd() 가
    // 모든 활성 세션의 PEB 를 읽어 변경 시 OnCwdChanged 콜백 발사.
    // 주기 1초: 사용자 체감 지연 < cd 입력 후 한 호흡, GUI 부담 없음 (PEB 읽기 ~수 μs/세션).
    // ──────────────────────────────────────────────────────────
    private System.Windows.Threading.DispatcherTimer? _cwdPollTimer;
    // _initialHost removed in first-pane-render-failure Option B.
    // PaneContainerControl is now the single owner of all host lifecycles —
    // first pane is created by BuildElement via the normal
    // WorkspaceActivatedMessage -> SwitchToWorkspace -> BuildGrid path, same
    // code path as split panes. Eliminates the HostReady subscribe race that
    // caused the first pane to render blank.

    public MainWindow()
    {
        InitializeComponent();

        var vm = Ioc.Default.GetRequiredService<MainWindowViewModel>();
        DataContext = vm;

        RestoreWindowBounds();

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
    }

    /// <summary>
    /// Runtime DPI change handler (monitor move, scale setting change).
    /// Invokes the unified scale pipeline via IEngineService.UpdateCellMetrics —
    /// atlas rebuild + per-surface cols/rows recompute + per-session
    /// resize_pty_only + vt_resize_locked. See dpi-scaling-integration cycle.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        // base.OnDpiChanged accepts the proposed window rect from WM_DPICHANGED,
        // which MSDN mandates be honored so Windows can keep the cursor anchor
        // and avoid stale-size flash.
        base.OnDpiChanged(oldDpi, newDpi);

        if (_engine == null || _shuttingDown) return;

        var settings = Ioc.Default.GetService<ISettingsService>();
        var font = settings?.Current.Terminal.Font ?? new FontSettings();

        int rc = _engine.UpdateCellMetrics(
            fontSizePt: (float)font.Size,
            fontFamily: font.Family,
            dpiScale: (float)newDpi.DpiScaleX,
            cellWidthScale: (float)font.CellWidthScale,
            cellHeightScale: (float)font.CellHeightScale,
            zoom: 1.0f);

        RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "dpi-changed",
            ("old", oldDpi.DpiScaleX), ("new", newDpi.DpiScaleX), ("rc", rc));
    }

    // Tech Debt #24: titlebar button clicks became unreliable in Maximized
    // state because WindowStyle=None + WindowChrome pushes the window ~8px
    // beyond the working area on every edge when maximized. Compensate with
    // BorderThickness and flip the MaxRestore glyph for visual feedback.
    private void OnWindowStateChanged(object? sender, System.EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            // Inset the window content so the full caption row stays on the
            // working area and caption buttons remain hit-testable.
            BorderThickness = new System.Windows.Thickness(8);
            if (MaxRestoreIcon != null)
            {
                // Two overlapping rectangles = Restore glyph
                MaxRestoreIcon.Data = System.Windows.Media.Geometry.Parse(
                    "M 2,0 H 10 V 8 H 8 V 10 H 0 V 2 H 2 Z");
            }
        }
        else
        {
            BorderThickness = new System.Windows.Thickness(0);
            if (MaxRestoreIcon != null)
            {
                // Single rectangle = Maximize glyph
                MaxRestoreIcon.Data = System.Windows.Media.Geometry.Parse(
                    "M 0,0 H 10 V 10 H 0 Z");
            }
        }
    }

    private void RestoreWindowBounds()
    {
        var settings = Ioc.Default.GetRequiredService<ISettingsService>();
        var win = settings.Current.Window;

        Width = win.Width;
        Height = win.Height;

        if (!double.IsNaN(win.Top) && !double.IsNaN(win.Left))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Top = win.Top;
            Left = win.Left;

            // 모니터 경계 벗어남 방지
            var screen = SystemParameters.WorkArea;
            if (Left + Width < 0 || Left > screen.Right ||
                Top + Height < 0 || Top > screen.Bottom)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        if (win.IsMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var settings = Ioc.Default.GetService<ISettingsService>();
        if (settings == null) return;

        var win = settings.Current.Window;
        win.IsMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal)
        {
            win.Width = Width;
            win.Height = Height;
            win.Top = Top;
            win.Left = Left;
        }

        settings.Save();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _engine = Ioc.Default.GetRequiredService<IEngineService>();
        _sessionManager = Ioc.Default.GetRequiredService<ISessionManager>();
        _workspaceService = Ioc.Default.GetRequiredService<IWorkspaceService>();

        var callbackContext = new GwCallbackContext
        {
            OnSessionCreated = id => { },
            OnSessionClosed = id => {
                Debug.WriteLine("${_sessionManager.Sessions.Count()}");
            },
            OnSessionActivated = id => { },
            OnTitleChanged = (id, title) =>
            {
                if (_shuttingDown) return;
                _sessionManager.UpdateTitle(id, title);
                (_sessionManager as Services.SessionManager)?.NotifySessionOutput(id);
            },
            OnCwdChanged = (id, cwd) =>
            {
                if (_shuttingDown) return;
                _sessionManager.UpdateCwd(id, cwd);
                (_sessionManager as Services.SessionManager)?.NotifySessionOutput(id);
            },
            OnOscNotify = (id, title, body) =>
            {
                if (_shuttingDown) return;
                Ioc.Default.GetService<IOscNotificationService>()?.HandleOscEvent(id, title, body);
            },
            OnChildExit = (id, code) =>
            {
                if (_shuttingDown) return;
                if (_sessionManager is Services.SessionManager sm)
                    sm.NotifyChildExit(id, code);
                _sessionManager.CloseSession(id);
                if (_sessionManager.Sessions.Count == 0)
                    this.Close();
            },
            OnRenderDone = null,
        };

        _engine.Initialize(callbackContext);

        if (!_engine.IsInitialized) return;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            InitializeRenderer);
    }

    private void InitializeRenderer()
    {
        // #1 irenderer-enter — runs on the UI thread via Dispatcher.BeginInvoke(Loaded).
        // ui=1 is normal. In Option B all work is done synchronously inside this
        // single callback — no nested Dispatcher.BeginInvoke, which eliminates
        // the priority-race window where BuildWindowCore's Normal(9) HostReady
        // fire could drain before a nested Loaded(6) AdoptInitialHost callback.
        //
        // ⚠️ DO NOT add nested Dispatcher.BeginInvoke / await Dispatcher.Yield
        // / Task.Run continuations inside this method (between PaneContainer.
        // Initialize and the CreateWorkspace call). first-pane-render-failure
        // Option B (design.md §0.1 C-7/C-8, §4.2 implementation order) requires
        // the entire chain — Initialize → RenderInit → RenderStart → CreateWorkspace
        // — to run synchronously on a single Dispatcher tick so that
        // PaneContainer is registered with the messenger *before* CreateWorkspace
        // publishes WorkspaceActivatedMessage. Any Dispatcher yield in between
        // re-opens the HostReady race window (HC-3) by allowing layout-pass
        // Render(7) callbacks to drain BuildWindowCore's Normal(9) enqueue out
        // of order. If you need async work, defer it to *after* CreateWorkspace
        // returns.
        RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "irenderer-enter",
            ("dispatcher_thread", Application.Current?.Dispatcher.CheckAccess() ?? false));

        // HC-4: PaneContainer.Initialize subscribes to WeakReferenceMessenger
        // synchronously (no longer deferred to Loaded event). This guarantees
        // that WorkspaceActivatedMessage published by CreateWorkspace below is
        // delivered and Receive()/SwitchToWorkspace/BuildGrid/BuildElement runs,
        // which creates the first TerminalHostControl with HostReady already
        // subscribed — atomically, same code path as split panes.
        PaneContainer.Initialize(_workspaceService);

        // Q-A4: hwnd-less RenderInit. gw_render_init now accepts NULL hwnd via
        // the new RendererConfig.allow_null_hwnd flag and skips the bootstrap
        // swapchain entirely (SurfaceManager creates per-pane swapchains later
        // via bind_surface). Dummy 100x100 size — the atlas recomputes real
        // cols/rows using font-dependent cell size.
        var dpiScale = (float)VisualTreeHelper.GetDpi(this).DpiScaleX;
        var fontSettings = Ioc.Default.GetRequiredService<ISettingsService>().Current.Terminal.Font;
        RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "renderinit-call",
            ("hwnd", IntPtr.Zero), ("w", 100), ("h", 100), ("dpi", dpiScale),
            ("font", fontSettings.Family), ("size", fontSettings.Size));
        int renderInitRc = _engine.RenderInit(IntPtr.Zero, 100, 100,
            (float)fontSettings.Size, fontSettings.Family, dpiScale);
        RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "renderinit-return",
            ("rc", renderInitRc));
        if (renderInitRc != 0) return;

        _engine.RenderSetClearColor(0x1E1E2E);

        // Q-D3: TsfBridge parent is now the MainWindow HWND (top-level WPF
        // window) instead of the first pane's child HWND. The hidden IME HWND
        // still attaches as a child via HwndSourceParameters.ParentWindow —
        // ADR-011's focus-tracking pattern works with any parent HWND so long
        // as the parent is the foreground window, and the main window is a
        // strictly better parent than an ephemeral pane child.
        var mainWindowHwnd = new WindowInteropHelper(this).Handle;
        _tsfBridge = new TsfBridge();
        if (_engine is EngineService es)
            _tsfBridge.Initialize(mainWindowHwnd, es.Handle);
        _engine.TsfAttach(_tsfBridge.Hwnd);

        _engine.RenderStart();

        // ──────────────────────────────────────────────────────────
        // M-11 Session Restore — 복원 경로 통합 (Design §15 Step 7, C-1 패치)
        //
        // 가드 원칙: App.OnStartup 에서 이미 복원/폴백 시도가 있었을 수 있지만,
        //   엔진 Initialize 는 이 시점에서 완료되므로 "실제 세션 생성" 은 여기서 수행.
        //   Workspaces.Count == 0 가드는 미래에 OnStartup 이 직접 생성하게 될 경우 대비.
        //
        // 이중 생성 차단:
        //   - pending 스냅샷이 있으면 RestoreFromSnapshot (복수 W + 복수 pane + CWD)
        //   - 없으면 기존 CreateWorkspace (첫 실행)
        //   - 복원 실패 시 CreateWorkspace 폴백 (App.WriteCrashLog 기록)
        //
        // 기존 경로 호환:
        //   RestoreFromSnapshot 는 내부에서 ActivateWorkspace 를 호출 →
        //   WorkspaceActivatedMessage 발행 → PaneContainerControl.Receive 가
        //   SwitchToWorkspace -> BuildGrid -> BuildElement 로 TerminalHostControl 생성
        //   (CreateWorkspace 와 동일 경로).
        // ──────────────────────────────────────────────────────────
        var pending = App.PendingRestoreSnapshot;
        if (_workspaceService.Workspaces.Count == 0)
        {
            if (pending is not null && pending.Workspaces.Count > 0)
            {
                try
                {
                    _workspaceService.RestoreFromSnapshot(pending);
                    RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "session-restored",
                        ("workspaces", pending.Workspaces.Count),
                        ("active_idx", pending.ActiveWorkspaceIndex));
                }
                catch (Exception ex)
                {
                    // 복원 실패 — 기존 경로 폴백. 데이터 유실 방지용 crash log.
                    App.WriteCrashLog("RestoreFromSnapshot", ex);
                    _workspaceService.CreateWorkspace();
                }
            }
            else
            {
                // 신규 실행 (session.json 미존재) 또는 빈 스냅샷 — 기존 경로.
                _workspaceService.CreateWorkspace();
            }
        }

        if (_sessionManager.ActiveSessionId is { } activeId)
            _engine.TsfFocus(activeId);

        // ──────────────────────────────────────────────────────────
        // M-11 followup: PEB CWD polling 타이머 시작
        // 1초 주기로 native poll_titles_and_cwd() 호출 → cwd 변경 감지 → fire_cwd_event
        // → C# OnCwdChanged → SessionManager.UpdateCwd → SessionInfo.Cwd 갱신
        // → 다음 SessionSnapshot 저장 시 cwd 가 session.json 에 기록됨.
        // OSC 7 미설정 PowerShell, cmd.exe 모두 자동 동작.
        // ──────────────────────────────────────────────────────────
        _cwdPollTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _cwdPollTimer.Tick += (_, _) =>
        {
            if (_shuttingDown || _engine == null) return;
            try { _engine.PollTitles(); }
            catch (Exception ex) { App.WriteCrashLog("CwdPollTimer.Tick", ex); }
            try { (_sessionManager as Services.SessionManager)?.TickAgentStateTimer(); }
            catch (Exception ex) { App.WriteCrashLog("AgentStateTimer.Tick", ex); }
        };
        _cwdPollTimer.Start();

        PreviewKeyDown += OnTerminalKeyDown;
        PreviewTextInput += OnTerminalTextInput;

        // Bubble-phase fallback for scenario A/D — child HwndHost can consume
        // WM_KEYDOWN before WPF tunnelling reaches the Window. See
        // docs/02-design/features/e2e-headless-input.design.md §3.1.2.
        AddHandler(KeyDownEvent,
                   new KeyEventHandler(OnTerminalKeyDownBubbled),
                   handledEventsToo: true);
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shuttingDown) return;
        e.Cancel = true;
        _shuttingDown = true;

        // ★ 사용자 체감 즉시 닫힘 — 윈도우를 먼저 숨긴 후 정리 진행
        this.Visibility = Visibility.Hidden;

        // M-11 followup: CWD 폴링 타이머 즉시 중단 (Snapshot 저장 직전)
        // 마지막 한 번 동기 호출로 최신 cwd 반영 후 정지.
        try
        {
            _cwdPollTimer?.Stop();
            _engine?.PollTitles();  // 종료 직전 마지막 cwd 캡쳐
        }
        catch (Exception ex) { App.WriteCrashLog("CwdPollTimer.Stop", ex); }

        // 1. UI 리소스 정리 (엔진보다 먼저)
        try { SaveWindowBounds(); }
        catch (Exception ex)
        {
            App.WriteCrashLog("SaveWindowBounds", ex);
        }

        // ──────────────────────────────────────────────────────────
        // M-11 Session Restore — 최종 저장 + 주기 타이머 중단 (Design §7, §15 Step 9)
        //
        // 실행 순서:
        //   1) UI 스레드에서 Collect (PaneLayoutService.Root / WorkspaceInfo 동기 읽기)
        //   2) SaveAsync (원자 쓰기, 100ms 타임아웃 — NFR-1)
        //   3) StopAsync (주기 타이머 중단 + 워커 task join)
        //   4) 엔진 DetachCallbacks 및 정리는 이후 기존 로직 유지
        //
        // 타임아웃: NFR-1 (<100ms) 근거. 종료 경로 2 초 전체 버짓 안에 여유.
        // 예외 처리: 실패 시 crash log 만 남기고 진행 (종료 경로 블록 금지).
        // ──────────────────────────────────────────────────────────
        try
        {
            var snapshotSvc = Ioc.Default.GetService<ISessionSnapshotService>();
            var wsSvc       = Ioc.Default.GetService<IWorkspaceService>();
            if (snapshotSvc != null && wsSvc != null && _sessionManager != null)
            {
                var finalSnapshot = GhostWin.Services.SessionSnapshotMapper.Collect(wsSvc, _sessionManager);
                await snapshotSvc.SaveAsync(finalSnapshot)
                                  .WaitAsync(TimeSpan.FromMilliseconds(100));
                await snapshotSvc.StopAsync();
            }
        }
        catch (TimeoutException tex)
        {
            App.WriteCrashLog("SessionSnapshot.SaveAsync timeout", tex);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("SessionSnapshot shutdown", ex);
        }

        _tsfBridge?.Dispose();

        var settings = Ioc.Default.GetService<ISettingsService>();
        (settings as IDisposable)?.Dispose();

        // 2. 엔진 정리 + 프로세스 종료 (WT 패턴)
        //
        // 재진입 crash 방지 순서:
        //   (a) DetachCallbacks — C++ 콜백 포인터 NULL + C# context/dispatcher null.
        //       이 시점 이후 네이티브 I/O thread의 on_exit fire는 양쪽 모두에서
        //       early-return. Dispatcher 큐에 CloseSession이 큐잉되지 않음.
        //   (b) Dispatcher 큐 flush — (a) 이전에 이미 큐잉된 콜백이 있을 수
        //       있으므로, 한 번 Yield하여 대기 중인 BeginInvoke 항목들을
        //       _shuttingDown 가드 하에서 안전하게 drain.
        //   (c) Task.Run(engine.Dispose) — 콜백이 완전히 차단된 상태에서
        //       gw_engine_destroy 실행. I/O thread join 중 fire되는 on_exit도
        //       C++ NULL check에서 drop됨.
        //
        // gw_engine_destroy 후 WPF/CLR finalizer가 해제된 네이티브 메모리에
        // 접근하므로 Environment.Exit로 즉시 종료해야 함. WT도 동일 패턴.
        _engine.DetachCallbacks();
        // Drain 이미 큐잉된 BeginInvoke 항목 — Background 우선순위로 한 번 양보.
        await this.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        var engineRef = _engine;
        _engine = null!;
        _ = Task.Run(() =>
        {
            Application.Current?.Dispatcher.Invoke(                () => Application.Current.Shutdown());
            (engineRef as IDisposable)?.Dispose();
        });

        // 3. 타임아웃 fallback — ConPty I/O 무한 블로킹 시
        await Task.Delay(TimeSpan.FromSeconds(2));
        App.WriteCrashLog("shutdown", new TimeoutException(
            "engine.Dispose blocked >2s (ConPty I/O hang)"));
        Environment.Exit(0);
    }

    // Caption button handlers
    private void OnMinimize(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaxRestore(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e)
        => Close();

    // OnTerminalResized removed in Phase 5-E.5 P0-2 (bisect-mode-termination).
    // Pane resizes are handled by PaneContainerControl.OnPaneResized via
    // ActiveLayout.OnPaneResized → SurfaceResize per-pane. The old path called
    // gw_render_resize which was a duplicate with broken uniform-size semantics.

    // BC-03 (keydiag-log-dedupe): suppresses duplicate ENTRY logs when the
    // Bubble handler (OnTerminalKeyDownBubbled, Scenario D fallback) re-invokes
    // OnTerminalKeyDown with the same KeyEventArgs. ThreadStatic because WPF
    // input is single-threaded but this guards against any cross-dispatcher use.
    [ThreadStatic]
    private static bool _keyDiagSuppressEntry;

    private void OnTerminalKeyDown(object sender, KeyEventArgs e)
    {
        // Diagnostic instrumentation — e2e-ctrl-key-injection §4 spec, v0.2 §11.6.
        // Gated at runtime by GHOSTWIN_KEYDIAG env var (cached LEVEL_OFF on first
        // call when unset → method body returns immediately, no allocation/IO).
        // [Conditional("DEBUG")] removed so Release builds can be diagnosed in
        // place — see e2e-ctrl-key-injection.design.md §11.6 NFR-01 deviation.
        if (!_keyDiagSuppressEntry)
            KeyDiag.LogEntry(e, _workspaceService);

        if (_engine is not { IsInitialized: true })
        {
            KeyDiag.LogExit("early-return:engine-not-initialized", e);
            return;
        }
        if (_sessionManager.ActiveSessionId is not { } activeId)
        {
            KeyDiag.LogExit("early-return:no-active-session", e);
            return;
        }

        // Alt+Arrow: pane focus navigation. Alt is a system modifier, so WPF
        // delivers WM_SYSKEYDOWN with Key=System and the real key in SystemKey.
        var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            FocusDirection? dir = actualKey switch
            {
                Key.Left => FocusDirection.Left,
                Key.Right => FocusDirection.Right,
                Key.Up => FocusDirection.Up,
                Key.Down => FocusDirection.Down,
                _ => null,
            };
            if (dir.HasValue)
            {
                _workspaceService.ActivePaneLayout?.MoveFocus(dir.Value);
                e.Handled = true;
                return;
            }
        }

        // App shortcuts — dispatched here because when focus sits inside the
        // TerminalHostControl HwndHost child, Ctrl+... WM_KEYDOWN is consumed
        // by DefWindowProc before WPF InputBindings run. Alt+... still works
        // via HwndSource preprocessing.
        //
        // IsCtrlDown/Shift/Alt helpers (scenario B defence, Design §3.1.2)
        // triangulate Keyboard.IsKeyDown with raw GetKeyState so every
        // injection path (real user, SendInput, FlaUI, Appium) lights up
        // the same branch.
        //
        // ★ Ctrl+Shift block MUST precede Ctrl-only block — more-specific
        //   modifier combo first, otherwise Ctrl+Shift+W could be swallowed
        //   by the Ctrl+W branch on keyboards/injectors where IsShiftDown()
        //   returns false transiently.
        if (IsCtrlDown() && IsShiftDown() && !IsAltDown())
        {
            KeyDiag.LogBranch(BranchCtrlShift, e);
            if (e.Key == Key.C)
            {
                TryCopySelection();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.V)
            {
                PasteFromClipboard(activeId);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.W)
            {
                _workspaceService.ActivePaneLayout?.CloseFocused();
                e.Handled = true;
                return;
            }
            if (actualKey == Key.I)
            {
                if (DataContext is ViewModels.MainWindowViewModel vm)
                    vm.ToggleNotificationPanelCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (actualKey == Key.U)
            {
                if (DataContext is ViewModels.MainWindowViewModel vm2)
                    vm2.JumpToUnreadCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        if (IsCtrlDown() && !IsShiftDown() && !IsAltDown())
        {
            KeyDiag.LogBranch(BranchCtrl, e);
            switch (e.Key)
            {
                case Key.T:
                    KeyDiag.LogKeyBindCommand(nameof(IWorkspaceService.CreateWorkspace));
                    _workspaceService.CreateWorkspace();
                    e.Handled = true;
                    return;
                case Key.W:
                    KeyDiag.LogKeyBindCommand(nameof(IWorkspaceService.CloseWorkspace));
                    if (_workspaceService.ActiveWorkspaceId is { } wsId)
                        _workspaceService.CloseWorkspace(wsId);
                    e.Handled = true;
                    return;
                case Key.Tab:
                {
                    KeyDiag.LogKeyBindCommand(nameof(IWorkspaceService.ActivateWorkspace));
                    var list = _workspaceService.Workspaces;
                    if (list.Count > 1 && _workspaceService.ActiveWorkspaceId is { } curId)
                    {
                        var idx = -1;
                        for (int i = 0; i < list.Count; i++)
                            if (list[i].Id == curId) { idx = i; break; }
                        if (idx >= 0)
                            _workspaceService.ActivateWorkspace(
                                list[(idx + 1) % list.Count].Id);
                    }
                    e.Handled = true;
                    return;
                }
            }
        }

        // Alt+V / Alt+H — direct dispatch as a belt-and-suspenders fallback
        // (InputBindings also handle these, but focus state may differ).
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.System)
        {
            if (actualKey == Key.V)
            {
                _workspaceService.ActivePaneLayout?.SplitFocused(SplitOrientation.Vertical);
                e.Handled = true;
                return;
            }
            if (actualKey == Key.H)
            {
                _workspaceService.ActivePaneLayout?.SplitFocused(SplitOrientation.Horizontal);
                e.Handled = true;
                return;
            }
        }

        byte[]? data = e.Key switch
        {
            Key.Enter => "\r"u8.ToArray(),
            Key.Back => "\x7f"u8.ToArray(),
            Key.Tab => "\t"u8.ToArray(),
            Key.Escape => "\x1b"u8.ToArray(),
            Key.Up => "\x1b[A"u8.ToArray(),
            Key.Down => "\x1b[B"u8.ToArray(),
            Key.Right => "\x1b[C"u8.ToArray(),
            Key.Left => "\x1b[D"u8.ToArray(),
            Key.Home => "\x1b[H"u8.ToArray(),
            Key.End => "\x1b[F"u8.ToArray(),
            Key.Delete => "\x1b[3~"u8.ToArray(),
            _ => null,
        };

        // Ctrl+C: 선택 있으면 복사, 없으면 SIGINT (WT 패턴)
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (TryCopySelection())
            {
                e.Handled = true;
                return;
            }
            data = "\x03"u8.ToArray();
        }

        // Ctrl+V: 붙여넣기
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            PasteFromClipboard(activeId);
            e.Handled = true;
            return;
        }

        // Shift+Insert: 붙여넣기
        if (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            PasteFromClipboard(activeId);
            e.Handled = true;
            return;
        }

        if (data != null)
        {
            _engine.WriteSession(activeId, data);
            // Auto-scroll to bottom on keyboard input (WT/Alacritty pattern)
            _engine.ScrollViewport(activeId, int.MaxValue);
            e.Handled = true;
        }
    }

    private void OnTerminalTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_engine is not { IsInitialized: true }) return;
        if (_sessionManager.ActiveSessionId is not { } activeId) return;
        if (string.IsNullOrEmpty(e.Text)) return;

        _engine.WriteSession(activeId, Encoding.UTF8.GetBytes(e.Text));
        // Auto-scroll to bottom on keyboard input
        _engine.ScrollViewport(activeId, int.MaxValue);
        e.Handled = true;
    }

    // Scenario D fallback — re-runs OnTerminalKeyDown if tunnelling didn't reach
    // it (child HwndHost consumed the event). Guarded by e.Handled.
    // BC-03: sets _keyDiagSuppressEntry so the re-entry doesn't emit a duplicate
    // ENTRY log line (BRANCH/EXIT still logged normally).
    private void OnTerminalKeyDownBubbled(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        _keyDiagSuppressEntry = true;
        try { OnTerminalKeyDown(sender, e); }
        finally { _keyDiagSuppressEntry = false; }
    }

    // Scenario B defence — Keyboard.IsKeyDown (WPF cache) OR raw GetKeyState
    // (OS-level, via GhostWin.Interop.VirtualKeys) so SendInput/FlaUI/Appium
    // injection paths all read consistent modifier state regardless of
    // KeyboardDevice cache refresh timing. VK constants + P/Invoke live in
    // GhostWin.Interop.VirtualKeys (BC-09 centralisation).
    private static bool IsCtrlDown()
        => Keyboard.IsKeyDown(Key.LeftCtrl)
           || Keyboard.IsKeyDown(Key.RightCtrl)
           || VirtualKeys.IsCtrlDownRaw();

    private static bool IsShiftDown()
        => Keyboard.IsKeyDown(Key.LeftShift)
           || Keyboard.IsKeyDown(Key.RightShift)
           || VirtualKeys.IsShiftDownRaw();

    private static bool IsAltDown()
        => Keyboard.IsKeyDown(Key.LeftAlt)
           || Keyboard.IsKeyDown(Key.RightAlt)
           || VirtualKeys.IsAltDownRaw();

    private const string BranchCtrl      = "ctrl-branch";
    private const string BranchCtrlShift = "ctrl-shift-branch";

    // ── 클립보드: 복사 (Ctrl+C / Ctrl+Shift+C) ──

    /// <summary>
    /// 활성 pane에 선택 영역이 있으면 클립보드에 복사하고 true 반환.
    /// 선택 영역이 없으면 false 반환 (호출측에서 SIGINT 등 대체 동작 수행).
    /// </summary>
    private bool TryCopySelection()
    {
        var host = PaneContainer.GetFocusedHost();
        if (host == null) return false;

        if (host._selection.CurrentRange is not { IsValid: true } range) return false;

        var text = _engine.GetSelectedText(
            host.SessionId,
            range.Start.Row, range.Start.Col,
            range.End.Row, range.End.Col);

        if (string.IsNullOrEmpty(text)) return false;

        // OLE 재시도 (클립보드 잠금 경합 대비, WT 패턴)
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                Clipboard.SetText(text);
                break;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                if (retry == 2) return false;
                Thread.Sleep(50);
            }
        }

        // 복사 후 선택 해제
        _engine.SetSelection(host.SessionId, 0, 0, 0, 0, false);
        host._selection.Clear();

        return true;
    }

    // ── 클립보드: 붙여넣기 (Ctrl+V / Ctrl+Shift+V / Shift+Insert) ──

    private void PasteFromClipboard(uint sessionId)
    {
        // OLE 재시도
        string? text = null;
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                text = Clipboard.GetText();
                break;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                if (retry == 2) return;
                Thread.Sleep(50);
            }
        }

        if (string.IsNullOrEmpty(text)) return;

        // C0/C1 제어 문자 필터 + 줄바꿸 정규화
        text = FilterForPaste(text);
        if (string.IsNullOrEmpty(text)) return;

        // Bracketed Paste Mode (mode 2004)
        bool bracketedPaste = _engine.GetMode(sessionId, 2004);

        byte[] payload;
        if (bracketedPaste)
        {
            // \x1b[200~ ... \x1b[201~ 로 감싸기
            var prefix = "\x1b[200~"u8;
            var suffix = "\x1b[201~"u8;
            var textBytes = Encoding.UTF8.GetBytes(text);
            payload = new byte[prefix.Length + textBytes.Length + suffix.Length];
            prefix.CopyTo(payload.AsSpan(0));
            textBytes.CopyTo(payload, prefix.Length);
            suffix.CopyTo(payload.AsSpan(prefix.Length + textBytes.Length));
        }
        else
        {
            // 비-bracket 모드: 줄바꿈을 \r로 통일 (터미널 입력 규약)
            text = text.Replace("\r\n", "\r").Replace("\n", "\r");
            payload = Encoding.UTF8.GetBytes(text);
        }

        _engine.WriteSession(sessionId, payload);
        _engine.ScrollViewport(sessionId, int.MaxValue);
    }

    /// <summary>
    /// 붙여넣기용 텍스트 필터: C0/C1 제어 문자 제거 (HT, LF, CR 제외).
    /// </summary>
    private static string FilterForPaste(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            // C0 제어 문자 (0x00-0x1F): HT(0x09), LF(0x0A), CR(0x0D)만 허용
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
                continue;
            // C1 제어 문자 (0x80-0x9F): 모두 제거
            if (c >= 0x80 && c <= 0x9F)
                continue;
            // DEL (0x7F): 제거
            if (c == 0x7F)
                continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
