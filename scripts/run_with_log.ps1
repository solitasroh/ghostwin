# IME 진단 로그를 강제로 캡처하면서 GhostWin.App 실행
# - VS launchSettings.json 캐시 우회
# - stderr까지 파일로 합쳐서 capture

$logFile  = 'C:\Users\Solit\Rootech\works\ghostwin\ghostwin-diag.log'
$stderrFile = 'C:\Users\Solit\Rootech\works\ghostwin\ghostwin-stderr.log'
$exe = 'C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\bin\Debug\net10.0-windows10.0.22621.0\win-x64\GhostWin.App.exe'

if (-not (Test-Path $exe)) {
    Write-Host "[ERR] EXE not found: $exe" -ForegroundColor Red
    exit 1
}

# 기존 로그 삭제
Remove-Item $logFile, $stderrFile -ErrorAction SilentlyContinue

# 환경변수 명시적 설정 (현재 프로세스에만)
$env:GHOSTWIN_LOG_FILE = $logFile
$env:GHOSTWIN_IMEDIAG  = "1"  # M-13 BS stale resurrect 진단

Write-Host "[INFO] Launching app with GHOSTWIN_LOG_FILE=$logFile" -ForegroundColor Cyan
Write-Host "[INFO] stderr will be redirected to $stderrFile" -ForegroundColor Cyan
Write-Host "[INFO] Type Korean (한) and close the app, then check both log files." -ForegroundColor Yellow
Write-Host ""

# stderr 리다이렉트해서 실행 (앱이 닫힐 때까지 대기)
& $exe 2> $stderrFile

Write-Host ""
Write-Host "[INFO] App exited. Inspecting logs..." -ForegroundColor Cyan
Write-Host ""

if (Test-Path $logFile) {
    $sz = (Get-Item $logFile).Length
    Write-Host "[OK] $logFile ($sz bytes)" -ForegroundColor Green
    Write-Host "--- IME-related lines ---" -ForegroundColor Yellow
    Select-String -Path $logFile -Pattern 'IME|composition|HandleComposition|tsf' -CaseSensitive:$false |
        Select-Object -First 30 |
        ForEach-Object { Write-Host $_.Line }
} else {
    Write-Host "[FAIL] Log file NOT created: $logFile" -ForegroundColor Red
}

Write-Host ""
if (Test-Path $stderrFile) {
    $sz = (Get-Item $stderrFile).Length
    Write-Host "[OK] $stderrFile ($sz bytes)" -ForegroundColor Green
    Write-Host "--- stderr first 20 lines ---" -ForegroundColor Yellow
    Get-Content $stderrFile -TotalCount 20
}
