Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;

public class GW5 {
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
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint idThread);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

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

function Take-Shot($name) {
    Start-Sleep -Milliseconds 500
    # Re-focus GhostWin before screenshot
    [GW5]::SetForegroundWindow($script:hwnd)
    Start-Sleep -Milliseconds 300
    $rect = New-Object GW5+RECT
    [GW5]::GetWindowRect($script:hwnd, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0) { $w = 1536; $h = 816; $rect.Left = 0; $rect.Top = 0 }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $path = "C:\Users\Solit\Rootech\works\ghostwin\$name.png"
    $bmp.Save($path)
    $g.Dispose()
    $bmp.Dispose()
    Write-Host "Screenshot: $name.png"
}

function Focus-GW {
    [GW5]::ShowWindow($script:hwnd, 9)
    Start-Sleep -Milliseconds 200
    [GW5]::SetForegroundWindow($script:hwnd)
    Start-Sleep -Milliseconds 500
    # Click center
    $rect = New-Object GW5+RECT
    [GW5]::GetWindowRect($script:hwnd, [ref]$rect) | Out-Null
    $cx = $rect.Left + [int](($rect.Right - $rect.Left)/2)
    $cy = $rect.Top + [int](($rect.Bottom - $rect.Top)/2)
    [GW5]::Click($cx, $cy)
    Start-Sleep -Milliseconds 300
}

$script:hwnd = [GW5]::FindGW()
if ($script:hwnd -eq [IntPtr]::Zero) {
    Write-Host "ERROR: GhostWin not found"
    exit 1
}
Write-Host "Found GhostWin: $($script:hwnd)"

# Maximize
[GW5]::ShowWindow($script:hwnd, 3)
Start-Sleep -Milliseconds 500

# First, switch to English layout to ensure clean state
$enLayout = [GW5]::LoadKeyboardLayout("00000409", 1)
[GW5]::ActivateKeyboardLayout($enLayout, 0)
Start-Sleep -Milliseconds 300
Write-Host "English layout activated"

# Focus
Focus-GW

# Check keyboard layout of target
$pid = [uint32]0
$tid = [GW5]::GetWindowThreadProcessId($script:hwnd, [ref]$pid)
$layout = [GW5]::GetKeyboardLayout($tid)
Write-Host "Target layout: $layout (04120412=Korean, 04090409=English)"

# === Step 1: Clear any existing input, type basic English ===
Write-Host ""
Write-Host "=== Basic English test ==="
Focus-GW
[System.Windows.Forms.SendKeys]::SendWait("echo hello")
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 2000
Take-Shot "test_01_echo_hello"

# === Step 2: Test Korean - 한글 ===
Write-Host ""
Write-Host "=== Test A: Korean hangul ==="
Focus-GW

# Type echo in English first
[System.Windows.Forms.SendKeys]::SendWait("echo ")
Start-Sleep -Milliseconds 300

# Toggle to Korean IME
[GW5]::Key([uint16]0x15)  # VK_HANGUL
Start-Sleep -Milliseconds 700

# han = G K S (2벌식)
[System.Windows.Forms.SendKeys]::SendWait("r")  # ㄱ
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("k")  # ㅏ
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("s")  # ㄴ
Start-Sleep -Milliseconds 500

# Take mid-composition screenshot
Take-Shot "test_02_mid_han"

# geul = R M F -> but wait, let me recheck 2벌식 mapping
# 한 = ㅎ(G) ㅏ(K) ㄴ(S) -- but we're NOT in Korean mode since layout was English
# Actually SendKeys sends characters, and VK_HANGUL toggles the OS IME
# Let me send the 2벌식 physical keys: g=ㅎ, k=ㅏ, s=ㄴ
# Wait - we already typed r,k,s above. In 2벌식: r=ㄱ, k=ㅏ, s=ㄴ -> 간 not 한
# 한 = ㅎ(g) ㅏ(k) ㄴ(s) -> g k s
# 글 = ㄱ(r) ㅡ(m) ㄹ(f) -> r m f

# We made an error above, let me continue and type the rest
[System.Windows.Forms.SendKeys]::SendWait("r")  # ㄱ
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("m")  # ㅡ
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("f")  # ㄹ
Start-Sleep -Milliseconds 500

# Toggle back to English
[GW5]::Key([uint16]0x15)
Start-Sleep -Milliseconds 300

[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 2000
Take-Shot "test_03_after_hangul"

# === Step 3: Test Korean - 안녕 ===
Write-Host ""
Write-Host "=== Test B: Korean annyeong ==="
Focus-GW

[System.Windows.Forms.SendKeys]::SendWait("echo ")
Start-Sleep -Milliseconds 300

# Toggle to Korean
[GW5]::Key([uint16]0x15)
Start-Sleep -Milliseconds 700

# 안 = ㅇ(d) ㅏ(k) ㄴ(s)
[System.Windows.Forms.SendKeys]::SendWait("d")
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("k")
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("s")
Start-Sleep -Milliseconds 500

# 녕 = ㄴ(s) ㅕ(u) ㅇ(d)
[System.Windows.Forms.SendKeys]::SendWait("s")
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("u")
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("d")
Start-Sleep -Milliseconds 500

# Toggle back
[GW5]::Key([uint16]0x15)
Start-Sleep -Milliseconds 300

[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 2000
Take-Shot "test_04_after_annyeong"

Write-Host ""
Write-Host "=== All tests complete ==="
