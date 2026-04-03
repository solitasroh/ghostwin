# Minimal Korean IME Test
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class W6 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$hwnd = [W6]::FindGW()
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "ERROR: not found"; exit 1 }
Write-Host "Found: $hwnd"

[W6]::ShowWindow($hwnd, 9)
Start-Sleep -Milliseconds 500
[W6]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 500

$r = New-Object W6+RECT
[W6]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$cx = $r.Left + [int](($r.Right - $r.Left)/2)
$cy = $r.Top + [int](($r.Bottom - $r.Top)/2)
[W6]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 100
[W6]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 50
[W6]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 1000

$fg = [W6]::GetForegroundWindow()
Write-Host "Foreground match: $($fg -eq $hwnd)"

# === Test 1: English ===
Write-Host "Test 1: echo test"
[System.Windows.Forms.SendKeys]::SendWait("echo test{ENTER}")
Start-Sleep -Milliseconds 3000

$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\kr_v6_1.png")
$g.Dispose(); $bmp.Dispose()
Write-Host "Saved kr_v6_1.png"

# === Test 2: Korean paste hangul ===
Write-Host "Test 2: echo + paste hangul"
[W6]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 300
[W6]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 100
[W6]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 50
[W6]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500

[System.Windows.Forms.SendKeys]::SendWait("echo ")
Start-Sleep -Milliseconds 300
[System.Windows.Forms.Clipboard]::SetText([string]::new([char[]]@(0xD55C, 0xAE00)))
Start-Sleep -Milliseconds 200
[System.Windows.Forms.SendKeys]::SendWait("^v")
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 3000

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\kr_v6_2.png")
$g.Dispose(); $bmp.Dispose()
Write-Host "Saved kr_v6_2.png"

# === Test 3: Korean paste annyeong ===
Write-Host "Test 3: echo + paste annyeong"
[W6]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 300
[W6]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 100
[W6]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 50
[W6]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500

[System.Windows.Forms.SendKeys]::SendWait("echo ")
Start-Sleep -Milliseconds 300
[System.Windows.Forms.Clipboard]::SetText([string]::new([char[]]@(0xC548, 0xB155)))
Start-Sleep -Milliseconds 200
[System.Windows.Forms.SendKeys]::SendWait("^v")
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 3000

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\kr_v6_3.png")
$g.Dispose(); $bmp.Dispose()
Write-Host "Saved kr_v6_3.png"

Write-Host "=== Done ==="
