$root = 'C:\Users\Solit\Rootech\works\ghostwin'

Write-Host "=== build/ directory ==="
if (Test-Path "$root\build") {
    Get-ChildItem -Recurse "$root\build" -Filter '*.dll' -ErrorAction SilentlyContinue |
        Select-Object FullName, LastWriteTime, Length |
        Format-Table -AutoSize
}

Write-Host ""
Write-Host "=== Searching ENTIRE C:\Users\Solit\Rootech for GhostWin.Engine.dll ==="
Get-ChildItem -Recurse 'C:\Users\Solit\Rootech' -Filter 'GhostWin.Engine.dll' -ErrorAction SilentlyContinue |
    Select-Object FullName, LastWriteTime |
    Format-Table -AutoSize

Write-Host ""
Write-Host "=== Last build log (last 30 lines) ==="
$buildLog = "$root\build\obj\GhostWin.Engine\Debug\GhostWin.Engine.log"
if (Test-Path $buildLog) {
    Write-Host "FILE: $buildLog"
    Get-Content $buildLog -Tail 30
}
