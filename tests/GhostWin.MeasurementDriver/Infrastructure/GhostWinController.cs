namespace GhostWin.MeasurementDriver.Infrastructure;

internal sealed class GhostWinController
{
    public nint MainWindowHandle { get; }

    public GhostWinController(nint mainWindowHandle)
    {
        MainWindowHandle = mainWindowHandle;
    }

    public void BringToForeground()
    {
        Win32.ShowWindow(MainWindowHandle, Win32.SW_RESTORE);
        Win32.SetForegroundWindow(MainWindowHandle);
    }
}
