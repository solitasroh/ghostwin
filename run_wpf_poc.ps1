$exe = "$PSScriptRoot\wpf-poc\bin\x64\Release\net10.0-windows\GhostWinPoC.exe"
if (-not (Test-Path $exe)) {
    Write-Error "Build first: scripts\build_wpf_poc.ps1"
    exit 1
}
& $exe
