#Requires -Version 5.1
<#
.SYNOPSIS
    Runs GhostWin render measurement scenarios repeatedly and aggregates results.

.DESCRIPTION
    This script is a thin repeat harness over measure_render_baseline.ps1.
    Each run keeps the original per-run artifacts, while this wrapper writes
    repeat-summary.csv, repeat-summary.json, and summary.txt at the repeat root.

.EXAMPLE
    .\scripts\measure_render_repeats.ps1 -Scenario pane-split-churn -RepeatCount 3 -DurationSec 8 -Configuration Release -ResetSession -Build
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('idle', 'load', 'resize', 'resize-4pane', 'pane-split-churn', 'workspace-switch-churn')]
    [string]$Scenario,

    [ValidateRange(1, 20)]
    [int]$RepeatCount = 3,

    [int]$DurationSec = 60,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$PresentMonPath,

    [string]$OutputDir,

    [switch]$Build,

    [switch]$ResetSession
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$baselineScript = Join-Path $repoRoot 'scripts\measure_render_baseline.ps1'

if (-not (Test-Path -LiteralPath $baselineScript)) {
    throw "Measurement baseline script not found: $baselineScript"
}

if (-not $OutputDir) {
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $OutputDir = Join-Path $repoRoot "artifacts\test-automation\$stamp\measurement-repeat\$Scenario"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path
Write-Host "[repeat] output -> $OutputDir"

function Get-SummaryValue {
    param(
        [string[]]$Lines,
        [string]$Key
    )

    $pattern = '^\s*' + [Regex]::Escape($Key) + '\s*:\s*(?<value>.+?)\s*$'
    foreach ($line in $Lines) {
        if ($line -match $pattern) {
            return $Matches.value
        }
    }
    return $null
}

function Convert-ToNullableBool {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    if ($Value -match '^(?i:true)$') { return $true }
    if ($Value -match '^(?i:false)$') { return $false }
    return $null
}

function Convert-ToNullableInt {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $parsed = 0
    if ([int]::TryParse($Value.Trim(), [ref]$parsed)) {
        return $parsed
    }
    return $null
}

function Read-RunSummary {
    param(
        [int]$RunIndex,
        [string]$RunDir
    )

    $summaryPath = Join-Path $RunDir 'summary.txt'
    if (-not (Test-Path -LiteralPath $summaryPath)) {
        throw "summary.txt not found for run ${RunIndex}: $summaryPath"
    }

    $lines = Get-Content -LiteralPath $summaryPath
    $metrics = @{}
    foreach ($line in $lines) {
        if ($line -match '^(?<metric>[a-z_]+)\s+(?<avg>[\d\.]+)\s+(?<p95>[\d\.]+)\s+(?<max>[\d\.]+)\s*$') {
            $metrics[$Matches.metric] = [pscustomobject]@{
                Avg = [double]$Matches.avg
                P95 = [double]$Matches.p95
                Max = [double]$Matches.max
            }
        }
    }

    $total = $metrics['total_us']
    $build = $metrics['build_us']
    $draw = $metrics['draw_us']
    $present = $metrics['present_us']

    return [pscustomobject]@{
        Run = $RunIndex
        Directory = (Split-Path -Leaf $RunDir)
        Scenario = Get-SummaryValue -Lines $lines -Key 'scenario'
        Configuration = Get-SummaryValue -Lines $lines -Key 'configuration'
        SampleCount = Convert-ToNullableInt (Get-SummaryValue -Lines $lines -Key 'sample_count')
        DriverValid = Convert-ToNullableBool (Get-SummaryValue -Lines $lines -Key 'driver_valid')
        ObservedPanes = Convert-ToNullableInt (Get-SummaryValue -Lines $lines -Key 'observed_panes')
        ObservedActions = Convert-ToNullableInt (Get-SummaryValue -Lines $lines -Key 'observed_actions')
        VisualValid = Convert-ToNullableBool (Get-SummaryValue -Lines $lines -Key 'visual_valid')
        VisualTextValid = Convert-ToNullableBool (Get-SummaryValue -Lines $lines -Key 'visual_text_valid')
        VisualActiveBorderComplete = Convert-ToNullableBool (Get-SummaryValue -Lines $lines -Key 'visual_active_border_complete')
        TotalP95Us = if ($total) { $total.P95 } else { $null }
        TotalMaxUs = if ($total) { $total.Max } else { $null }
        BuildP95Us = if ($build) { $build.P95 } else { $null }
        DrawP95Us = if ($draw) { $draw.P95 } else { $null }
        PresentP95Us = if ($present) { $present.P95 } else { $null }
    }
}

function Get-DoubleStats {
    param(
        [object[]]$Runs,
        [string]$PropertyName
    )

    $values = @($Runs |
        ForEach-Object { $_.$PropertyName } |
        Where-Object { $null -ne $_ } |
        ForEach-Object { [double]$_ })

    if ($values.Count -eq 0) {
        return [pscustomobject]@{ Avg = $null; Max = $null }
    }

    $avg = ($values | Measure-Object -Average).Average
    $max = ($values | Measure-Object -Maximum).Maximum
    return [pscustomobject]@{
        Avg = [Math]::Round($avg, 1)
        Max = [Math]::Round($max, 1)
    }
}

$baselineScenario = $Scenario
$panes = 1
if ($Scenario -eq 'resize-4pane') {
    $baselineScenario = 'resize'
    $panes = 4
}

$runs = @()
for ($i = 1; $i -le $RepeatCount; $i++) {
    $runName = 'run-{0:D2}' -f $i
    $runDir = Join-Path $OutputDir $runName
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null

    $args = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $baselineScript,
        '-Scenario', $baselineScenario,
        '-DurationSec', "$DurationSec",
        '-Configuration', $Configuration,
        '-OutputDir', $runDir,
        '-Panes', "$panes"
    )

    if ($Build -and $i -eq 1) {
        $args += '-Build'
    }
    if ($ResetSession) {
        $args += '-ResetSession'
    }
    if (-not [string]::IsNullOrWhiteSpace($PresentMonPath)) {
        $args += @('-PresentMonPath', $PresentMonPath)
    }

    Write-Host "[repeat] run $i/$RepeatCount -> $runName"
    & powershell.exe @args
    if ($LASTEXITCODE -ne 0) {
        throw "Measurement repeat run $i failed with exit code $LASTEXITCODE"
    }

    $runs += Read-RunSummary -RunIndex $i -RunDir $runDir
}

$csvPath = Join-Path $OutputDir 'repeat-summary.csv'
$jsonPath = Join-Path $OutputDir 'repeat-summary.json'
$summaryPath = Join-Path $OutputDir 'summary.txt'

$runs | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8

$driverFailures = @($runs | Where-Object { $_.DriverValid -eq $false })
$visualRuns = @($runs | Where-Object { $null -ne $_.VisualValid })
$visualFailures = @($visualRuns | Where-Object {
    $_.VisualValid -eq $false -or
    $_.VisualTextValid -eq $false -or
    $_.VisualActiveBorderComplete -eq $false
})

$totalP95 = Get-DoubleStats -Runs $runs -PropertyName 'TotalP95Us'
$buildP95 = Get-DoubleStats -Runs $runs -PropertyName 'BuildP95Us'
$drawP95 = Get-DoubleStats -Runs $runs -PropertyName 'DrawP95Us'
$presentP95 = Get-DoubleStats -Runs $runs -PropertyName 'PresentP95Us'

$aggregate = [pscustomobject]@{
    Scenario = $Scenario
    BaselineScenario = $baselineScenario
    Configuration = $Configuration
    RepeatCount = $RepeatCount
    DurationSec = $DurationSec
    DriverValid = $driverFailures.Count -eq 0
    VisualValid = if ($visualRuns.Count -gt 0) { $visualFailures.Count -eq 0 } else { $null }
    FailureCount = $driverFailures.Count + $visualFailures.Count
    TotalP95Us = $totalP95
    BuildP95Us = $buildP95
    DrawP95Us = $drawP95
    PresentP95Us = $presentP95
    Runs = $runs
}

$aggregate | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = @()
$lines += "scenario:       $Scenario"
$lines += "configuration:  $Configuration"
$lines += "repeat_count:   $RepeatCount"
$lines += "duration_sec:   $DurationSec"
$lines += "driver_valid:   $($aggregate.DriverValid)"
if ($null -ne $aggregate.VisualValid) {
    $lines += "visual_valid:   $($aggregate.VisualValid)"
}
$lines += "failure_count:  $($aggregate.FailureCount)"
$lines += ''
$lines += ('{0,-14} {1,10} {2,10}' -f 'metric', 'avg_p95', 'max_p95')
$lines += ('-' * 38)
$lines += ('{0,-14} {1,10} {2,10}' -f 'total_us', $aggregate.TotalP95Us.Avg, $aggregate.TotalP95Us.Max)
$lines += ('{0,-14} {1,10} {2,10}' -f 'build_us', $aggregate.BuildP95Us.Avg, $aggregate.BuildP95Us.Max)
$lines += ('{0,-14} {1,10} {2,10}' -f 'draw_us', $aggregate.DrawP95Us.Avg, $aggregate.DrawP95Us.Max)
$lines += ('{0,-14} {1,10} {2,10}' -f 'present_us', $aggregate.PresentP95Us.Avg, $aggregate.PresentP95Us.Max)
$lines += ''
$lines += "artifacts:      repeat-summary.csv, repeat-summary.json"
$lines | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host ''
$lines | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host "[repeat] summary -> $summaryPath"

if ($aggregate.FailureCount -gt 0) {
    throw "Measurement repeat gate failed with $($aggregate.FailureCount) failed run(s)."
}
