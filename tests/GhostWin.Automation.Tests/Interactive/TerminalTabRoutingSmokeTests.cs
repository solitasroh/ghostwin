using System.Runtime.InteropServices;
using FluentAssertions;
using GhostWin.Automation.Tests.Infrastructure;

namespace GhostWin.Automation.Tests.Interactive;

[Trait("Category", "Interactive")]
public sealed class TerminalTabRoutingSmokeTests
{
    [Fact]
    public async Task Plain_tab_after_terminal_click_is_not_yielded_to_wpf_navigation()
    {
        if (Environment.GetEnvironmentVariable("GHOSTWIN_INTERACTIVE_AUTOMATION") != "1")
            return;

        var logPath = Path.Combine(Path.GetTempPath(), "ghostwin-a11y.log");
        TryDelete(logPath);

        using var app = await DailyApp.LaunchAsync(nameof(Plain_tab_after_terminal_click_is_not_yielded_to_wpf_navigation));
        app.Should().NotBeNull();

        await app!.WaitForReadyAsync();
        var mainHwnd = app.Window.Properties.NativeWindowHandle.Value;
        var terminalHwnd = FindTerminalChildHwnd(mainHwnd);

        PrepareMainWindow(mainHwnd);
        ClickClientCenter(terminalHwnd);
        await Task.Delay(300);

        TryDelete(logPath);
        SendTabToMainWindow(mainHwnd);
        await Task.Delay(300);

        var log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
        log.Should().NotContain("Tab passthrough -> WPF nav");
    }

    [Fact]
    public async Task Plain_space_after_terminal_click_does_not_activate_chrome_focus()
    {
        if (Environment.GetEnvironmentVariable("GHOSTWIN_INTERACTIVE_AUTOMATION") != "1")
            return;

        using var app = await DailyApp.LaunchAsync(nameof(Plain_space_after_terminal_click_does_not_activate_chrome_focus));
        app.Should().NotBeNull();

        var ready = await app!.WaitForReadyAsync();
        var mainHwnd = app.Window.Properties.NativeWindowHandle.Value;
        var terminalHwnd = FindTerminalChildHwnd(mainHwnd);

        PrepareMainWindow(mainHwnd);
        ClickClientCenter(terminalHwnd);
        await Task.Delay(300);

        SendSpaceToMainWindow(mainHwnd);
        await Task.Delay(300);

        var afterSpace = await app.Client.GetStateAsync();
        afterSpace.WorkspaceCount.Should().Be(ready.WorkspaceCount);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort isolation for the shared diagnostic file.
        }
    }

    private static void PrepareMainWindow(IntPtr hwnd)
    {
        ShowWindow(hwnd, SW_RESTORE);
        _ = SetForegroundWindow(hwnd);
    }

    private static void ClickClientCenter(IntPtr hwnd)
    {
        GetClientRect(hwnd, out var rect).Should().BeTrue();
        var x = (rect.Right - rect.Left) / 2;
        var y = (rect.Bottom - rect.Top) / 2;
        var lParam = (IntPtr)((y << 16) | (x & 0xFFFF));

        _ = SendMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
        _ = SendMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
    }

    private static void SendTabToMainWindow(IntPtr hwnd)
    {
        _ = SendMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_TAB, IntPtr.Zero);
        _ = SendMessage(hwnd, WM_KEYUP, (IntPtr)VK_TAB, IntPtr.Zero);
    }

    private static void SendSpaceToMainWindow(IntPtr hwnd)
    {
        _ = SendMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_SPACE, IntPtr.Zero);
        _ = SendMessage(hwnd, WM_KEYUP, (IntPtr)VK_SPACE, IntPtr.Zero);
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

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private const int SW_RESTORE = 9;
    private const int MK_LBUTTON = 0x0001;
    private const int VK_TAB = 0x09;
    private const int VK_SPACE = 0x20;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
}
