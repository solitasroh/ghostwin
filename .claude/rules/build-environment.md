---
paths:
  - "GhostWin.sln"
  - "src/**/*.csproj"
  - "src/**/*.vcxproj"
  - "tests/**/*.vcxproj"
  - "scripts/**/*.ps1"
  - "external/ghostty/**"
  - ".zig-version"
  - "src/**"
---

# 빌드 환경 규칙 (2026-04-14 — VS 통합)

## ⚠️ 빌드 명령 (필수 준수)

**빌드 검증 시 반드시 솔루션 전체 빌드를 사용할 것.** 개별 프로젝트 빌드(`dotnet build *.csproj`)는 C++ 프로젝트 누락 + 프로젝트 간 참조 불일치를 감지하지 못함.

```powershell
# ★ 권장 (VS Developer Command Prompt 또는 VS 내장 터미널)
msbuild GhostWin.sln /p:Configuration=Debug /p:Platform=x64

# VS GUI
GhostWin.sln 열고 빌드(Ctrl+Shift+B) 또는 F5
```

- `dotnet build GhostWin.sln`은 C++ vcxproj를 빌드하지 못함 (VCTargets 누락) → **빌드 검증에 사용 금지**
- `dotnet build *.csproj` 단독은 중간 확인용으로만 허용, 최종 검증은 반드시 `msbuild GhostWin.sln`
- **libghostty-vt (Zig)**: `powershell -ExecutionPolicy Bypass -File scripts/build_libghostty.ps1` (첫 빌드 시 자동 실행됨)

## Visual Studio

- **VS 18** 사용 (`C:\Program Files\Microsoft Visual Studio\18\<Edition>`) — Community / Professional / Enterprise / Insiders 모두 호환
- **PlatformToolset**: `v145` (VS 18 기본)
- **MSVC 14.51+**
- 설치 위치 확인: `C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe -latest -prerelease -find MSBuild\Current\Bin\amd64\MSBuild.exe`

## Windows SDK

- **10.0.22621.0** 고정 (vcxproj `WindowsTargetPlatformVersion`)

## 빌드 도구

- **Zig**: 0.15.2 (libghostty-vt 빌드용, 외부 스크립트)
- **.NET SDK**: 10.0

## 솔루션 프로젝트

### sln 등록 (`msbuild GhostWin.sln` 으로 자동 빌드)

| 프로젝트 | 경로 | 빌드 도구 |
|---------|------|-----------|
| GhostWin.Core | `src/GhostWin.Core/` | dotnet (SDK-style) |
| GhostWin.Interop | `src/GhostWin.Interop/` | dotnet |
| GhostWin.Services | `src/GhostWin.Services/` | dotnet |
| GhostWin.App | `src/GhostWin.App/` | dotnet (WinExe) |
| GhostWin.Hook | `src/GhostWin.Hook/` | dotnet |
| GhostWin.Engine | `src/GhostWin.Engine/` | MSBuild (C++ DLL) |
| GhostWin.Engine.Tests | `tests/GhostWin.Engine.Tests/` | MSBuild (C++ Exe — `GhostWinTestName` property 로 단일 테스트 빌드) |
| GhostWin.E2E.Tests | `tests/GhostWin.E2E.Tests/` | dotnet |
| GhostWin.MeasurementDriver | `tests/GhostWin.MeasurementDriver/` | dotnet (콘솔) |

### sln 미등록 (별도 `dotnet test` 호출 필요)

| 프로젝트 | 경로 | 비고 |
|---------|------|------|
| GhostWin.Core.Tests | `tests/GhostWin.Core.Tests/` | `dotnet test tests/GhostWin.Core.Tests/GhostWin.Core.Tests.csproj` |
| GhostWin.App.Tests | `tests/GhostWin.App.Tests/` | `dotnet test tests/GhostWin.App.Tests/GhostWin.App.Tests.csproj` |

## CRT 처리 (ADR-001/003)

ghostty-vt.lib가 GNU-CRT 타겟이라 MSVC 자동 CRT 링크 부분 실패. vcxproj에 명시적:
- **Debug**: `ucrtd.lib + vcruntimed.lib + msvcrtd.lib`
- **Release**: `ucrt.lib + vcruntime.lib + msvcrt.lib`

## libghostty-vt Zig 빌드 (ADR-001)

```
zig build -Demit-lib-vt=true -Dapp-runtime=none -Dtarget=x86_64-windows-gnu -Dsimd=false
```

- GNU 타겟 + SIMD 비활성화: CRT 독립
- vcxproj Pre-build에서 DLL 없을 때만 자동 실행
- **크로스 드라이브 주의**: Zig 글로벌 캐시(C:)와 프로젝트(D:)가 다른 드라이브면 `ZIG_GLOBAL_CACHE_DIR`을 프로젝트 드라이브로 설정 필수

## 디버깅

- `launchSettings.json`에 `nativeDebugging: true` 설정됨
- F5 실행 시 C# + C++ 브레이크포인트 동시 사용 가능
- Engine PDB가 App 출력 경로에 복사됨 (네이티브 콜스택 추적 가능)

## 테스트 실행

```
msbuild tests/GhostWin.Engine.Tests/GhostWin.Engine.Tests.vcxproj \
    /p:GhostWinTestName=vt_core_test /p:Configuration=Debug
```

사용 가능한 테스트: `tests/GhostWin.Engine.Tests/README.md` 참조

## 한국어 Windows

- vcxproj는 `/utf-8` 옵션 적용됨 (C4819 경고 회피)

## 제거된 레거시 (2026-04-14)

- CMakeLists.txt → vcxproj
- scripts/build_ghostwin.ps1, build_wpf.ps1, build_wpf_poc.ps1, build_incremental.ps1 → VS 솔루션
