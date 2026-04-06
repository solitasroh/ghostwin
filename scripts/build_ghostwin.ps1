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
    # Fallback to VS 18 Insiders
    $vcvarsall = 'C:\Program Files\Microsoft Visual Studio\18\Insiders\VC\Auxiliary\Build\vcvarsall.bat'
}
if (-not (Test-Path $vcvarsall)) {
    # Fallback to VS 2022 Professional
    $vcvarsall = 'C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvarsall.bat'
}
if (-not (Test-Path $vcvarsall)) {
    # Fallback to VS 2019 BuildTools
    $vcvarsall = 'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvarsall.bat'
}
if (-not (Test-Path $vcvarsall)) {
    Write-Error "vcvarsall.bat not found. Install VS C++ Desktop workload."
    exit 1
}

# vswhere.exe가 PATH에 없으면 VS Installer 경로 추가 (vcvarsall 내부에서 필요)
$vswhereDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path "$vswhereDir\vswhere.exe") -and ($env:PATH -notlike "*$vswhereDir*")) {
    $env:PATH = "$vswhereDir;$env:PATH"
}

# MSVC 출력을 영어로 강제 — 한국어 /showIncludes 접두사가 Ninja lexer에서 CP949 파싱 에러 유발
$env:VSLANG = 1033

cmd /c "`"$vcvarsall`" x64 10.0.22621.0 -vcvars_ver=14.51 && set" 2>&1 | ForEach-Object {
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

$cmakeArgs = @('-B', 'build', '-G', 'Ninja',
    '-DCMAKE_C_COMPILER=cl', '-DCMAKE_CXX_COMPILER=cl',
    "-DCMAKE_BUILD_TYPE=$Config")
$ErrorActionPreference = 'Continue'
& cmake @cmakeArgs 2>&1 | Write-Host
$ErrorActionPreference = 'Stop'

if ($LASTEXITCODE -ne 0) {
    Write-Error 'CMake configure failed'
    exit 1
}

# 한국어 Windows: CMake가 감지한 /showIncludes 접두사가 CP949 한글이라 Ninja lexer 에러 유발
# CMake 캐시 컴파일러 파일을 영어 접두사로 패치 후 재생성
# 한국어 /showIncludes 접두사 패치 — CMake 컴파일러 캐시 파일 직접 수정
$cmakeVer = & cmake --version
$cmakeVer = ($cmakeVer | Select-Object -First 1) -replace 'cmake version ',''
$needReconfig = $false
foreach ($name in @('CMakeCCompiler.cmake', 'CMakeCXXCompiler.cmake')) {
    $compFile = Join-Path $buildDir "CMakeFiles\$cmakeVer\$name"
    Write-Verbose "  Checking $compFile"
    if (-not (Test-Path $compFile)) {
        Write-Host "  [WARN] Not found: $compFile" -ForegroundColor Yellow
        continue
    }
    $bytes = [System.IO.File]::ReadAllBytes($compFile)
    $text = [System.Text.Encoding]::Default.GetString($bytes)
    if ($text -match 'CL_SHOWINCLUDES_PREFIX' -and $text -notmatch 'Note: including file:') {
        Write-Host "  Patching $name (ko_KR locale -> English)" -ForegroundColor Yellow
        $text = $text -replace '(CL_SHOWINCLUDES_PREFIX ")[^"]*(")', '${1}Note: including file: ${2}'
        [System.IO.File]::WriteAllBytes($compFile, [System.Text.Encoding]::Default.GetBytes($text))
        $needReconfig = $true
    }
}
if ($needReconfig) {
    $ErrorActionPreference = 'Continue'
    & cmake -B build 2>&1 | Out-Null
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'CMake reconfigure after patch failed'
        exit 1
    }
}

Write-Host '  Configure OK' -ForegroundColor Green

# ─── Step 3: Build ───
Write-Host '[3/4] Building...' -ForegroundColor Cyan
$ErrorActionPreference = 'Continue'
& cmake --build build 2>&1 | Write-Host
$ErrorActionPreference = 'Stop'

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

$ErrorActionPreference = 'Continue'
& $testExe 2>&1 | Write-Host
$ErrorActionPreference = 'Stop'
$testExit = $LASTEXITCODE

if ($testExit -eq 0) {
    Write-Host 'ALL TESTS PASSED' -ForegroundColor Green
}
else {
    Write-Error "Tests failed (exit code: $testExit)"
    exit 1
}
