Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;

public class KoreanIMETest {
    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left, Top, Right, Bottom;
    }

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_HANGUL = 0x15;
    public const byte VK_RETURN = 0x0D;
    public const byte VK_SPACE = 0x20;

    public static IntPtr FindGhostWinWindow() {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            if (!IsWindowVisible(hWnd)) return true;
            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (title.Contains("GhostWin") || title.Contains("ghostwin") || title.Contains("Ghostwin")) {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        Thread.Sleep(100);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(50);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(100);
    }

    public static void PressKey(byte vk) {
        keybd_event(vk, 0, 0, IntPtr.Zero);
        Thread.Sleep(30);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        Thread.Sleep(200);
    }

    public static void TypeChar(char c) {
        byte vk = (byte)Char.ToUpper(c);
        bool shift = Char.IsUpper(c);
        if (shift) keybd_event(0x10, 0, 0, IntPtr.Zero);
        keybd_event(vk, 0, 0, IntPtr.Zero);
        Thread.Sleep(30);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        if (shift) keybd_event(0x10, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        Thread.Sleep(100);
    }

    public static void ListWindows() {
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            if (!IsWindowVisible(hWnd)) return true;
            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            Console.WriteLine("  " + sb.ToString());
            return true;
        }, IntPtr.Zero);
    }
}
"@

# Find window
$hwnd = [KoreanIMETest]::FindGhostWinWindow()
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Host "ERROR: GhostWin window not found"
    Write-Host "Visible windows:"
    [KoreanIMETest]::ListWindows()
    exit 1
}

Write-Host "Found GhostWin window: $hwnd"

# Activate window
[KoreanIMETest]::ShowWindow($hwnd, 9)
Start-Sleep -Milliseconds 200
[KoreanIMETest]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 500

# Get window rect
$rect = New-Object KoreanIMETest+RECT
[KoreanIMETest]::GetWindowRect($hwnd, [ref]$rect)
Write-Host "Window rect: L=$($rect.Left) T=$($rect.Top) R=$($rect.Right) B=$($rect.Bottom)"

# Click on terminal area (center of window)
$cx = [int](($rect.Left + $rect.Right) / 2)
$cy = [int](($rect.Top + $rect.Bottom) / 2)
Write-Host "Clicking at ($cx, $cy)"
[KoreanIMETest]::Click($cx, $cy)
Start-Sleep -Milliseconds 500

# Activate Korean keyboard layout
$hkl = [KoreanIMETest]::LoadKeyboardLayout("00000412", 1)
Write-Host "Korean layout loaded: $hkl"
[KoreanIMETest]::ActivateKeyboardLayout($hkl, 0)
Start-Sleep -Milliseconds 300

Write-Host ""
Write-Host "=== Test A: echo hangul ==="

# Type "echo " in English
foreach ($c in "echo ".ToCharArray()) {
    if ($c -eq ' ') {
        [KoreanIMETest]::PressKey([KoreanIMETest]::VK_SPACE)
    } else {
        [KoreanIMETest]::TypeChar($c)
    }
}
Start-Sleep -Milliseconds 300

# Toggle to Korean IME
[KoreanIMETest]::PressKey([KoreanIMETest]::VK_HANGUL)
Start-Sleep -Milliseconds 500

# Type han-geul (Korean 2-set):
# han = H(G) + A(K) + N(S)
# geul = G(R) + EU(M) + L(F)

$keys_han = @(0x47, 0x4B, 0x53)  # G K S
foreach ($vk in $keys_han) {
    [KoreanIMETest]::PressKey($vk)
}
Start-Sleep -Milliseconds 300

$keys_geul = @(0x52, 0x4D, 0x46)  # R M F
foreach ($vk in $keys_geul) {
    [KoreanIMETest]::PressKey($vk)
}
Start-Sleep -Milliseconds 500

# Toggle back to English
[KoreanIMETest]::PressKey([KoreanIMETest]::VK_HANGUL)
Start-Sleep -Milliseconds 300

# Press Enter
[KoreanIMETest]::PressKey([KoreanIMETest]::VK_RETURN)
Start-Sleep -Milliseconds 2000

# Screenshot after Test A
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$bitmap = New-Object System.Drawing.Bitmap($rect.Right - $rect.Left + 40, $rect.Bottom - $rect.Top + 40)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($rect.Left - 20, $rect.Top - 20, 0, 0, $bitmap.Size)
$bitmap.Save("C:\Users\Solit\Rootech\works\ghostwin\test_korean_A.png")
$graphics.Dispose()
$bitmap.Dispose()
Write-Host "Screenshot saved: test_korean_A.png"

Start-Sleep -Milliseconds 1000

Write-Host ""
Write-Host "=== Test B: echo annyeong ==="

# Type "echo "
foreach ($c in "echo ".ToCharArray()) {
    if ($c -eq ' ') {
        [KoreanIMETest]::PressKey([KoreanIMETest]::VK_SPACE)
    } else {
        [KoreanIMETest]::TypeChar($c)
    }
}
Start-Sleep -Milliseconds 300

# Toggle to Korean IME
[KoreanIMETest]::PressKey([KoreanIMETest]::VK_HANGUL)
Start-Sleep -Milliseconds 500

# an = O(D) + A(K) + N(S)
$keys_an = @(0x44, 0x4B, 0x53)  # D K S
foreach ($vk in $keys_an) {
    [KoreanIMETest]::PressKey($vk)
}
Start-Sleep -Milliseconds 300

# nyeong = N(S) + YEO(U) + NG(D)
$keys_nyeong = @(0x53, 0x55, 0x44)  # S U D
foreach ($vk in $keys_nyeong) {
    [KoreanIMETest]::PressKey($vk)
}
Start-Sleep -Milliseconds 500

# Toggle back to English
[KoreanIMETest]::PressKey([KoreanIMETest]::VK_HANGUL)
Start-Sleep -Milliseconds 300

# Press Enter
[KoreanIMETest]::PressKey([KoreanIMETest]::VK_RETURN)
Start-Sleep -Milliseconds 2000

# Screenshot after Test B
$bitmap2 = New-Object System.Drawing.Bitmap($rect.Right - $rect.Left + 40, $rect.Bottom - $rect.Top + 40)
$graphics2 = [System.Drawing.Graphics]::FromImage($bitmap2)
$graphics2.CopyFromScreen($rect.Left - 20, $rect.Top - 20, 0, 0, $bitmap2.Size)
$bitmap2.Save("C:\Users\Solit\Rootech\works\ghostwin\test_korean_B.png")
$graphics2.Dispose()
$bitmap2.Dispose()
Write-Host "Screenshot saved: test_korean_B.png"

Write-Host ""
Write-Host "=== Tests Complete ==="
