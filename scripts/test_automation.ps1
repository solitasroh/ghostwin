#Requires -Version 5.1
<#
.SYNOPSIS
    Runs GhostWin automation suites with separated Daily and Interactive reports.

.DESCRIPTION
    Daily uses the new Test-Control based suite and enables real app execution.
    Interactive runs foreground-dependent tests from the same automation test project.
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
    [ValidateSet('Daily', 'Interactive', 'Measurement', 'Native', 'All')]
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
$solutionBuilt = $false
$nativeEngineTests = @(
    'vt_core_test',
    'vt_bridge_cell_test',
    'conpty_integration_test',
    'dx11_render_test',
    'render_state_test',
    'surface_manager_state_test',
    'session_manager_thread_safety_test',
    'session_visual_state_test',
    'tsf_init_test',
    'quad_korean_test'
)

function Find-MSBuild {
    $pathCommand = Get-Command 'msbuild' -ErrorAction SilentlyContinue
    if ($pathCommand) {
        return $pathCommand.Source
    }

    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        return $null
    }

    $vsPath = & $vswhere -latest -prerelease `
        -requires Microsoft.Component.MSBuild `
        -property installationPath 2>$null | Select-Object -First 1
    if (-not $vsPath) {
        $vsPath = & $vswhere -latest `
            -requires Microsoft.Component.MSBuild `
            -property installationPath 2>$null | Select-Object -First 1
    }
    if (-not $vsPath) {
        return $null
    }

    $candidate = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path -LiteralPath $candidate) {
        return $candidate
    }

    return $null
}

function Invoke-SolutionBuild {
    if ($NoBuild -or $script:solutionBuilt) {
        return
    }

    $msbuild = Find-MSBuild
    if (-not $msbuild) {
        throw 'MSBuild not found. Install Visual Studio with Microsoft.Component.MSBuild or run from a Developer PowerShell.'
    }

    Write-Host "[build] msbuild -> $msbuild" -ForegroundColor Cyan
    Write-Host "[build] build GhostWin.sln ($Configuration)" -ForegroundColor Cyan
    & $msbuild (Join-Path $repoRoot 'GhostWin.sln') `
        "/p:Configuration=$Configuration" `
        '/p:Platform=x64' `
        '/nologo' `
        '/verbosity:minimal'
    if ($LASTEXITCODE -ne 0) {
        throw "solution build failed with exit code $LASTEXITCODE"
    }

    $script:solutionBuilt = $true
}

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
    if (-not $NoBuild -and -not $script:solutionBuilt) {
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

function Invoke-NativeEngineTests {
    $msbuild = Find-MSBuild
    if (-not $msbuild) {
        throw 'MSBuild not found. Install Visual Studio with Microsoft.Component.MSBuild or run from a Developer PowerShell.'
    }

    $nativeRoot = Join-Path $runRoot 'native'
    New-Item -ItemType Directory -Force -Path $nativeRoot | Out-Null
    $project = Join-Path $repoRoot 'tests\GhostWin.Engine.Tests\GhostWin.Engine.Tests.vcxproj'
    $outDir = Join-Path $repoRoot "build\tests\$Configuration"

    foreach ($testName in $nativeEngineTests) {
        $logFile = Join-Path $nativeRoot "$testName.log"
        $errFile = Join-Path $nativeRoot "$testName.stderr.log"

        if (-not $NoBuild) {
            Write-Host "[native] build $testName" -ForegroundColor Cyan
            & $msbuild $project `
                "/p:GhostWinTestName=$testName" `
                "/p:Configuration=$Configuration" `
                '/p:Platform=x64' `
                '/nologo' `
                '/verbosity:minimal'
            if ($LASTEXITCODE -ne 0) {
                throw "Native test build failed: $testName (exit $LASTEXITCODE)"
            }
        }

        $exe = Join-Path $outDir "$testName.exe"
        if (-not (Test-Path -LiteralPath $exe)) {
            throw "Native test executable not found: $exe"
        }

        Write-Host "[native] run $testName" -ForegroundColor Cyan
        $proc = Start-Process -FilePath $exe `
            -RedirectStandardOutput $logFile `
            -RedirectStandardError $errFile `
            -Wait -PassThru -WindowStyle Hidden
        if ($proc.ExitCode -ne 0) {
            Get-Content -LiteralPath $logFile -ErrorAction SilentlyContinue | Write-Host
            Get-Content -LiteralPath $errFile -ErrorAction SilentlyContinue | Write-Host
            throw "Native test failed: $testName (exit $($proc.ExitCode))"
        }
    }
}

$dailyProject = Join-Path $repoRoot 'tests\GhostWin.Automation.Tests\GhostWin.Automation.Tests.csproj'
$interactiveProject = Join-Path $repoRoot 'tests\GhostWin.Automation.Tests\GhostWin.Automation.Tests.csproj'

if ($Suite -in @('Daily', 'Interactive', 'All')) {
    Invoke-SolutionBuild
}

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
        -Environment @{
            GHOSTWIN_AUTOMATION_RUN_REAL_APP = '1'
            GHOSTWIN_INTERACTIVE_AUTOMATION = '1'
        }
}

if ($Suite -in @('Measurement', 'All')) {
    Invoke-MeasurementBaseline
}

if ($Suite -in @('Native', 'All')) {
    Invoke-NativeEngineTests
}

Write-Host "Automation results: $runRoot" -ForegroundColor Green
