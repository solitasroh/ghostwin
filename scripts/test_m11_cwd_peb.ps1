# GhostWin M-11 followup — PEB CWD polling 자동 검증 스크립트
# 2026-04-15
#
# DEPRECATED (2026-04-16, M-11.5 E2E Test Harness 체계화):
#   이 스크립트의 검증 로직은 xUnit 으로 이식됨.
#   대체: dotnet test tests/GhostWin.E2E.Tests --filter "Class=FileStateScenarios&TestName=PebCwdPolling_Fill"
#   이 파일은 참고용으로 보존되며 Phase 5 종료 후 삭제 예정.
#
# 동작:
#   1. 실행 중인 GhostWin.App.exe 있으면 정상 종료 후 대기
#   2. MSBuild 로 GhostWin.sln 빌드 (C++ 엔진 포함)
#   3. session.json 삭제 (깨끗한 상태)
#   4. GhostWin.App.exe 실행
#   5. 4초 대기 (DispatcherTimer 1s × 3회 + snapshot 10s 주기 or 종료 시 저장)
#   6. WM_CLOSE 로 정상 종료 → OnClosing 이 snapshot 저장
#   7. session.json 파싱 → leaf.cwd 가 채워졌는지 검증
#
# 핵심: 키보드/마우스/포커스 불필요 — PowerShell 은 %USERPROFILE% 에서 시작하므로
# PEB 폴링이 작동하면 SessionInfo.Cwd 가 자동으로 채워져야 함.
#
# 종료 코드:
#   0: PASS (cwd 채워짐 — PEB 폴링 작동)
#   1: FAIL (빌드 실패)
#   2: FAIL (session.json 미생성)
#   3: FAIL (cwd 필드 null/empty — PEB 폴링 미작동)
#   4: FAIL (환경 문제 — MSBuild 못 찾음 등)

$ErrorActionPreference = 'Stop'

$repoRoot     = Split-Path -Parent $PSScriptRoot
$slnPath      = Join-Path $repoRoot 'GhostWin.sln'
$appExe       = Join-Path $repoRoot 'src\GhostWin.App\bin\Debug\net10.0-windows\win-x64\GhostWin.App.exe'
$sessionPath  = Join-Path $env:APPDATA 'GhostWin\session.json'
$bakPath      = "$sessionPath.bak"

function Write-Section($msg) {
    Write-Host ''
    Write-Host "─── $msg ───" -ForegroundColor Cyan
}

function Find-MSBuild {
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $null }

    # VS 18 Insiders 우선, 없으면 일반 VS18
    $vsPath = & $vswhere -latest -prerelease `
        -requires Microsoft.Component.MSBuild `
        -property installationPath 2>$null | Select-Object -First 1
    if (-not $vsPath) {
        $vsPath = & $vswhere -latest `
            -requires Microsoft.Component.MSBuild `
            -property installationPath 2>$null | Select-Object -First 1
    }
    if (-not $vsPath) { return $null }

    $msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path $msbuild) { return $msbuild }
    return $null
}

function Stop-RunningApp {
    $running = Get-Process -Name 'GhostWin.App' -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "⚠️ GhostWin.App.exe 실행 중 (PID $($running.Id)) — 정상 종료 시도..." -ForegroundColor Yellow
        foreach ($p in $running) {
            $null = $p.CloseMainWindow()
        }
        Start-Sleep -Seconds 2
        $still = Get-Process -Name 'GhostWin.App' -ErrorAction SilentlyContinue
        if ($still) {
            Write-Host "정상 종료 실패 — 강제 종료" -ForegroundColor Yellow
            $still | Stop-Process -Force
            Start-Sleep -Milliseconds 500
        }
    }
}

function Find-FirstLeaf($node) {
    if ($null -eq $node) { return $null }
    if ($node.type -eq 'leaf') { return $node }
    if ($node.type -eq 'split') {
        $l = Find-FirstLeaf $node.left
        if ($l) { return $l }
        return Find-FirstLeaf $node.right
    }
    return $null
}

# ══════════════════════════════════════════════════════════
# 1. 환경 확인
# ══════════════════════════════════════════════════════════
Write-Section '1. 환경 확인'

$msbuild = Find-MSBuild
if (-not $msbuild) {
    Write-Host "❌ MSBuild 를 찾지 못했습니다. VS 18 (Insiders or Community) 설치 필요." -ForegroundColor Red
    exit 4
}
Write-Host "MSBuild: $msbuild"

# ══════════════════════════════════════════════════════════
# 2. 실행 중인 앱 종료
# ══════════════════════════════════════════════════════════
Write-Section '2. 실행 중인 앱 확인'
Stop-RunningApp

# ══════════════════════════════════════════════════════════
# 3. 빌드
# ══════════════════════════════════════════════════════════
Write-Section '3. 빌드 (MSBuild GhostWin.sln)'
Push-Location $repoRoot
try {
    & $msbuild $slnPath /p:Configuration=Debug /p:Platform=x64 /nologo /v:minimal /m
    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ 빌드 실패 (exit $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
}
finally { Pop-Location }
Write-Host "✅ 빌드 성공" -ForegroundColor Green

if (-not (Test-Path $appExe)) {
    Write-Host "❌ 빌드는 성공했는데 $appExe 가 없습니다." -ForegroundColor Red
    exit 1
}

# ══════════════════════════════════════════════════════════
# 4. session.json 청소 (깨끗한 시작)
# ══════════════════════════════════════════════════════════
Write-Section '4. session.json 초기화'
Remove-Item $sessionPath -ErrorAction SilentlyContinue
Remove-Item $bakPath     -ErrorAction SilentlyContinue
Write-Host "삭제 완료: $sessionPath (+ .bak)"

# ══════════════════════════════════════════════════════════
# 5. 앱 실행
# ══════════════════════════════════════════════════════════
Write-Section '5. GhostWin.App 실행'
$proc = Start-Process -FilePath $appExe -PassThru
Write-Host "PID $($proc.Id) 시작, 4초 대기 (PEB 폴링 × 3회 + snapshot 준비)..."
Start-Sleep -Seconds 4

# ══════════════════════════════════════════════════════════
# 6. 정상 종료
# ══════════════════════════════════════════════════════════
Write-Section '6. 정상 종료 (WM_CLOSE — OnClosing 에서 snapshot 저장)'
if (-not $proc.HasExited) {
    # MainWindowHandle 이 있어야 CloseMainWindow 가 작동
    $proc.Refresh()
    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) {
        Write-Host "⚠️ MainWindowHandle 없음, 1초 추가 대기"
        Start-Sleep -Seconds 1
        $proc.Refresh()
    }
    $closed = $proc.CloseMainWindow()
    Write-Host "CloseMainWindow: $closed"

    if (-not $proc.WaitForExit(8000)) {
        Write-Host "⚠️ 8초 내 종료 실패 — 강제 종료" -ForegroundColor Yellow
        $proc.Kill()
    }
}
Start-Sleep -Milliseconds 500  # 파일 flush 대기

# ══════════════════════════════════════════════════════════
# 7. session.json 검증
# ══════════════════════════════════════════════════════════
Write-Section '7. session.json 검증'
if (-not (Test-Path $sessionPath)) {
    Write-Host "❌ FAIL: session.json 이 생성되지 않았습니다." -ForegroundColor Red
    exit 2
}

$rawJson = Get-Content $sessionPath -Raw
Write-Host ""
Write-Host "── 저장된 session.json ──" -ForegroundColor Gray
Write-Host $rawJson -ForegroundColor Gray
Write-Host "───────────────────────" -ForegroundColor Gray
Write-Host ""

$json = $rawJson | ConvertFrom-Json
if ($json.workspaces.Count -eq 0) {
    Write-Host "❌ FAIL: workspaces 가 비어 있음" -ForegroundColor Red
    exit 2
}

$leaf = Find-FirstLeaf $json.workspaces[0].root
if (-not $leaf) {
    Write-Host "❌ FAIL: leaf 노드를 찾을 수 없음" -ForegroundColor Red
    exit 2
}

if ([string]::IsNullOrEmpty($leaf.cwd)) {
    Write-Host "❌ FAIL: leaf.cwd 가 null/empty — PEB 폴링이 작동하지 않음" -ForegroundColor Red
    Write-Host ""
    Write-Host "예상 원인:"
    Write-Host "  1. C++ poll_titles_and_cwd 변경이 빌드에 반영 안 됨 (클린 빌드 시도)"
    Write-Host "  2. DispatcherTimer 가 시작되지 않음 (MainWindow 초기화 경로 확인)"
    Write-Host "  3. PowerShell 이 3초 안에 완전히 시작 안 됨 (대기 시간 늘리기)"
    exit 3
}

Write-Host "✅ PASS: leaf.cwd = '$($leaf.cwd)'" -ForegroundColor Green
Write-Host "   PEB 폴링 정상 작동 — OSC 7 미지원 쉘에서도 cwd 획득 가능!" -ForegroundColor Green
Write-Host ""
Write-Host "다음 단계: 실제 cd 후 종료 → 재실행 → cwd 복원 확인 (수동 smoke)"
exit 0
