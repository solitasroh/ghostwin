using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GhostWinPoC.Interop;
using Wpf.Ui.Controls;

namespace GhostWinPoC;

public partial class MainWindow : FluentWindow
{
    private nint _engine;
    private uint _activeSessionId;
    private TsfBridge? _tsfBridge;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        _engine = app.EngineHandle;

        if (_engine == IntPtr.Zero)
        {
            StatusText.Text = "Engine: creation failed";
            return;
        }

        StatusText.Text = "Engine: created, waiting for layout...";

        // Delay render init until layout is complete and HWND has size
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, InitializeRenderer);
    }

    private void InitializeRenderer()
    {
        var hwnd = TerminalHost.ChildHwnd;
        if (hwnd == IntPtr.Zero)
        {
            StatusText.Text = "Engine: HwndHost not ready";
            return;
        }

        // Get physical pixel size via DPI
        var dpi = VisualTreeHelper.GetDpi(TerminalHost);
        var w = (uint)Math.Max(1, TerminalHost.ActualWidth * dpi.DpiScaleX);
        var h = (uint)Math.Max(1, TerminalHost.ActualHeight * dpi.DpiScaleY);

        StatusText.Text = $"Engine: init renderer {w}x{h} hwnd=0x{hwnd:X}...";

        // Write C# side diagnostic
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ghostwin_csharp_debug.log"),
            $"render_init called: engine=0x{_engine:X} hwnd=0x{hwnd:X} w={w} h={h}\n"); }
        catch { /* ignore */ }

        int result = NativeEngine.gw_render_init(_engine, hwnd, w, h, 14.0f, "Cascadia Mono");

        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ghostwin_csharp_debug.log"),
            $"render_init returned: {result}\n"); }
        catch { /* ignore */ }
        if (result != 0)
        {
            StatusText.Text = $"Engine: render_init failed ({result}) size={w}x{h}";
            return;
        }

        StatusText.Text = "Engine: renderer OK";

        // Set dark background
        NativeEngine.gw_render_set_clear_color(_engine, 0x1E1E2E);

        // Setup TSF hidden HWND
        _tsfBridge = new TsfBridge();
        _tsfBridge.Initialize(hwnd);
        NativeEngine.gw_tsf_attach(_engine, _tsfBridge.Hwnd);

        // Create first session
        // Note: SessionId starts from 0, so 0 is a valid ID.
        // gw_session_create returns the ID on success. We use session_count to verify.
        _activeSessionId = NativeEngine.gw_session_create(
            _engine, null, null, 80, 24);

        var count = NativeEngine.gw_session_count(_engine);
        if (count == 0)
        {
            StatusText.Text = "Engine: session_create failed";
            return;
        }

        StatusText.Text = $"Engine: session #{_activeSessionId} active";

        // Start render loop
        NativeEngine.gw_render_start(_engine);

        // Focus TSF
        NativeEngine.gw_tsf_focus(_engine, _activeSessionId);

        // Keyboard input → engine
        PreviewKeyDown += OnTerminalKeyDown;
        PreviewTextInput += OnTerminalTextInput;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_engine != IntPtr.Zero)
        {
            NativeEngine.gw_render_stop(_engine);
        }
        _tsfBridge?.Dispose();
    }

    private void OnNewTabClick(object sender, RoutedEventArgs e)
    {
        if (_engine == IntPtr.Zero) return;
        var id = NativeEngine.gw_session_create(_engine, null, null, 80, 24);
        if (id > 0)
        {
            _activeSessionId = id;
            NativeEngine.gw_session_activate(_engine, id);
            StatusText.Text = $"Engine: session #{id} active (total: {NativeEngine.gw_session_count(_engine)})";
        }
    }

    private void OnTerminalResized(uint widthPx, uint heightPx)
    {
        if (_engine == IntPtr.Zero) return;
        NativeEngine.gw_render_resize(_engine, widthPx, heightPx);
    }

    private void OnTerminalKeyDown(object sender, KeyEventArgs e)
    {
        if (_engine == IntPtr.Zero || _activeSessionId == 0) return;

        // Map special keys to VT sequences
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

        // Ctrl+C
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            data = "\x03"u8.ToArray();

        if (data != null)
        {
            unsafe
            {
                fixed (byte* ptr = data)
                {
                    NativeEngine.gw_session_write(_engine, _activeSessionId,
                        (nint)ptr, (uint)data.Length);
                }
            }
            e.Handled = true;
        }
    }

    private void OnTerminalTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_engine == IntPtr.Zero || _activeSessionId == 0) return;
        if (string.IsNullOrEmpty(e.Text)) return;

        var utf8 = Encoding.UTF8.GetBytes(e.Text);
        unsafe
        {
            fixed (byte* ptr = utf8)
            {
                NativeEngine.gw_session_write(_engine, _activeSessionId,
                    (nint)ptr, (uint)utf8.Length);
            }
        }
        e.Handled = true;
    }
}
