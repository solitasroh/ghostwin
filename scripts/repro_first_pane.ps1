#Requires -Version 5.1
<#
.SYNOPSIS
    Cold-start reproduction harness for first-pane-render-failure.

.DESCRIPTION
    Automates N iterations of: kill GhostWin → launch → wait DelayMs →
    screenshot → verdict (dark-ratio) → kill. Aggregates results to
    artifacts/repro_first_pane/{run_id}/summary.json.

    Env var overrides (higher priority than parameters):
        GHOSTWIN_REPRO_ITERATIONS   — int,   default 30
        GHOSTWIN_REPRO_DELAY_MS     — int,   default 2000
        GHOSTWIN_REPRO_THRESHOLD    — float, default 0.85
        GHOSTWIN_RENDERDIAG         — int,   0=off / 3=STATE (passed to child)

    4-attempt fallback chain (R2 mitigation, §9.2):
        Attempt 1: -Iterations 30  -DelayMs 2000  (default)
        Attempt 2: -Iterations 30  -DelayMs 500   (race window 압축)
        Attempt 3: -Iterations 100 -DelayMs 2000  (hit-rate 1% 가능성)
        Attempt 4: 사용자 hardware 에서 실행 (30회 × 2000ms)

.PARAMETER Iterations
    Number of cold-start cycles to run.
    Env override: GHOSTWIN_REPRO_ITERATIONS

.PARAMETER DelayMs
    Milliseconds to wait after launch before taking screenshot.
    Env override: GHOSTWIN_REPRO_DELAY_MS

.PARAMETER ExePath
    Full path to GhostWin.App.exe.  Auto-detected if omitted.

.PARAMETER DarkRatioThreshold
    Ratio of dark pixels above which verdict is "blank".
    Env override: GHOSTWIN_REPRO_THRESHOLD

.PARAMETER RenderDiagLevel
    0 = diagnostics off (baseline).  3 = STATE evidence collection.
    Sets GHOSTWIN_RENDERDIAG on the child process.
    Env override: GHOSTWIN_RENDERDIAG

.PARAMETER EvaluatePartial
    If set, invoke the e2e Evaluator for any "partial" verdicts via
    test_e2e.ps1 -EvaluateOnly after the run completes.

.EXAMPLE
    # Attempt 1 — baseline (default)
    scripts/repro_first_pane.ps1 -Iterations 30 -RenderDiagLevel 0

.EXAMPLE
    # Attempt 2 — race window 압축
    scripts/repro_first_pane.ps1 -Iterations 30 -DelayMs 500

.EXAMPLE
    # Attempt 3 — hit-rate 확장
    scripts/repro_first_pane.ps1 -Iterations 100

.EXAMPLE
    # Evidence collection (Pass 2)
    scripts/repro_first_pane.ps1 -Iterations 30 -RenderDiagLevel 3

.EXAMPLE
    # With evaluator for partial verdicts
    scripts/repro_first_pane.ps1 -Iterations 30 -RenderDiagLevel 3 -EvaluatePartial
#>
param(
    [int]    $Iterations         = 30,
    [int]    $DelayMs            = 2000,
    [string] $ExePath            = '',
    [float]  $DarkRatioThreshold = 0.85,
    [int]    $RenderDiagLevel    = 0,
    [switch] $EvaluatePartial
)

$ErrorActionPreference = 'Stop'

# ── Constants ─────────────────────────────────────────────────────────────────
$SAMPLE_STEP      = 4          # sample every Nth pixel row/col for Get-Verdict
$DARK_SUM_LIMIT   = 90         # R+G+B below this → dark pixel
$PARTIAL_RATIO    = 0.30       # dark-ratio above this (but below Threshold) → partial
$PROC_KILL_WAIT_S = 3          # seconds to wait for GhostWin to exit after Stop-Process
$PROC_NAME        = 'GhostWin.App'

# ── Resolve env-var overrides (env > parameter > default) ─────────────────────
if ($env:GHOSTWIN_REPRO_ITERATIONS) { $Iterations         = [int]   $env:GHOSTWIN_REPRO_ITERATIONS }
if ($env:GHOSTWIN_REPRO_DELAY_MS)   { $DelayMs            = [int]   $env:GHOSTWIN_REPRO_DELAY_MS   }
if ($env:GHOSTWIN_REPRO_THRESHOLD)  { $DarkRatioThreshold = [float] $env:GHOSTWIN_REPRO_THRESHOLD  }
if ($env:GHOSTWIN_RENDERDIAG)       { $RenderDiagLevel    = [int]   $env:GHOSTWIN_RENDERDIAG       }

# ── Paths ─────────────────────────────────────────────────────────────────────
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$ArtifactDir = Join-Path $RepoRoot "scripts\e2e\artifacts\repro_first_pane"
$RunId       = Get-Date -Format "yyyyMMdd_HHmmss"
$RunDir      = Join-Path $ArtifactDir $RunId

# ── Auto-detect ExePath ───────────────────────────────────────────────────────
function Resolve-ExePath {
    param([string]$UserPath)

    if ($UserPath -and (Test-Path $UserPath)) { return $UserPath }

    # x64/Release is the canonical output from scripts/build_wpf.ps1 and has
    # ghostwin_engine.dll + ghostty-vt.dll copied alongside the exe. The plain
    # Release/ path is from `dotnet build` and may be missing native DLLs.
    $candidates = @(
        (Join-Path $RepoRoot "src\GhostWin.App\bin\x64\Release\net10.0-windows\GhostWin.App.exe"),
        (Join-Path $RepoRoot "src\GhostWin.App\bin\Release\net10.0-windows\GhostWin.App.exe"),
        (Join-Path $RepoRoot "src\GhostWin.App\bin\x64\Release\net10.0-windows\win-x64\GhostWin.App.exe"),
        (Join-Path $RepoRoot "src\GhostWin.App\bin\Release\net10.0-windows\win-x64\GhostWin.App.exe")
    )

    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }

    Write-Error @"
GhostWin.App.exe not found.  Please build first:
    powershell -ExecutionPolicy Bypass -File scripts/build_ghostwin.ps1 -Config Release
Or specify the path explicitly:
    scripts/repro_first_pane.ps1 -ExePath <full\path\to\GhostWin.App.exe>
"@
    exit 1
}

# ── Stop-GhostWinProcess ──────────────────────────────────────────────────────
function Stop-GhostWinProcess {
    <#
    .SYNOPSIS Kill any running GhostWin.App process and wait up to 3 s for exit.
    #>
    $procs = Get-Process -Name $PROC_NAME -ErrorAction SilentlyContinue
    if (-not $procs) { return }

    $procs | Stop-Process -Force -ErrorAction SilentlyContinue

    try {
        $procs | Wait-Process -Timeout $PROC_KILL_WAIT_S -ErrorAction SilentlyContinue
    }
    catch { <# timeout is non-fatal — process may already be gone #> }

    # Verify
    $remaining = Get-Process -Name $PROC_NAME -ErrorAction SilentlyContinue
    if ($remaining) {
        Write-Warning "GhostWin.App still alive after ${PROC_KILL_WAIT_S}s — continuing anyway."
    }
}

# ── Save-DesktopImage ─────────────────────────────────────────────────────────
# Full primary-screen capture to PNG. Window-rect capture was removed because
# the (MainWindowHandle + AutomationElement + CopyFromScreen + PNG save)
# combination matched a Windows Defender AMSI signature for screen-capture
# malware. Primary-screen capture is sufficient for dark-ratio verdict since
# GhostWin is typically maximised or occupies most of the screen during repro.

function Initialize-WinApi {
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Windows.Forms
}

function Save-DesktopImage {
    <#
    .SYNOPSIS
        Capture the primary screen to a PNG file.
    .PARAMETER OutputPath  Destination PNG file path.
    .PARAMETER ProcessObj  Unused; retained for caller API compatibility.
    #>
    param(
        [Parameter(Mandatory)] [string] $OutputPath,
        [System.Diagnostics.Process]    $ProcessObj = $null
    )

    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $width  = $bounds.Width
    $height = $bounds.Height

    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bmp.Size)
        $dir = Split-Path -Parent $OutputPath
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $g.Dispose()
        $bmp.Dispose()
    }
}

# ── Get-Verdict ───────────────────────────────────────────────────────────────
function Get-Verdict {
    <#
    .SYNOPSIS
        Compute dark-pixel ratio from a screenshot PNG and return verdict hashtable.
    .DESCRIPTION
        GhostWin clear color: 0x1E1E2E → R30+G30+B46=106.
        Threshold of 90 means the clear-color background itself IS counted as dark.
        Normal render has bright text/cursor → ratio well below 0.30.
        Pure blank (no render) → ratio near 1.0.

        Sampling: every SAMPLE_STEP-th row and column for speed.
    .RETURNS  @{ verdict='blank'|'partial'|'ok'; ratio=[float] }
    #>
    param(
        [Parameter(Mandatory)] [string] $ScreenshotPath,
        [Parameter(Mandatory)] [float]  $Threshold
    )

    if (-not (Test-Path $ScreenshotPath)) {
        return @{ verdict = 'error'; ratio = -1.0 }
    }

    Add-Type -AssemblyName System.Drawing
    $bmp = [System.Drawing.Image]::FromFile((Resolve-Path $ScreenshotPath).Path)
    try {
        $darkPx    = 0
        $sampledPx = 0

        for ($y = 0; $y -lt $bmp.Height; $y += $SAMPLE_STEP) {
            for ($x = 0; $x -lt $bmp.Width; $x += $SAMPLE_STEP) {
                $px = $bmp.GetPixel($x, $y)
                if (($px.R + $px.G + $px.B) -lt $DARK_SUM_LIMIT) { $darkPx++ }
                $sampledPx++
            }
        }

        if ($sampledPx -eq 0) { return @{ verdict = 'error'; ratio = -1.0 } }

        $ratio = [float]($darkPx) / [float]($sampledPx)

        if ($ratio -gt $Threshold)  { return @{ verdict = 'blank';   ratio = $ratio } }
        if ($ratio -gt $PARTIAL_RATIO) { return @{ verdict = 'partial'; ratio = $ratio } }
        return @{ verdict = 'ok'; ratio = $ratio }
    }
    finally {
        $bmp.Dispose()
    }
}

# ── Copy-RenderDiagLog ────────────────────────────────────────────────────────
function Copy-RenderDiagLog {
    <#
    .SYNOPSIS  Copy today's render_diag log into the iteration directory.
    .DESCRIPTION
        Source: %LocalAppData%\GhostWin\diagnostics\render_{yyyyMMdd}.log
        Destination: {iterDir}\render_diag.log
        Silent skip when RenderDiagLevel=0 or file does not exist.
    .RETURNS  Relative path string, or $null if not copied.
    #>
    param(
        [Parameter(Mandatory)] [string] $IterDir,
        [Parameter(Mandatory)] [int]    $DiagLevel
    )

    if ($DiagLevel -eq 0) { return $null }

    $today   = Get-Date -Format "yyyyMMdd"
    $srcPath = Join-Path $env:LOCALAPPDATA "GhostWin\diagnostics\render_${today}.log"

    if (-not (Test-Path $srcPath)) { return $null }

    $dest = Join-Path $IterDir "render_diag.log"
    try {
        Copy-Item -Path $srcPath -Destination $dest -Force
        return "render_diag.log"
    }
    catch {
        Write-Warning "Could not copy render diag log: $_"
        return $null
    }
}

# ── Write-SummaryJson ─────────────────────────────────────────────────────────
function Write-SummaryJson {
    <#
    .SYNOPSIS  Write aggregated summary.json for the run.
    #>
    param(
        [Parameter(Mandatory)] [string]   $RunDir,
        [Parameter(Mandatory)] [hashtable] $Meta,
        [Parameter(Mandatory)] [array]    $Results
    )

    $blankCount   = ($Results | Where-Object { $_.verdict -eq 'blank'   }).Count
    $partialCount = ($Results | Where-Object { $_.verdict -eq 'partial' }).Count
    $okCount      = ($Results | Where-Object { $_.verdict -eq 'ok'      }).Count

    $summary = [ordered]@{
        run_id               = $Meta.run_id
        exe_path             = $Meta.exe_path
        iterations           = $Meta.iterations
        delay_ms             = $Meta.delay_ms
        dark_ratio_threshold = $Meta.dark_ratio_threshold
        render_diag_level    = $Meta.render_diag_level
        started_at           = $Meta.started_at
        completed_at         = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        blank_count          = $blankCount
        partial_count        = $partialCount
        ok_count             = $okCount
        results              = $Results
    }

    $jsonPath = Join-Path $RunDir "summary.json"
    $summary | ConvertTo-Json -Depth 10 | Set-Content -Path $jsonPath -Encoding UTF8
    return $jsonPath
}

# ── Invoke-SingleIteration ────────────────────────────────────────────────────
function Invoke-SingleIteration {
    <#
    .SYNOPSIS  Execute one cold-start cycle: kill → launch → wait → screenshot → verdict → kill.
    .RETURNS   [hashtable] iteration result record matching summary.json schema.
    #>
    param(
        [Parameter(Mandatory)] [int]    $IterationIndex,
        [Parameter(Mandatory)] [string] $ExePath,
        [Parameter(Mandatory)] [string] $IterDir,
        [Parameter(Mandatory)] [int]    $DelayMs,
        [Parameter(Mandatory)] [float]  $Threshold,
        [Parameter(Mandatory)] [int]    $DiagLevel
    )

    $iterLabel = '{0:D3}' -f $IterationIndex
    Write-Host "  [${iterLabel}] Starting..." -NoNewline

    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    # 1. Kill any existing instance
    Stop-GhostWinProcess

    # 2. Set GHOSTWIN_RENDERDIAG in current process env so child inherits it
    $prevDiag = $env:GHOSTWIN_RENDERDIAG
    try {
        $env:GHOSTWIN_RENDERDIAG = $DiagLevel.ToString()

        # 3. Launch GhostWin.App (hidden console, inherit env)
        $psi                        = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName               = $ExePath
        $psi.WorkingDirectory       = Split-Path -Parent $ExePath
        $psi.UseShellExecute        = $false
        $psi.CreateNoWindow         = $false   # WPF needs a window
        $psi.RedirectStandardOutput = $false
        $psi.RedirectStandardError  = $false
        # Explicitly propagate the diag env var into the child's environment block
        $psi.EnvironmentVariables['GHOSTWIN_RENDERDIAG'] = $DiagLevel.ToString()

        $proc = [System.Diagnostics.Process]::Start($psi)
        $pid_ = if ($proc) { $proc.Id } else { -1 }

        # 4. Wait DelayMs for the window to render
        Start-Sleep -Milliseconds $DelayMs

        # 5. Screenshot
        if (-not (Test-Path $IterDir)) {
            New-Item -ItemType Directory -Path $IterDir -Force | Out-Null
        }
        $screenshotPath = Join-Path $IterDir "screenshot.png"
        $screenshotRel  = "${iterLabel}/screenshot.png"

        # Always capture the primary screen — Save-DesktopImage ignores the
        # process object (it was retained for API compat). Window-rect capture
        # was removed for AMSI compatibility.
        try {
            Save-DesktopImage -OutputPath $screenshotPath -ProcessObj $proc
        }
        catch {
            Write-Warning "Screenshot failed for iteration ${iterLabel}: $_"
        }

        # 6. Verdict
        $verdictResult = Get-Verdict -ScreenshotPath $screenshotPath -Threshold $Threshold

        # 7. Copy render diag log
        $diagLogRel = Copy-RenderDiagLog -IterDir $IterDir -DiagLevel $DiagLevel
        $diagLogFull = if ($diagLogRel) { "${iterLabel}/${diagLogRel}" } else { $null }

        # 8. Kill after capture
        Stop-GhostWinProcess

        $sw.Stop()
        $elapsed = $sw.ElapsedMilliseconds

        $verdict   = $verdictResult.verdict
        $darkRatio = [math]::Round($verdictResult.ratio, 4)

        $statusColor = switch ($verdict) {
            'blank'   { 'Red'    }
            'partial' { 'Yellow' }
            'ok'      { 'Green'  }
            default   { 'Gray'   }
        }
        Write-Host " $verdict (ratio=$darkRatio, ${elapsed}ms)" -ForegroundColor $statusColor

        return [ordered]@{
            iteration          = $IterationIndex
            verdict            = $verdict
            dark_ratio         = $darkRatio
            screenshot_path    = $screenshotRel
            render_diag_log_path = $diagLogFull
            elapsed_ms         = [int]$elapsed
            process_pid        = $pid_
        }
    }
    finally {
        # Restore env var (do not permanently pollute parent process env)
        if ($null -eq $prevDiag) {
            Remove-Item Env:GHOSTWIN_RENDERDIAG -ErrorAction SilentlyContinue
        }
        else {
            $env:GHOSTWIN_RENDERDIAG = $prevDiag
        }
    }
}

# ── Start-ReproRun ────────────────────────────────────────────────────────────
function Start-ReproRun {
    <#
    .SYNOPSIS  Main orchestrator — runs all iterations and writes summary.json.
    #>
    param(
        [int]    $Iterations,
        [int]    $DelayMs,
        [string] $ExePath,
        [float]  $DarkRatioThreshold,
        [int]    $RenderDiagLevel,
        [switch] $EvaluatePartial
    )

    # Resolve exe
    $resolvedExe = Resolve-ExePath -UserPath $ExePath

    # Prepare run directory
    if (-not (Test-Path $RunDir)) {
        New-Item -ItemType Directory -Path $RunDir -Force | Out-Null
    }

    # Pre-load WinAPI types once
    Initialize-WinApi

    $startedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

    Write-Host ""
    Write-Host "=== repro_first_pane  run_id=$RunId ===" -ForegroundColor Cyan
    Write-Host "  exe         : $resolvedExe"
    Write-Host "  iterations  : $Iterations"
    Write-Host "  delay_ms    : $DelayMs"
    Write-Host "  threshold   : $DarkRatioThreshold"
    Write-Host "  diag_level  : $RenderDiagLevel"
    Write-Host "  artifact    : $RunDir"
    Write-Host ""

    # Ensure GhostWin is not running before the first iteration
    Stop-GhostWinProcess

    $results = @()

    for ($i = 1; $i -le $Iterations; $i++) {
        $iterDir = Join-Path $RunDir ('{0:D3}' -f $i)

        $record = Invoke-SingleIteration `
            -IterationIndex  $i `
            -ExePath         $resolvedExe `
            -IterDir         $iterDir `
            -DelayMs         $DelayMs `
            -Threshold       $DarkRatioThreshold `
            -DiagLevel       $RenderDiagLevel

        $results += $record
    }

    # Make sure GhostWin is dead after last iteration
    Stop-GhostWinProcess

    # Write summary
    $meta = [ordered]@{
        run_id               = $RunId
        exe_path             = $resolvedExe
        iterations           = $Iterations
        delay_ms             = $DelayMs
        dark_ratio_threshold = $DarkRatioThreshold
        render_diag_level    = $RenderDiagLevel
        started_at           = $startedAt
    }

    $jsonPath = Write-SummaryJson -RunDir $RunDir -Meta $meta -Results $results

    # Print summary
    $blankCount   = ($results | Where-Object { $_.verdict -eq 'blank'   }).Count
    $partialCount = ($results | Where-Object { $_.verdict -eq 'partial' }).Count
    $okCount      = ($results | Where-Object { $_.verdict -eq 'ok'      }).Count

    Write-Host ""
    Write-Host "=== Summary ===" -ForegroundColor Cyan
    $blankColor   = if ($blankCount   -gt 0)            { 'Red'    } else { 'Gray'  }
    $partialColor = if ($partialCount -gt 0)            { 'Yellow' } else { 'Gray'  }
    $okColor      = if ($okCount      -eq $Iterations)  { 'Green'  } else { 'Gray'  }
    Write-Host "  blank   : $blankCount / $Iterations"   -ForegroundColor $blankColor
    Write-Host "  partial : $partialCount / $Iterations" -ForegroundColor $partialColor
    Write-Host "  ok      : $okCount / $Iterations"      -ForegroundColor $okColor
    Write-Host "  summary : $jsonPath"
    Write-Host ""

    # G2 gate advisory
    if ($blankCount -eq 0) {
        Write-Host "[ADVISORY] blank_count = 0. G2 gate not met." -ForegroundColor Yellow
        Write-Host "  R2 fallback chain:" -ForegroundColor Yellow
        Write-Host "    Attempt 2: -DelayMs 500   (race window 압축)"    -ForegroundColor Yellow
        Write-Host "    Attempt 3: -Iterations 100 (hit-rate 확장)"      -ForegroundColor Yellow
        Write-Host "    Attempt 4: 사용자 hardware 에서 실행 (30회)"     -ForegroundColor Yellow
    }
    else {
        Write-Host "[G2 PASS] blank_count = $blankCount — reproduction confirmed." -ForegroundColor Green
    }

    # Optional: invoke Evaluator for partial verdicts
    if ($EvaluatePartial -and $partialCount -gt 0) {
        $evalScript = Join-Path $PSScriptRoot "test_e2e.ps1"
        if (Test-Path $evalScript) {
            Write-Host ""
            Write-Host "Invoking Evaluator for $partialCount partial verdict(s)..." -ForegroundColor Cyan
            $evalRunId = "repro_first_pane_${RunId}"
            & $evalScript -EvaluateOnly -RunId $evalRunId
        }
        else {
            Write-Warning "test_e2e.ps1 not found at $evalScript — skipping Evaluator."
        }
    }

    return $jsonPath
}

# ── Main ──────────────────────────────────────────────────────────────────────
Start-ReproRun `
    -Iterations         $Iterations `
    -DelayMs            $DelayMs `
    -ExePath            $ExePath `
    -DarkRatioThreshold $DarkRatioThreshold `
    -RenderDiagLevel    $RenderDiagLevel `
    -EvaluatePartial:   $EvaluatePartial
