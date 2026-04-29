@echo off
REM ─────────────────────────────────────────────────────────────────────────
REM build_full.cmd — full GhostWin.sln build (C++ engine + .NET projects)
REM
REM Why this script exists:
REM   VS 18 Community ships with an empty MSBuild\Sdks\ directory, so a plain
REM   `msbuild GhostWin.sln` fails with `Microsoft.NET.Sdk` not found. We
REM   redirect MSBuildSDKsPath to the system-installed .NET 10 SDK Sdks
REM   folder. We also import VsDevCmd so the C++ projects can resolve the
REM   v145 toolset, and we stage the vswhere PATH so VsDevCmd's internal
REM   probes succeed.
REM
REM Usage:
REM   scripts\build_full.cmd                         # Debug x64 (default)
REM   scripts\build_full.cmd Release                 # Release x64
REM
REM See:
REM   .claude\rules\build-environment.md             # canonical build rules
REM ─────────────────────────────────────────────────────────────────────────

setlocal

REM Default Configuration if not provided.
if "%~1"=="" (set "CFG=Debug") else (set "CFG=%~1")

REM 1. vswhere lives outside %PATH% by default; VsDevCmd needs it.
set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%"

REM 2. Import VS 18 build environment (VC v145 toolset, ucrt headers, etc).
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64 -no_logo
if errorlevel 1 (
  echo [build_full] VsDevCmd.bat failed.
  exit /b 1
)

REM 3. Point MSBuild at the system .NET 10 SDK Sdks folder, since VS's own
REM    MSBuild\Sdks\ is empty on this install.
set "MSBuildSDKsPath=C:\Program Files\dotnet\sdk\10.0.203\Sdks"

REM 4. Build the full solution.
msbuild GhostWin.sln /p:Configuration=%CFG% /p:Platform=x64 /m /nologo /verbosity:minimal
exit /b %ERRORLEVEL%
