param(
    [string]$LogLevel = "1",
    [int]$WaitBeforeSendMs = 3000,
    [int]$WaitAfterSendMs = 1500
)

# Exp-C — H7 vs H8 isolation experiment for e2e-ctrl-key-injection.
#
# Reference:
#   docs/02-design/features/e2e-ctrl-key-injection.design.md §5
#   memory: project_e2e_ctrl_key_injection_in_progress.md (Pass 2 H7 candidate)
#
# Hypothesis under test:
#   H7 — WPF normal-modifier path drops SendInput WM_KEYDOWN before PreviewKeyDown
#   H8 — window.focus() (Alt-tap + SetForegroundWindow + BringWindowToTop) is the
#        side-effect that breaks subsequent SendInput Ctrl+T chords
#
# Bypass surface (compared to e2e harness path):
#   - No pywinauto import
#   - No window.focus()        — Start-Process gives GhostWin initial foreground
#   - No _release_all_modifiers (Fix A reverted)
#   - No Python ctypes layer   (Fix B reverted)
#   - PowerShell native P/Invoke SendInput batch only
#
# Outcome interpretation:
#   - keyinput.log has TWO entries (LeftCtrl + T) → H7 falsified, H8 confirmed
#       → root cause is window.focus() or another e2e operator side effect
#   - keyinput.log has ZERO new ENTRY events → H7 strongly supported
#       → SendInput Ctrl+T is dropped before WPF PreviewKeyDown, regardless of
#         injection layer. Next step: HwndSource.AddHook raw WM trace.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\diag_exp_c_raw_sendinput.ps1
#
# Prerequisites:
#   - Debug build present at src\GhostWin.App\bin\Debug\net10.0-windows\
#     (run: dotnet build src\GhostWin.App\GhostWin.App.csproj -c Debug)
#   - User must NOT touch keyboard/mouse during the wait window — any input
#     would steal foreground from GhostWin and contaminate the test.

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
# KeyDiag.cs has had [Conditional("DEBUG")] removed; the env-var gate alone
# enables logging in Release builds, so we use the existing x64 Release exe.
$exe = "$root\src\GhostWin.App\bin\x64\Release\net10.0-windows\GhostWin.App.exe"
$logDir = "$env:LocalAppData\GhostWin\diagnostics"
$logFile = "$logDir\keyinput.log"

if (-not (Test-Path $exe)) {
    Write-Error "Release x64 build not found: $exe"
    Write-Error "Run: powershell -ExecutionPolicy Bypass -File scripts\build_ghostwin.ps1 -Config Release"
    exit 2
}

# Reset log file (and ensure dir exists)
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
if (Test-Path $logFile) {
    Remove-Item $logFile -Force
    Write-Host "Reset existing log: $logFile" -ForegroundColor Yellow
}

# Compile P/Invoke surface — single Add-Type call, cached for reruns
if (-not ("ExpC.Native" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace ExpC
{
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    public static class Native
    {
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const ushort VK_CONTROL = 0x11;
        public const ushort VK_T = 0x54;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public static uint SendCtrlT()
        {
            INPUT[] events = new INPUT[4];

            // Ctrl down
            events[0].type = INPUT_KEYBOARD;
            events[0].u.ki.wVk = VK_CONTROL;
            events[0].u.ki.wScan = 0;
            events[0].u.ki.dwFlags = 0;
            events[0].u.ki.time = 0;
            events[0].u.ki.dwExtraInfo = IntPtr.Zero;

            // T down
            events[1].type = INPUT_KEYBOARD;
            events[1].u.ki.wVk = VK_T;
            events[1].u.ki.wScan = 0;
            events[1].u.ki.dwFlags = 0;
            events[1].u.ki.time = 0;
            events[1].u.ki.dwExtraInfo = IntPtr.Zero;

            // T up
            events[2].type = INPUT_KEYBOARD;
            events[2].u.ki.wVk = VK_T;
            events[2].u.ki.wScan = 0;
            events[2].u.ki.dwFlags = KEYEVENTF_KEYUP;
            events[2].u.ki.time = 0;
            events[2].u.ki.dwExtraInfo = IntPtr.Zero;

            // Ctrl up
            events[3].type = INPUT_KEYBOARD;
            events[3].u.ki.wVk = VK_CONTROL;
            events[3].u.ki.wScan = 0;
            events[3].u.ki.dwFlags = KEYEVENTF_KEYUP;
            events[3].u.ki.time = 0;
            events[3].u.ki.dwExtraInfo = IntPtr.Zero;

            return SendInput((uint)events.Length, events, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
"@
}

$env:GHOSTWIN_KEYDIAG = $LogLevel
Write-Host ""
Write-Host "===== Exp-C: raw PowerShell SendInput Ctrl+T =====" -ForegroundColor Cyan
Write-Host "GHOSTWIN_KEYDIAG = $LogLevel" -ForegroundColor Cyan
Write-Host "Log path:          $logFile" -ForegroundColor Cyan
Write-Host "Exe:               $exe" -ForegroundColor Cyan
Write-Host ""
Write-Host "Step 1/4: Launching GhostWin (Start-Process gives it initial foreground)..." -ForegroundColor Yellow

$proc = Start-Process -FilePath $exe -PassThru
Write-Host "         Process started: PID $($proc.Id)" -ForegroundColor Green
Write-Host ""

Write-Host "Step 2/4: Waiting $WaitBeforeSendMs ms for GhostWin to finish init..." -ForegroundColor Yellow
Write-Host "         (DO NOT touch keyboard or mouse — any input steals foreground)" -ForegroundColor Red
Start-Sleep -Milliseconds $WaitBeforeSendMs
Write-Host ""

# Verify foreground window belongs to GhostWin process before injecting
$fg = [ExpC.Native]::GetForegroundWindow()
$fgPid = 0
[void][ExpC.Native]::GetWindowThreadProcessId($fg, [ref]$fgPid)
if ($fgPid -ne $proc.Id) {
    Write-Host "WARNING: foreground window is PID $fgPid, expected GhostWin PID $($proc.Id)" -ForegroundColor Red
    Write-Host "         Test will likely contaminate — SendInput will go to wrong process" -ForegroundColor Red
} else {
    Write-Host "Foreground confirmed: PID $fgPid (GhostWin)" -ForegroundColor Green
}
Write-Host ""

Write-Host "Step 3/4: Injecting Ctrl+T (raw SendInput batch, 4 events)..." -ForegroundColor Yellow
$sent = [ExpC.Native]::SendCtrlT()
Write-Host "         SendInput returned: $sent / 4 events" -ForegroundColor Green
Write-Host ""

Write-Host "Step 4/4: Waiting $WaitAfterSendMs ms for WPF to dispatch, then stopping GhostWin..." -ForegroundColor Yellow
Start-Sleep -Milliseconds $WaitAfterSendMs

try {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
} catch {
    Write-Host "         Stop-Process failed: $_" -ForegroundColor Red
}
Start-Sleep -Milliseconds 300
Write-Host ""

# Dump log
Write-Host "===== Log dump =====" -ForegroundColor Cyan
if (Test-Path $logFile) {
    $lines = Get-Content $logFile
    $count = ($lines | Measure-Object -Line).Lines
    Write-Host "Total entries: $count" -ForegroundColor Green
    Write-Host "--- begin keyinput.log ---" -ForegroundColor Yellow
    $lines | ForEach-Object { Write-Host $_ }
    Write-Host "--- end keyinput.log ---" -ForegroundColor Yellow
    Write-Host ""

    # Verdict
    $entries = $lines | Where-Object { $_ -match "evt=ENTRY" }
    $entryCount = ($entries | Measure-Object).Count
    $hasCtrl = $false
    $hasT = $false
    foreach ($line in $entries) {
        if ($line -match "key=LeftCtrl") { $hasCtrl = $true }
        if ($line -match "key=T\b") { $hasT = $true }
    }

    Write-Host "===== Verdict =====" -ForegroundColor Cyan
    Write-Host "ENTRY events:    $entryCount" -ForegroundColor Cyan
    Write-Host "LeftCtrl seen:   $hasCtrl" -ForegroundColor Cyan
    Write-Host "T (key) seen:    $hasT" -ForegroundColor Cyan
    Write-Host ""

    if ($hasCtrl -and $hasT) {
        Write-Host "  H7 FALSIFIED — raw SendInput Ctrl+T DOES reach PreviewKeyDown" -ForegroundColor Green
        Write-Host "  H8 CONFIRMED (or partial) — window.focus() / pywinauto / Fix A is the contaminator" -ForegroundColor Green
        Write-Host "  Next: redesign focus() to remove the side-effect path" -ForegroundColor Green
    } elseif ($entryCount -eq 0) {
        Write-Host "  H7 STRONGLY SUPPORTED — zero ENTRY events for raw SendInput" -ForegroundColor Red
        Write-Host "  WPF normal-modifier path is dropping SendInput WM_KEYDOWN entirely" -ForegroundColor Red
        Write-Host "  Next: HwndSource.AddHook raw WM trace to confirm where it's dropped" -ForegroundColor Red
    } else {
        Write-Host "  PARTIAL — entries exist but Ctrl/T missing. Inspect log manually." -ForegroundColor Yellow
        Write-Host "  Likely focus race or unrelated key event noise." -ForegroundColor Yellow
    }
} else {
    Write-Host "LOG FILE NOT CREATED — keyinput.log absent" -ForegroundColor Red
    Write-Host "(KeyDiag never wrote anything — env var or directory issue)" -ForegroundColor Red
}

Write-Host ""
Write-Host "Done." -ForegroundColor Cyan
