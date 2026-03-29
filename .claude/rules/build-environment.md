---
paths:
  - "CMakeLists.txt"
  - "scripts/**/*.ps1"
  - "external/ghostty/**"
  - ".zig-version"
---

# 빌드 환경 규칙

## Visual Studio
- **MSVC 14.51.36014** 사용 필수 (`-vcvars_ver=14.51`, 14.50은 동적 CRT 누락)
- VS 2026 Community (v18): `C:\Program Files\Microsoft Visual Studio\18\Community`

## Windows SDK
- **10.0.22621.0** 기본 사용 (UCRT include + lib 모두 존재)

## 빌드 도구
- **Zig**: 0.15.2 (`.zig-version` 고정)
- **CMake**: 4.0+ / **Ninja**: `C:\ninja\bin\ninja.exe`

## libghostty-vt Zig 빌드 (ADR-001)
```
zig build -Demit-lib-vt=true -Dapp-runtime=none -Dtarget=x86_64-windows-gnu -Dsimd=false
```
- GNU 타겟 + SIMD 비활성화: CRT 독립, Zig 내장 libc
- `--libc msvc_libc.txt` 불필요

## CMake 빌드
```powershell
vcvarsall.bat x64 -vcvars_ver=14.51
cmake -B build -G Ninja -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl
cmake --build build
```

## 한국어 Windows 주의
- Ninja + MSVC 조합에서 `/showIncludes` CP949 접두사 → lexing error 발생
- `build_ghostwin.ps1`에서 CMake 캐시 패치로 해결됨 (영어 접두사 강제)
