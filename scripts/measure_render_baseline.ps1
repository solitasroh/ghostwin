<#
.SYNOPSIS
    M-14 W1 render baseline collector — idle / load / resize scenarios.

.DESCRIPTION
    Launches GhostWin.App.exe with `GHOSTWIN_RENDER_PERF=1`, captures
    structured `render-perf` log lines, and converts them to CSV with
    per-scenario summary (avg / p95 / max / count).

    Release-only performance judgement per Design 5.3. Debug runs are
    tagged "DEBUG-REFERENCE-ONLY" and excluded from go/no-go.

.PARAMETER Scenario
    idle   — launch app, wait DurationSec, kill. No input drive.
    load   — launch app + instructs user to trigger heavy output manually
             (e.g. `Get-ChildItem -Recurse C:\Windows` inside the terminal).
             Script times the duration and collects perf samples.
    resize — launch app + instructs user to repeat window resize during the
             capture window. 4-pane baseline: user prepares splits before
             invoking the script.

.PARAMETER DurationSec
    Seconds to capture perf samples after app launch. Default 60.

.PARAMETER Configuration
    Debug | Release. Default Release (per Design 5.3 go/no-go gate).

.PARAMETER PresentMonPath
    Optional path to PresentMon.exe. When provided, runs PresentMon in
    parallel and saves its CSV alongside the render-perf log. See
    https://github.com/GameTechDev/PresentMon.

.PARAMETER OutputDir
    Directory for output artifacts. Default:
    `docs/04-report/features/m14-baseline/<scenario>-<timestamp>/`

.PARAMETER Build
    If set, runs `msbuild GhostWin.sln /p:Configuration=<cfg>` before launch.

.EXAMPLE
    .\scripts\measure_render_baseline.ps1 -Scenario idle -DurationSec 60

.EXAMPLE
    .\scripts\measure_render_baseline.ps1 -Scenario resize -DurationSec 60 `
        -PresentMonPath "C:\Tools\PresentMon-2.0\PresentMon-2.0-x64.exe"

.NOTES
    See docs/02-design/features/m14-render-thread-safety.design.md §5.3 for
    the log schema. `present_us` is wall-clock Present(1, 0) block — includes
    VSync wait, driver queue, and DWM compositor; cross-reference with
    PresentMon output for compositor-level timing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('idle', 'load', 'resize')]
    [string]$Scenario,

    [int]$DurationSec = 60,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$PresentMonPath,

    [string]$OutputDir,

    [switch]$Build,

    # M-14 W4: informational label for output filenames + CSV row `panes`
    # column cross-check. Does NOT pre-configure panes in GhostWin —
    # multi-pane setup still requires manual splitting before launch (see
    # scenario description for `resize` / `load` + Panes > 1).
    [ValidateRange(1, 8)]
    [int]$Panes = 1
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# ── Output dir ──────────────────────────────────────────────────────────────
if (-not $OutputDir) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $tag = if ($Panes -gt 1) { "$Scenario-${Panes}pane-$stamp" } else { "$Scenario-$stamp" }
    $OutputDir = Join-Path $repoRoot "docs\04-report\features\m14-baseline\$tag"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Write-Host "[baseline] output -> $OutputDir"

$logFile       = Join-Path $OutputDir 'ghostwin.log'
$csvFile       = Join-Path $OutputDir 'render-perf.csv'
$summaryFile   = Join-Path $OutputDir 'summary.txt'
$presentMonCsv = Join-Path $OutputDir 'presentmon.csv'

# ── Optional: build ─────────────────────────────────────────────────────────
if ($Build) {
    Write-Host "[baseline] msbuild GhostWin.sln ($Configuration)"
    & msbuild (Join-Path $repoRoot 'GhostWin.sln') `
        "/p:Configuration=$Configuration" `
        '/p:Platform=x64' `
        '/nologo' `
        '/verbosity:minimal'
    if ($LASTEXITCODE -ne 0) {
        throw "msbuild failed (exit $LASTEXITCODE)"
    }
}

# ── Locate app exe ──────────────────────────────────────────────────────────
# GhostWin.App is a .NET WinExe. The target framework moniker embeds the
# Windows SDK version (e.g. net10.0-windows10.0.22621.0) and the RID
# subfolder (win-x64), so exact path varies. Use a glob over the most
# common layouts before falling back to a recursive search.
$appRoot = Join-Path $repoRoot "src\GhostWin.App\bin"
$globPatterns = @(
    "x64\$Configuration\net10.0-windows*\win-x64\GhostWin.App.exe"
    "x64\$Configuration\net10.0-windows*\GhostWin.App.exe"
    "$Configuration\net10.0-windows*\win-x64\GhostWin.App.exe"
    "$Configuration\net10.0-windows*\GhostWin.App.exe"
)
$appExe = $null
foreach ($p in $globPatterns) {
    $hit = Get-ChildItem -Path $appRoot -Filter 'GhostWin.App.exe' -Recurse -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -like (Join-Path $appRoot $p) } |
           Select-Object -First 1
    if ($hit) { $appExe = $hit.FullName; break }
}
if (-not $appExe) {
    # Last resort: any GhostWin.App.exe under bin\<Cfg> that is not in tests
    $appExe = (Get-ChildItem -Path $appRoot -Filter 'GhostWin.App.exe' -Recurse -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match [regex]::Escape("\$Configuration\") } |
               Select-Object -First 1).FullName
}
if (-not $appExe) {
    $list = $globPatterns -join "`n  "
    throw @"
GhostWin.App.exe not found under $appRoot. Searched:
  $list
Use -Build or run msbuild manually first.
"@
}
Write-Host "[baseline] app -> $appExe"

if ($Configuration -eq 'Debug') {
    Write-Warning 'Debug configuration — results are DEBUG-REFERENCE-ONLY and must NOT be used for M-14 go/no-go (Design 5.3).'
}

# ── Scenario prep ───────────────────────────────────────────────────────────
switch ($Scenario) {
    'idle' {
        Write-Host '[baseline] idle — launch app, wait, kill. No user action expected.'
    }
    'load' {
        Write-Host '[baseline] load — after launch, inside the GhostWin terminal run a heavy output command, e.g.:'
        Write-Host '              Get-ChildItem -Recurse C:\Windows\System32 | Format-List'
        Write-Host '[baseline] automated load-input drive is NOT yet implemented; still manual.'
    }
    'resize' {
        Write-Host '[baseline] resize — M-14 W4: main window is resized automatically via Win32'
        Write-Host '              SetWindowPos every 500ms between two target sizes.'
        Write-Host '              No manual drag needed. For 4-pane: pre-split the panes before invoking.'
    }
}

# ── M-14 W4: Win32 automation helpers (used by `resize` scenario) ───────────
if ($Scenario -eq 'resize') {
    if (-not ('W4Automation.Win32' -as [type])) {
        Add-Type -Namespace 'W4Automation' -Name 'Win32' -MemberDefinition @'
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(
                System.IntPtr hWnd, System.IntPtr hWndInsertAfter,
                int X, int Y, int cx, int cy, uint uFlags);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool IsWindowVisible(System.IntPtr hWnd);

            public const int SWP_NOZORDER = 0x0004;
            public const int SWP_NOACTIVATE = 0x0010;
            public const int SWP_NOMOVE = 0x0002;
            public const int SWP_ASYNCWINDOWPOS = 0x4000;
            public const int SW_RESTORE = 9;
'@
    }
}

# Wait for process's main window to be visible. Returns handle or 0.
# (ProcessId param name avoids the $Pid automatic variable.)
function Wait-MainWindow {
    param([int]$ProcessId, [int]$TimeoutMs = 10000)
    $start = [Environment]::TickCount
    while (([Environment]::TickCount - $start) -lt $TimeoutMs) {
        $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowHandle -ne [System.IntPtr]::Zero) {
            return $p.MainWindowHandle
        }
        Start-Sleep -Milliseconds 100
    }
    return [System.IntPtr]::Zero
}

# ── PresentMon (optional) ───────────────────────────────────────────────────
$presentMonProc = $null
if ($PresentMonPath) {
    if (-not (Test-Path $PresentMonPath)) {
        throw "PresentMon not found at: $PresentMonPath"
    }
    Write-Host "[baseline] PresentMon -> $PresentMonPath"
    # -process_name filter + -terminate_after_timed + -output_file for CSV
    $pmArgs = @(
        '--process_name', 'GhostWin.App.exe'
        '--output_file', $presentMonCsv
        '--timed', "$DurationSec"
        '--terminate_after_timed'
        '--stop_existing_session'
    )
    $presentMonProc = Start-Process -FilePath $PresentMonPath `
        -ArgumentList $pmArgs -PassThru -WindowStyle Hidden
}

# ── Launch app ──────────────────────────────────────────────────────────────
$env:GHOSTWIN_RENDER_PERF = '1'
$env:GHOSTWIN_LOG_FILE    = $logFile

$app = Start-Process -FilePath $appExe -PassThru
Write-Host "[baseline] launched pid=$($app.Id) — capturing for ${DurationSec}s (panes=$Panes)"

try {
    if ($Scenario -eq 'resize') {
        $hwnd = Wait-MainWindow -ProcessId $app.Id -TimeoutMs 15000
        if ($hwnd -eq [System.IntPtr]::Zero) {
            Write-Warning '[baseline] MainWindowHandle not observed within 15s — falling back to untimed sleep'
            Start-Sleep -Seconds $DurationSec
        } else {
            Write-Host "[baseline] resize automation: hwnd=0x$('{0:X}' -f [int64]$hwnd)"
            # Alternate between two window sizes. NOMOVE keeps position.
            # Chose sizes that cross the cap_cols boundary to force both the
            # fast (within-cap) and slow (realloc) reshape paths in
            # RenderFrame::reshape + TerminalRenderState::resize.
            $sizes = @(
                @{ cx = 1024; cy = 768  },
                @{ cx = 1400; cy = 900  }
            )
            $flags = [W4Automation.Win32]::SWP_NOZORDER -bor `
                     [W4Automation.Win32]::SWP_NOACTIVATE -bor `
                     [W4Automation.Win32]::SWP_NOMOVE -bor `
                     [W4Automation.Win32]::SWP_ASYNCWINDOWPOS
            $end = [Environment]::TickCount + ($DurationSec * 1000)
            $i = 0
            # Ensure window is restored (not minimized) before resizing.
            [void][W4Automation.Win32]::ShowWindow($hwnd, [W4Automation.Win32]::SW_RESTORE)
            while ([Environment]::TickCount -lt $end) {
                $s = $sizes[$i % $sizes.Count]
                [void][W4Automation.Win32]::SetWindowPos(
                    $hwnd, [System.IntPtr]::Zero,
                    0, 0, $s.cx, $s.cy, $flags)
                Start-Sleep -Milliseconds 500
                $i++
            }
            Write-Host "[baseline] resize automation finished — $i transitions"
        }
    } else {
        Start-Sleep -Seconds $DurationSec
    }
}
finally {
    if (-not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 2
        if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    }
    if ($presentMonProc -and -not $presentMonProc.HasExited) {
        Stop-Process -Id $presentMonProc.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item Env:GHOSTWIN_RENDER_PERF -ErrorAction SilentlyContinue
    Remove-Item Env:GHOSTWIN_LOG_FILE    -ErrorAction SilentlyContinue
}

# ── Parse render-perf log -> CSV ────────────────────────────────────────────
if (-not (Test-Path $logFile)) {
    throw "log file not produced: $logFile"
}

$pattern = '\[INF\]\[render-perf\]\s+frame=(?<frame>\d+)\s+sid=(?<sid>\d+)\s+panes=(?<panes>\d+)\s+vt_dirty=(?<vt>[01])\s+visual_dirty=(?<vd>[01])\s+resize=(?<rz>[01])\s+start_us=(?<su>[\d\.]+)\s+build_us=(?<bu>[\d\.]+)\s+draw_us=(?<du>[\d\.]+)\s+present_us=(?<pu>[\d\.]+)\s+total_us=(?<tu>[\d\.]+)\s+quads=(?<qu>\d+)'

$samples = @()
Get-Content -LiteralPath $logFile | ForEach-Object {
    if ($_ -match $pattern) {
        $samples += [pscustomobject]@{
            frame        = [int64]$Matches.frame
            sid          = [int]$Matches.sid
            panes        = [int]$Matches.panes
            vt_dirty     = [int]$Matches.vt
            visual_dirty = [int]$Matches.vd
            resize       = [int]$Matches.rz
            start_us     = [double]$Matches.su
            build_us     = [double]$Matches.bu
            draw_us      = [double]$Matches.du
            present_us   = [double]$Matches.pu
            total_us     = [double]$Matches.tu
            quads        = [int]$Matches.qu
        }
    }
}

Write-Host "[baseline] parsed $($samples.Count) render-perf samples"
if ($samples.Count -eq 0) {
    Write-Warning 'no render-perf samples parsed — is GHOSTWIN_RENDER_PERF honored by the build?'
    return
}

$samples | Export-Csv -NoTypeInformation -Path $csvFile
Write-Host "[baseline] csv -> $csvFile"

# ── Summary (avg / p95 / max per column) ────────────────────────────────────
function Get-P95 {
    param([double[]]$values)
    if ($values.Count -eq 0) { return 0 }
    $sorted = $values | Sort-Object
    $idx = [Math]::Ceiling($sorted.Count * 0.95) - 1
    if ($idx -lt 0) { $idx = 0 }
    if ($idx -ge $sorted.Count) { $idx = $sorted.Count - 1 }
    return $sorted[$idx]
}

$metrics = 'start_us','build_us','draw_us','present_us','total_us'
$lines = @()
$lines += "scenario:       $Scenario"
$lines += "configuration:  $Configuration"
$lines += "duration_sec:   $DurationSec"
$lines += "panes:          $Panes (declared); observed max in CSV: $(($samples | Measure-Object -Property panes -Maximum).Maximum)"
$lines += "sample_count:   $($samples.Count)"
$lines += ''
$lines += ('{0,-12} {1,10} {2,10} {3,10}' -f 'metric', 'avg', 'p95', 'max')
$lines += ('-' * 46)
foreach ($m in $metrics) {
    $vals = ($samples | ForEach-Object { [double]$_.$m })
    $avg = [math]::Round(($vals | Measure-Object -Average).Average, 1)
    $p95 = [math]::Round((Get-P95 -values $vals), 1)
    $max = [math]::Round(($vals | Measure-Object -Maximum).Maximum, 1)
    $lines += ('{0,-12} {1,10} {2,10} {3,10}' -f $m, $avg, $p95, $max)
}

$summary = $lines -join [Environment]::NewLine
Set-Content -LiteralPath $summaryFile -Value $summary -Encoding UTF8
Write-Host ''
Write-Host $summary
Write-Host ''
Write-Host "[baseline] summary -> $summaryFile"

if ($PresentMonPath -and (Test-Path $presentMonCsv)) {
    Write-Host "[baseline] presentmon -> $presentMonCsv"
}
