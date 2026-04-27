$dll = 'C:\Users\Solit\Rootech\works\ghostwin\build\Debug\ghostwin_engine.dll'
$bytes = [System.IO.File]::ReadAllBytes($dll)
$asciiText = [System.Text.Encoding]::ASCII.GetString($bytes)
Write-Host "=== Searching for diagnostic strings in $dll ==="
Write-Host ""
$patterns = @('IME-OVERLAY', 'HandleCompositionUpdate', 'ime-diag', 'GHOSTWIN_LOG_FILE')
foreach ($p in $patterns) {
    $hit = $asciiText.IndexOf($p)
    if ($hit -ge 0) {
        Write-Host "FOUND: '$p' at offset $hit"
    } else {
        Write-Host "MISSING: '$p'"
    }
}
