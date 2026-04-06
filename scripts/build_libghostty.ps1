#Requires -Version 5.1
<#
.SYNOPSIS
    libghostty-vt Windows 빌드 스크립트

.DESCRIPTION
    Zig 0.15.2로 libghostty-vt를 Windows GNU 타겟, SIMD 비활성화로 빌드합니다.
    CRT 독립 static lib을 생성합니다 (VirtualAlloc 직접 사용, CRT 불필요).

    ADR-001: -Dsimd=false + x86_64-windows-gnu 채택 근거 참조

    Prerequisites:
      - Zig 0.15.2 (scoop install zig@0.15.2)
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$GhosttyDir = Join-Path $ScriptDir '..\external\ghostty'

# ─── Step 1: Zig 확인 ───
Write-Host '[1/3] Verifying Zig...' -ForegroundColor Cyan
$zigVer = & zig version 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error 'Zig not found. Install with: scoop install zig@0.15.2'
    exit 1
}
Write-Host "  Zig $zigVer"

# ─── Step 2: 빌드 ───
Write-Host '[2/3] Building libghostty-vt (x86_64-windows-gnu, simd=false)...' -ForegroundColor Cyan

# Zig 크로스 드라이브 경로 패닉 방지: 글로벌 캐시를 프로젝트와 같은 드라이브에 배치
$projectDrive = (Split-Path -Qualifier $GhosttyDir)
$env:ZIG_GLOBAL_CACHE_DIR = "$projectDrive\zig-cache"

Push-Location $GhosttyDir
try {
    $ErrorActionPreference = 'Continue'
    & zig build -Demit-lib-vt=true -Dapp-runtime=none -Dtarget=x86_64-windows-gnu -Dsimd=false 2>&1 | Write-Host
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'zig build failed'
        exit 1
    }
}
finally {
    Pop-Location
}

# ─── Step 3: 산출물 확인 ───
Write-Host '[3/3] Verifying output...' -ForegroundColor Cyan
$staticLib = Join-Path $GhosttyDir 'zig-out\lib\ghostty-vt-static.lib'

if (Test-Path $staticLib) {
    $size = [math]::Round((Get-Item $staticLib).Length / 1MB, 1)
    Write-Host "  Static lib: ghostty-vt-static.lib (${size}MB)" -ForegroundColor Green
    Write-Host 'BUILD SUCCESS' -ForegroundColor Green
}
else {
    Write-Error "Build output not found at $staticLib"
    exit 1
}
