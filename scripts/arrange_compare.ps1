Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Arrange {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@

$w = 1280; $h = 800

# Minimize Claude Code WezTerm (the one with "Review" or "memory" in title)
$ccWez = Get-Process wezterm-gui | Where-Object { $_.MainWindowTitle -like '*Review*' -or $_.MainWindowTitle -like '*memory*' -or $_.MainWindowTitle -like '*Claude*' }
foreach ($p in $ccWez) {
    [Win32Arrange]::ShowWindow($p.MainWindowHandle, 6)  # SW_MINIMIZE
    Write-Host "Minimized Claude Code: $($p.Id)"
}

# Comparison terminals
$wez = (Get-Process wezterm-gui | Where-Object { $_.MainWindowTitle -like '*pwsh*' })[0]
$al = (Get-Process alacritty)[0]
$wt = (Get-Process WindowsTerminal)[0]
$gw = (Get-Process ghostwin_winui -ErrorAction SilentlyContinue)[0]

# Arrange in 4 quadrants
if ($wez) { [Win32Arrange]::ShowWindow($wez.MainWindowHandle, 9); [Win32Arrange]::SetWindowPos($wez.MainWindowHandle, [IntPtr]::Zero, 0, 0, $w, $h, 0x0040) }
if ($al)  { [Win32Arrange]::ShowWindow($al.MainWindowHandle, 9);  [Win32Arrange]::SetWindowPos($al.MainWindowHandle, [IntPtr]::Zero, $w, 0, $w, $h, 0x0040) }
if ($wt)  { [Win32Arrange]::ShowWindow($wt.MainWindowHandle, 9);  [Win32Arrange]::SetWindowPos($wt.MainWindowHandle, [IntPtr]::Zero, 0, $h, $w, $h, 0x0040) }
if ($gw)  { [Win32Arrange]::ShowWindow($gw.MainWindowHandle, 9);  [Win32Arrange]::SetWindowPos($gw.MainWindowHandle, [IntPtr]::Zero, $w, $h, $w, $h, 0x0040) }

Write-Host "Arranged: WezTerm(TL) Alacritty(TR) WT(BL) GhostWin(BR)"
