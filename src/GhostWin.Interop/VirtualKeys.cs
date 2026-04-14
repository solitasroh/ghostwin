// BC-09 (pre-m11-backlog-cleanup): centralised Win32 virtual-key constants
// and GetKeyState P/Invoke. Previously duplicated in MainWindow.xaml.cs and
// Diagnostics/KeyDiag.cs — collapsed here so that KeyDiag and MainWindow keep
// their modifier state checks in sync.
//
// Scope note: TerminalHostControl.cs still declares its own GetKeyState +
// VK_LBUTTON/VK_MENU because it also maintains a larger cluster of user32
// imports (MapVirtualKey, GetKeyboardLayout, ToUnicodeEx, GetForegroundWindow,
// GetWindowThreadProcessId, GetKeyboardState). Migrating that cluster is a
// future cleanup; BC-09 explicitly keeps it scoped to the small shared VK set.

using System.Runtime.InteropServices;

namespace GhostWin.Interop;

/// <summary>
/// Shared Win32 virtual-key constants and raw <c>GetKeyState</c> access.
/// Used by WPF key handlers to cross-check the WPF <c>Keyboard</c> cache
/// against OS-level modifier state.
/// </summary>
public static class VirtualKeys
{
    public const int VK_SHIFT   = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU    = 0x12; // Alt

    /// <summary>
    /// Win32 <c>GetKeyState</c>. High-bit (0x8000) indicates the key is down.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "GetKeyState")]
    public static extern short GetKeyState(int nVirtKey);

    /// <summary>Raw (OS-level) Ctrl-down check. Bypasses the WPF keyboard cache.</summary>
    public static bool IsCtrlDownRaw() => (GetKeyState(VK_CONTROL) & 0x8000) != 0;

    /// <summary>Raw (OS-level) Shift-down check. Bypasses the WPF keyboard cache.</summary>
    public static bool IsShiftDownRaw() => (GetKeyState(VK_SHIFT) & 0x8000) != 0;

    /// <summary>Raw (OS-level) Alt-down check. Bypasses the WPF keyboard cache.</summary>
    public static bool IsAltDownRaw() => (GetKeyState(VK_MENU) & 0x8000) != 0;
}
