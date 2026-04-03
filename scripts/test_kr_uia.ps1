# Korean IME Test via UIAutomation ValuePattern
# WinUI3 does not accept SendInput/SendKeys - must use UIA

Add-Type @"
using System;
using System.Runtime.InteropServices;

public class W8 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError=true)]
    public static extern uint SendInput(uint n, INPUT[] inp, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    public static void Key(ushort vk) {
        var inp = new INPUT[2];
        inp[0].type = 1; inp[0].u.ki.wVk = vk; inp[0].u.ki.dwFlags = 0;
        inp[1].type = 1; inp[1].u.ki.wVk = vk; inp[1].u.ki.dwFlags = 2;
        SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(200);
    }
}
"@

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process ghostwin_winui -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "ERROR: ghostwin not running"; exit 1 }
$hwnd = [IntPtr]$proc.MainWindowHandle
Write-Host "GhostWin PID=$($proc.Id) HWND=$hwnd"

[W8]::ShowWindow($hwnd, 9)
Start-Sleep -Milliseconds 500
[W8]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 500

$r = New-Object W8+RECT
[W8]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
Write-Host "Window: ${w}x${h}"

# Click to focus SwapChainPanel -> triggers TextBox focus
$cx = $r.Left + [int]($w/2)
$cy = $r.Top + [int]($h/2)
[W8]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 100
[W8]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 50
[W8]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500

# Find TextBox via UIA
$auto = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$editCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Edit
)
$tb = $auto.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCond)
if (-not $tb) { Write-Host "ERROR: TextBox not found"; exit 1 }
Write-Host "TextBox found via UIA"

function Shot($name) {
    [W8]::SetForegroundWindow($script:hwnd)
    Start-Sleep -Milliseconds 300
    $rr = New-Object W8+RECT
    [W8]::GetWindowRect($script:hwnd, [ref]$rr) | Out-Null
    $ww = $rr.Right - $rr.Left; $hh = $rr.Bottom - $rr.Top
    if ($ww -gt 0) {
        $bmp = New-Object System.Drawing.Bitmap($ww, $hh)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($rr.Left, $rr.Top, 0, 0, (New-Object System.Drawing.Size($ww, $hh)))
        $bmp.Save("C:\Users\Solit\Rootech\works\ghostwin\$name.png")
        $g.Dispose(); $bmp.Dispose()
        Write-Host "  Screenshot: $name.png"
    }
}

function TypeViaUIA($text) {
    # Set value in TextBox via UIA ValuePattern
    try {
        $vp = $tb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue($text)
        Start-Sleep -Milliseconds 300
        Write-Host "  UIA SetValue: $text"
        return $true
    } catch {
        Write-Host "  UIA SetValue FAILED: $_"
        return $false
    }
}

function PressEnter {
    # TextBox's PreviewKeyDown handles Enter -> sends "\r" to ConPTY
    # We need to trigger Enter on the TextBox
    # Try SendInput Enter since the TextBox has UIA focus
    $tb.SetFocus()
    Start-Sleep -Milliseconds 200
    [W8]::Key([uint16]0x0D)
    Start-Sleep -Milliseconds 500
}

# === Test 1: English input ===
Write-Host "`n=== Test 1: echo test ==="
$tb.SetFocus()
Start-Sleep -Milliseconds 200
TypeViaUIA "echo test"
PressEnter
Start-Sleep -Milliseconds 2000
Shot "kr8_1_english"

# === Test 2: Korean hangul ===
Write-Host "`n=== Test 2: echo + Korean hangul ==="
$tb.SetFocus()
Start-Sleep -Milliseconds 200
TypeViaUIA "echo "
# Now we need IME composition for Korean
# Since UIA ValuePattern bypasses IME, let's try setting Korean text directly
# This tests if the TextChanged handler sends it to ConPTY
TypeViaUIA "echo hangul-test"
PressEnter
Start-Sleep -Milliseconds 2000
Shot "kr8_2_english_cmd"

# === Test 3: Direct Korean via ValuePattern ===
Write-Host "`n=== Test 3: Direct Korean ValuePattern ==="
$tb.SetFocus()
Start-Sleep -Milliseconds 200

# Set Korean text directly - this goes through TextChanged handler
# which calls SendTextToTerminal for non-composing text
$vp = $tb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)

# Set "echo " first
$vp.SetValue("echo ")
Start-Sleep -Milliseconds 500

# Now try setting Korean chars - this bypasses IME composition
# The TextChanged handler should pick it up
$vp.SetValue([string]([char]0xD55C) + [string]([char]0xAE00))
Start-Sleep -Milliseconds 500
Write-Host "  Set Korean: hangul"

PressEnter
Start-Sleep -Milliseconds 2000
Shot "kr8_3_korean_direct"

# === Test 4: Full command with Korean ===
Write-Host "`n=== Test 4: Full echo command with Korean ==="
$tb.SetFocus()
Start-Sleep -Milliseconds 200

# Try setting the full command at once
$fullCmd = "echo " + [string]([char]0xD55C) + [string]([char]0xAE00)
$vp.SetValue($fullCmd)
Start-Sleep -Milliseconds 300
Write-Host "  Set: $fullCmd"

PressEnter
Start-Sleep -Milliseconds 2000
Shot "kr8_4_echo_hangul"

# === Test 5: echo annyeong ===
Write-Host "`n=== Test 5: echo annyeong ==="
$tb.SetFocus()
Start-Sleep -Milliseconds 200

$cmd2 = "echo " + [string]([char]0xC548) + [string]([char]0xB155)
$vp.SetValue($cmd2)
Start-Sleep -Milliseconds 300
Write-Host "  Set: $cmd2"

PressEnter
Start-Sleep -Milliseconds 2000
Shot "kr8_5_echo_annyeong"

Write-Host "`n=== All tests complete ==="
