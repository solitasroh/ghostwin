param([string]$Level = "3")

# R4 diagnosis Step 3-2 helper.
# Runs e2e MQ-6 (Ctrl+T SendInput) with KeyDiag and dumps the log.

$root = Split-Path $PSScriptRoot -Parent
$logFile = "$env:LocalAppData\GhostWin\diagnostics\keyinput.log"
$pythonExe = "$root\scripts\e2e\venv\Scripts\python.exe"
$runnerPy = "$root\scripts\e2e\runner.py"

# Reset previous log
Remove-Item $logFile -Force -ErrorAction SilentlyContinue

# Set diag env
$env:GHOSTWIN_KEYDIAG = $Level

Write-Host "GHOSTWIN_KEYDIAG=$Level" -ForegroundColor Cyan
Write-Host "Log path:    $logFile" -ForegroundColor Cyan
Write-Host ""

$pyArgs = @(
    $runnerPy,
    "--scenario", "MQ-6",
    "--run-id", "diag_ps_mq6",
    "--verbose"
)

& $pythonExe $pyArgs
$exitCode = $LASTEXITCODE

Write-Host ""
Write-Host "=== runner exit: $exitCode ===" -ForegroundColor Cyan
Write-Host ""

if (Test-Path $logFile) {
    $lineCount = (Get-Content $logFile | Measure-Object -Line).Lines
    Write-Host "LOG CAPTURED: $lineCount lines" -ForegroundColor Green
    Write-Host "--- log content ---" -ForegroundColor Yellow
    Get-Content $logFile
    Write-Host "--- end log ---" -ForegroundColor Yellow
} else {
    Write-Host "LOG NOT CREATED — keyinput.log absent" -ForegroundColor Red
    Write-Host "(this means SendInput Ctrl+T did NOT reach PreviewKeyDown — H2 strongly confirmed)" -ForegroundColor Red
}

exit $exitCode
