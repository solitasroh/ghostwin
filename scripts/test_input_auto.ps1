#Requires -Version 5.1
<#
.SYNOPSIS
    GhostWin Input Automation Test
.DESCRIPTION
    Launches ghostwin_winui.exe, sends keystrokes via UIAutomation,
    and captures screenshots for verification.
#>
param(
    [string]$ExePath = "",
    [int]$TimeoutSec = 15
)

$ErrorActionPreference = 'Stop'
$ProjectDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (-not $ExePath) {
    $ExePath = Join-Path $ProjectDir 'build\ghostwin_winui.exe'
}

if (-not (Test-Path $ExePath)) {
    Write-Error "Executable not found: $ExePath"
    exit 1
}

# Load UIAutomation
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# P/Invoke for keybd_event
$pinvoke = @'
using System;
using System.Runtime.InteropServices;
public class NativeKeys {
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    public const byte VK_HANGUL = 0x15;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    public static void PressKey(byte vk) {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void ToggleHangul() {
        PressKey(VK_HANGUL);
    }
}
'@
Add-Type -TypeDefinition $pinvoke -Language CSharp

Write-Host "=== GhostWin Input Automation Test ===" -ForegroundColor Cyan
Write-Host "Exe: $ExePath"

function Take-Screenshot([string]$Name) {
    $outPath = Join-Path $ProjectDir "test_auto_$Name.png"
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Write-Host "  Screenshot: $outPath" -ForegroundColor DarkGray
    return $outPath
}

# 1. Launch app
Write-Host "`n[1/5] Launching app..." -ForegroundColor Yellow
$proc = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 3

if ($proc.HasExited) {
    Write-Error "App exited immediately with code $($proc.ExitCode)"
    exit 1
}
Write-Host "  PID: $($proc.Id)" -ForegroundColor Green

# 2. Find window
Write-Host "[2/5] Finding main window..." -ForegroundColor Yellow
$automation = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)

$mainWindow = $null
for ($i = 0; $i -lt 10; $i++) {
    $mainWindow = $automation.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $condition)
    if ($mainWindow) { break }
    Start-Sleep -Milliseconds 500
}

if (-not $mainWindow) {
    Write-Error "Could not find main window"
    Stop-Process $proc -Force
    exit 1
}
Write-Host "  Window: $($mainWindow.Current.Name)" -ForegroundColor Green

# 3. Focus
Write-Host "[3/5] Setting focus..." -ForegroundColor Yellow
try { $mainWindow.SetFocus() } catch { Write-Warning "SetFocus: $_" }
Start-Sleep -Milliseconds 500

# 4. Tests
Write-Host "[4/5] Running input tests..." -ForegroundColor Yellow

# Test A: English
Write-Host "  Test A: English input" -ForegroundColor White
[System.Windows.Forms.SendKeys]::SendWait("echo hello")
Start-Sleep -Milliseconds 300
Take-Screenshot "A_english" | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 500
Take-Screenshot "A_english_result" | Out-Null

# Test B: Korean via clipboard
Write-Host "  Test B: Korean paste" -ForegroundColor White
[System.Windows.Forms.Clipboard]::SetText("echo han-geul")
Start-Sleep -Milliseconds 200
[System.Windows.Forms.SendKeys]::SendWait("^v")
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 500
Take-Screenshot "B_korean_paste" | Out-Null

# Test C: Korean IME direct typing
Write-Host "  Test C: Korean IME typing" -ForegroundColor White
[NativeKeys]::ToggleHangul()
Start-Sleep -Milliseconds 500
Take-Screenshot "C0_after_hangul_toggle" | Out-Null

# Type: g(ㅎ) k(ㅏ) s(ㄴ) = 한
[NativeKeys]::PressKey(0x47)  # g = ㅎ
Start-Sleep -Milliseconds 300
Take-Screenshot "C1_composing_h" | Out-Null

[NativeKeys]::PressKey(0x4B)  # k = ㅏ
Start-Sleep -Milliseconds 300
Take-Screenshot "C2_composing_ha" | Out-Null

[NativeKeys]::PressKey(0x53)  # s = ㄴ
Start-Sleep -Milliseconds 300
Take-Screenshot "C3_composing_han" | Out-Null

[NativeKeys]::PressKey(0x20)  # Space (confirm)
Start-Sleep -Milliseconds 300
Take-Screenshot "C4_confirmed" | Out-Null

# Restore English
[NativeKeys]::ToggleHangul()
Start-Sleep -Milliseconds 300

[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 500
Take-Screenshot "C5_final" | Out-Null

# 5. Summary
Write-Host "`n[5/5] Test complete" -ForegroundColor Yellow
Write-Host "Screenshots saved to project root (test_auto_*.png)" -ForegroundColor Cyan
Write-Host "Review screenshots to verify input correctness." -ForegroundColor Cyan

# Cleanup
Stop-Process $proc -Force -ErrorAction SilentlyContinue
Write-Host "Done." -ForegroundColor Green
