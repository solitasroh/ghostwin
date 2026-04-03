Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;

public class SI {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr LoadKeyboardLayout(string id, uint f);
    [DllImport("user32.dll")] public static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint f);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
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
            var sb = new System.Text.StringBuilder(len + 1);
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

$hwnd = [SI]::FindGW()
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Host "ERROR: GhostWin not found"
    exit 1
}
Write-Host "Found: $hwnd"

# Focus
[SI]::ShowWindow($hwnd, 9)
Start-Sleep -Milliseconds 300
[SI]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 500

# Click center
$rect = New-Object SI+RECT
[SI]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$cx = $rect.Left + [int](($rect.Right - $rect.Left)/2)
$cy = $rect.Top + [int](($rect.Bottom - $rect.Top)/2)
[SI]::Click($cx, $cy)
Start-Sleep -Milliseconds 500

Write-Host "=== Basic input test: echo test ==="
# Type "echo test" + Enter
$keys = @(0x45, 0x43, 0x48, 0x4F, 0x20, 0x54, 0x45, 0x53, 0x54, 0x0D)
foreach ($k in $keys) {
    [SI]::Key([uint16]$k)
}
Start-Sleep -Milliseconds 2000

# Screenshot basic
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\test_basic.png")
$g.Dispose()
$bmp.Dispose()
Write-Host "Screenshot: test_basic.png"

Write-Host ""
Write-Host "=== Test A: Korean input hangul ==="
# Type "echo "
foreach ($k in @(0x45, 0x43, 0x48, 0x4F, 0x20)) {
    [SI]::Key([uint16]$k)
}
Start-Sleep -Milliseconds 300

# Toggle Korean IME (VK_HANGUL = 0x15)
[SI]::Key([uint16]0x15)
Start-Sleep -Milliseconds 500

# Korean 2-set: han = G(0x47)+K(0x4B)+S(0x53), geul = R(0x52)+M(0x4D)+F(0x46)
foreach ($k in @(0x47, 0x4B, 0x53)) {
    [SI]::Key([uint16]$k)
}
Start-Sleep -Milliseconds 400

foreach ($k in @(0x52, 0x4D, 0x46)) {
    [SI]::Key([uint16]$k)
}
Start-Sleep -Milliseconds 500

# Toggle back to English
[SI]::Key([uint16]0x15)
Start-Sleep -Milliseconds 300

# Enter
[SI]::Key([uint16]0x0D)
Start-Sleep -Milliseconds 2000

# Screenshot A
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\test_korean_A.png")
$g.Dispose()
$bmp.Dispose()
Write-Host "Screenshot: test_korean_A.png"

Start-Sleep -Milliseconds 1000

Write-Host ""
Write-Host "=== Test B: Korean input annyeong ==="
# Type "echo "
foreach ($k in @(0x45, 0x43, 0x48, 0x4F, 0x20)) {
    [SI]::Key([uint16]$k)
}
Start-Sleep -Milliseconds 300

# Toggle Korean IME
[SI]::Key([uint16]0x15)
Start-Sleep -Milliseconds 500

# an = D(0x44)+K(0x4B)+S(0x53)
foreach ($k in @(0x44, 0x4B, 0x53)) {
    [SI]::Key([uint16]$k)
}
Start-Sleep -Milliseconds 400

# nyeong = S(0x53)+U(0x55)+D(0x44)
foreach ($k in @(0x53, 0x55, 0x44)) {
    [SI]::Key([uint16]$k)
}
Start-Sleep -Milliseconds 500

# Toggle back to English
[SI]::Key([uint16]0x15)
Start-Sleep -Milliseconds 300

# Enter
[SI]::Key([uint16]0x0D)
Start-Sleep -Milliseconds 2000

# Screenshot B
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\test_korean_B.png")
$g.Dispose()
$bmp.Dispose()
Write-Host "Screenshot: test_korean_B.png"

Write-Host ""
Write-Host "=== All tests complete ==="
