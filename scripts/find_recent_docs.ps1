$paths = @(
    'C:\Users\Solit\Rootech\works\ghostwin\docs',
    'C:\Users\Solit\obsidian\note\Projects\GhostWin'
)
$cutoff = (Get-Date).AddHours(-2)

foreach ($p in $paths) {
    if (-not (Test-Path $p)) {
        Write-Host "MISSING: $p"
        continue
    }
    Write-Host "=== $p ==="
    Get-ChildItem -Recurse $p -Filter '*.md' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $cutoff } |
        Sort-Object LastWriteTime -Descending |
        Select-Object @{N='Modified';E={$_.LastWriteTime.ToString('HH:mm:ss')}}, Length, FullName |
        Format-Table -AutoSize
    Write-Host ""
}
