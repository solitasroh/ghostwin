#Requires -Version 5.1
<#
.SYNOPSIS
    GhostWin VtCore 빌드 + 테스트 스크립트

.DESCRIPTION
    MSVC 환경을 설정하고 CMake + Ninja로 VtCore 래퍼와 테스트를 빌드/실행합니다.

    Prerequisites:
      - VS 2026 Community (C++ Desktop 워크로드)
      - CMake 3.25+
      - Ninja
      - libghostty-vt 빌드 완료 (build_libghostty.ps1 먼저 실행)
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Debug'
)

$ErrorActionPreference = 'Stop'
$ProjectDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

# ─── Step 1: MSVC 환경 Import ───
Write-Host '[1/4] Setting up MSVC environment...' -ForegroundColor Cyan

$vcvarsall = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat'
if (-not (Test-Path $vcvarsall)) {
    # Fallback to VS 2019 BuildTools
    $vcvarsall = 'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvarsall.bat'
}
if (-not (Test-Path $vcvarsall)) {
    Write-Error "vcvarsall.bat not found. Install VS C++ Desktop workload."
    exit 1
}

cmd /c "`"$vcvarsall`" x64 && set" 2>&1 | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)') {
        [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
    }
}

$clPath = Get-Command cl -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $clPath) {
    Write-Error 'cl.exe not found after vcvarsall. MSVC environment import failed.'
    exit 1
}
Write-Host "  cl.exe: $clPath"

# ─── Step 2: CMake Configure ───
Write-Host '[2/4] CMake configure...' -ForegroundColor Cyan
Set-Location $ProjectDir

$buildDir = Join-Path $ProjectDir 'build'
if (Test-Path $buildDir) {
    Remove-Item -Recurse -Force $buildDir
}

& cmake -B build -G Ninja `
    -DCMAKE_C_COMPILER=cl `
    -DCMAKE_CXX_COMPILER=cl `
    -DCMAKE_BUILD_TYPE=$Config 2>&1 | Write-Host

if ($LASTEXITCODE -ne 0) {
    Write-Error 'CMake configure failed'
    exit 1
}
Write-Host '  Configure OK' -ForegroundColor Green

# ─── Step 3: Build ───
Write-Host '[3/4] Building...' -ForegroundColor Cyan
& cmake --build build 2>&1 | Write-Host

if ($LASTEXITCODE -ne 0) {
    Write-Error 'Build failed'
    exit 1
}
Write-Host '  Build OK' -ForegroundColor Green

# ─── Step 4: Test ───
Write-Host '[4/4] Running tests...' -ForegroundColor Cyan
$testExe = Join-Path $buildDir 'vt_core_test.exe'
if (-not (Test-Path $testExe)) {
    Write-Error "Test executable not found: $testExe"
    exit 1
}

& $testExe 2>&1 | Write-Host
$testExit = $LASTEXITCODE

if ($testExit -eq 0) {
    Write-Host 'ALL TESTS PASSED' -ForegroundColor Green
}
else {
    Write-Error "Tests failed (exit code: $testExit)"
    exit 1
}
