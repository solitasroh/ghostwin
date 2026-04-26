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

    # Reserved for future multi-pane support. The current script only
    # supports fresh 1-pane launches; higher values are rejected below so
    # the output cannot be mistaken for a real multi-pane baseline.
    [ValidateRange(1, 8)]
    [int]$Panes = 1,

    # M-15 Stage A: temporarily move %APPDATA%\GhostWin\session.json{,.bak}
    # before launch and restore them in the finally block. Without this,
    # session restore replays the user's last workspace tree, which makes
    # `panes` and pane count non-deterministic across runs. Opt-in so it
    # never silently shuffles a user's working state.
    [switch]$ResetSession
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# ── Output dir ──────────────────────────────────────────────────────────────
if (-not $OutputDir) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $tag = if ($Panes -gt 1) { "$Scenario-${Panes}pane-$stamp" } else { "$Scenario-$stamp" }
    # M-15 Stage A: artifacts now live under m15-baseline/ to match the
    # follow-up milestone scope. Existing m14-baseline/ runs stay where they
    # were committed for traceability.
    $OutputDir = Join-Path $repoRoot "docs\04-report\features\m15-baseline\$tag"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Write-Host "[baseline] output -> $OutputDir"

if ($Panes -gt 1 -and -not ($Scenario -eq 'resize' -and $Panes -eq 4)) {
    throw "Only -Panes 1 (any scenario) or -Panes 4 (with -Scenario resize) are supported."
}

$logFile       = Join-Path $OutputDir 'ghostwin.log'
$csvFile       = Join-Path $OutputDir 'render-perf.csv'
$summaryFile   = Join-Path $OutputDir 'summary.txt'
$presentMonCsv = Join-Path $OutputDir 'presentmon.csv'
# M-15 Stage A artifacts (driver result + CPU capture)
$driverJson    = Join-Path $OutputDir 'driver-result.json'
$cpuCsv        = Join-Path $OutputDir 'cpu.csv'

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
        Write-Host '[baseline] load — driver auto-types a fixed workload (M-15 Stage A):'
        Write-Host '              Get-ChildItem -Recurse C:\Windows\System32 | Format-List'
        Write-Host '              (override via --workload on the driver if needed.)'
    }
    'resize' {
        Write-Host '[baseline] resize — M-14 W4: main window is resized automatically via Win32'
        Write-Host '              SetWindowPos every 500ms between two target sizes.'
        Write-Host '              No manual drag needed.'
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

# ── M-15 Stage A: MeasurementDriver + CPU capture helpers ──────────────────
# The driver exe owns UI automation (window foreground, pane split, load typing,
# pane count verification). The script launches it before/after the capture
# window and merges its JSON contract into summary.txt.
function Resolve-MeasurementDriverExe {
    param([string]$RepoRoot, [string]$Configuration)

    $driverRoot = Join-Path $RepoRoot 'tests\GhostWin.MeasurementDriver\bin'
    # msbuild emits to bin\x64\<Cfg>\net10.0-windows; dotnet build emits to
    # bin\<Cfg>\net10.0-windows. Probe both.
    $candidates = @(
        (Join-Path $driverRoot "x64\$Configuration\net10.0-windows\GhostWin.MeasurementDriver.exe"),
        (Join-Path $driverRoot "$Configuration\net10.0-windows\GhostWin.MeasurementDriver.exe")
    )
    foreach ($p in $candidates) {
        if (Test-Path -LiteralPath $p) { return $p }
    }
    throw "GhostWin.MeasurementDriver.exe not found. Looked in:`n  $($candidates -join "`n  ")"
}

function Start-CpuCapture {
    param([string]$OutputCsv, [int]$DurationSec)

    # \Processor Information(_Total)\% Processor Utility — system-wide,
    #   accounts for CPU frequency (preferred over % Processor Time).
    # \Process(GhostWin.App)\% Processor Time — process-scoped CPU.
    # 1s sampling × DurationSec samples; if the script kills typeperf early
    # (Stop-Process in finally), partial CSV remains usable.
    $counters = @(
        '\Processor Information(_Total)\% Processor Utility',
        '\Process(GhostWin.App)\% Processor Time'
    )
    $argList = $counters + @('-si', '1', '-sc', "$DurationSec", '-o', $OutputCsv, '-f', 'CSV')

    # PS5 vs PS7 의 Start-Process -ArgumentList quoting 차이로, 공백+괄호가
    # 든 typeperf 카운터 이름이 PS5 에서 split 되어 "유효한 카운터 없음"
    # 으로 fail. 명시적으로 quote 해 single string 으로 전달해 양쪽 호환.
    $sb = [System.Text.StringBuilder]::new()
    foreach ($a in $argList) {
        if ($sb.Length -gt 0) { [void]$sb.Append(' ') }
        if ($a -match '\s|"') {
            [void]$sb.Append('"').Append(($a -replace '"', '\"')).Append('"')
        } else {
            [void]$sb.Append($a)
        }
    }
    $argString = $sb.ToString()

    # M-15 Stage A diagnostic: typeperf runs hidden, so failures (missing
    # counter / instance / quoting issues) are silent. Capture stdout/stderr
    # next to the CSV so the script leaves a forensic trail.
    $stdout = "$OutputCsv.stdout.log"
    $stderr = "$OutputCsv.stderr.log"

    return Start-Process -FilePath 'typeperf.exe' `
        -ArgumentList $argString `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -PassThru -WindowStyle Hidden
}

function Invoke-MeasurementDriver {
    param(
        [string]$DriverExe,
        [string]$Scenario,
        [int]$ProcessId,
        [string]$OutputJson,
        [string]$Workload = ''
    )

    $argList = @('--scenario', $Scenario, '--pid', "$ProcessId", '--output-json', $OutputJson)
    if ($Workload) {
        $argList += @('--workload', $Workload)
    }
    $proc = Start-Process -FilePath $DriverExe -ArgumentList $argList `
        -Wait -PassThru -WindowStyle Hidden
    if ($proc.ExitCode -ne 0) {
        throw "MeasurementDriver failed (scenario=$Scenario, exit=$($proc.ExitCode))"
    }
    return Get-Content -LiteralPath $OutputJson -Raw | ConvertFrom-Json
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

# ── M-15 Stage A: optional session suspension for fresh-state baselines ────
$sessionBackupDir = $null
if ($ResetSession) {
    $appDataGw = Join-Path $env:APPDATA 'GhostWin'
    if (Test-Path $appDataGw) {
        $sessionBackupDir = Join-Path $env:TEMP "m15-session-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        New-Item -ItemType Directory -Path $sessionBackupDir -Force | Out-Null
        foreach ($f in 'session.json', 'session.json.bak') {
            $src = Join-Path $appDataGw $f
            if (Test-Path $src) {
                Move-Item -LiteralPath $src -Destination $sessionBackupDir
            }
        }
        Write-Host "[baseline] -ResetSession — backed up session to $sessionBackupDir"
    }
}

# ── Launch app ──────────────────────────────────────────────────────────────
$env:GHOSTWIN_RENDER_PERF = '1'
$env:GHOSTWIN_LOG_FILE    = $logFile

# M-15 Stage A: locate measurement driver exe (must be built; use -Build switch
# or msbuild GhostWin.sln /p:Configuration=$Configuration in advance).
$driverExe = Resolve-MeasurementDriverExe -RepoRoot $repoRoot -Configuration $Configuration
Write-Host "[baseline] driver -> $driverExe"

$app = Start-Process -FilePath $appExe -PassThru
Write-Host "[baseline] launched pid=$($app.Id) — capturing for ${DurationSec}s (panes=$Panes)"

# M-15 Stage A: start CPU capture in parallel with the capture window. typeperf
# attaches to the GhostWin.App process; if it kills early via the finally
# Stop-Process below, the partial cpu.csv is still usable.
$cpuProc = $null
$driverResult = $null
$cpuProc = Start-CpuCapture -OutputCsv $cpuCsv -DurationSec $DurationSec

try {
    if ($Scenario -eq 'idle') {
        # M-15 Stage A: foreground + main window check via the driver. The
        # driver returns a Success contract once it confirmed the window
        # handle. No keyboard / split / load input.
        $driverResult = Invoke-MeasurementDriver -DriverExe $driverExe `
            -Scenario 'idle' -ProcessId $app.Id -OutputJson $driverJson
        Start-Sleep -Seconds $DurationSec
    }
    elseif ($Scenario -eq 'resize' -and $Panes -eq 4) {
        # M-15 Stage A: driver performs Alt+V / Alt+H splits to reach 4 panes,
        # verifies pane count via UIA (E2E_TerminalHost), then this script
        # reuses the W4 SetWindowPos loop to drive the resize workload.
        $driverResult = Invoke-MeasurementDriver -DriverExe $driverExe `
            -Scenario 'resize-4pane' -ProcessId $app.Id -OutputJson $driverJson
        if (-not $driverResult.Valid) {
            throw "4-pane resize baseline invalid: $($driverResult.Reason) (observed=$($driverResult.ObservedPanes))"
        }
        Write-Host "[baseline] driver split OK — observed_panes=$($driverResult.ObservedPanes)"
        # Fall through to the existing resize SetWindowPos loop below; the
        # 'resize' branch right after handles both 1-pane and 4-pane cases.
        $hwnd = Wait-MainWindow -ProcessId $app.Id -TimeoutMs 15000
        if ($hwnd -eq [System.IntPtr]::Zero) {
            Write-Warning '[baseline] MainWindowHandle not observed within 15s — falling back to untimed sleep'
            Start-Sleep -Seconds $DurationSec
        } else {
            Write-Host "[baseline] resize automation: hwnd=0x$('{0:X}' -f [int64]$hwnd)"
            $sizes = @(@{cx=1024;cy=768}, @{cx=1400;cy=900})
            $flags = [W4Automation.Win32]::SWP_NOZORDER -bor `
                     [W4Automation.Win32]::SWP_NOACTIVATE -bor `
                     [W4Automation.Win32]::SWP_NOMOVE -bor `
                     [W4Automation.Win32]::SWP_ASYNCWINDOWPOS
            $end = [Environment]::TickCount + ($DurationSec * 1000)
            $i = 0
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
    }
    elseif ($Scenario -eq 'load') {
        # M-15 Stage A: driver foregrounds the window and types the fixed
        # workload; the Start-Sleep below holds the capture window open
        # while output keeps streaming through the GhostWin renderer.
        $driverResult = Invoke-MeasurementDriver -DriverExe $driverExe `
            -Scenario 'load' -ProcessId $app.Id -OutputJson $driverJson
        if (-not $driverResult.Valid) {
            throw "Load baseline invalid: $($driverResult.Reason)"
        }
        Start-Sleep -Seconds $DurationSec
    }
    elseif ($Scenario -eq 'resize') {
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
    # M-15 Stage A: terminate CPU capture before app close so typeperf flushes
    # the partial CSV.
    if ($cpuProc -and -not $cpuProc.HasExited) {
        Stop-Process -Id $cpuProc.Id -Force -ErrorAction SilentlyContinue
    }
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

    # M-15 Stage A: restore the user's session even if the run threw above.
    # The fresh session.json the app may have written during the run is
    # overwritten on purpose so the user's pre-baseline workspace tree wins.
    if ($sessionBackupDir -and (Test-Path $sessionBackupDir)) {
        $appDataGw = Join-Path $env:APPDATA 'GhostWin'
        foreach ($f in Get-ChildItem -LiteralPath $sessionBackupDir) {
            Copy-Item -LiteralPath $f.FullName `
                -Destination (Join-Path $appDataGw $f.Name) -Force
        }
        Remove-Item -LiteralPath $sessionBackupDir -Recurse -Force
        Write-Host '[baseline] -ResetSession — session restored'
    }
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
# M-15 Stage A: CPU + driver artifacts
if (Test-Path -LiteralPath $cpuCsv) {
    $lines += "cpu_csv:        $(Split-Path -Leaf $cpuCsv)"
}
if ($driverResult) {
    $lines += "driver_valid:   $($driverResult.Valid)"
    if ($null -ne $driverResult.ObservedPanes) {
        $lines += "observed_panes: $($driverResult.ObservedPanes)"
    }
    if ($driverResult.Reason) {
        $lines += "reason:         $($driverResult.Reason)"
    }
}
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
