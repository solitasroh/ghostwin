using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace GhostWin.Interop;

public class TsfBridge : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TSF_PENDING = WM_USER + 50;

    private HwndSource? _hwndSource;
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
        _hwndSource?.Dispose();
        _hwndSource = null;
    }
}
