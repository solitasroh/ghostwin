# GhostWin Terminal — Project Rules

## 빌드 환경

### Visual Studio
- **사용**: Visual Studio Community 2026 (v18)
  - 경로: `C:\Program Files\Microsoft Visual Studio\18\Community`
  - vcvarsall: `...\VC\Auxiliary\Build\vcvarsall.bat`
  - **MSVC 14.51.36014** 사용 필수 (`-vcvars_ver=14.51`, 14.50은 동적 CRT 누락)
- **레거시**: VS 2019 BuildTools (MSVC 14.29) — `C:\Program Files (x86)\...\2019\BuildTools`

### Windows SDK
- **10.0.22621.0** — UCRT include + lib 모두 존재 (기본 사용)
- 10.0.26100.0 — ucrt include 누락, um/x64만 사용 가능
- 10.0.19041.0

### 빌드 도구
- **Zig**: 0.15.2 (scoop)
- **CMake**: 4.0+
- **Ninja**: `C:\ninja\bin\ninja.exe`
- **Git**: 2.53.0

### libghostty-vt 빌드 (ADR-001)
```
zig build -Demit-lib-vt=true -Dapp-runtime=none -Dtarget=x86_64-windows-gnu -Dsimd=false
```
- **GNU 타겟 + SIMD 비활성화**: CRT 독립, C++ 의존성 없음, Zig 내장 libc
- `--libc msvc_libc.txt` **불필요** (GNU 타겟)
- ghostty 소스에 MSVC 호환 패치 3건 적용됨 (MSVC 빌드 시에만 필요)
- 산출물: `external/ghostty/zig-out/lib/ghostty-vt-static.lib` (7.8MB)

### CMake 빌드
```powershell
# MSVC 14.51 환경 설정 필수
vcvarsall.bat x64 -vcvars_ver=14.51
cmake -B build -G Ninja -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl
cmake --build build
```

## 아키텍처 결정 (ADR)

| ADR | 결정 | 근거 |
|-----|------|------|
| [001](docs/adr/001-simd-false-gnu-target.md) | windows-gnu + simd=false | MSVC CRT 초기화 문제 회피, CRT 독립 |
| [002](docs/adr/002-c-bridge-pattern.md) | C 브릿지 레이어 | MSVC C++ typedef 충돌 회피 |
| [003](docs/adr/003-dll-dynamic-crt.md) | DLL→static lib 회귀 | DLL에서 CRT 미초기화 crash |

## 행동 규칙

### 의존성 누락 시 대응 (필수 준수)
- 빌드/실행에 필요한 의존성이 누락된 경우:
  1. **절대 우회하지 말 것** (임의 판단 금지)
  2. 누락 항목을 정확히 식별 → 사용자에게 보고
  3. 설치 방법 안내 → 확인 후 설치 → 원래 작업 재개

### 빌드 실패 시 대응
1. 에러 메시지 정확히 분석
2. 누락 파일/라이브러리 → 설치부터 (우회 금지)
3. 코드 문제 → 수정 후 재빌드

### 커밋 규칙
- **Co-Authored-By 금지**: AI 도구 언급을 커밋 메시지에 포함하지 말 것
- 커밋 메시지는 순수하게 변경 내용만 기술

### 스크립트 작성 규칙
- **PowerShell (.ps1) 우선** — bat/cmd 금지
- 스크립트 위치: `scripts/`
  - `build_libghostty.ps1` — libghostty-vt Zig 빌드
  - `build_ghostwin.ps1` — CMake + Ninja + 테스트
