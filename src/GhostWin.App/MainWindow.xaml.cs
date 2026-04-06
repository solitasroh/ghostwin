using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using GhostWin.App.ViewModels;
using GhostWin.Core.Interfaces;
using GhostWin.Interop;
using Wpf.Ui.Controls;

namespace GhostWin.App;

public partial class MainWindow : FluentWindow
{
    private IEngineService _engine = null!;
    private ISessionManager _sessionManager = null!;
    private TsfBridge? _tsfBridge;

    public MainWindow()
    {
        InitializeComponent();

        var vm = Ioc.Default.GetRequiredService<MainWindowViewModel>();
        DataContext = vm;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Mica fallback for unsupported environments
        try { WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Mica; }
        catch { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)); }

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
        var hwnd = TerminalHost.ChildHwnd;
        if (hwnd == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(TerminalHost);
        var w = (uint)Math.Max(1, TerminalHost.ActualWidth * dpi.DpiScaleX);
        var h = (uint)Math.Max(1, TerminalHost.ActualHeight * dpi.DpiScaleY);

        if (_engine.RenderInit(hwnd, w, h, 14.0f, "Cascadia Mono") != 0) return;

        _engine.RenderSetClearColor(0x1E1E2E);

        _tsfBridge = new TsfBridge();
        if (_engine is EngineService es)
            _tsfBridge.Initialize(hwnd, es.Handle);
        _engine.TsfAttach(_tsfBridge.Hwnd);

        _engine.RenderStart();

        // Create first session via SessionManager (triggers MVVM flow)
        _sessionManager.CreateSession();

        if (_sessionManager.ActiveSessionId is { } activeId)
            _engine.TsfFocus(activeId);

        PreviewKeyDown += OnTerminalKeyDown;
        PreviewTextInput += OnTerminalTextInput;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
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

    private void OnTerminalResized(uint widthPx, uint heightPx)
    {
        if (_engine is not { IsInitialized: true }) return;
        _engine.RenderResize(widthPx, heightPx);
    }

    private void OnTerminalKeyDown(object sender, KeyEventArgs e)
    {
        if (_engine is not { IsInitialized: true }) return;
        if (_sessionManager.ActiveSessionId is not { } activeId) return;

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
