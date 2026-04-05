$ErrorActionPreference = 'Stop'

Write-Host "1. Building the project..." -ForegroundColor Cyan
& .\scripts\build_incremental.ps1

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed, cannot run E2E test."
    exit 1
}

$ExePath = ".\build\ghostwin.exe" 
if (-not (Test-Path $ExePath)) {
    $ExePath = ".\build\ghostwin_winui.exe"
}

Write-Host "2. Launching GhostWin in the background..." -ForegroundColor Cyan
$process = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 3 # 앱 켜지고 기본 JSON 파일이 생성될 때까지 대기

$LogFile = ".\ghostwin_debug.log"
$ConfigPath = "$env:APPDATA\GhostWin\ghostwin.json"

if (-not (Test-Path $ConfigPath)) {
    Write-Host "❌ Config file was not created at $ConfigPath!" -ForegroundColor Red
    Stop-Process -Id $process.Id -Force
    exit 1
}

Write-Host "3. Modifying ghostwin.json to trigger hot-reload..." -ForegroundColor Cyan
$jsonString = Get-Content -Raw -Path $ConfigPath
# ConvertFrom-Json 대신 정규식으로 안전하게 교체 (순서 유지 목적)
$newJson = $jsonString -replace '"theme":\s*"[^"]*"', '"theme": "dracula"'

# 만약 이미 dracula라면 다른 것으로 변경
if ($jsonString -match '"theme":\s*"dracula"') {
    $newJson = $jsonString -replace '"theme":\s*"dracula"', '"theme": "nord"'
}

Set-Content -Path $ConfigPath -Value $newJson -Encoding UTF8

Write-Host "File modified. Waiting for FileWatcherRAII debounce (200ms)..." -ForegroundColor Yellow
Start-Sleep -Seconds 1 # 확실히 감지되도록 1초 대기

Write-Host "4. Checking logs for reload event..." -ForegroundColor Cyan
# 로그 파일이 잠겨있을 수 있으므로 읽기 전용으로 열기 시도
$LogContent = Get-Content $LogFile -Tail 20 -ErrorAction SilentlyContinue

$ReloadFound = $false
# FileWatcher 로그 또는 SettingsManager 로그 모두 확인
foreach ($line in $LogContent) {
    if ($line -match "Config loaded \(changed flags=") {
        $ReloadFound = $true
        Write-Host "✅ Hot-reload detected in logs: $line" -ForegroundColor Green
        break
    }
}

if (-not $ReloadFound) {
    Write-Host "❌ Hot-reload event NOT FOUND in log. Tail of log is:" -ForegroundColor Red
    $LogContent | ForEach-Object { Write-Host "   $_" }
}

Write-Host "5. Closing GhostWin..." -ForegroundColor Cyan
Stop-Process -Id $process.Id -Force

Write-Host "E2E Test Complete." -ForegroundColor Cyan
