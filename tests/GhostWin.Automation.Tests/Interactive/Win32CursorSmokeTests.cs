using System.Runtime.InteropServices;
using FluentAssertions;
using GhostWin.Automation.Tests.Infrastructure;

namespace GhostWin.Automation.Tests.Interactive;

[Trait("Category", "Interactive")]
public sealed class Win32CursorSmokeTests
{
    [Theory]
    [InlineData("text", 32513)]
    [InlineData("pointer", 32649)]
    [InlineData("default", 32512)]
    public async Task Osc22_updates_actual_win32_cursor(string value, int expectedCursorId)
    {
        if (Environment.GetEnvironmentVariable("GHOSTWIN_INTERACTIVE_AUTOMATION") != "1")
            return;

        using var app = await DailyApp.LaunchAsync($"{nameof(Osc22_updates_actual_win32_cursor)}_{value}");
        app.Should().NotBeNull();

        var ready = await app!.WaitForReadyAsync();
        ready.ActiveSessionId.Should().NotBeNull();

        var terminalHwnd = FindTerminalChildHwnd(app.Window.Properties.NativeWindowHandle.Value);
        var cursorTarget = GetCenterPoint(terminalHwnd);

        PrepareMainWindow(app.Window.Properties.NativeWindowHandle.Value);
        MoveMouseTo(cursorTarget.X - 8, cursorTarget.Y - 8);

        await app.Client.InjectOscAsync("22", value, ready.ActiveSessionId);
        await app.WaitForStateAsync(
            $"cursor oracle reports {expectedCursorId}",
            _ => app.ReadElementText(AutomationIds.MouseCursorId)
                .Contains($"cursorId={expectedCursorId}", StringComparison.Ordinal));

        MoveMouseTo(cursorTarget.X, cursorTarget.Y);

        var expectedCursor = LoadSystemCursor(expectedCursorId);
        var currentCursor = WaitForCurrentCursor(expectedCursor, terminalHwnd, cursorTarget);

        currentCursor.Should().Be(expectedCursor);
    }

    private static IntPtr FindTerminalChildHwnd(IntPtr mainWindowHandle)
    {
        IntPtr result = IntPtr.Zero;
        EnumChildWindows(mainWindowHandle, (hwnd, _) =>
        {
            if (string.Equals(GetWindowClassName(hwnd), "GhostWinTermChild", StringComparison.Ordinal))
            {
                result = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        result.Should().NotBe(IntPtr.Zero, "GhostWinTermChild HWND should exist");
        return result;
    }

    private static POINT GetCenterPoint(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var rect).Should().BeTrue();
        return new POINT
        {
            X = rect.Left + ((rect.Right - rect.Left) / 2),
            Y = rect.Top + ((rect.Bottom - rect.Top) / 2),
        };
    }

    private static void PrepareMainWindow(IntPtr hwnd)
    {
        ShowWindow(hwnd, SW_RESTORE);
        _ = SetForegroundWindow(hwnd);
    }

    private static void MoveMouseTo(int x, int y)
        => SetCursorPos(x, y).Should().BeTrue();

    private static IntPtr WaitForCurrentCursor(IntPtr expectedCursor, IntPtr terminalHwnd, POINT cursorTarget)
    {
        IntPtr current = IntPtr.Zero;
        var matched = SpinWait.SpinUntil(() =>
        {
            MoveMouseTo(cursorTarget.X, cursorTarget.Y);
            SendSetCursor(terminalHwnd);

            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref info)) return false;
            current = info.hCursor;
            return current == expectedCursor;
        }, TimeSpan.FromSeconds(3));

        matched.Should().BeTrue($"current cursor should become expected handle {expectedCursor}, current={current}");
        return current;
    }

    private static IntPtr LoadSystemCursor(int cursorId)
    {
        var cursor = LoadCursor(IntPtr.Zero, new IntPtr(cursorId));
        cursor.Should().NotBe(IntPtr.Zero, $"system cursor {cursorId} should load");
        return cursor;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static void SendSetCursor(IntPtr hwnd)
    {
        const int lParam = (WM_MOUSEMOVE << 16) | HTCLIENT;
        _ = SendMessage(hwnd, WM_SETCURSOR, hwnd, new IntPtr(lParam));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    private const int HTCLIENT = 1;
    private const int SW_RESTORE = 9;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_SETCURSOR = 0x0020;

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
}
