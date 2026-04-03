Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;

public class GW {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] inp, int size);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT {
        public uint type;
        public INPUTUNION u;
    }
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public static IntPtr FindGW() {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString().IndexOf("Ghost", StringComparison.OrdinalIgnoreCase) >= 0) {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void Key(ushort vk) {
        var inp = new INPUT[2];
        inp[0].type = 1;
        inp[0].u.ki.wVk = vk;
        inp[0].u.ki.dwFlags = 0;
        inp[1].type = 1;
        inp[1].u.ki.wVk = vk;
        inp[1].u.ki.dwFlags = 2;
        SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(200);
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        Thread.Sleep(100);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(50);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(200);
    }
}
"@

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Take-Screenshot($path) {
    $rect = New-Object GW+RECT
    [GW]::GetWindowRect($script:hwnd, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) {
        $w = 1200; $h = 800
        $rect.Left = 0; $rect.Top = 0
    }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $bmp.Save($path)
    $g.Dispose()
    $bmp.Dispose()
    Write-Host "Screenshot: $path"
}

$script:hwnd = [GW]::FindGW()
if ($script:hwnd -eq [IntPtr]::Zero) {
    Write-Host "ERROR: GhostWin not found"
    exit 1
}
Write-Host "Found GhostWin: $($script:hwnd)"

# Focus and maximize
[GW]::ShowWindow($script:hwnd, 3)
Start-Sleep -Milliseconds 500
[GW]::SetForegroundWindow($script:hwnd)
Start-Sleep -Milliseconds 500

# Click center
$rect = New-Object GW+RECT
[GW]::GetWindowRect($script:hwnd, [ref]$rect) | Out-Null
$cx = $rect.Left + [int](($rect.Right - $rect.Left)/2)
$cy = $rect.Top + [int](($rect.Bottom - $rect.Top)/2)
Write-Host "Clicking ($cx, $cy)"
[GW]::Click($cx, $cy)
Start-Sleep -Milliseconds 500

# Verify focus
$fg = [GW]::GetForegroundWindow()
Write-Host "Foreground: $fg, expected: $($script:hwnd), match: $($fg -eq $script:hwnd)"

# === Basic test with SendKeys ===
Write-Host ""
Write-Host "=== Basic test: echo hello ==="
[System.Windows.Forms.SendKeys]::SendWait("echo hello")
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 2000

Take-Screenshot "C:\Users\Solit\Rootech\works\ghostwin\test_sendkeys.png"

# === Test A: Korean hangul ===
Write-Host ""
Write-Host "=== Test A: echo + Korean hangul ==="
[System.Windows.Forms.SendKeys]::SendWait("echo ")
Start-Sleep -Milliseconds 300

# Toggle Hangul IME via SendInput
[GW]::Key([uint16]0x15)
Start-Sleep -Milliseconds 500

# Korean 2-set: han = gks, geul = rmf
[System.Windows.Forms.SendKeys]::SendWait("gks")
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("rmf")
Start-Sleep -Milliseconds 500

# Toggle back
[GW]::Key([uint16]0x15)
Start-Sleep -Milliseconds 300

[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 2000

Take-Screenshot "C:\Users\Solit\Rootech\works\ghostwin\test_korean_A.png"

# === Test B: Korean annyeong ===
Write-Host ""
Write-Host "=== Test B: echo + Korean annyeong ==="
[System.Windows.Forms.SendKeys]::SendWait("echo ")
Start-Sleep -Milliseconds 300

[GW]::Key([uint16]0x15)
Start-Sleep -Milliseconds 500

# an = dks, nyeong = sud
[System.Windows.Forms.SendKeys]::SendWait("dks")
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("sud")
Start-Sleep -Milliseconds 500

[GW]::Key([uint16]0x15)
Start-Sleep -Milliseconds 300

[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 2000

Take-Screenshot "C:\Users\Solit\Rootech\works\ghostwin\test_korean_B.png"

Write-Host ""
Write-Host "=== All tests complete ==="
