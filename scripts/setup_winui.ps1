#Requires -Version 5.1
<#
.SYNOPSIS
    Windows App SDK + CppWinRT NuGet 패키지 다운로드 및 cppwinrt 헤더 생성

.DESCRIPTION
    dotnet CLI를 사용하여 WinUI3 개발에 필요한 NuGet 패키지를 다운로드하고,
    cppwinrt.exe로 C++/WinRT projection 헤더를 생성합니다.
    결과물은 external/winui/ 에 배치됩니다.

    Prerequisites:
      - dotnet CLI (dotnet.exe)
      - Windows SDK 10.0.22621.0
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProjectDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$WinUIDir = Join-Path $ProjectDir 'external\winui'

# ─── Version Matrix ───
$WinAppSDKVersion = '1.6.250205002'
$CppWinRTVersion = '2.0.240405.15'
$SDKVersion = '10.0.22621.0'

Write-Host '=== GhostWin WinUI3 Setup ===' -ForegroundColor Cyan
Write-Host "  WindowsAppSDK: $WinAppSDKVersion"
Write-Host "  CppWinRT: $CppWinRTVersion"
Write-Host "  Target SDK: $SDKVersion"

# ─── Step 1: Create temp project for NuGet restore ───
Write-Host '[1/4] Creating temp project for NuGet restore...' -ForegroundColor Cyan

$tempDir = Join-Path $ProjectDir "external\winui_temp_$(Get-Random)"
if (Test-Path $tempDir) { Remove-Item -Recurse -Force $tempDir }
New-Item -ItemType Directory -Path $tempDir | Out-Null

# Minimal .csproj that references the NuGet packages
$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <Platform>x64</Platform>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="$WinAppSDKVersion" />
    <PackageReference Include="Microsoft.Windows.CppWinRT" Version="$CppWinRTVersion" />
  </ItemGroup>
</Project>
"@
$csproj | Set-Content (Join-Path $tempDir 'temp.csproj') -Encoding UTF8

# ─── Step 2: Restore packages ───
Write-Host '[2/4] Restoring NuGet packages (dotnet restore)...' -ForegroundColor Cyan
Push-Location $tempDir
try {
    $restoreOutput = & dotnet restore --verbosity minimal 2>&1
    $restoreOutput | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'dotnet restore failed'
        exit 1
    }
} finally {
    Pop-Location
}

# ─── Step 3: Copy packages to external/winui ───
Write-Host '[3/4] Extracting packages to external/winui/...' -ForegroundColor Cyan

if (Test-Path $WinUIDir) { Remove-Item -Recurse -Force $WinUIDir }
New-Item -ItemType Directory -Path $WinUIDir | Out-Null
New-Item -ItemType Directory -Path "$WinUIDir\include" | Out-Null
New-Item -ItemType Directory -Path "$WinUIDir\lib" | Out-Null
New-Item -ItemType Directory -Path "$WinUIDir\bin" | Out-Null

# Find NuGet cache
$nugetCache = Join-Path $env:USERPROFILE '.nuget\packages'

# Windows App SDK
$wasdkDir = Join-Path $nugetCache "microsoft.windowsappsdk\$WinAppSDKVersion"
if (-not (Test-Path $wasdkDir)) {
    Write-Error "WindowsAppSDK package not found at $wasdkDir"
    exit 1
}

# Copy Windows App SDK headers
$wasdkInclude = Join-Path $wasdkDir 'include'
if (Test-Path $wasdkInclude) {
    Copy-Item "$wasdkInclude\*" "$WinUIDir\include\" -Recurse -Force
    Write-Host "  Copied WinAppSDK headers"
}

# Copy Bootstrap lib
$wasdkLib = Join-Path $wasdkDir 'lib\win10-x64'
if (Test-Path $wasdkLib) {
    Copy-Item "$wasdkLib\*" "$WinUIDir\lib\" -Recurse -Force
    Write-Host "  Copied WinAppSDK libs"
}

# Copy runtime DLLs
$wasdkBin = Join-Path $wasdkDir 'runtimes\win-x64\native'
if (-not (Test-Path $wasdkBin)) {
    $wasdkBin = Join-Path $wasdkDir 'runtimes\win10-x64\native'
}
if (Test-Path $wasdkBin) {
    Copy-Item "$wasdkBin\*" "$WinUIDir\bin\" -Force
    Write-Host "  Copied WinAppSDK runtime DLLs"
}

# CppWinRT
$cppwinrtDir = Join-Path $nugetCache "microsoft.windows.cppwinrt\$CppWinRTVersion"
if (-not (Test-Path $cppwinrtDir)) {
    Write-Error "CppWinRT package not found at $cppwinrtDir"
    exit 1
}

# Find cppwinrt.exe
$cppwinrtExe = Get-ChildItem -Path $cppwinrtDir -Filter 'cppwinrt.exe' -Recurse | Select-Object -First 1
if (-not $cppwinrtExe) {
    Write-Error "cppwinrt.exe not found in $cppwinrtDir"
    exit 1
}
Write-Host "  Found cppwinrt.exe: $($cppwinrtExe.FullName)"

# ─── Step 4: Generate C++/WinRT projection headers ───
Write-Host '[4/4] Generating C++/WinRT projection headers...' -ForegroundColor Cyan

$cppwinrtOutput = Join-Path $WinUIDir 'cppwinrt'
New-Item -ItemType Directory -Path $cppwinrtOutput -Force | Out-Null

# Generate from Windows SDK metadata
$sdkMetadata = "C:\Program Files (x86)\Windows Kits\10\UnionMetadata\$SDKVersion"
if (-not (Test-Path $sdkMetadata)) {
    Write-Warning "Windows SDK metadata not found at $sdkMetadata, trying without SDK refs"
}

# Step 4a: Generate Windows SDK projection headers
if (-not (Test-Path $sdkMetadata)) {
    Write-Error "Windows SDK metadata required at $sdkMetadata"
    exit 1
}

Write-Host "  Running cppwinrt.exe (Windows SDK)..."
& $cppwinrtExe.FullName -input $sdkMetadata -output $cppwinrtOutput -optimize 2>&1 | ForEach-Object { Write-Host $_ }

if ($LASTEXITCODE -ne 0) {
    Write-Warning "cppwinrt.exe (SDK) returned non-zero exit code: $LASTEXITCODE"
}

# Step 4b: Generate WinAppSDK projection headers
# WinAppSDK winmd depends on WebView2 winmd — use it as -ref to resolve types
$winmdMain = Join-Path $wasdkDir 'lib\uap10.0'
$winmdFw = Join-Path $wasdkDir 'lib\uap10.0.18362'

# Find WebView2 winmd in NuGet cache (required as reference for Microsoft.UI.Xaml.winmd)
$wv2Dir = Get-ChildItem -Path (Join-Path $nugetCache 'microsoft.web.webview2') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$wv2Winmd = $null
if ($wv2Dir) {
    $wv2Winmd = Get-ChildItem -Path $wv2Dir.FullName -Filter 'Microsoft.Web.WebView2.Core.winmd' -Recurse | Select-Object -First 1
}

if (-not $wv2Winmd) {
    Write-Host '  WebView2 NuGet not found, installing via dotnet restore...'
    Push-Location $tempDir
    # Re-use temp project with WebView2 added
    $csprojWv2 = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <Platform>x64</Platform>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Web.WebView2" Version="*" />
  </ItemGroup>
</Project>
"@
    $wv2TempDir = Join-Path $ProjectDir "external\winui_wv2_$(Get-Random)"
    New-Item -ItemType Directory -Path $wv2TempDir -Force | Out-Null
    $csprojWv2 | Set-Content (Join-Path $wv2TempDir 'temp_wv2.csproj') -Encoding UTF8
    & dotnet restore --verbosity minimal 2>&1 | ForEach-Object { Write-Host $_ }
    Pop-Location
    Remove-Item -Recurse -Force $wv2TempDir -ErrorAction SilentlyContinue

    $wv2Dir = Get-ChildItem -Path (Join-Path $nugetCache 'microsoft.web.webview2') -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if ($wv2Dir) {
        $wv2Winmd = Get-ChildItem -Path $wv2Dir.FullName -Filter 'Microsoft.Web.WebView2.Core.winmd' -Recurse | Select-Object -First 1
    }
}

if ($wv2Winmd) {
    Write-Host "  Running cppwinrt.exe (WinAppSDK + framework winmd)..."
    $wasdkArgs = @('-input', $winmdMain, '-input', $winmdFw, '-ref', $sdkMetadata, '-ref', $wv2Winmd.FullName, '-output', $cppwinrtOutput, '-optimize')
    & $cppwinrtExe.FullName @wasdkArgs 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "cppwinrt.exe (WinAppSDK) returned non-zero exit code: $LASTEXITCODE"
    }
    # Also generate WebView2 projection headers (needed by Microsoft.UI.Xaml.Controls.h)
    Write-Host "  Running cppwinrt.exe (WebView2 projection headers)..."
    $wv2Args = @('-input', $wv2Winmd.FullName, '-ref', $sdkMetadata, '-output', $cppwinrtOutput, '-optimize')
    & $cppwinrtExe.FullName @wv2Args 2>&1 | ForEach-Object { Write-Host $_ }
} else {
    Write-Warning "WebView2 winmd not found. WinAppSDK C++/WinRT headers not generated."
    Write-Warning "Install WebView2 NuGet: dotnet add package Microsoft.Web.WebView2"
}

# Copy WinAppSDK native headers (MddBootstrap.h, dxinterop.h, etc.)
$wasdkIncludeHeaders = Join-Path $wasdkDir 'include'
if (Test-Path $wasdkIncludeHeaders) {
    Copy-Item "$wasdkIncludeHeaders\*" "$cppwinrtOutput\" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Merged WinAppSDK native headers"
}

# ─── Cleanup temp project ───
Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue

# ─── Verify ───
$headerCount = (Get-ChildItem -Path $cppwinrtOutput -Filter '*.h' -Recurse | Measure-Object).Count
Write-Host ''
Write-Host '=== Setup Complete ===' -ForegroundColor Green
Write-Host "  Headers: $headerCount files in $cppwinrtOutput"
Write-Host "  Libs:    $(Join-Path $WinUIDir 'lib')"
Write-Host "  DLLs:    $(Join-Path $WinUIDir 'bin')"

if ($headerCount -eq 0) {
    Write-Warning 'No C++/WinRT headers generated. Build may fail.'
}
