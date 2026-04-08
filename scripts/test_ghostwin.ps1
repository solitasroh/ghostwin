#Requires -Version 5.1
<#
.SYNOPSIS
    GhostWin xUnit test runner (pure .NET, no C++ build required).

.DESCRIPTION
    Runs `dotnet test` for tests/GhostWin.Core.Tests. Does NOT invoke vcvarsall.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER PassThru
    Additional arguments forwarded to `dotnet test`.

.EXAMPLE
    scripts/test_ghostwin.ps1
    scripts/test_ghostwin.ps1 -Configuration Release
    scripts/test_ghostwin.ps1 -- --filter "FullyQualifiedName~RemoveLeaf"
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [Parameter(ValueFromRemainingArguments)]
    [string[]]$PassThru = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Korean Windows: force UTF-8 + English CLI to avoid CP949 mojibake
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$testProject = Join-Path $root 'tests\GhostWin.Core.Tests\GhostWin.Core.Tests.csproj'

if (-not (Test-Path $testProject)) {
    Write-Error "Test project not found: $testProject"
    exit 1
}

Write-Host '[1/2] Restoring packages...' -ForegroundColor Cyan
& dotnet restore $testProject
if ($LASTEXITCODE -ne 0) {
    Write-Error 'dotnet restore failed'
    exit 1
}

Write-Host "[2/2] Running tests (Configuration=$Configuration)..." -ForegroundColor Cyan
$testArgs = @($testProject, '-c', $Configuration, '--no-restore') + $PassThru
& dotnet test @testArgs

$exitCode = $LASTEXITCODE
if ($exitCode -eq 0) {
    Write-Host 'ALL TESTS PASSED' -ForegroundColor Green
} else {
    Write-Host "Tests FAILED (exit code: $exitCode)" -ForegroundColor Red
}

exit $exitCode
