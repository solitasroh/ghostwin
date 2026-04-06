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
        if (parent != IntPtr.Zero && GetForegroundWindow() == parent && GetFocus() != hwnd)
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
