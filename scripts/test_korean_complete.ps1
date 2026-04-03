# Korean IME Complete Test for GhostWin Terminal
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;

public class GWC {
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
        uint sent = SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(200);
    }

    public static void KeySlow(ushort vk) {
        Key(vk);
        Thread.Sleep(50);  // extra delay
    }
}
"@

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

function Screenshot($name) {
    # Re-focus GhostWin
    [GWC]::SetForegroundWindow($script:hwnd)
    Start-Sleep -Milliseconds 200

    $r = New-Object GWC+RECT
    [GWC]::GetWindowRect($script:hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    if ($w -le 0) { return }

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $path = "C:\Users\Solit\Rootech\works\ghostwin\$name.png"
    $bmp.Save($path)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "  Screenshot: $name.png"
}

function FocusTextBox {
    $auto = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit
    )
    $tb = $auto.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($tb) {
        $tb.SetFocus()
        Start-Sleep -Milliseconds 200
        return $true
    }
    return $false
}

$script:hwnd = [GWC]::FindGW()
if ($script:hwnd -eq [IntPtr]::Zero) { Write-Host "ERROR: not found"; exit 1 }
Write-Host "GhostWin: $($script:hwnd)"

[GWC]::ShowWindow($script:hwnd, 9)
Start-Sleep -Milliseconds 300
[GWC]::SetForegroundWindow($script:hwnd)
Start-Sleep -Milliseconds 500

# Focus the hidden TextBox
$focused = FocusTextBox
Write-Host "TextBox focused: $focused"

# ===== Test 0: English input =====
Write-Host ""
Write-Host "=== Test 0: English echo hello ==="

# Type via SendInput keys: e(0x45) c(0x43) h(0x48) o(0x4F) space(0x20) h(0x48) e(0x45) l(0x4C) l(0x4C) o(0x4F)
foreach ($vk in @(0x45, 0x43, 0x48, 0x4F, 0x20, 0x48, 0x45, 0x4C, 0x4C, 0x4F)) {
    [GWC]::Key([uint16]$vk)
}
Start-Sleep -Milliseconds 300
[GWC]::Key([uint16]0x0D)  # Enter
Start-Sleep -Milliseconds 2000
Screenshot "kr_final_0_english"

# ===== Test A: Korean hangul =====
Write-Host ""
Write-Host "=== Test A: Korean hangul ==="
FocusTextBox | Out-Null

# Type "echo " via VK
foreach ($vk in @(0x45, 0x43, 0x48, 0x4F, 0x20)) {
    [GWC]::Key([uint16]$vk)
}
Start-Sleep -Milliseconds 300

# Toggle Korean IME
[GWC]::Key([uint16]0x15)  # VK_HANGUL
Start-Sleep -Milliseconds 700

# han = G(0x47) K(0x4B) S(0x53)  (2벌식: ㅎ ㅏ ㄴ)
[GWC]::KeySlow([uint16]0x47)
[GWC]::KeySlow([uint16]0x4B)
[GWC]::KeySlow([uint16]0x53)
Start-Sleep -Milliseconds 500

# Mid-composition screenshot
Screenshot "kr_final_1_mid_han"

# geul = R(0x52) M(0x4D) F(0x46) (2벌식: ㄱ ㅡ ㄹ)
[GWC]::KeySlow([uint16]0x52)
[GWC]::KeySlow([uint16]0x4D)
[GWC]::KeySlow([uint16]0x46)
Start-Sleep -Milliseconds 500

# Toggle back to English
[GWC]::Key([uint16]0x15)
Start-Sleep -Milliseconds 300

# Enter
[GWC]::Key([uint16]0x0D)
Start-Sleep -Milliseconds 2000
Screenshot "kr_final_2_hangul"

# ===== Test B: Korean annyeong =====
Write-Host ""
Write-Host "=== Test B: Korean annyeong ==="
FocusTextBox | Out-Null

# Type "echo "
foreach ($vk in @(0x45, 0x43, 0x48, 0x4F, 0x20)) {
    [GWC]::Key([uint16]$vk)
}
Start-Sleep -Milliseconds 300

# Toggle Korean
[GWC]::Key([uint16]0x15)
Start-Sleep -Milliseconds 700

# an = D(0x44) K(0x4B) S(0x53) (ㅇ ㅏ ㄴ)
[GWC]::KeySlow([uint16]0x44)
[GWC]::KeySlow([uint16]0x4B)
[GWC]::KeySlow([uint16]0x53)
Start-Sleep -Milliseconds 500

# nyeong = S(0x53) U(0x55) D(0x44) (ㄴ ㅕ ㅇ)
[GWC]::KeySlow([uint16]0x53)
[GWC]::KeySlow([uint16]0x55)
[GWC]::KeySlow([uint16]0x44)
Start-Sleep -Milliseconds 500

# Toggle back
[GWC]::Key([uint16]0x15)
Start-Sleep -Milliseconds 300

# Enter
[GWC]::Key([uint16]0x0D)
Start-Sleep -Milliseconds 2000
Screenshot "kr_final_3_annyeong"

Write-Host ""
Write-Host "=== All tests complete ==="
