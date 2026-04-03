Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;

public class GWInput {
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
    [DllImport("user32.dll")] public static extern IntPtr LoadKeyboardLayout(string id, uint f);
    [DllImport("user32.dll")] public static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint f);

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

    public static void UnicodeKey(char c) {
        var inp = new INPUT[2];
        inp[0].type = 1;
        inp[0].u.ki.wVk = 0;
        inp[0].u.ki.wScan = (ushort)c;
        inp[0].u.ki.dwFlags = 4; // KEYEVENTF_UNICODE
        inp[1].type = 1;
        inp[1].u.ki.wVk = 0;
        inp[1].u.ki.wScan = (ushort)c;
        inp[1].u.ki.dwFlags = 6; // KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
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

    public static void Screenshot(int x, int y, int w, int h, string path) {
        var bmp = new System.Drawing.Bitmap(w, h);
        var g = System.Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
        bmp.Save(path);
        g.Dispose();
        bmp.Dispose();
    }
}
"@

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$hwnd = [GWInput]::FindGW()
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Host "ERROR: GhostWin not found"
    exit 1
}
Write-Host "Found GhostWin: $hwnd"

# Maximize and focus
[GWInput]::ShowWindow($hwnd, 3) # SW_MAXIMIZE
Start-Sleep -Milliseconds 500
[GWInput]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 500

# Get rect after maximize
$rect = New-Object GWInput+RECT
[GWInput]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
Write-Host "Window: ${w}x${h} at ($($rect.Left),$($rect.Top))"

# Click center
$cx = $rect.Left + [int]($w/2)
$cy = $rect.Top + [int]($h/2)
Write-Host "Click ($cx, $cy)"
[GWInput]::Click($cx, $cy)
Start-Sleep -Milliseconds 500

# Verify focus
$fg = [GWInput]::GetForegroundWindow()
Write-Host "Foreground after click: $fg (want: $hwnd) match=$($fg -eq $hwnd)"

# === Test basic English input using SendKeys ===
Write-Host ""
Write-Host "=== Trying SendKeys approach ==="
[System.Windows.Forms.SendKeys]::SendWait("echo hello")
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 2000

# Screenshot
[GWInput]::Screenshot($rect.Left, $rect.Top, $w, $h, "C:\Users\Solit\Rootech\works\ghostwin\test_sendkeys.png")
Write-Host "Screenshot: test_sendkeys.png"

# Check if basic input worked by re-checking foreground
$fg2 = [GWInput]::GetForegroundWindow()
Write-Host "Foreground now: $fg2"

Write-Host ""
Write-Host "=== Test A: Korean input via SendKeys ==="
[System.Windows.Forms.SendKeys]::SendWait("echo ")
Start-Sleep -Milliseconds 300

# Toggle Hangul
[GWInput]::Key([uint16]0x15) # VK_HANGUL
Start-Sleep -Milliseconds 500

# Korean 2-set keys for "han geul": G K S R M F
[System.Windows.Forms.SendKeys]::SendWait("gks")
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("rmf")
Start-Sleep -Milliseconds 500

# Toggle back
[GWInput]::Key([uint16]0x15)
Start-Sleep -Milliseconds 300

[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 2000

[GWInput]::Screenshot($rect.Left, $rect.Top, $w, $h, "C:\Users\Solit\Rootech\works\ghostwin\test_korean_A2.png")
Write-Host "Screenshot: test_korean_A2.png"

Write-Host ""
Write-Host "=== Test B: Korean input annyeong ==="
[System.Windows.Forms.SendKeys]::SendWait("echo ")
Start-Sleep -Milliseconds 300

[GWInput]::Key([uint16]0x15) # VK_HANGUL
Start-Sleep -Milliseconds 500

# an = D K S, nyeong = S U D
[System.Windows.Forms.SendKeys]::SendWait("dks")
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("sud")
Start-Sleep -Milliseconds 500

[GWInput]::Key([uint16]0x15)
Start-Sleep -Milliseconds 300

[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 2000

[GWInput]::Screenshot($rect.Left, $rect.Top, $w, $h, "C:\Users\Solit\Rootech\works\ghostwin\test_korean_B2.png")
Write-Host "Screenshot: test_korean_B2.png"

Write-Host ""
Write-Host "=== All tests complete ==="
