$root = 'C:\Users\Solit\Rootech\works\ghostwin'
Write-Host "=== Searching for GhostWin.Engine.dll under $root ==="
Get-ChildItem -Recurse -LiteralPath $root -Filter 'GhostWin.Engine.dll' -ErrorAction SilentlyContinue |
    Select-Object FullName, LastWriteTime, @{N='SizeKB';E={[math]::Round($_.Length/1024,1)}} |
    Format-Table -AutoSize

Write-Host ""
Write-Host "=== App bin contents ==="
$appBin = Join-Path $root 'src\GhostWin.App\bin\Debug\net10.0-windows10.0.22621.0\win-x64'
if (Test-Path $appBin) {
    Get-ChildItem $appBin -Filter 'GhostWin.*' | Select-Object Name, LastWriteTime, Length | Format-Table -AutoSize
} else {
    Write-Host "App bin path not found: $appBin"
}
