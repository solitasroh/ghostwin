$ErrorActionPreference = 'Stop'

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host " GhostWin Exhaustive Settings E2E Tests  " -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

Write-Host "1. Building the project..." -ForegroundColor Cyan
& .\scripts\build_incremental.ps1
if ($LASTEXITCODE -ne 0) { 
    Write-Error "Build failed, cannot run E2E test."
    exit 1 
}

$ExePath = ".\build\ghostwin_winui.exe"

$LogFile = ".\ghostwin_debug.log"
# Clear previous log to ensure clean seek
if (Test-Path $LogFile) { Remove-Item $LogFile -Force }
New-Item -Path $LogFile -ItemType File -Force | Out-Null

$ConfigPath = "$env:APPDATA\GhostWin\ghostwin.json"
# 기존의 더티해진 설정 파일을 삭제하여 무조건 기본 템플릿이 재생성되게 만듭니다.
if (Test-Path $ConfigPath) { Remove-Item $ConfigPath -Force }

Write-Host "2. Launching GhostWin..." -ForegroundColor Cyan
$process = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 4 # App startup and initial JSON generation overhead

if (-not (Test-Path $ConfigPath)) {
    Write-Host "❌ Config file was not created!" -ForegroundColor Red
    Stop-Process -Id $process.Id -Force
    exit 1
}

Write-Host "3. Running Domain-specific Hot-Reload Tests..." -ForegroundColor Cyan

function Test-Setting {
    param (
        [string]$testName,
        [string]$expectedFlagHexString,
        [scriptblock]$modifier
    )
    Write-Host (" {0,-25} (expecting {1,-5}) ... " -f "[$testName]", $expectedFlagHexString) -NoNewline
    
    $startSize = (Get-Item $LogFile).Length
    
    # Read, modify using scriptblock (regex replacement), write back
    $jsonString = Get-Content -Raw -Path $ConfigPath
    $newJson = & $modifier $jsonString 
    Set-Content -Path $ConfigPath -Value $newJson -Encoding UTF8

    Start-Sleep -Milliseconds 600 # 200ms debounce + processing margin
    
    # Read newly appended lines using System.IO (resolves file lock issues)
    $fs = [System.IO.File]::Open($LogFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $fs.Seek($startSize, [System.IO.SeekOrigin]::Begin) | Out-Null
    $reader = New-Object System.IO.StreamReader($fs)
    $newLogs = $reader.ReadToEnd()
    $reader.Close()
    $fs.Close()
    
    if ($newLogs -match "changed flags=$expectedFlagHexString") {
        Write-Host "✅ Pass" -ForegroundColor Green
    } else {
        Write-Host "❌ Fail" -ForegroundColor Red
        Write-Host "    Actual appended logs:" -ForegroundColor Yellow
        Write-Host $newLogs -ForegroundColor DarkGray
    }
}

# 8 Category Bitmask Tests
Test-Setting "TerminalFont"       "0x1"  { param($s) $s -replace '"size":\s*11.25', '"size": 15.0' }
Test-Setting "TerminalColors"     "0x2"  { param($s) $s -replace '"theme":\s*"catppuccin-mocha"', '"theme": "nord"' }
Test-Setting "TerminalCursor"     "0x4"  { param($s) $s -replace '"style":\s*"block"', '"style": "bar"' }
Test-Setting "TerminalWindow"     "0x8"  { param($s) $s -replace '"mica_enabled":\s*true', '"mica_enabled": false' }
Test-Setting "MultiplexerSidebar" "0x10" { param($s) $s -replace '"width":\s*200', '"width": 250' }
Test-Setting "MultiplexerBehavior""0x20" { param($s) $s -replace '"layout":\s*true', '"layout": false' }
Test-Setting "AgentConfig"        "0x40" { param($s) $s -replace '"ring_width":\s*2.5', '"ring_width": 3.0' }
Test-Setting "Keybindings"        "0x80" { param($s) $s -replace '"workspace.close":\s*"Ctrl\+W"', '"workspace.close": "Ctrl+Shift+W"' }

Write-Host "4. Cleaning up..." -ForegroundColor Cyan
Stop-Process -Id $process.Id -Force
Remove-Item $ConfigPath -Force # 다음 실행을 위해 초기 상태 복구
Write-Host "✅ Exhaustive E2E Test Suite Completed." -ForegroundColor Green
