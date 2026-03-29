# ADR-004: MSVC /utf-8 소스 인코딩 강제

- **상태**: 채택
- **날짜**: 2026-03-29
- **관련**: Phase 2 ConPTY 구현 중 발견

## 배경

Phase 1에서는 `<windows.h>`를 직접 include하는 소스 파일이 없었으나, Phase 2에서 `conpty_session.cpp`가 `<windows.h>`를 include하면서 문제가 발생했다.

## 발견된 문제

한국어 Windows(CP949)에서 MSVC가 UTF-8 소스 파일을 컴파일할 때:

1. C4819 경고 발생: "현재 코드 페이지(949)에서 표현할 수 없는 문자가 포함"
2. 이 경고가 헤더 파싱을 방해하여 `<cstdint>` 등 표준 라이브러리 헤더를 찾지 못하는 C1083 에러로 이어짐
3. 소스 파일에 실제 비-ASCII 문자가 없어도 발생 (MSVC의 인코딩 탐지 로직 문제)

## 결정

`CMakeLists.txt`에 `/utf-8` 컴파일 플래그를 추가하여 모든 소스 파일을 UTF-8로 강제 해석:

```cmake
add_compile_options("$<$<C_COMPILER_ID:MSVC>:/utf-8>")
add_compile_options("$<$<CXX_COMPILER_ID:MSVC>:/utf-8>")
```

## 대안 검토

| 방안 | 판정 | 이유 |
|------|:----:|------|
| UTF-8 BOM 추가 | 기각 | git diff 오염, 다른 도구와 호환성 문제 |
| `/source-charset:utf-8` | 불필요 | `/utf-8`이 source + execution charset 모두 포함 |
| 소스에서 비-ASCII 제거 | 불충분 | 이미 순수 ASCII인데도 C4819 발생 |
| **`/utf-8` 플래그** | **채택** | 근본 해결, 모든 소스에 일괄 적용 |

## 영향

- Phase 1 코드 포함 모든 소스에 적용 (기존 빌드 정상 유지)
- 향후 추가되는 모든 `.c`/`.cpp` 파일에 자동 적용
- 성능 영향 없음
