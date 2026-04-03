#Requires -Version 5.1
<#
.SYNOPSIS
    한글 IME 자동화 테스트 (SendInput 기반)
.DESCRIPTION
    SendInput API로 실제 키보드 입력을 시뮬레이션하여
    WinUI3 TextBox IME 조합을 테스트합니다.
#>
param(
    [string]$ExePath = "",
    [int]$WaitMs = 500
)

$ErrorActionPreference = 'Stop'
$ProjectDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (-not $ExePath) {
    $ExePath = Join-Path $ProjectDir 'build\ghostwin_winui.exe'
}
if (-not (Test-Path $ExePath)) {
    Write-Error "Not found: $ExePath"
    exit 1
}

# SendInput P/Invoke
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT {
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct INPUT {
    public uint type;
    public KEYBDINPUT ki;
    // padding for union alignment
    public long padding1;
}

public static class InputSender {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;
    public const ushort VK_HANGUL = 0x15;
    public const ushort VK_SPACE = 0x20;
    public const ushort VK_RETURN = 0x0D;

    public static void PressKey(ushort vk) {
        INPUT[] inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = vk;
        inputs[0].ki.dwFlags = 0;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = vk;
        inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void TypeString(string keys) {
        foreach (char c in keys) {
            ushort vk = (ushort)char.ToUpper(c);
            PressKey(vk);
            System.Threading.Thread.Sleep(50);
        }
    }

    public static void ToggleHangul() {
        PressKey(VK_HANGUL);
    }
}
'@ -Language CSharp

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

function Take-Screenshot([string]$Name) {
    $outPath = Join-Path $ProjectDir "kr_auto_$Name.png"
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return $outPath
}

Write-Host "=== Korean IME Automation Test (SendInput) ===" -ForegroundColor Cyan

# Launch app
$proc = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 3
if ($proc.HasExited) { Write-Error "App exited"; exit 1 }
Write-Host "PID: $($proc.Id)"

# Get window handle and set foreground
Start-Sleep -Milliseconds 500
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) {
    # Try finding by process
    Start-Sleep -Seconds 2
    $proc.Refresh()
    $hwnd = $proc.MainWindowHandle
}
[InputSender]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500

# Click center of window to ensure focus on TextBox
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MouseClick {
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint cButtons, UIntPtr dwExtraInfo);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    public static void ClickWindow(IntPtr hwnd) {
        RECT r;
        GetWindowRect(hwnd, out r);
        int cx = (r.L + r.R) / 2;
        int cy = (r.T + r.B) / 2;
        SetCursorPos(cx, cy);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // LBUTTONDOWN
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // LBUTTONUP
    }
}
'@ -Language CSharp

[MouseClick]::ClickWindow($hwnd)
Start-Sleep -Milliseconds 500

# ═══ Test 1: English ═══
Write-Host "`n[Test 1] English: echo hello" -ForegroundColor Yellow
[InputSender]::TypeString("echo hello")
Start-Sleep -Milliseconds 200
[InputSender]::PressKey([InputSender]::VK_RETURN)
Start-Sleep -Milliseconds $WaitMs
$s1 = Take-Screenshot "1_english"
Write-Host "  Screenshot: $s1"

# ═══ Test 2: Toggle to Korean IME ═══
Write-Host "[Test 2] Toggle Korean IME" -ForegroundColor Yellow
[InputSender]::ToggleHangul()
Start-Sleep -Milliseconds $WaitMs
$s2 = Take-Screenshot "2_hangul_on"
Write-Host "  Screenshot: $s2"

# ═══ Test 3: Korean syllable "한" (ㅎ=g, ㅏ=k, ㄴ=s in 2벌식) ═══
Write-Host "[Test 3] Korean: 한 (g+k+s)" -ForegroundColor Yellow
[InputSender]::TypeString("gks")
Start-Sleep -Milliseconds $WaitMs
$s3 = Take-Screenshot "3_han"
Write-Host "  Screenshot: $s3"

# ═══ Test 4: Continue to "한글" (ㄱ=r, ㅡ=m, ㄹ=f) ═══
Write-Host "[Test 4] Korean: 글 (r+m+f)" -ForegroundColor Yellow
[InputSender]::TypeString("rmf")
Start-Sleep -Milliseconds $WaitMs
$s4 = Take-Screenshot "4_hangeul"
Write-Host "  Screenshot: $s4"

# ═══ Test 5: Space to confirm + Enter ═══
Write-Host "[Test 5] Confirm + Enter" -ForegroundColor Yellow
[InputSender]::PressKey([InputSender]::VK_SPACE)
Start-Sleep -Milliseconds 200
[InputSender]::PressKey([InputSender]::VK_RETURN)
Start-Sleep -Milliseconds $WaitMs
$s5 = Take-Screenshot "5_confirmed"
Write-Host "  Screenshot: $s5"

# ═══ Test 6: Multi-syllable "안녕하세요" ═══
Write-Host "[Test 6] Korean: 안녕하세요" -ForegroundColor Yellow
# 안=dk+s, 녕=s+u+d, 하=g+k, 세=t+p, 요=y+h
[InputSender]::TypeString("dkssud")  # 안녕
Start-Sleep -Milliseconds $WaitMs
$s6a = Take-Screenshot "6a_annyeong"
Write-Host "  Screenshot: $s6a"

[InputSender]::TypeString("gktpyh")  # 하세요
Start-Sleep -Milliseconds $WaitMs
$s6b = Take-Screenshot "6b_annyeonghaseyo"
Write-Host "  Screenshot: $s6b"

[InputSender]::PressKey([InputSender]::VK_RETURN)
Start-Sleep -Milliseconds $WaitMs
$s6c = Take-Screenshot "6c_final"
Write-Host "  Screenshot: $s6c"

# ═══ Restore English ═══
[InputSender]::ToggleHangul()
Start-Sleep -Milliseconds 300

# ═══ Summary ═══
Write-Host "`n=== Test Complete ===" -ForegroundColor Cyan
Write-Host "Screenshots: kr_auto_*.png" -ForegroundColor DarkGray
Write-Host "Check kr_auto_4_hangeul.png for '한글' composition" -ForegroundColor White
Write-Host "Check kr_auto_6c_final.png for '안녕하세요' output" -ForegroundColor White

Stop-Process $proc -Force -ErrorAction SilentlyContinue
