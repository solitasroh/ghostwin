$path = 'C:\Users\Solit\Rootech\works\ghostwin\.rkit\state\pdca-status.json'
$status = Get-Content $path -Raw | ConvertFrom-Json
$feat = $status.features.'m13-input-ux'
if ($feat) {
    Write-Host "=== m13-input-ux PDCA status ==="
    $feat | ConvertTo-Json -Depth 6
} else {
    Write-Host "m13-input-ux not found in pdca-status.json"
    Write-Host "All features:"
    $status.features.PSObject.Properties.Name | ForEach-Object { Write-Host "  - $_" }
}
