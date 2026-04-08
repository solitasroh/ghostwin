#Requires -Version 5.1
<#
.SYNOPSIS
    GhostWin E2E test harness orchestrator.

.DESCRIPTION
    Bootstraps a Python virtual environment under scripts/e2e/venv,
    installs pinned dependencies (SHA256 hash gated), then dispatches
    to scripts/e2e/runner.py.

    The Evaluator side (D19/D20 separation) is wired via -Evaluate /
    -EvaluateOnly / -Apply switches per e2e-evaluator-automation.design.md
    §2.2 D6-D9.

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

.PARAMETER Evaluate
    After running the operator, prepare an Evaluator handoff — writes
    evaluator_invocation.txt to the run's artifact directory and prints
    a copy-paste block for the user to invoke the e2e-evaluator subagent
    in Claude Code.

.PARAMETER EvaluateOnly
    Skip the operator and only prepare the Evaluator handoff for an
    existing run. Requires -RunId to be set explicitly (or uses the
    latest run by LastWriteTime as a fallback).

.PARAMETER Apply
    Validate evaluator_summary.json written back by the subagent and
    print the final verdict. Sets exit code: 0 PASS, 1 FAIL, 2 UNCLEAR.
    Requires -RunId to be set explicitly (never implicit).

.EXAMPLE
    scripts/test_e2e.ps1 -Bootstrap

.EXAMPLE
    scripts/test_e2e.ps1 -Scenario MQ-1

.EXAMPLE
    scripts/test_e2e.ps1 -All -Rebuild

.EXAMPLE
    scripts/test_e2e.ps1 -All -Evaluate

.EXAMPLE
    scripts/test_e2e.ps1 -EvaluateOnly -RunId diag_all_h9_fix

.EXAMPLE
    scripts/test_e2e.ps1 -Apply -RunId diag_all_h9_fix
#>
param(
    [string]$Scenario,
    [switch]$All,
    [switch]$Bootstrap,
    [string]$RunId = (Get-Date -Format "yyyyMMdd_HHmmss"),
    [switch]$Rebuild,
    [switch]$Evaluate,
    [switch]$EvaluateOnly,
    [switch]$Apply
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
    }
    return $exit
}

function Resolve-LatestRunId {
    # Only used when -EvaluateOnly is set without -RunId. Sorts artifacts by
    # LastWriteTime (non-timestamp run ids like "diag_all_h9_fix" break
    # lexicographic ordering).
    $artifactsRoot = Join-Path $E2ERoot "artifacts"
    if (-not (Test-Path $artifactsRoot)) {
        throw "no artifacts directory at $artifactsRoot — run with -All first"
    }
    $latest = Get-ChildItem -Path $artifactsRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "summary.json") } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $latest) {
        throw "no runs with summary.json in $artifactsRoot — run with -All first"
    }
    Write-Host "[e2e] resolved latest run: $($latest.Name)" -ForegroundColor Yellow
    return $latest.Name
}

function Invoke-EvaluatorWrapper {
    param([string]$ResolvedRunId)

    $runDir = Join-Path $E2ERoot "artifacts\$ResolvedRunId"
    $summaryPath = Join-Path $runDir "summary.json"
    if (-not (Test-Path $summaryPath)) {
        throw "summary.json not found at $summaryPath"
    }

    # Parse operator summary — minimum contract (design §3.4)
    $summary = Get-Content $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $summary.PSObject.Properties.Name -contains 'run_id') {
        throw "summary.json missing required 'run_id' field"
    }
    if ($summary.run_id -ne $ResolvedRunId) {
        Write-Host "[e2e] WARNING: summary.run_id=$($summary.run_id) differs from dir name=$ResolvedRunId" -ForegroundColor Yellow
    }

    $promptPath = Join-Path $E2ERoot "evaluator_prompt.md"
    $invocationPath = Join-Path $runDir "evaluator_invocation.txt"

    # Build invocation block — user pastes into Claude Code Task tool
    $block = @"
=== Evaluator handoff — run_id=$ResolvedRunId ===
Operator: OK=$($summary.operator_ok) ERROR=$($summary.operator_error) SKIPPED=$($summary.skipped)
Artifacts: $runDir

Step 1 — in Claude Code, invoke the Task tool with:

  subagent_type: e2e-evaluator
  description: "E2E evaluate run $ResolvedRunId"
  prompt: |
    Read $promptPath for your full operating manual.
    Then evaluate the run at:
      $runDir
    Write the result to:
      $runDir\evaluator_summary.json
    Per the schema in evaluator_prompt.md §9.

Step 2 — after the subagent finishes, run:
  scripts/test_e2e.ps1 -Apply -RunId $ResolvedRunId

The full invocation text is also saved to:
  $invocationPath
"@

    # Write invocation file (wrapper-owned, not operator)
    Set-Content -Path $invocationPath -Value $block -NoNewline
    Write-Host ""
    Write-Host $block -ForegroundColor Cyan
    Write-Host ""
    Write-Host "[e2e] evaluator handoff prepared" -ForegroundColor Green
}

function Test-EvaluatorSummaryShape {
    param([Parameter(Mandatory=$true)] $Summary)

    # Minimum required top-level fields (D11 schema)
    $required = @('run_id','evaluator_id','prompt_version','evaluated_at',
                  'results','total','passed','failed','unclear','match_rate','verdict')
    foreach ($key in $required) {
        if (-not ($Summary.PSObject.Properties.Name -contains $key)) {
            throw "evaluator_summary.json missing required field: $key"
        }
    }
    if ($Summary.verdict -notin @('PASS','FAIL','UNCLEAR')) {
        throw "invalid verdict: $($Summary.verdict)"
    }
    $total = [int]$Summary.total
    $passed = [int]$Summary.passed
    $failed = [int]$Summary.failed
    $unclear = [int]$Summary.unclear
    if (($passed + $failed + $unclear) -ne $total) {
        throw "count mismatch: passed+failed+unclear=$($passed+$failed+$unclear), total=$total"
    }
    # Per-scenario sanity
    foreach ($r in $Summary.results) {
        foreach ($f in @('scenario','pass','confidence','observations','issues','failure_class','evidence')) {
            if (-not ($r.PSObject.Properties.Name -contains $f)) {
                throw "results entry missing field: $f (scenario=$($r.scenario))"
            }
        }
    }
}

function Invoke-EvaluatorApply {
    param([Parameter(Mandatory=$true)] [string]$ResolvedRunId)

    $runDir = Join-Path $E2ERoot "artifacts\$ResolvedRunId"
    $evalPath = Join-Path $runDir "evaluator_summary.json"
    if (-not (Test-Path $evalPath)) {
        throw "evaluator_summary.json not found at $evalPath — has the subagent written it yet?"
    }

    # Cross-check with operator summary
    $summaryPath = Join-Path $runDir "summary.json"
    if (Test-Path $summaryPath) {
        $operator = Get-Content $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } else {
        $operator = $null
    }

    $evaluator = Get-Content $evalPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Test-EvaluatorSummaryShape -Summary $evaluator

    if ($null -ne $operator -and $evaluator.run_id -ne $operator.run_id) {
        throw "run_id mismatch: evaluator=$($evaluator.run_id), operator=$($operator.run_id) — cross-run paste detected"
    }

    # Write SHA256 sidecar (D14 write-authority enforcement)
    $sidecarPath = "$evalPath.sha256"
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($evalPath)
        try {
            $hashBytes = $sha.ComputeHash($stream)
            $hex = -join ($hashBytes | ForEach-Object { $_.ToString("x2") })
        } finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
    Set-Content -Path $sidecarPath -Value $hex -NoNewline

    # Print verdict table
    Write-Host ""
    Write-Host "=== Evaluator verdict — run_id=$ResolvedRunId ===" -ForegroundColor Cyan
    Write-Host ("  verdict:     {0}" -f $evaluator.verdict)
    Write-Host ("  match_rate:  {0}" -f $evaluator.match_rate)
    Write-Host ("  passed:      {0} / {1}" -f $evaluator.passed, $evaluator.total)
    Write-Host ("  failed:      {0}" -f $evaluator.failed)
    Write-Host ("  unclear:     {0}" -f $evaluator.unclear)
    Write-Host ("  evaluator:   {0}" -f $evaluator.evaluator_id)
    Write-Host ("  prompt_ver:  {0}" -f $evaluator.prompt_version)
    Write-Host ("  sidecar:     {0}" -f $sidecarPath)
    Write-Host ""
    Write-Host "  scenarios:"
    foreach ($r in $evaluator.results) {
        $mark = if ($null -eq $r.pass) { "??" } elseif ($r.pass) { "OK" } else { "FAIL" }
        $cls = if ($r.failure_class) { " [$($r.failure_class)]" } else { "" }
        Write-Host ("    {0,-6} {1} ({2}){3}" -f $r.scenario, $mark, $r.confidence, $cls)
    }
    Write-Host ""

    # PowerShell switch inside a function does not reliably propagate `return`
    # to the caller on every host — assign to a variable and return explicitly.
    $exitCode = switch ($evaluator.verdict) {
        "PASS"    { 0 }
        "FAIL"    { 1 }
        "UNCLEAR" { 2 }
        default   { 3 }
    }
    return $exitCode
}

Initialize-Venv

if ($Bootstrap) {
    Write-Host "[e2e] bootstrap-only mode complete" -ForegroundColor Green
    exit 0
}

# -Apply mode: validate paste-back result, print verdict, set exit code.
if ($Apply) {
    if (-not $PSBoundParameters.ContainsKey('RunId')) {
        throw "-Apply requires -RunId to be set explicitly (never implicit)"
    }
    $applyExit = Invoke-EvaluatorApply -ResolvedRunId $RunId
    exit $applyExit
}

# -EvaluateOnly: skip Invoke-Runner, prepare handoff for an existing run.
if ($EvaluateOnly) {
    $targetRunId = if ($PSBoundParameters.ContainsKey('RunId')) { $RunId } else { Resolve-LatestRunId }
    Invoke-EvaluatorWrapper -ResolvedRunId $targetRunId
    exit 0
}

# Normal path: Invoke-Runner, then optionally Invoke-EvaluatorWrapper.
$runnerExit = Invoke-Runner

if ($runnerExit -eq 0 -and $Evaluate) {
    Invoke-EvaluatorWrapper -ResolvedRunId $RunId
}

exit $runnerExit
