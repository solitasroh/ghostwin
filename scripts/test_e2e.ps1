#Requires -Version 5.1
<#
.SYNOPSIS
    GhostWin E2E test harness orchestrator.

.DESCRIPTION
    Bootstraps a Python virtual environment under scripts/e2e/venv,
    installs pinned dependencies (SHA256 hash gated), then dispatches
    to scripts/e2e/runner.py.

    See docs/02-design/features/e2e-test-harness.design.md §7.

.PARAMETER Scenario
    Single scenario id, e.g. "MQ-1".

.PARAMETER All
    Run every scenario in the registry.

.PARAMETER Bootstrap
    Initialize venv + install requirements only. Skip runner invocation.
    Useful for first-time setup or CI prewarm.

.PARAMETER RunId
    Override the run id (defaults to yyyyMMdd_HHmmss).

.PARAMETER Rebuild
    Delete the existing venv before re-creating.

.EXAMPLE
    scripts/test_e2e.ps1 -Bootstrap

.EXAMPLE
    scripts/test_e2e.ps1 -Scenario MQ-1

.EXAMPLE
    scripts/test_e2e.ps1 -All -Rebuild
#>
param(
    [string]$Scenario,
    [switch]$All,
    [switch]$Bootstrap,
    [string]$RunId = (Get-Date -Format "yyyyMMdd_HHmmss"),
    [switch]$Rebuild
)

$ErrorActionPreference = "Stop"
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$E2ERoot    = Join-Path $RepoRoot "scripts\e2e"
$VenvDir    = Join-Path $E2ERoot  "venv"
$VenvPython = Join-Path $VenvDir  "Scripts\python.exe"
$Reqs       = Join-Path $E2ERoot  "requirements.txt"
$ReqsHash   = Join-Path $VenvDir  ".requirements.sha256"

# Korean Windows: force UTF-8 + English CLI output
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8       = '1'

function Initialize-Venv {
    if ($Rebuild -and (Test-Path $VenvDir)) {
        Write-Host "[e2e] removing existing venv (rebuild)" -ForegroundColor Yellow
        Remove-Item -Recurse -Force $VenvDir
    }
    if (-not (Test-Path $VenvPython)) {
        Write-Host "[e2e] creating venv at $VenvDir" -ForegroundColor Cyan
        & python -m venv $VenvDir
        if ($LASTEXITCODE -ne 0) {
            throw "venv creation failed - is Python 3.12+ on PATH? See .claude/rules/behavior.md (no workaround)"
        }
    }
    if (-not (Test-Path $Reqs)) {
        throw "requirements.txt not found at $Reqs"
    }
    # SHA256 via .NET API: Get-FileHash is broken on some PS5.1 installs
    # where Microsoft.PowerShell.Utility module fails to auto-load.
    # The .NET path works on every supported PowerShell host.
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Reqs)
        try {
            $hashBytes   = $sha.ComputeHash($stream)
            $currentHash = -join ($hashBytes | ForEach-Object { $_.ToString("x2") })
        }
        finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
    $cachedHash = if (Test-Path $ReqsHash) { (Get-Content $ReqsHash -Raw).Trim() } else { "" }
    if ($currentHash -ne $cachedHash) {
        Write-Host "[e2e] installing requirements (hash changed or first run)" -ForegroundColor Cyan
        & $VenvPython -m pip install --upgrade pip
        if ($LASTEXITCODE -ne 0) { throw "pip upgrade failed" }
        & $VenvPython -m pip install -r $Reqs
        if ($LASTEXITCODE -ne 0) { throw "pip install failed" }
        Set-Content -Path $ReqsHash -Value $currentHash -NoNewline
        Write-Host "[e2e] requirements installed and hash cached" -ForegroundColor Green
    }
    else {
        Write-Host "[e2e] requirements unchanged - skipping pip install" -ForegroundColor DarkGray
    }
}

function Invoke-Runner {
    $runnerPy = Join-Path $E2ERoot "runner.py"
    if (-not (Test-Path $runnerPy)) {
        throw "runner.py not found at $runnerPy - implement Step 5 of design before -Scenario/-All invocation"
    }
    $runnerArgs = @("--run-id", $RunId)
    if ($All) {
        $runnerArgs += "--all"
    }
    elseif ($Scenario) {
        $runnerArgs += @("--scenario", $Scenario)
    }
    else {
        throw "specify -Scenario MQ-N, -All, or -Bootstrap"
    }
    Write-Host "[e2e] runner $($runnerArgs -join ' ')" -ForegroundColor Green
    & $VenvPython $runnerPy @runnerArgs
    $exit = $LASTEXITCODE

    if ($exit -eq 0) {
        $artifactDir = Join-Path $E2ERoot "artifacts\$RunId"
        Write-Host "[e2e] run complete - artifacts in $artifactDir" -ForegroundColor Green
        Write-Host "Next step: in Claude Code, invoke Evaluator Task with:" -ForegroundColor Cyan
        Write-Host "  prompt: $E2ERoot\evaluator_prompt.md" -ForegroundColor Cyan
        Write-Host "  artifact dir: $artifactDir" -ForegroundColor Cyan
    }
    exit $exit
}

Initialize-Venv

if ($Bootstrap) {
    Write-Host "[e2e] bootstrap-only mode complete" -ForegroundColor Green
    exit 0
}

Invoke-Runner
