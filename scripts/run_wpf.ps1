$root = Split-Path $PSScriptRoot -Parent
$exe = "$root\src\GhostWin.App\bin\x64\Release\net10.0-windows\GhostWin.App.exe"
if (-not (Test-Path $exe)) {
    $exe = "$root\src\GhostWin.App\bin\Release\net10.0-windows\GhostWin.App.exe"
}
if (-not (Test-Path $exe)) {
    Write-Error "Build first: msbuild GhostWin.sln /p:Configuration=Release /p:Platform=x64 /m"
    exit 1
}
& $exe
