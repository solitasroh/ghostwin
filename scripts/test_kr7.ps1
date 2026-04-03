# Korean IME Test v7 - using Get-Process for handle
Add-Type @"
using System;
using System.Runtime.InteropServices;

public class W7 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$proc = Get-Process ghostwin_winui -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "ERROR: ghostwin not running"; exit 1 }
$hwnd = [IntPtr]$proc.MainWindowHandle
Write-Host "GhostWin PID=$($proc.Id) HWND=$hwnd"

[W7]::ShowWindow($hwnd, 9)
Start-Sleep -Milliseconds 500
[W7]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 500

$r = New-Object W7+RECT
[W7]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
Write-Host "Window: ${w}x${h} at ($($r.Left),$($r.Top))"

$cx = $r.Left + [int]($w/2)
$cy = $r.Top + [int]($h/2)
[W7]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 100
[W7]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 50
[W7]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 1000

$fg = [W7]::GetForegroundWindow()
Write-Host "Foreground: $fg, match: $($fg -eq $hwnd)"

# === Test 1: English input ===
Write-Host "`n=== Test 1: echo test ==="
[System.Windows.Forms.SendKeys]::SendWait("echo test{ENTER}")
Start-Sleep -Milliseconds 3000

if ($w -gt 0 -and $h -gt 0) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\kr7_1_english.png")
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Saved: kr7_1_english.png"
}

# === Test 2: Korean paste (hangul) ===
Write-Host "`n=== Test 2: echo + Korean paste ==="
[W7]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 300
[W7]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 50
[W7]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 50
[W7]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500

[System.Windows.Forms.SendKeys]::SendWait("echo ")
Start-Sleep -Milliseconds 300

# Paste Korean text
[System.Windows.Forms.Clipboard]::SetText([char]0xD55C + [string][char]0xAE00)
Start-Sleep -Milliseconds 200
[System.Windows.Forms.SendKeys]::SendWait("^v")
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 3000

if ($w -gt 0 -and $h -gt 0) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\kr7_2_hangul_paste.png")
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Saved: kr7_2_hangul_paste.png"
}

# === Test 3: Korean paste (annyeong) ===
Write-Host "`n=== Test 3: echo + Korean annyeong paste ==="
[W7]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 300
[W7]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 50
[W7]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 50
[W7]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500

[System.Windows.Forms.SendKeys]::SendWait("echo ")
Start-Sleep -Milliseconds 300

[System.Windows.Forms.Clipboard]::SetText([char]0xC548 + [string][char]0xB155)
Start-Sleep -Milliseconds 200
[System.Windows.Forms.SendKeys]::SendWait("^v")
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 3000

if ($w -gt 0 -and $h -gt 0) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\kr7_3_annyeong_paste.png")
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Saved: kr7_3_annyeong_paste.png"
}

Write-Host "`n=== Complete ==="
