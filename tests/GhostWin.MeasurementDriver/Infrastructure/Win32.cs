using System.Runtime.InteropServices;

namespace GhostWin.MeasurementDriver.Infrastructure;

internal static partial class Win32
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

    internal const int SW_RESTORE = 9;
}
