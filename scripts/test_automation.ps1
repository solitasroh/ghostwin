#Requires -Version 5.1
<#
.SYNOPSIS
    Runs GhostWin automation suites with separated Daily and Interactive reports.

.DESCRIPTION
    Daily uses the new Test-Control based suite and enables real app execution.
    Interactive runs legacy foreground-dependent tests only when requested.
    Measurement delegates render baselines to measure_render_baseline.ps1 but
    stores artifacts under the same automation result root.

.EXAMPLE
    scripts/test_automation.ps1
    scripts/test_automation.ps1 -Suite Interactive
    scripts/test_automation.ps1 -Suite Measurement -MeasurementScenario idle -DurationSec 10 -NoBuild
    scripts/test_automation.ps1 -Suite All -NoBuild
#>
[CmdletBinding()]
param(
    [ValidateSet('Daily', 'Interactive', 'Measurement', 'All')]
    [string]$Suite = 'Daily',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('idle', 'load', 'resize', 'resize-4pane')]
    [string]$MeasurementScenario = 'idle',

    [int]$DurationSec = 60,

    [string]$PresentMonPath = '',

    [switch]$ResetSession,

    [switch]$NoBuild,

    [string]$ResultsRoot = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

if ([string]::IsNullOrWhiteSpace($ResultsRoot)) {
    $ResultsRoot = Join-Path $repoRoot 'artifacts\test-automation'
}

$timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$runRoot = Join-Path $ResultsRoot $timestamp
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

function Invoke-DotNetTest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Project,

        [Parameter(Mandatory = $true)]
        [string]$Filter,

        [Parameter(Mandatory = $true)]
        [string]$SuiteName,

        [Parameter(Mandatory = $true)]
        [string]$LogFileName,

        [hashtable]$Environment = @{}
    )

    $resultDir = Join-Path $runRoot $SuiteName
    New-Item -ItemType Directory -Force -Path $resultDir | Out-Null

    $previousValues = @{}
    foreach ($key in $Environment.Keys) {
        $previousValues[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key], 'Process')
    }

    try {
        $testArgs = @(
            'test',
            $Project,
            '-c', $Configuration,
            '--filter', $Filter,
            '--logger', "trx;LogFileName=$LogFileName",
            '--results-directory', $resultDir
        )
        if ($NoBuild) {
            $testArgs += '--no-build'
        }

        Write-Host "[$SuiteName] dotnet $($testArgs -join ' ')" -ForegroundColor Cyan
        & dotnet @testArgs
        if ($LASTEXITCODE -ne 0) {
            throw "$SuiteName suite failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        foreach ($key in $Environment.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previousValues[$key], 'Process')
        }
    }
}

function Invoke-MeasurementBaseline {
    $baselineScript = Join-Path $repoRoot 'scripts\measure_render_baseline.ps1'
    if (-not (Test-Path -LiteralPath $baselineScript)) {
        throw "Measurement baseline script not found: $baselineScript"
    }

    $measurementRoot = Join-Path $runRoot 'measurement'
    $scenarioOutput = Join-Path $measurementRoot $MeasurementScenario
    New-Item -ItemType Directory -Force -Path $scenarioOutput | Out-Null

    $baselineScenario = $MeasurementScenario
    $panes = 1
    if ($MeasurementScenario -eq 'resize-4pane') {
        $baselineScenario = 'resize'
        $panes = 4
    }

    $baselineArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $baselineScript,
        '-Scenario', $baselineScenario,
        '-DurationSec', "$DurationSec",
        '-Configuration', $Configuration,
        '-OutputDir', $scenarioOutput,
        '-Panes', "$panes"
    )
    if (-not $NoBuild) {
        $baselineArgs += '-Build'
    }
    if ($ResetSession) {
        $baselineArgs += '-ResetSession'
    }
    if (-not [string]::IsNullOrWhiteSpace($PresentMonPath)) {
        $baselineArgs += @('-PresentMonPath', $PresentMonPath)
    }

    Write-Host "[measurement] powershell.exe $($baselineArgs -join ' ')" -ForegroundColor Cyan
    & powershell.exe @baselineArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Measurement suite failed with exit code $LASTEXITCODE"
    }
}

$dailyProject = Join-Path $repoRoot 'tests\GhostWin.Automation.Tests\GhostWin.Automation.Tests.csproj'
$interactiveProject = Join-Path $repoRoot 'tests\GhostWin.E2E.Tests\GhostWin.E2E.Tests.csproj'

if ($Suite -in @('Daily', 'All')) {
    Invoke-DotNetTest `
        -Project $dailyProject `
        -Filter 'Category=DailyE2E' `
        -SuiteName 'daily' `
        -LogFileName 'daily.trx' `
        -Environment @{ GHOSTWIN_AUTOMATION_RUN_REAL_APP = '1' }
}

if ($Suite -in @('Interactive', 'All')) {
    Invoke-DotNetTest `
        -Project $interactiveProject `
        -Filter 'Category=Interactive' `
        -SuiteName 'interactive' `
        -LogFileName 'interactive.trx' `
        -Environment @{ GHOSTWIN_INTERACTIVE_AUTOMATION = '1' }
}

if ($Suite -in @('Measurement', 'All')) {
    Invoke-MeasurementBaseline
}

Write-Host "Automation results: $runRoot" -ForegroundColor Green
