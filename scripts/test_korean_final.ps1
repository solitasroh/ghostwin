# Korean IME Test for GhostWin Terminal
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;

public class GWF {
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
    [DllImport("user32.dll", SetLastError=true)]
    public static extern uint SendInput(uint n, INPUT[] inp, int size);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    public static IntPtr FindGW() {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len+1);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString().IndexOf("Ghost", StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void Key(ushort vk) {
        var inp = new INPUT[2];
        inp[0].type = 1; inp[0].u.ki.wVk = vk; inp[0].u.ki.dwFlags = 0;
        inp[1].type = 1; inp[1].u.ki.wVk = vk; inp[1].u.ki.dwFlags = 2;
        SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(200);
    }
}
"@

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$hwnd = [GWF]::FindGW()
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "ERROR: not found"; exit 1 }
Write-Host "GhostWin: $hwnd"

# Restore (not maximize to avoid covering issues) and focus
[GWF]::ShowWindow($hwnd, 9)
Start-Sleep -Milliseconds 300
[GWF]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 500

# Get rect
$r = New-Object GWF+RECT
[GWF]::GetWindowRect($hwnd, [ref]$r) | Out-Null
Write-Host "Rect: L=$($r.Left) T=$($r.Top) R=$($r.Right) B=$($r.Bottom)"

# Click center of window
$cx = $r.Left + [int](($r.Right - $r.Left)/2)
$cy = $r.Top + [int](($r.Bottom - $r.Top)/2)
[GWF]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 100
[GWF]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 50
[GWF]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500

$fg = [GWF]::GetForegroundWindow()
Write-Host "Foreground match: $($fg -eq $hwnd)"

# ===== English test =====
Write-Host "--- Sending: echo hello ---"
[System.Windows.Forms.SendKeys]::SendWait("echo hello{ENTER}")
Start-Sleep -Milliseconds 2000

# Re-focus and screenshot
[GWF]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 300

$r2 = New-Object GWF+RECT
[GWF]::GetWindowRect($hwnd, [ref]$r2) | Out-Null
$sw = $r2.Right - $r2.Left
$sh = $r2.Bottom - $r2.Top
Write-Host "Screenshot size: ${sw}x${sh}"
if ($sw -gt 0 -and $sh -gt 0) {
    $bmp = New-Object System.Drawing.Bitmap($sw, $sh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r2.Left, $r2.Top, 0, 0, (New-Object System.Drawing.Size($sw, $sh)))
    $bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\kr_test_1_english.png")
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Saved: kr_test_1_english.png"
}

# ===== Korean test A: hangul =====
Write-Host "--- Korean test A ---"
[GWF]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 300
[GWF]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 100
[GWF]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 50
[GWF]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300

[System.Windows.Forms.SendKeys]::SendWait("echo ")
Start-Sleep -Milliseconds 300

# VK_HANGUL toggle
[GWF]::Key([uint16]0x15)
Start-Sleep -Milliseconds 700

# 한 = ㅎ(g) ㅏ(k) ㄴ(s)
# 글 = ㄱ(r) ㅡ(m) ㄹ(f)
[System.Windows.Forms.SendKeys]::SendWait("g")
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("k")
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("s")
Start-Sleep -Milliseconds 400

[System.Windows.Forms.SendKeys]::SendWait("r")
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("m")
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("f")
Start-Sleep -Milliseconds 500

# Toggle back
[GWF]::Key([uint16]0x15)
Start-Sleep -Milliseconds 300

[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 2000

# Screenshot
[GWF]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 300
$r3 = New-Object GWF+RECT
[GWF]::GetWindowRect($hwnd, [ref]$r3) | Out-Null
$sw = $r3.Right - $r3.Left; $sh = $r3.Bottom - $r3.Top
if ($sw -gt 0 -and $sh -gt 0) {
    $bmp = New-Object System.Drawing.Bitmap($sw, $sh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r3.Left, $r3.Top, 0, 0, (New-Object System.Drawing.Size($sw, $sh)))
    $bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\kr_test_2_hangul.png")
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Saved: kr_test_2_hangul.png"
}

# ===== Korean test B: annyeong =====
Write-Host "--- Korean test B ---"
[GWF]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 300
[GWF]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 100
[GWF]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 50
[GWF]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300

[System.Windows.Forms.SendKeys]::SendWait("echo ")
Start-Sleep -Milliseconds 300

[GWF]::Key([uint16]0x15)
Start-Sleep -Milliseconds 700

# 안 = ㅇ(d) ㅏ(k) ㄴ(s)
# 녕 = ㄴ(s) ㅕ(u) ㅇ(d)
[System.Windows.Forms.SendKeys]::SendWait("d")
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("k")
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("s")
Start-Sleep -Milliseconds 400

[System.Windows.Forms.SendKeys]::SendWait("s")
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("u")
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("d")
Start-Sleep -Milliseconds 500

[GWF]::Key([uint16]0x15)
Start-Sleep -Milliseconds 300

[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 2000

# Screenshot
[GWF]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 300
$r4 = New-Object GWF+RECT
[GWF]::GetWindowRect($hwnd, [ref]$r4) | Out-Null
$sw = $r4.Right - $r4.Left; $sh = $r4.Bottom - $r4.Top
if ($sw -gt 0 -and $sh -gt 0) {
    $bmp = New-Object System.Drawing.Bitmap($sw, $sh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r4.Left, $r4.Top, 0, 0, (New-Object System.Drawing.Size($sw, $sh)))
    $bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\kr_test_3_annyeong.png")
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Saved: kr_test_3_annyeong.png"
}

Write-Host "=== Complete ==="
