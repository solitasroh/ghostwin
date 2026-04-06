param([string]$Config = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

Write-Host "=== GhostWin WPF Build ===" -ForegroundColor Cyan

# Step 1: Build engine DLL (CMake + Ninja)
Write-Host "`n--- Step 1: Building engine DLL ---" -ForegroundColor Yellow
& "$root\scripts\build_ghostwin.ps1" -Config $Config
if ($LASTEXITCODE -ne 0) {
    Write-Error "Engine build failed"
    exit 1
}

# Step 2: Build WPF App (dotnet)
$appDir = "$root\src\GhostWin.App"
Write-Host "`n--- Step 2: Building WPF App ---" -ForegroundColor Yellow
dotnet build "$appDir\GhostWin.App.csproj" -c $Config
if ($LASTEXITCODE -ne 0) {
    Write-Error "WPF build failed"
    exit 1
}

# Step 3: Copy native DLLs to WPF output
$buildDir = "$root\build"
$outDirs = @(
    "$appDir\bin\Release\net10.0-windows",
    "$appDir\bin\x64\Release\net10.0-windows"
)

$dlls = @("ghostwin_engine.dll", "ghostty-vt.dll")
foreach ($dir in $outDirs) {
    if (-not (Test-Path $dir)) { continue }
    foreach ($dll in $dlls) {
        $src = "$buildDir\$dll"
        if (Test-Path $src) {
            Copy-Item $src $dir -Force
            Write-Host "  Copied $dll -> $dir" -ForegroundColor Green
        } else {
            Write-Warning "$dll not found at $src"
        }
    }
}

Write-Host "`n=== Build Complete ===" -ForegroundColor Cyan
Write-Host "Run: scripts\run_wpf.ps1" -ForegroundColor Green
