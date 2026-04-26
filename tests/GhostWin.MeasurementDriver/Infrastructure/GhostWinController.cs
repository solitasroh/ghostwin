using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

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
        // Give the focus change time to settle so the next keystroke
        // (Alt+V split) routes to GhostWin's KeyBinding handler.
        Thread.Sleep(250);
    }

    // GhostWin uses Alt+V for vertical split and Alt+H for horizontal split
    // (see src/GhostWin.App/MainWindow.xaml KeyBinding and
    //  src/settings/settings_manager.cpp surface.split_right/down).
    public void SplitVertical()
        => Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.KEY_V);

    public void SplitHorizontal()
        => Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.KEY_H);
}
