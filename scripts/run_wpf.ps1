$root = Split-Path $PSScriptRoot -Parent
$exe = "$root\src\GhostWin.App\bin\x64\Release\net10.0-windows\GhostWin.App.exe"
if (-not (Test-Path $exe)) {
    $exe = "$root\src\GhostWin.App\bin\Release\net10.0-windows\GhostWin.App.exe"
}
if (-not (Test-Path $exe)) {
    Write-Error "Build first: scripts\build_wpf.ps1"
    exit 1
}
& $exe
