$ErrorActionPreference = 'Stop'
$env:VSLANG = 1033
$vswhereDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path "$vswhereDir\vswhere.exe") -and ($env:PATH -notlike "*$vswhereDir*")) {
    $env:PATH = "$vswhereDir;$env:PATH"
}
$vcvarsall = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat'
cmd /c "`"$vcvarsall`" x64 10.0.22621.0 -vcvars_ver=14.51 && set" 2>&1 | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)') {
        [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
    }
}
Set-Location (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
cmake -B build -G Ninja -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl -DCMAKE_BUILD_TYPE=Release 2>&1 | Write-Host
cmake --build build 2>&1 | Write-Host
if ($LASTEXITCODE -eq 0) { Write-Host 'Build OK' -ForegroundColor Green }
else { Write-Error 'Build failed' }
