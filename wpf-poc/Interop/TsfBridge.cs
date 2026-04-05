using System.Windows.Interop;

namespace GhostWinPoC.Interop;

public class TsfBridge : IDisposable
{
    private HwndSource? _hwndSource;

    public nint Hwnd => _hwndSource?.Handle ?? IntPtr.Zero;

    public void Initialize(nint parentHwnd)
    {
        var param = new HwndSourceParameters("GhostWinTsfInput")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = 0x00000000, // WS_OVERLAPPED
            ParentWindow = parentHwnd,
        };
        _hwndSource = new HwndSource(param);
        _hwndSource.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _hwndSource?.Dispose();
        _hwndSource = null;
    }
}
