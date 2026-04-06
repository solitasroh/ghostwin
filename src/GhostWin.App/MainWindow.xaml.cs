using System.Text;
using System.Windows;
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
    private TsfBridge? _tsfBridge;

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
        // Set up PaneContainer with engine reference
        PaneContainer.Initialize(_engine);

        // Create first session
        _sessionManager.CreateSession();

        if (_sessionManager.ActiveSessionId is not { } activeId) return;

        // Create initial pane for the first session
        var initialHost = PaneContainer.SetInitialPane(activeId);

        // Wait for HwndHost to create its child HWND
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
            _engine.TsfFocus(activeId);

            // Set up resize handler for initial host
            initialHost.RenderResizeRequested += OnTerminalResized;

            PreviewKeyDown += OnTerminalKeyDown;
            PreviewTextInput += OnTerminalTextInput;
        });

        // Handle pane focus changes
        PaneContainer.PaneFocusChanged += sessionId =>
        {
            _engine.TsfFocus(sessionId);
            _sessionManager.ActivateSession(sessionId);
        };

        // Connect split/close commands from ViewModel
        var vm = (MainWindowViewModel)DataContext;
        vm.SplitRequested += OnSplitRequested;
        vm.ClosePaneRequested += OnClosePaneRequested;
    }

    private void OnSplitRequested(SplitOrientation direction)
    {
        if (_engine is not { IsInitialized: true }) return;

        var newSessionId = _sessionManager.CreateSession();
        if (newSessionId == 0) return;

        PaneContainer.SplitFocused(direction, newSessionId);
    }

    private void OnClosePaneRequested()
    {
        if (_engine is not { IsInitialized: true }) return;

        var closingLeaf = PaneContainer.FocusedLeaf;
        if (closingLeaf == null) return;

        // If only one pane, fall back to tab close
        if (PaneContainer.Root?.IsLeaf == true)
        {
            if (closingLeaf.SessionId.HasValue)
                _sessionManager.CloseSession(closingLeaf.SessionId.Value);
            return;
        }

        var sessionId = closingLeaf.SessionId;
        PaneContainer.CloseFocusedPane();

        if (sessionId.HasValue)
            _sessionManager.CloseSession(sessionId.Value);
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

    private void OnTerminalResized(uint widthPx, uint heightPx)
    {
        if (_engine is not { IsInitialized: true }) return;
        _engine.RenderResize(widthPx, heightPx);
    }

    private void OnTerminalKeyDown(object sender, KeyEventArgs e)
    {
        if (_engine is not { IsInitialized: true }) return;
        if (_sessionManager.ActiveSessionId is not { } activeId) return;

        // Alt+Arrow: pane focus navigation
        if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            FocusDirection? dir = e.Key switch
            {
                Key.Left => FocusDirection.Left,
                Key.Right => FocusDirection.Right,
                Key.Up => FocusDirection.Up,
                Key.Down => FocusDirection.Down,
                _ => null,
            };
            if (dir.HasValue)
            {
                PaneContainer.MoveFocus(dir.Value);
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
