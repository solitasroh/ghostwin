using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GhostWin.Interop;

public class TsfBridge : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TSF_PENDING = WM_USER + 50;

    private HwndSource? _hwndSource;
    private DispatcherTimer? _focusTimer;
    private nint _engine;

    public nint Hwnd => _hwndSource?.Handle ?? IntPtr.Zero;

    public void Initialize(nint parentHwnd, nint engine)
    {
        _engine = engine;

        var param = new HwndSourceParameters("GhostWinTsfInput")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = 0x40000000, // WS_CHILD
            ParentWindow = parentHwnd,
        };
        _hwndSource = new HwndSource(param);
        _hwndSource.AddHook(WndProc);

        // ADR-011: 50ms focus timer
        _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _focusTimer.Tick += OnFocusTick;
        _focusTimer.Start();
    }

    private void OnFocusTick(object? sender, EventArgs e)
    {
        if (_hwndSource == null) return;
        var hwnd = _hwndSource.Handle;
        var parent = GetParent(hwnd);

        // first-pane-render-failure Option B regression fix (2026-04-08):
        //
        // Preserve the pre-Option-B invariant that the hidden TSF HWND must
        // NOT steal focus when its parent is the top-level (foreground)
        // window. Originally TsfBridge was initialized with the initial
        // pane's child HWND as parent — a non-top-level HWND that is never
        // the foreground window — so `GetForegroundWindow() == parent` was
        // always false and the SetFocus branch was dead code in practice.
        //
        // After Option B changed parent to the main window HWND (so TsfBridge
        // can initialize before any pane exists), the original condition
        // became always true, causing SetFocus(tsfHwnd) to fire on every
        // 50ms tick. The TSF hidden HWND is an invisible -32000 offscreen
        // WS_CHILD — stealing focus to it prevents MainWindow from receiving
        // WPF PreviewKeyDown events and breaks mouse click focus management
        // (the pane regains focus on click but loses it 50ms later).
        //
        // TSF IME routing does not require the hidden HWND to hold Win32
        // focus: gw_tsf_attach + gw_tsf_focus on the native side handle
        // keyboard input through ITfThreadMgr, which tracks the process's
        // input thread focus, not this specific HWND.
        //
        // Skip the SetFocus entirely when parent is the foreground window —
        // this matches the effective old behavior (SetFocus never called)
        // and keeps keyboard/mouse input routed through MainWindow.
        if (parent == GetForegroundWindow())
            return;

        if (parent != IntPtr.Zero && GetFocus() != hwnd)
            SetFocus(hwnd);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_TSF_PENDING)
        {
            NativeEngine.gw_tsf_send_pending(_engine);
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _focusTimer?.Stop();
        _focusTimer = null;
        _hwndSource?.Dispose();
        _hwndSource = null;
    }

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);
}
