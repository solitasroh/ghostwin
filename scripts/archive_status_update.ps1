# Archive 5 cycles + remove noise entries from .rkit/state/pdca-status.json
# One-shot maintenance script (M-13 + family + session-restore + 3 completed cycles)

$path = '.rkit/state/pdca-status.json'
$json = Get-Content $path -Raw | ConvertFrom-Json

$now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

$archives = @{
    'dpi-scaling-integration' = 100
    'io-thread-timeout-v2'    = 100
    'vt-mutex-redesign'       = 100
    'm13-input-ux'            = 100
    'session-restore'         = 100
}

foreach ($name in $archives.Keys) {
    $existing = $json.features.PSObject.Properties[$name]
    $startedAt = $now
    $iter = 0
    if ($existing) {
        if ($existing.Value.startedAt)      { $startedAt = $existing.Value.startedAt }
        if ($existing.Value.iterationCount) { $iter      = $existing.Value.iterationCount }
        $json.features.PSObject.Properties.Remove($name)
    }
    $summary = [PSCustomObject]@{
        phase          = 'archived'
        matchRate      = $archives[$name]
        iterationCount = $iter
        startedAt      = $startedAt
        archivedAt     = $now
        archivedTo     = "docs/archive/2026-04/$name/"
    }
    $json.features | Add-Member -NotePropertyName $name -NotePropertyValue $summary
}

$noise = @('Diagnostics', 'engine-api', 'GhostWin.App', 'Input', 'scripts')
foreach ($n in $noise) {
    if ($json.features.PSObject.Properties[$n]) {
        $json.features.PSObject.Properties.Remove($n)
    }
}

$json.lastUpdated = $now
$json | ConvertTo-Json -Depth 100 | Set-Content $path -NoNewline -Encoding UTF8

Write-Host 'OK status updated. Active features now:'
($json.features | Get-Member -MemberType NoteProperty).Name | Sort-Object | ForEach-Object {
    Write-Host ('  {0,-40} phase={1}' -f $_, $json.features.$_.phase)
}
