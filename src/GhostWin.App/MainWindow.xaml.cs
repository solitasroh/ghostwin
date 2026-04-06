using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using GhostWin.Core.Interfaces;
using GhostWin.Interop;
using Wpf.Ui.Controls;

namespace GhostWin.App;

public partial class MainWindow : FluentWindow
{
    private IEngineService _engine = null!;
    private uint _activeSessionId;
    private bool _hasActiveSession;
    private TsfBridge? _tsfBridge;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _engine = Ioc.Default.GetRequiredService<IEngineService>();

        var callbackContext = new GwCallbackContext
        {
            OnSessionCreated = id => StatusText.Text = $"Session #{id} created",
            OnSessionClosed = id => StatusText.Text = $"Session #{id} closed",
            OnSessionActivated = id => StatusText.Text = $"Session #{id} activated",
            OnTitleChanged = (id, title) => Title = $"GhostWin — {title}",
            OnCwdChanged = (id, cwd) => StatusText.Text = $"CWD: {cwd}",
            OnChildExit = (id, code) => Close(),
            OnRenderDone = null,
        };

        _engine.Initialize(callbackContext);

        if (!_engine.IsInitialized)
        {
            StatusText.Text = "Engine: creation failed";
            return;
        }

        StatusText.Text = "Engine: created, waiting for layout...";
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            InitializeRenderer);
    }

    private void InitializeRenderer()
    {
        var hwnd = TerminalHost.ChildHwnd;
        if (hwnd == IntPtr.Zero)
        {
            StatusText.Text = "Engine: HwndHost not ready";
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(TerminalHost);
        var w = (uint)Math.Max(1, TerminalHost.ActualWidth * dpi.DpiScaleX);
        var h = (uint)Math.Max(1, TerminalHost.ActualHeight * dpi.DpiScaleY);

        int result = _engine.RenderInit(hwnd, w, h, 14.0f, "Cascadia Mono");
        if (result != 0)
        {
            StatusText.Text = $"Engine: render_init failed ({result})";
            return;
        }

        _engine.RenderSetClearColor(0x1E1E2E);

        // TSF
        _tsfBridge = new TsfBridge();
        _tsfBridge.Initialize(hwnd, /* engine handle for TSF */ GetEngineHandle());
        _engine.TsfAttach(_tsfBridge.Hwnd);

        // Create first session
        _activeSessionId = _engine.CreateSession(null, null, 80, 24);
        _hasActiveSession = _engine.SessionCount > 0;

        if (!_hasActiveSession)
        {
            StatusText.Text = "Engine: session_create failed";
            return;
        }

        StatusText.Text = $"Session #{_activeSessionId} active";

        _engine.RenderStart();
        _engine.TsfFocus(_activeSessionId);

        PreviewKeyDown += OnTerminalKeyDown;
        PreviewTextInput += OnTerminalTextInput;
    }

    private nint GetEngineHandle()
    {
        if (_engine is EngineService es)
            return es.Handle;
        return IntPtr.Zero;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _tsfBridge?.Dispose();
        // Engine cleanup is handled by App.OnExit → IEngineService.Dispose
    }

    private void OnTerminalResized(uint widthPx, uint heightPx)
    {
        if (_engine is not { IsInitialized: true }) return;
        _engine.RenderResize(widthPx, heightPx);
    }

    private void OnTerminalKeyDown(object sender, KeyEventArgs e)
    {
        if (!_engine.IsInitialized || !_hasActiveSession) return;

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
            _engine.WriteSession(_activeSessionId, data);
            e.Handled = true;
        }
    }

    private void OnTerminalTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_engine.IsInitialized || !_hasActiveSession) return;
        if (string.IsNullOrEmpty(e.Text)) return;

        var utf8 = Encoding.UTF8.GetBytes(e.Text);
        _engine.WriteSession(_activeSessionId, utf8);
        e.Handled = true;
    }
}
