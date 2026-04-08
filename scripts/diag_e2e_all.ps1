param(
    [string]$LogLevel = "1",
    [string]$RunId = "diag_all_h9_fix"
)

# Exp-D2a regression — full e2e --all run with KeyDiag enabled.
#
# Purpose:
#   After H9 confirmation (Pass 5) and Exp-D2a fix (focus() Alt-tap removed),
#   run every MQ scenario via the same code path as scripts/test_e2e.ps1 -All
#   while keeping GHOSTWIN_KEYDIAG=1 so we can verify Ctrl-key chord entries
#   in keyinput.log alongside Operator success counts.
#
# Acceptance (Plan §8 G2/G3 partial):
#   - Operator OK count == total (8/8)
#   - keyinput.log contains LeftCtrl/T/W chord entries (no longer LeftAlt-only)
#
# Reference:
#   docs/01-plan/features/e2e-ctrl-key-injection.plan.md §8 Acceptance
#   scripts/diag_e2e_mq6.ps1 (single-scenario sibling)

$ErrorActionPreference = "Stop"

$root      = Split-Path $PSScriptRoot -Parent
$logDir    = "$env:LocalAppData\GhostWin\diagnostics"
$logFile   = "$logDir\keyinput.log"
$harness   = "$root\scripts\test_e2e.ps1"
$artifacts = "$root\scripts\e2e\artifacts\$RunId"

# Reset previous KeyDiag log
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
if (Test-Path $logFile) {
    Remove-Item $logFile -Force
    Write-Host "Reset existing log: $logFile" -ForegroundColor Yellow
}

$env:GHOSTWIN_KEYDIAG = $LogLevel
Write-Host ""
Write-Host "===== Exp-D2a regression: e2e --all with KeyDiag =====" -ForegroundColor Cyan
Write-Host "GHOSTWIN_KEYDIAG = $LogLevel" -ForegroundColor Cyan
Write-Host "Log path:          $logFile" -ForegroundColor Cyan
Write-Host "Harness:           $harness" -ForegroundColor Cyan
Write-Host "Run ID:            $RunId" -ForegroundColor Cyan
Write-Host ""

& $harness -All -RunId $RunId
$exit = $LASTEXITCODE

Write-Host ""
Write-Host "===== runner exit: $exit =====" -ForegroundColor Cyan
Write-Host ""

# Operator outcome summary
$summaryPath = "$artifacts\summary.json"
if (Test-Path $summaryPath) {
    Write-Host "Summary: $summaryPath" -ForegroundColor Green
    Get-Content $summaryPath | Out-Host
} else {
    Write-Host "WARNING: summary.json not found at $summaryPath" -ForegroundColor Red
}
Write-Host ""

# KeyDiag log dump
Write-Host "===== keyinput.log =====" -ForegroundColor Cyan
if (Test-Path $logFile) {
    $lines = Get-Content $logFile
    $count = ($lines | Measure-Object -Line).Lines
    Write-Host "Total entries: $count" -ForegroundColor Green

    $entries = $lines | Where-Object { $_ -match "evt=ENTRY" }
    $ctrlEntries  = $entries | Where-Object { $_ -match "key=LeftCtrl|key=RightCtrl" }
    $tEntries     = $entries | Where-Object { $_ -match "key=T\b" }
    $wEntries     = $entries | Where-Object { $_ -match "key=W\b" }
    $altEntries   = $entries | Where-Object { $_ -match "key=System.*syskey=LeftAlt" }
    $altVEntries  = $entries | Where-Object { $_ -match "syskey=V\b" }
    $altHEntries  = $entries | Where-Object { $_ -match "syskey=H\b" }

    Write-Host ""
    Write-Host "Chord coverage:" -ForegroundColor Cyan
    Write-Host ("  LeftCtrl/RightCtrl entries:  {0}" -f $ctrlEntries.Count)
    Write-Host ("  T entries (Ctrl+T):          {0}" -f $tEntries.Count)
    Write-Host ("  W entries (Ctrl+W / +Shift): {0}" -f $wEntries.Count)
    Write-Host ("  Alt+V (syskey=V):            {0}" -f $altVEntries.Count)
    Write-Host ("  Alt+H (syskey=H):            {0}" -f $altHEntries.Count)
    Write-Host ("  Stale Alt-only (key=System): {0}" -f $altEntries.Count)
    Write-Host ""

    Write-Host "--- begin keyinput.log ---" -ForegroundColor Yellow
    $lines | ForEach-Object { Write-Host $_ }
    Write-Host "--- end keyinput.log ---" -ForegroundColor Yellow
    Write-Host ""

    if ($ctrlEntries.Count -gt 0 -and $tEntries.Count -gt 0) {
        Write-Host "VERDICT: H9 fix effective — Ctrl chord entries present in e2e harness path" -ForegroundColor Green
    } else {
        Write-Host "VERDICT: H9 fix did NOT propagate — no Ctrl chord entries observed" -ForegroundColor Red
    }
} else {
    Write-Host "LOG NOT CREATED — keyinput.log absent" -ForegroundColor Red
}

exit $exit
