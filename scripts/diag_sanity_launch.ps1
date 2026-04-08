param([int]$Seconds = 4)

$root = Split-Path $PSScriptRoot -Parent
$exe = "$root\src\GhostWin.App\bin\Debug\net10.0-windows\GhostWin.App.exe"

if (-not (Test-Path $exe)) {
    Write-Error "Debug build not found at $exe"
    exit 2
}

# Ensure KeyDiag is OFF for sanity check
Remove-Item Env:GHOSTWIN_KEYDIAG -ErrorAction SilentlyContinue

Write-Host "Launching $exe (no KeyDiag)..."
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds $Seconds

if ($proc.HasExited) {
    Write-Host "CRASHED exit=$($proc.ExitCode)" -ForegroundColor Red
    exit 1
}

Write-Host "ALIVE pid=$($proc.Id) — terminating cleanly" -ForegroundColor Green
Stop-Process -Id $proc.Id -Force
Start-Sleep -Milliseconds 500
Write-Host "OK"
exit 0
