Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@

$w = 1280; $h = 800

$wez = (Get-Process wezterm-gui | Where-Object { $_.MainWindowTitle -like '*pwsh*' })[0]
$al = (Get-Process alacritty)[0]
$wt = (Get-Process WindowsTerminal)[0]
$gw = Get-Process ghostwin_winui -ErrorAction SilentlyContinue

Write-Host "WezTerm: $($wez.Id)"
Write-Host "Alacritty: $($al.Id)"
Write-Host "WT: $($wt.Id)"
if ($gw) { Write-Host "GhostWin: $($gw.Id)" } else { Write-Host "GhostWin: NOT RUNNING" }

if ($wez) { [Win32]::ShowWindow($wez.MainWindowHandle, 9); [Win32]::SetWindowPos($wez.MainWindowHandle, [IntPtr]::Zero, 0, 0, $w, $h, 0x0040) }
if ($al)  { [Win32]::ShowWindow($al.MainWindowHandle, 9);  [Win32]::SetWindowPos($al.MainWindowHandle, [IntPtr]::Zero, $w, 0, $w, $h, 0x0040) }
if ($wt)  { [Win32]::ShowWindow($wt.MainWindowHandle, 9);  [Win32]::SetWindowPos($wt.MainWindowHandle, [IntPtr]::Zero, 0, $h, $w, $h, 0x0040) }
if ($gw)  { [Win32]::ShowWindow($gw.MainWindowHandle, 9);  [Win32]::SetWindowPos($gw.MainWindowHandle, [IntPtr]::Zero, $w, $h, $w, $h, 0x0040) }

Write-Host "Windows arranged in 4 quadrants (2560x1600)"
