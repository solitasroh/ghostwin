using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GhostWin.App.Diagnostics;

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
        // #4 buildwindow-enter — BuildWindowCore 진입점.
        // H3 가설 검증: 동일 인스턴스에서 BuildWindowCore 가 2회 이상 호출되면 H3 confirmed.
        // parent hwnd 를 기록하여 re-parenting 여부 추적.
        RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "buildwindow-enter",
            ("parent", hwndParent.Handle), ("pane_id", PaneId),
            ("instance_hash", GetHashCode()));

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

        // #5 buildwindow-created — CreateWindowEx 완료 직후.
        // child_hwnd=0 이면 CreateWindowEx 실패 (GetLastError 필요).
        RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "buildwindow-created",
            ("child_hwnd", _childHwnd), ("w", w), ("h", h), ("pane_id", PaneId));

        // #6 hostready-enqueue — Dispatcher.BeginInvoke 직전.
        // priority=Normal(9). H1 가설: 이 enqueue 가 Loaded(6) lambda 보다 늦게 dequeue 될 수 있음.
        // HEISENBUG NOTE: 이 Dispatcher.BeginInvoke 는 production code 의 것 — RenderDiag 는
        // 절대 추가 BeginInvoke 를 삽입하지 않음.
        //
        // ⚠️ DO NOT modify the BeginInvoke priority below without auditing
        // first-pane-render-failure HC-3 (design.md §0.1 C-5, §2.1 race diagram).
        // Option B intentionally relies on the *subscriber being attached
        // synchronously by BuildElement before BuildWindowCore returns* — i.e.
        // the race is closed by attach-ordering, not by priority alignment.
        // Lowering priority to Loaded(6) would re-introduce HC-3 by reopening
        // the window where a layout-pass-induced Render(7) drains this Normal
        // queue out of order. Raising it to Send/Background also breaks the
        // contract. Keep it at the default (Normal=9) and rely on the Option B
        // attach-before-fire invariant established in MainWindow.InitializeRenderer.
        RenderDiag.LogEvent(RenderDiag.LEVEL_TIMING, "hostready-enqueue",
            ("priority", "Normal"), ("pane_id", PaneId), ("child_hwnd", _childHwnd));

        Dispatcher.BeginInvoke(() =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var pw = (uint)Math.Max(1, ActualWidth * dpi.DpiScaleX);
            var ph = (uint)Math.Max(1, ActualHeight * dpi.DpiScaleY);

            // #7 hostready-fire — HostReady.Invoke 직전, subscriber_count 측정.
            // subscriber_count == 0 이면 H1 confirmed: Loaded(6) lambda 가 이미 완료됐고
            // OnHostReady 구독자가 없는 상태에서 HostReady 가 fire 된 것.
            var handler = HostReady;  // local copy — atomic snapshot (thread-safe)
            int subscriberCount = handler?.GetInvocationList().Length ?? 0;
            RenderDiag.LogEvent(RenderDiag.LEVEL_STATE, "hostready-fire",
                ("subscriber_count", subscriberCount),
                ("pane_id", PaneId),
                ("child_hwnd", _childHwnd));

            handler?.Invoke(this, new(PaneId, _childHwnd, pw, ph));
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
