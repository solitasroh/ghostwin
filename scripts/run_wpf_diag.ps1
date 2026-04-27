$root = Split-Path $PSScriptRoot -Parent
$exe = "$root\src\GhostWin.App\bin\x64\Release\net10.0-windows\GhostWin.App.exe"
if (-not (Test-Path $exe)) {
    $exe = "$root\src\GhostWin.App\bin\Release\net10.0-windows\GhostWin.App.exe"
}
if (-not (Test-Path $exe)) {
    Write-Error "Build first: scripts\build_wpf.ps1"
    exit 1
}

$env:GHOSTWIN_RESIZE_DIAG = "1"
$env:GHOSTWIN_RENDERDIAG = "3"
Write-Host "=== Diagnostic mode ON (GHOSTWIN_RESIZE_DIAG=1, GHOSTWIN_RENDERDIAG=3) ==="
Write-Host "Log file: $root\ghostwin_debug.log"
Write-Host ""
Write-Host "Test procedure:"
Write-Host "  1. Wait for PowerShell prompt"
Write-Host "  2. Type 'echo hello' + Enter"
Write-Host "  3. Press Alt+V (vertical split)"
Write-Host "  4. Check if first pane content disappears"
Write-Host "  5. Close the app"
Write-Host ""

# Clear previous log
$logPath = "$root\ghostwin_debug.log"
if (Test-Path $logPath) { Remove-Item $logPath }

& $exe
Write-Host ""
Write-Host "=== App closed. Log saved to: $logPath ==="
if (Test-Path $logPath) {
    Write-Host "Log lines: $((Get-Content $logPath).Count)"
}
