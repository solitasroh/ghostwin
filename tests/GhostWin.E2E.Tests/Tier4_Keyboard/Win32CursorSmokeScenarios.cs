using System;
using System.Runtime.InteropServices;
using System.Threading;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using GhostWin.E2E.Tests.Infrastructure;
using GhostWin.E2E.Tests.Stubs;
using Xunit;

namespace GhostWin.E2E.Tests.Tier4_Keyboard;

[Trait("Tier", "4")]
[Trait("Category", "E2E")]
[Trait("Nightly", "true")]
[Trait("Slow", "true")]
[Collection("GhostWin-App")]
public class Win32CursorSmokeScenarios : IClassFixture<GhostWinAppFixture>
{
    private readonly GhostWinAppFixture _fixture;

    public Win32CursorSmokeScenarios(GhostWinAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("text", 32513)]
    [InlineData("pointer", 32649)]
    [InlineData("ew-resize", 32644)]
    [InlineData("default", 32512)]
    public void InjectOsc22_UpdatesActualWin32Cursor(string value, int expectedCursorId)
    {
        var sessionId = WaitForSessionProbe();
        var terminalHwnd = FindTerminalChildHwnd();
        var cursorTarget = GetCenterPoint(terminalHwnd);

        ActivateMainWindow();
        MoveMouseTo(cursorTarget.X - 8, cursorTarget.Y - 8);

        OscInjector.InjectOsc22ToRunningApp(value, sessionId);

        MoveMouseTo(cursorTarget.X, cursorTarget.Y);
        SendSetCursor(terminalHwnd);

        var expectedCursor = LoadSystemCursor(expectedCursorId);
        var currentCursor = WaitForCurrentCursor(expectedCursor);

        currentCursor.Should().Be(expectedCursor);
    }

    private uint WaitForSessionProbe()
    {
        string currentValue = string.Empty;
        var matched = SpinWait.SpinUntil(() =>
        {
            currentValue = ReadProbeValue(AutomationIds.MouseCursorSession);
            return currentValue.StartsWith("sessionId=", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(8));

        matched.Should().BeTrue($"session probe should be populated, current='{currentValue}'");
        return uint.Parse(currentValue["sessionId=".Length..]);
    }

    private IntPtr FindTerminalChildHwnd()
    {
        var mainWindowHwnd = new IntPtr(_fixture.MainWindow.Properties.NativeWindowHandle.Value);
        IntPtr result = IntPtr.Zero;

        EnumChildWindows(mainWindowHwnd, (hwnd, _) =>
        {
            var className = GetWindowClassName(hwnd);
            if (string.Equals(className, "GhostWinTermChild", StringComparison.Ordinal))
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

    private void ActivateMainWindow()
    {
        var mainWindowHwnd = new IntPtr(_fixture.MainWindow.Properties.NativeWindowHandle.Value);
        ShowWindow(mainWindowHwnd, SW_RESTORE);
        SetForegroundWindow(mainWindowHwnd).Should().BeTrue();
        SpinWait.SpinUntil(() => GetForegroundWindow() == mainWindowHwnd, TimeSpan.FromSeconds(3))
            .Should().BeTrue("GhostWin main window should become foreground");
    }

    private static void MoveMouseTo(int x, int y)
        => SetCursorPos(x, y).Should().BeTrue();

    private static IntPtr WaitForCurrentCursor(IntPtr expectedCursor)
    {
        IntPtr current = IntPtr.Zero;
        var matched = SpinWait.SpinUntil(() =>
        {
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

    private string ReadProbeValue(string automationId)
    {
        var element = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        element.Should().NotBeNull($"AutomationId '{automationId}' should exist");
        return ReadElementText(element!);
    }

    private static string ReadElementText(AutomationElement element)
    {
        var helpText = element.Properties.HelpText.ValueOrDefault;
        if (!string.IsNullOrEmpty(helpText))
            return helpText;

        var name = element.Properties.Name.ValueOrDefault;
        if (!string.IsNullOrEmpty(name))
            return name;

        var valuePattern = element.Patterns.Value.PatternOrDefault;
        if (valuePattern != null)
            return valuePattern.Value.Value ?? string.Empty;

        return string.Empty;
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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
}
