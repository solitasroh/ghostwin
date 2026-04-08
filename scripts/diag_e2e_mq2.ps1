param([string]$Level = "3")

# R4 diagnosis Step 3 helper for e2e-ctrl-key-injection.
# Runs e2e harness MQ-2 (Alt+V) with KeyDiag instrumentation enabled,
# then prints the captured log file (or its absence).

$root = Split-Path $PSScriptRoot -Parent
$logFile = "$env:LocalAppData\GhostWin\diagnostics\keyinput.log"
$pythonExe = "$root\scripts\e2e\venv\Scripts\python.exe"
$runnerPy = "$root\scripts\e2e\runner.py"

# Reset previous log
Remove-Item $logFile -Force -ErrorAction SilentlyContinue

# Set diag env (PowerShell native — inherits to Python child + GhostWin grandchild)
$env:GHOSTWIN_KEYDIAG = $Level

Write-Host "GHOSTWIN_KEYDIAG=$Level" -ForegroundColor Cyan
Write-Host "Log path:    $logFile" -ForegroundColor Cyan
Write-Host "Using default exe path (bin/x64/Release/...)" -ForegroundColor Cyan
Write-Host ""

# Build args as array to avoid PowerShell line wrap parsing issues
# No --exe: rely on app_lifecycle.EXE_PATH default which points to
# bin/x64/Release/net10.0-windows/GhostWin.App.exe (matches new build)
$pyArgs = @(
    $runnerPy,
    "--scenario", "MQ-2",
    "--run-id", "diag_ps_mq2",
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
    Write-Host "(this means GhostWin process never wrote any KeyDiag entry)" -ForegroundColor Red
}

exit $exitCode
