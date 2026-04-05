param([string]$Config = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

Write-Host "=== GhostWin WPF PoC Build ===" -ForegroundColor Cyan

# Step 1: Build engine DLL (CMake + Ninja)
Write-Host "`n--- Step 1: Building engine DLL ---" -ForegroundColor Yellow
& "$root\scripts\build_ghostwin.ps1" -Config $Config
if ($LASTEXITCODE -ne 0) {
    Write-Error "Engine build failed"
    exit 1
}

# Step 2: Copy native DLLs to WPF output
$wpfDir = "$root\wpf-poc"
$buildDir = "$root\build"
$outDir = "$wpfDir\bin\$Config\net10.0-windows"

if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$dlls = @("ghostwin_engine.dll", "ghostty-vt.dll")
foreach ($dll in $dlls) {
    $src = "$buildDir\$dll"
    if (Test-Path $src) {
        Copy-Item $src $outDir -Force
        Write-Host "  Copied $dll" -ForegroundColor Green
    } else {
        Write-Warning "$dll not found at $src"
    }
}

# Step 3: Build WPF PoC (dotnet)
Write-Host "`n--- Step 3: Building WPF PoC ---" -ForegroundColor Yellow
dotnet build "$wpfDir\GhostWinPoC.csproj" -c $Config
if ($LASTEXITCODE -ne 0) {
    Write-Error "WPF build failed"
    exit 1
}

# Ensure DLLs are in output after dotnet build
foreach ($dll in $dlls) {
    $src = "$buildDir\$dll"
    if (Test-Path $src) {
        Copy-Item $src $outDir -Force
    }
}

Write-Host "`n=== Build Complete ===" -ForegroundColor Cyan
Write-Host "Run: $outDir\GhostWinPoC.exe" -ForegroundColor Green
