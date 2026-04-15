# GhostWin M-11 전체 복원 E2E 자동 검증
# 2026-04-15
#
# DEPRECATED (2026-04-16, M-11.5 E2E Test Harness 체계화):
#   이 스크립트의 검증 로직은 xUnit 으로 이식됨.
#   대체: dotnet test tests/GhostWin.E2E.Tests --filter "Class=FileStateScenarios&TestName=CwdRestore_RoundTrip"
#   이 파일은 참고용으로 보존되며 Phase 5 종료 후 삭제 예정.
#
# 핵심 아이디어: 키보드 입력 없이 검증 가능
#   Start-Process 의 -WorkingDirectory 로 앱 시작 CWD 변경
#   → PowerShell 이 해당 CWD 에서 시작
#   → PEB 폴링이 cwd 캡쳐
#   → 종료 시 session.json 에 저장
#   → 재실행 시 기본 CWD 에서 실행해도 session.json 의 cwd 가 복원됨
#
# 검증 단계:
#   Run 1: C:\temp 에서 앱 실행 → cwd="C:\temp" 저장 확인
#   Run 2: repoRoot 에서 앱 실행 (default CWD 변경됨)
#           → session.json 에서 복원 → 새 pwsh 세션이 C:\temp 에서 시작되어야 함
#           → 폴링이 다시 "C:\temp" 캡쳐 → session.json 재기록
#
# 종료 코드: 0=PASS, 1=빌드실패, 2=Run1 저장실패, 3=Run2 복원실패

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $PSScriptRoot
$slnPath     = Join-Path $repoRoot 'GhostWin.sln'
$appExe      = Join-Path $repoRoot 'src\GhostWin.App\bin\Debug\net10.0-windows\win-x64\GhostWin.App.exe'
$sessionPath = Join-Path $env:APPDATA 'GhostWin\session.json'
$bakPath     = "$sessionPath.bak"
$testCwd     = 'C:\temp'

function Write-Section($msg) {
    Write-Host ''
    Write-Host "=== $msg ===" -ForegroundColor Cyan
}

function Stop-RunningApp {
    $running = Get-Process -Name 'GhostWin.App' -ErrorAction SilentlyContinue
    if ($running) {
        foreach ($p in $running) { $null = $p.CloseMainWindow() }
        Start-Sleep -Seconds 2
        $still = Get-Process -Name 'GhostWin.App' -ErrorAction SilentlyContinue
        if ($still) { $still | Stop-Process -Force; Start-Sleep -Milliseconds 500 }
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

function Run-App-And-Close($cwd, $waitSec = 4) {
    $proc = Start-Process -FilePath $appExe -WorkingDirectory $cwd -PassThru
    Start-Sleep -Seconds $waitSec
    if (-not $proc.HasExited) {
        $proc.Refresh()
        $null = $proc.CloseMainWindow()
        if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
    }
    Start-Sleep -Milliseconds 500
}

# Prep: 환경
Write-Section '환경 확인'
if (-not (Test-Path $testCwd)) { New-Item -ItemType Directory -Path $testCwd | Out-Null }
Stop-RunningApp

if (-not (Test-Path $appExe)) {
    Write-Host "앱 바이너리 없음 → 빌드 필요. scripts/test_m11_cwd_peb.ps1 을 먼저 실행하세요." -ForegroundColor Yellow
    exit 1
}

# Run 1: C:\temp 에서 실행
Write-Section "Run 1: $testCwd 에서 앱 실행"
Remove-Item $sessionPath -ErrorAction SilentlyContinue
Remove-Item $bakPath -ErrorAction SilentlyContinue
Run-App-And-Close -cwd $testCwd

if (-not (Test-Path $sessionPath)) {
    Write-Host "FAIL: Run 1 에서 session.json 이 생성되지 않음" -ForegroundColor Red
    exit 2
}

$json = Get-Content $sessionPath -Raw | ConvertFrom-Json
$leaf = Find-FirstLeaf $json.workspaces[0].root
Write-Host "Run 1 기록된 cwd: '$($leaf.cwd)'"

if ($leaf.cwd -ne $testCwd) {
    Write-Host "FAIL: Run 1 에서 cwd 가 '$testCwd' 로 저장 안 됨 (실제: '$($leaf.cwd)')" -ForegroundColor Red
    exit 2
}
Write-Host "  [OK] Run 1 기록 일치" -ForegroundColor Green

# Run 2: repoRoot 에서 재실행 (default CWD 가 바뀌었지만 복원은 C:\temp 여야)
Write-Section "Run 2: $repoRoot 에서 재실행 (복원 검증)"
Run-App-And-Close -cwd $repoRoot

if (-not (Test-Path $sessionPath)) {
    Write-Host "FAIL: Run 2 에서 session.json 이 사라짐" -ForegroundColor Red
    exit 3
}

$json2 = Get-Content $sessionPath -Raw | ConvertFrom-Json
$leaf2 = Find-FirstLeaf $json2.workspaces[0].root
Write-Host "Run 2 기록된 cwd: '$($leaf2.cwd)'"

if ($leaf2.cwd -ne $testCwd) {
    Write-Host "FAIL: Run 2 에서 cwd 가 '$testCwd' 로 복원되지 않음 (실제: '$($leaf2.cwd)')" -ForegroundColor Red
    Write-Host "  → RestoreFromSnapshot 이 저장된 cwd 를 사용하지 않았거나, CreateSession(cwd) 오버로드가 작동 안 함" -ForegroundColor Yellow
    exit 3
}

Write-Host ""
Write-Host "=== PASS: M-11 전체 복원 E2E 성공 ===" -ForegroundColor Green
Write-Host "  Run 1: $testCwd 에서 실행 → cwd 저장 OK" -ForegroundColor Green
Write-Host "  Run 2: $repoRoot 에서 재실행 → session.json 에서 cwd 복원 → pwsh 가 $testCwd 에서 시작됨" -ForegroundColor Green
exit 0
