param([string]$LogLevel = "3")

# Step 2 / Step 3 helper for e2e-ctrl-key-injection diagnosis.
#
# Sets GHOSTWIN_KEYDIAG=$LogLevel and launches the Debug build of GhostWin.App
# so KeyDiag emits ENTRY/BRANCH/EXIT events to:
#   %LocalAppData%\GhostWin\diagnostics\keyinput.log
#
# Levels:
#   1 = ENTRY only
#   2 = ENTRY + EXIT
#   3 = ENTRY + BRANCH + EXIT  (default — full traversal trace)

$root = Split-Path $PSScriptRoot -Parent
$exe = "$root\src\GhostWin.App\bin\Debug\net10.0-windows\GhostWin.App.exe"

if (-not (Test-Path $exe)) {
    Write-Error "Debug build not found: $exe"
    Write-Error "Run: dotnet build src\GhostWin.App\GhostWin.App.csproj -c Debug"
    exit 2
}

$logDir = "$env:LocalAppData\GhostWin\diagnostics"
$logFile = "$logDir\keyinput.log"

# Reset log file (and ensure dir exists)
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
if (Test-Path $logFile) {
    Remove-Item $logFile -Force
    Write-Host "Reset existing log: $logFile" -ForegroundColor Yellow
}

$env:GHOSTWIN_KEYDIAG = $LogLevel
Write-Host "GHOSTWIN_KEYDIAG=$LogLevel" -ForegroundColor Cyan
Write-Host "Log path: $logFile" -ForegroundColor Cyan
Write-Host "Launching: $exe" -ForegroundColor Cyan
Write-Host ""
Write-Host "App will run in foreground. Close it normally when done." -ForegroundColor Yellow
Write-Host ""

& $exe
$exitCode = $LASTEXITCODE

Write-Host ""
Write-Host "App exited with code $exitCode" -ForegroundColor Cyan

if (Test-Path $logFile) {
    $lineCount = (Get-Content $logFile | Measure-Object -Line).Lines
    Write-Host "Log captured: $lineCount lines" -ForegroundColor Green
} else {
    Write-Host "WARNING: log file not created" -ForegroundColor Red
}

exit $exitCode
