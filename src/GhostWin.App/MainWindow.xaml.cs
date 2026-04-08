using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using GhostWin.App.Controls;
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
    private Controls.TerminalHostControl? _initialHost;

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
        PaneContainer.Initialize(_workspaceService);

        // Create a placeholder TerminalHostControl — this host will persist as
        // the initial pane (no replacement). RenderInit binds SwapChain to its HWND.
        // We hold a local reference until AdoptInitialHost transfers ownership to
        // PaneContainer; afterwards _initialHost is cleared so the host is owned
        // exclusively by PaneContainerControl._hostControls.
        var initialHost = new Controls.TerminalHostControl();
        _initialHost = initialHost;

        // Wrap in a Border immediately so the visual tree has a leaf-shaped
        // ancestor when SwapChain binds. AdoptInitialHost will replace this
        // with its own Border once paneId is known (idempotent).
        PaneContainer.Content = new Border { Child = initialHost };

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var hwnd = initialHost.ChildHwnd;
            if (hwnd == IntPtr.Zero) return;

            var dpi = VisualTreeHelper.GetDpi(initialHost);
            var w = (uint)Math.Max(1, initialHost.ActualWidth * dpi.DpiScaleX);
            var h = (uint)Math.Max(1, initialHost.ActualHeight * dpi.DpiScaleY);

            if (_engine.RenderInit(hwnd, w, h, 14.0f, "Cascadia Mono") != 0) return;
            _engine.RenderSetClearColor(0x1E1E2E);

            _tsfBridge = new TsfBridge();
            if (_engine is EngineService es)
                _tsfBridge.Initialize(hwnd, es.Handle);
            _engine.TsfAttach(_tsfBridge.Hwnd);

            _engine.RenderStart();

            // Create the first workspace. WorkspaceService internally creates
            // a session and initializes a fresh PaneLayoutService instance, then
            // emits WorkspaceCreatedMessage + WorkspaceActivatedMessage.
            var workspaceId = _workspaceService.CreateWorkspace();
            if (_sessionManager.ActiveSessionId is not { } activeId) return;
            var initialPaneLayout = _workspaceService.GetPaneLayout(workspaceId);
            if (initialPaneLayout?.FocusedPaneId is not { } initialPaneId) return;

            _engine.TsfFocus(activeId);

            // Detach from temporary Border before AdoptInitialHost re-parents.
            if (initialHost.Parent is Border tempBorder)
                tempBorder.Child = null;

            // Register this host as the workspace's root pane.
            PaneContainer.AdoptInitialHost(initialHost, workspaceId, initialPaneId, activeId);

            // PaneResizeRequested is handled by PaneContainerControl.AdoptInitialHost
            // via OnPaneResized → ActiveLayout.OnPaneResized → SurfaceResize.
            // (Removed in Phase 5-E.5 P0-2: gw_render_resize was a duplicate path.)
            PreviewKeyDown += OnTerminalKeyDown;
            PreviewTextInput += OnTerminalTextInput;

            // Drop ghost reference: PaneContainerControl owns this host now.
            _initialHost = null;
        });
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowBounds();
        _tsfBridge?.Dispose();

        // 렌더링 중지
        if (_engine is { IsInitialized: true })
            _engine.RenderStop();

        // 엔진 정리 + 프로세스 강제 종료
        // ConPTY I/O 스레드가 gw_engine_destroy에서 블로킹되므로
        // 별도 스레드에서 destroy 후 강제 종료
        var engineRef = _engine;
        Task.Run(() =>
        {
            (engineRef as IDisposable)?.Dispose();
            Environment.Exit(0);
        });
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
        if (_engine is not { IsInitialized: true }) return;
        if (_sessionManager.ActiveSessionId is not { } activeId) return;

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

        // App shortcuts — directly dispatched here instead of via Window.InputBindings.
        // When keyboard focus is inside TerminalHostControl (HwndHost), a plain
        // WM_KEYDOWN is consumed by the child HWND's WndProc → DefWindowProc
        // before WPF's InputBinding has a chance to run. WM_SYSKEYDOWN (Alt+...)
        // is preprocessed by HwndSource so Alt+V/H still works via bindings,
        // but Ctrl+... does not. Handling these in PreviewKeyDown guarantees
        // they fire regardless of focus state.
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
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

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
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

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            data = "\x03"u8.ToArray();

        if (data != null)
        {
            _engine.WriteSession(activeId, data);
            e.Handled = true;
        }
    }

    private void OnTerminalTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_engine is not { IsInitialized: true }) return;
        if (_sessionManager.ActiveSessionId is not { } activeId) return;
        if (string.IsNullOrEmpty(e.Text)) return;

        _engine.WriteSession(activeId, Encoding.UTF8.GetBytes(e.Text));
        e.Handled = true;
    }
}
