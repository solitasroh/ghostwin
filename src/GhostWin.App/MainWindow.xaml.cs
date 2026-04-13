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
            OnSessionClosed = id => { },
            OnSessionActivated = id => { },
            OnTitleChanged = (id, title) => _sessionManager.UpdateTitle(id, title),
            OnCwdChanged = (id, cwd) => _sessionManager.UpdateCwd(id, cwd),
            OnChildExit = (id, code) =>
            {
                _sessionManager.CloseSession(id);
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
        RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "renderinit-call",
            ("hwnd", IntPtr.Zero), ("w", 100), ("h", 100), ("dpi", dpiScale));
        int renderInitRc = _engine.RenderInit(IntPtr.Zero, 100, 100, 14.0f, "Cascadia Mono", dpiScale);
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

        // Create the first workspace. WorkspaceService.CreateWorkspace
        // synchronously:
        //   1. creates a new session
        //   2. instantiates a fresh PaneLayoutService
        //   3. publishes WorkspaceCreatedMessage
        //   4. publishes WorkspaceActivatedMessage
        // Because PaneContainer.Initialize (HC-4) already registered with the
        // messenger, step 4 triggers PaneContainerControl.Receive which calls
        // SwitchToWorkspace -> BuildGrid -> BuildElement. BuildElement creates
        // a new TerminalHostControl and subscribes HostReady *atomically with*
        // the host creation. When BuildWindowCore later fires HostReady via
        // Dispatcher.BeginInvoke, the subscriber is already in place — no race.
        var workspaceId = _workspaceService.CreateWorkspace();
        if (_sessionManager.ActiveSessionId is { } activeId)
            _engine.TsfFocus(activeId);

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

        // 1. UI 리소스 정리 (엔진보다 먼저)
        SaveWindowBounds();
        _tsfBridge?.Dispose();

        var settings = Ioc.Default.GetService<ISettingsService>();
        (settings as IDisposable)?.Dispose();

        // 2. 엔진 정리 + 프로세스 종료 (WT 패턴)
        // gw_engine_destroy 후 WPF/CLR finalizer가 해제된 네이티브 메모리에
        // 접근하므로 Environment.Exit로 즉시 종료해야 함. WT도 동일 패턴.
        // 별도 스레드: ConPTY I/O 블로킹 시 UI 스레드 보호.
        var engineRef = _engine;
        _ = Task.Run(() =>
        {
            (engineRef as IDisposable)?.Dispose();
            Environment.Exit(0);
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

    private void OnTerminalKeyDown(object sender, KeyEventArgs e)
    {
        // Diagnostic instrumentation — e2e-ctrl-key-injection §4 spec, v0.2 §11.6.
        // Gated at runtime by GHOSTWIN_KEYDIAG env var (cached LEVEL_OFF on first
        // call when unset → method body returns immediately, no allocation/IO).
        // [Conditional("DEBUG")] removed so Release builds can be diagnosed in
        // place — see e2e-ctrl-key-injection.design.md §11.6 NFR-01 deviation.
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
        if (IsCtrlDown() && !IsShiftDown() && !IsAltDown())
        {
            KeyDiag.LogBranch(BranchCtrl, e);
            switch (e.Key)
            {
                case Key.T:
                    _workspaceService.CreateWorkspace();
                    e.Handled = true;
                    return;
                case Key.W:
                    if (_workspaceService.ActiveWorkspaceId is { } wsId)
                        _workspaceService.CloseWorkspace(wsId);
                    e.Handled = true;
                    return;
                case Key.Tab:
                {
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
    private void OnTerminalKeyDownBubbled(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        OnTerminalKeyDown(sender, e);
    }

    // Scenario B defence — Keyboard.IsKeyDown (WPF cache) OR raw GetKeyState
    // (OS-level) so SendInput/FlaUI/Appium injection paths all read consistent
    // modifier state regardless of KeyboardDevice cache refresh timing.
    private static bool IsCtrlDown()
        => Keyboard.IsKeyDown(Key.LeftCtrl)
           || Keyboard.IsKeyDown(Key.RightCtrl)
           || (GetKeyStateRaw(VK_CONTROL) & 0x8000) != 0;

    private static bool IsShiftDown()
        => Keyboard.IsKeyDown(Key.LeftShift)
           || Keyboard.IsKeyDown(Key.RightShift)
           || (GetKeyStateRaw(VK_SHIFT) & 0x8000) != 0;

    private static bool IsAltDown()
        => Keyboard.IsKeyDown(Key.LeftAlt)
           || Keyboard.IsKeyDown(Key.RightAlt)
           || (GetKeyStateRaw(VK_MENU) & 0x8000) != 0;

    private const int VK_SHIFT   = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU    = 0x12; // Alt

    private const string BranchCtrl      = "ctrl-branch";
    private const string BranchCtrlShift = "ctrl-shift-branch";

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetKeyState")]
    private static extern short GetKeyStateRaw(int nVirtKey);

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
