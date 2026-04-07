using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GhostWin.App.Controls;

public record HostReadyEventArgs(uint PaneId, nint Hwnd, uint WidthPx, uint HeightPx);
public record PaneResizeEventArgs(uint PaneId, uint WidthPx, uint HeightPx);
public record PaneClickedEventArgs(uint PaneId);

public class TerminalHostControl : HwndHost
{
    private nint _childHwnd;
    private static bool _classRegistered;
    private const string ChildClassName = "GhostWinTermChild";
    // ConcurrentDictionary: WndProc may be invoked re-entrantly from native
    // SendMessage chains while BuildWindowCore/DestroyWindowCore are running
    // on the UI thread. Lock-free reads make the lookup safe without a global mutex.
    private static readonly ConcurrentDictionary<nint, TerminalHostControl> _hostsByHwnd = new();

    public nint ChildHwnd => _childHwnd;
    public uint PaneId { get; set; }
    /// <summary>
    /// The terminal session this host displays. PaneContainerControl uses this
    /// for migration: when a leaf's paneId changes (e.g. after Split allocates
    /// new IDs), the existing host whose SessionId matches is reused, so the
    /// child HWND (and any swap chain target bound to it) is preserved.
    /// </summary>
    public uint SessionId { get; set; }

    public event EventHandler<HostReadyEventArgs>? HostReady;
    public event EventHandler<PaneResizeEventArgs>? PaneResizeRequested;
    public event EventHandler<PaneClickedEventArgs>? PaneClicked;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        if (!_classRegistered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = Marshal.GetHINSTANCE(typeof(TerminalHostControl).Module),
                lpszClassName = ChildClassName,
                hbrBackground = IntPtr.Zero,
            };
            RegisterClassEx(ref wc);
            _classRegistered = true;
        }

        int w = Math.Max(1, (int)ActualWidth);
        int h = Math.Max(1, (int)ActualHeight);

        _childHwnd = CreateWindowEx(
            0, ChildClassName, "",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, w, h,
            hwndParent.Handle, IntPtr.Zero,
            Marshal.GetHINSTANCE(typeof(TerminalHostControl).Module),
            IntPtr.Zero);

        _hostsByHwnd[_childHwnd] = this;

        Dispatcher.BeginInvoke(() =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var pw = (uint)Math.Max(1, ActualWidth * dpi.DpiScaleX);
            var ph = (uint)Math.Max(1, ActualHeight * dpi.DpiScaleY);
            HostReady?.Invoke(this, new(PaneId, _childHwnd, pw, ph));
        });

        return new HandleRef(this, _childHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _hostsByHwnd.TryRemove(hwnd.Handle, out _);
        DestroyWindow(hwnd.Handle);
        _childHwnd = IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_childHwnd == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var widthPx = (uint)(sizeInfo.NewSize.Width * dpi.DpiScaleX);
        var heightPx = (uint)(sizeInfo.NewSize.Height * dpi.DpiScaleY);
        if (widthPx < 1) widthPx = 1;
        if (heightPx < 1) heightPx = 1;

        SetWindowPos(_childHwnd, IntPtr.Zero, 0, 0,
            (int)widthPx, (int)heightPx, SWP_NOZORDER | SWP_NOMOVE);

        PaneResizeRequested?.Invoke(this, new(PaneId, widthPx, heightPx));
    }

    private static readonly WndProcDelegate _wndProcDelegate = WndProc;

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
        {
            if (_hostsByHwnd.TryGetValue(hwnd, out var host))
            {
                // Capture only paneId by value — host may be disposed before
                // the dispatcher drains the queued callback. The lookup
                // inside the lambda re-validates the host is still alive.
                var paneId = host.PaneId;
                var hostHwnd = hwnd;
                host.Dispatcher.BeginInvoke(() =>
                {
                    if (_hostsByHwnd.TryGetValue(hostHwnd, out var live) &&
                        live._childHwnd != IntPtr.Zero)
                    {
                        live.PaneClicked?.Invoke(live, new PaneClickedEventArgs(paneId));
                    }
                });
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    const uint WS_CHILD = 0x40000000;
    const uint WS_VISIBLE = 0x10000000;
    const uint WS_CLIPCHILDREN = 0x02000000;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOMOVE = 0x0002;
    const uint WM_LBUTTONDOWN = 0x0201;
    const uint WM_RBUTTONDOWN = 0x0204;
    const uint WM_MBUTTONDOWN = 0x0207;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }
}
