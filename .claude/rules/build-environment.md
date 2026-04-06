---
paths:
  - "CMakeLists.txt"
  - "scripts/**/*.ps1"
  - "external/ghostty/**"
  - ".zig-version"
  - "src/**"
---

# 빌드 환경 규칙

## ⚠️ 빌드 명령 (반드시 스크립트 사용)
- **`cmake --build build` 직접 실행 금지** — MSVC include 경로 누락으로 `cstdint` 등 표준 헤더 에러 발생
- GhostWin 빌드: `powershell -ExecutionPolicy Bypass -File scripts/build_ghostwin.ps1 -Config Release`
- libghostty 빌드: `powershell -ExecutionPolicy Bypass -File scripts/build_libghostty.ps1`
- 스크립트가 vcvarsall 환경 설정 + 한국어 `/showIncludes` 패치를 자동 처리

## Visual Studio
- **MSVC 14.51** 사용 필수 (`-vcvars_ver=14.51`, 14.50은 동적 CRT 누락)
- VS 18 Insiders: `C:\Program Files\Microsoft Visual Studio\18\Insiders` (현재 설치)
- VS 2022 Professional: fallback 경로
- 스크립트가 Community → Insiders → Professional → BuildTools 순으로 자동 탐색

## Windows SDK
- **10.0.22621.0** 기본 사용 (UCRT include + lib 모두 존재)

## 빌드 도구
- **Zig**: 0.15.2 (`C:\zig\zig-x86_64-windows-0.15.2\`)
- **CMake**: 4.0+ / **Ninja**: `C:\ninja\bin\ninja.exe`
- **.NET SDK**: 10.0 (WPF PoC 빌드용)

## libghostty-vt Zig 빌드 (ADR-001)
```
zig build -Demit-lib-vt=true -Dapp-runtime=none -Dtarget=x86_64-windows-gnu -Dsimd=false
```
- GNU 타겟 + SIMD 비활성화: CRT 독립, Zig 내장 libc
- `--libc msvc_libc.txt` 불필요
- **크로스 드라이브 주의**: Zig 글로벌 캐시(C:)와 프로젝트(D:)가 다른 드라이브면 `ZIG_GLOBAL_CACHE_DIR`을 프로젝트 드라이브로 설정 필수 (상대 경로 변환 assert 패닉)

## WPF PoC 빌드
```powershell
powershell -ExecutionPolicy Bypass -File scripts/build_wpf_poc.ps1 -Config Release
```
- Step 1: Engine DLL (CMake + Ninja → `ghostwin_engine.dll`)
- Step 2: 네이티브 DLL을 WPF 출력 경로로 복사
- Step 3: `dotnet build` (WPF PoC → `wpf-poc/bin/x64/Release/net10.0-windows/`)
- 실행: `powershell -ExecutionPolicy Bypass -File run_wpf_poc.ps1`

## CMake 빌드
```powershell
vcvarsall.bat x64 -vcvars_ver=14.51
cmake -B build -G Ninja -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl
cmake --build build
```

## 한국어 Windows 주의
- Ninja + MSVC 조합에서 `/showIncludes` CP949 접두사 → lexing error 발생
- `build_ghostwin.ps1`에서 CMake 캐시 패치로 해결됨 (영어 접두사 강제)
