# ADR-005: Windows SDK 10.0.22621.0 버전 고정

- **상태**: 채택
- **날짜**: 2026-03-29
- **관련**: Phase 2 ConPTY 구현 중 발견

## 배경

Phase 2에서 `conpty_session.cpp`가 `<windows.h>`를 include하면서 Windows SDK 헤더 경로가 빌드에 영향을 미치게 되었다. 빌드 환경에 SDK 22621과 26100이 공존한다.

## 발견된 문제

Windows SDK 10.0.26100.0의 `shared/specstrings.h`가 `specstrings_strict.h`를 include하지만, SDK 26100 설치에 이 파일이 누락되어 있다:

```
C:\Program Files (x86)\Windows Kits\10\include\10.0.26100.0\shared\specstrings.h(674):
  fatal error C1083: 'specstrings_strict.h': No such file or directory
```

SDK 22621에는 해당 파일이 존재하여 정상 빌드된다.

## 결정

`vcvarsall.bat` 호출 시 SDK 버전을 명시적으로 지정:

```powershell
# build_ghostwin.ps1
cmd /c "`"$vcvarsall`" x64 10.0.22621.0 -vcvars_ver=14.51 && set"
```

`CMakeLists.txt`에도 방어적으로 설정:

```cmake
set(CMAKE_SYSTEM_VERSION 10.0.22621.0)
set(CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION 10.0.22621.0)
```

## 대안 검토

| 방안 | 판정 | 이유 |
|------|:----:|------|
| SDK 26100 재설치/수리 | 불확실 | 불완전 설치인지 SDK 자체 결함인지 불명 |
| SDK 26100만 제거 | 위험 | 다른 프로젝트에 영향 가능 |
| **vcvarsall에 SDK 버전 명시** | **채택** | 확실한 제어, 다른 프로젝트에 영향 없음 |

## 영향

- `build_ghostwin.ps1` 수정 (vcvarsall 인수 추가)
- `build_libghostty.ps1`은 Zig 빌드이므로 영향 없음 (Windows SDK 미사용)
- 향후 SDK 업데이트 시 이 버전 고정을 재검토해야 함
- Phase 1 코드는 `<windows.h>` 미사용이므로 이전에는 문제가 없었음

## 검증

SDK 22621 지정 후:
- `conpty_session.cpp` 컴파일 성공
- Phase 1 테스트 7/7 PASS 유지
- ConPTY 통합 테스트 8/8 PASS
